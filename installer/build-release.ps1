# NovaDownload - full release build.
#
# One command reproduces every shipped artifact from a clean tree:
#
#   release/windows-installer/NovaDownload-1.0.0-win-x64-setup.exe
#   release/portable/NovaDownload-1.0.0-portable-win-x64.zip
#   release/chrome-extension/ , release/edge-extension/ , release/firefox-extension/
#   release/chrome-extension.zip , release/edge-extension.zip , release/firefox-extension.zip
#   release/checksums/SHA256SUMS.txt
#   release/BUILD-MANIFEST.txt
#
#   powershell -NoProfile -File installer\build-release.ps1
#   powershell -NoProfile -File installer\build-release.ps1 -SkipInstaller -SkipPortable
#
# The application and the native messaging host are published self-contained for win-x64 into a
# single staging folder, so one copy of the .NET runtime serves both executables and the installer
# has no prerequisite to satisfy.

param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Version = '1.0.0',
    [switch]$SkipExtensions,
    [switch]$SkipInstaller,
    [switch]$SkipPortable,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $installerRoot

$buildRoot = Join-Path $repo 'build'
$staging = Join-Path $buildRoot 'app'
$extensionsStaging = Join-Path $buildRoot 'extensions'
$releaseRoot = Join-Path $repo 'release'
$installerOutput = Join-Path $releaseRoot 'windows-installer'
$portableRoot = Join-Path $releaseRoot 'portable'
$checksumRoot = Join-Path $releaseRoot 'checksums'
$zipTimestamp = [DateTimeOffset]::Parse('2020-01-01T00:00:00Z')

$utf8 = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step([string]$text) {
    Write-Output ''
    Write-Output "== $text"
}

function Write-Detail([string]$text) {
    Write-Output "   $text"
}

function Assert-UnderRoot([string]$path, [string]$root, [string]$what) {
    $resolved = (Resolve-Path -LiteralPath $path).Path
    if ($resolved -notlike "$root*") { throw "Refusing to touch $what outside $root : $resolved" }
    return $resolved
}

function Remove-BuildDirectory([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $resolved = Assert-UnderRoot $path $buildRoot 'the staging tree'
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

# Deterministic archive: forward-slash entry names (the Web Store and most unzippers trip over
# backslashes) and a fixed timestamp, so two builds of the same sources produce identical bytes.
function New-DeterministicZip([string]$source, [string]$zipPath) {
    $parent = Split-Path -Parent $zipPath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName | ForEach-Object {
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

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ISCC.exe carries no version resource, so read the installed version like the Windows app list does.
function Get-InnoSetupVersion {
    foreach ($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        $entry = Get-ItemProperty -Path "$root\Inno Setup 6_is1" -ErrorAction SilentlyContinue
        if ($entry -and $entry.DisplayVersion) { return [string]$entry.DisplayVersion }
    }
    return 'unknown'
}

function Invoke-DotNet([string[]]$arguments, [string]$what) {
    Write-Detail "dotnet $($arguments -join ' ')"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

function Stop-StaleApp() {
    $running = @(Get-Process -Name 'NovaDownload.App' -ErrorAction SilentlyContinue)
    foreach ($process in $running) {
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if (-not $path) { continue }
        if (-not $path.StartsWith($repo, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        Write-Detail "stopping the running build under test (pid $($process.Id))"
        $process | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}

# --------------------------------------------------------------------------- toolchain

Write-Step 'Toolchain'
$dotnetVersion = (& dotnet --version).Trim()
Write-Detail "dotnet $dotnetVersion"
if ($LASTEXITCODE -ne 0) { throw 'dotnet is not available on PATH.' }

$nodeVersion = 'not available'
if (Get-Command node -ErrorAction SilentlyContinue) {
    $nodeVersion = (& node --version).Trim()
    Write-Detail "node $nodeVersion"
}

$isccCandidates = @()
if ($env:NOVADOWNLOAD_ISCC) { $isccCandidates += $env:NOVADOWNLOAD_ISCC }
$isccCandidates += @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $null
foreach ($candidate in $isccCandidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate)) { $iscc = $candidate; break }
}
$isccVersion = 'not available'
if ($iscc) {
    if (-not $SkipInstaller) {
        $isccVersion = Get-InnoSetupVersion
        Write-Detail "Inno Setup $isccVersion  ($iscc)"
    } else {
        Write-Detail 'Inno Setup found (installer build skipped)'
    }
} elseif (-not $SkipInstaller) {
    throw @'
Inno Setup 6 was not found. Install it (winget install -e --id JRSoftware.InnoSetup) or set
NOVADOWNLOAD_ISCC to the full path of ISCC.exe, then re-run. Use -SkipInstaller to build only the
application and extension packages.
'@
}

$gitRevision = 'not a git working tree'
if (Test-Path -LiteralPath (Join-Path $repo '.git')) {
    $gitRevision = (& git -C $repo rev-parse --short HEAD 2>$null)
    $dirty = (& git -C $repo status --porcelain 2>$null | Measure-Object).Count
    if ($dirty -gt 0) { $gitRevision = "$gitRevision (working tree has $dirty uncommitted change(s))" }
}
Write-Detail "git $gitRevision"

# --------------------------------------------------------------------------- staging

Write-Step 'Staging the application payload'
Stop-StaleApp
Remove-BuildDirectory $staging
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$publishCommon = @(
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    '-o', $staging,
    '--nologo'
)

Invoke-DotNet (@('publish', (Join-Path $repo 'src\NovaDownload.App\NovaDownload.App.csproj')) + $publishCommon) 'Publishing the application'

# Published into the same folder on purpose: both runtimeconfig.json files declare the frameworks
# as included, so the host resolves them from the shared directory instead of a second copy.
Invoke-DotNet (@('publish', (Join-Path $repo 'src\NovaDownload.NativeHost\NovaDownload.NativeHost.csproj')) + $publishCommon) 'Publishing the native messaging host'

$appExe = Join-Path $staging 'NovaDownload.App.exe'
$hostExe = Join-Path $staging 'NovaDownload.NativeHost.exe'
if (-not (Test-Path -LiteralPath $appExe)) { throw "Staging is missing $appExe" }
if (-not (Test-Path -LiteralPath $hostExe)) { throw "Staging is missing $hostExe" }

# Verify the shared-runtime layout really works before it is packaged.
Write-Step 'Verifying the staged host'
$identity = & $hostExe --identity --json 2>&1 | Out-String
$identity = $identity.Trim()
Write-Detail $identity
if ($LASTEXITCODE -ne 0) {
    throw "NovaDownload.NativeHost.exe --identity failed in the staged folder (exit $LASTEXITCODE). The shared-runtime layout is not usable; publish the host into its own self-contained folder instead."
}
$identityJson = $identity | ConvertFrom-Json
if (-not $identityJson.chromiumId) { throw 'The host did not report a chromiumId.' }

$stagedFiles = (Get-ChildItem -LiteralPath $staging -Recurse -File | Measure-Object -Property Length -Sum)
Write-Detail ("{0} files, {1:N1} MB" -f $stagedFiles.Count, ($stagedFiles.Sum / 1MB))

# --------------------------------------------------------------------------- extensions

if (-not $SkipExtensions) {
    Write-Step 'Building the browser extension packages'
    Remove-BuildDirectory $extensionsStaging
    New-Item -ItemType Directory -Path $extensionsStaging -Force | Out-Null

    $extensionScript = Join-Path $repo 'browser-extensions\build-extensions.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $extensionScript -OutputRoot $releaseRoot -SkipTests
    if ($LASTEXITCODE -ne 0) { throw 'The extension packaging script failed.' }

    foreach ($browser in @('chrome', 'edge', 'firefox')) {
        $from = Join-Path $releaseRoot "$browser-extension"
        if (-not (Test-Path -LiteralPath $from)) { throw "Missing extension package: $from" }
        Copy-Item -LiteralPath $from -Destination (Join-Path $extensionsStaging $browser) -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $releaseRoot "$browser-extension.zip") -Destination $extensionsStaging -Force
    }

    # The installer ships the loadable folders plus the store-shaped archives.
    Write-Detail ("staged {0} extension folders and archives" -f 6)
} else {
    Write-Step 'Building the browser extension packages (skipped)'
}

# --------------------------------------------------------------------------- portable

if (-not $SkipPortable) {
    Write-Step 'Building the portable archive'
    if (-not (Test-Path -LiteralPath $portableRoot)) { New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null }
    $portableZip = Join-Path $portableRoot "NovaDownload-$Version-portable-$RuntimeIdentifier.zip"
    New-DeterministicZip $staging $portableZip
    Write-Detail ("{0}  ({1:N1} MB)" -f $portableZip, ((Get-Item -LiteralPath $portableZip).Length / 1MB))
} else {
    Write-Step 'Building the portable archive (skipped)'
}

# --------------------------------------------------------------------------- installer

if (-not $SkipInstaller) {
    Write-Step 'Compiling the installer'
    if (-not (Test-Path -LiteralPath $installerOutput)) { New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null }

    $iss = Join-Path $installerRoot 'NovaDownload.iss'
    & $iscc "/DAppVersion=$Version" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }

    $setup = Get-ChildItem -LiteralPath $installerOutput -Filter '*.exe' -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $setup) { throw 'ISCC reported success but no setup executable was produced.' }
    Write-Detail ("{0}  ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
} else {
    Write-Step 'Compiling the installer (skipped)'
}

# --------------------------------------------------------------------------- checksums

Write-Step 'Checksums'
if (-not (Test-Path -LiteralPath $checksumRoot)) { New-Item -ItemType Directory -Path $checksumRoot -Force | Out-Null }

$artifacts = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -File |
    Where-Object { $_.Extension -in @('.exe', '.zip') -and $_.FullName -notlike "$checksumRoot*" } |
    Sort-Object FullName)

$lines = foreach ($artifact in $artifacts) {
    $relative = $artifact.FullName.Substring($releaseRoot.Length).TrimStart('\').Replace('\', '/')
    "{0}  {1}" -f (Get-Sha256 $artifact.FullName), $relative
}
[System.IO.File]::WriteAllText((Join-Path $checksumRoot 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), $utf8)
$lines | ForEach-Object { Write-Detail $_ }

# --------------------------------------------------------------------------- manifest

Write-Step 'Build manifest'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$pathWidth = 70
foreach ($artifact in $artifacts) {
    $relative = $artifact.FullName.Substring($releaseRoot.Length).TrimStart('\')
    if ($relative.Length -gt $pathWidth) { $pathWidth = $relative.Length }
}

$manifest = New-Object System.Collections.Generic.List[string]
$manifest.Add("NovaDownload release manifest")
$manifest.Add("version:            $Version")
$manifest.Add("configuration:      $Configuration")
$manifest.Add("runtime:            $RuntimeIdentifier (self-contained)")
$manifest.Add("built (UTC):        $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss', $invariant))")
$manifest.Add("git revision:       $gitRevision")
$manifest.Add("dotnet SDK:         $dotnetVersion")
$manifest.Add("node:               $nodeVersion")
$manifest.Add("Inno Setup:         $isccVersion")
$manifest.Add("installer AppId:    $([regex]::Match((Get-Content (Join-Path $installerRoot 'NovaDownload.iss') -Raw), '#define AppId ""\{\{([0-9A-Fa-f-]+)\}""').Groups[1].Value)")
$manifest.Add("chromium ext id:    $($identityJson.chromiumId)")
$manifest.Add("firefox ext id:     $($identityJson.firefoxId)")
$manifest.Add("native host name:   $($identityJson.host)")
$manifest.Add("")
$manifest.Add("Artifacts:")
$artifactFormat = "  {0,-$pathWidth}  {1,12}  {2}"
$manifest.Add(($artifactFormat -f 'path', 'bytes', 'sha256'))
foreach ($artifact in $artifacts) {
    $relative = $artifact.FullName.Substring($releaseRoot.Length).TrimStart('\')
    $line = $artifactFormat -f $relative, $artifact.Length.ToString('N0', $invariant), (Get-Sha256 $artifact.FullName)
    $manifest.Add($line)
}
$manifestText = ($manifest -join "`n") + "`n"
[System.IO.File]::WriteAllText((Join-Path $releaseRoot 'BUILD-MANIFEST.txt'), $manifestText, $utf8)
$manifest | ForEach-Object { Write-Detail $_ }

# --------------------------------------------------------------------------- tests

if (-not $SkipTests) {
    Write-Step 'Verifying the packages'
    $runner = Join-Path $repo 'tests\extension-tests\run-tests.js'
    if ((Test-Path -LiteralPath $runner) -and (Get-Command node -ErrorAction SilentlyContinue)) {
        & node $runner --package
        if ($LASTEXITCODE -ne 0) { throw 'The extension test suite failed against the packaged output.' }
    } else {
        Write-Detail 'node not found: skipped'
    }
}

Write-Output ''
Write-Output "Release build complete: $releaseRoot"
