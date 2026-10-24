# NovaDownload — browser extension packaging.
#
# Builds one loadable package per browser from the shared sources plus the per-browser manifest:
#
#   release/chrome-extension/    (manifest.json + shared code, ready for "Load unpacked")
#   release/edge-extension/
#   release/firefox-extension/
#   release/chrome-extension.zip (store-upload shaped: manifest.json at the archive root)
#   release/edge-extension.zip
#   release/firefox-extension.zip
#
#   powershell -File browser-extensions\build-extensions.ps1
#   powershell -File browser-extensions\build-extensions.ps1 -OutputRoot D:\out -NoZip
#
# Reproducible: archive entries use a fixed timestamp and forward-slash names, so two builds of the
# same sources produce byte-identical zips and therefore identical checksums.

param(
    [string]$OutputRoot,
    [switch]$NoZip,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }

$common = Join-Path $root 'common'
$browsers = @('chrome', 'edge', 'firefox')
$zipTimestamp = [DateTimeOffset]::Parse('2020-01-01T00:00:00Z')
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Remove-GeneratedDirectory([string]$path) {
    if (-not (Test-Path $path)) { return }
    $resolved = (Resolve-Path $path).Path
    if ($resolved -notlike "$OutputRoot*") {
        throw "Refusing to delete outside the output root: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Copy-SharedSources([string]$destination) {
    Get-ChildItem -Path $common -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($common.Length).TrimStart('\')
        $target = Join-Path $destination $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

# ZipArchive directly, so entry names use forward slashes (the Web Store rejects backslashes) and
# entries carry a fixed timestamp.
function New-ExtensionZip([string]$source, [string]$zipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -Path $source -Recurse -File | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($source.Length).TrimStart('\').Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $zipTimestamp
            $input = [System.IO.File]::OpenRead($_.FullName)
            try {
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            } finally { $input.Dispose() }
        }
    } finally {
        $archive.Dispose()
    }
}

if (-not (Test-Path $OutputRoot)) { New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null }
$OutputRoot = (Resolve-Path $OutputRoot).Path

$summary = @()

foreach ($browser in $browsers) {
    $manifest = Join-Path $root "$browser\manifest.json"
    if (-not (Test-Path $manifest)) { throw "Missing manifest: $manifest" }

    # Fail early on an invalid manifest instead of shipping a package a browser will reject.
    $parsed = Get-Content $manifest -Raw | ConvertFrom-Json
    if ($parsed.manifest_version -ne 3) { throw "$browser manifest is not Manifest V3" }

    $target = Join-Path $OutputRoot "$browser-extension"
    Remove-GeneratedDirectory $target
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    Copy-SharedSources $target
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $target 'manifest.json') -Force

    $files = Get-ChildItem -Path $target -Recurse -File
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum

    $zipPath = $null
    if (-not $NoZip) {
        $zipPath = Join-Path $OutputRoot "$browser-extension.zip"
        New-ExtensionZip $target $zipPath
    }

    $summary += [pscustomobject]@{
        Browser  = $browser
        Version  = $parsed.version
        Folder   = $target
        Files    = $files.Count
        Bytes    = $bytes
        Zip      = $zipPath
    }
}

Write-Output 'Extension packages'
Write-Output '------------------'
foreach ($row in $summary) {
    Write-Output ("  {0,-8} v{1}  {2,3} files  {3,7:N0} bytes  {4}" -f $row.Browser, $row.Version, $row.Files, $row.Bytes, $row.Folder)
    if ($row.Zip) { Write-Output ("           zip: {0}" -f $row.Zip) }
}

if (-not $SkipTests) {
    $runner = Join-Path $repo 'tests\extension-tests\run-tests.js'
    if ((Test-Path $runner) -and (Get-Command node -ErrorAction SilentlyContinue)) {
        Write-Output ''
        Write-Output 'Verifying packages'
        Write-Output '------------------'
        & node $runner --package
        if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed against the packaged output.' }
    } else {
        Write-Output ''
        Write-Output 'node not found: skipping the package verification step.'
    }
}
