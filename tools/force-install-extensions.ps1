# NovaDownload — force-install browser extensions.
#
# What it does:
#   1. builds/uses the existing Chromium and Firefox extension packages
#   2. signs a local CRX3 package for Chromium (Chrome/Edge)
#   3. writes Chrome/Edge ExtensionInstallForcelist + ExtensionSettings policies
#   4. writes Firefox Extensions Install/Locked enterprise policies
#   5. rewrites the Chromium native-messaging allow-list to match the signed CRX id
#
# Security notes:
#   - The private key is generated and kept in %LOCALAPPDATA%\NovaDownload\force-install.
#   - The key and generated .crx/.xpi are local artifacts and should not be committed.
#   - Firefox policy installation of an unsigned .xpi requires a Firefox distribution channel
#     that permits unsigned add-ons; ESR or a signed AMO package may be required.
#
# Examples:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\force-install-extensions.ps1 -Browser Edge
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\force-install-extensions.ps1 -Browser All

param(
    [ValidateSet('Chrome', 'Edge', 'Firefox', 'All')]
    [string[]]$Browser = @('Edge'),

    [string]$WorkingRoot = (Join-Path $env:LOCALAPPDATA 'NovaDownload\force-install'),

    [switch]$SkipBuild,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repo 'release'
$packer = Join-Path $PSScriptRoot 'force-install\pack-crx.mjs'

if (-not (Test-Path -LiteralPath $packer)) {
    throw "CRX packer not found: $packer"
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js is required by tools/force-install/pack-crx.mjs'
}

function Ensure-ReleasePackages {
    $needed = @(
        (Join-Path $releaseRoot 'chrome-extension'),
        (Join-Path $releaseRoot 'edge-extension'),
        (Join-Path $releaseRoot 'firefox-extension')
    )
    if ($SkipBuild -and ($needed | ForEach-Object { Test-Path -LiteralPath $_ }) -notcontains $false) {
        return
    }

    $build = Join-Path $repo 'browser-extensions\build-extensions.ps1'
    if (-not (Test-Path -LiteralPath $build)) {
        throw "Extension build script not found: $build"
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File $build -SkipTests
    if ($LASTEXITCODE -ne 0) {
        throw 'Building extension packages failed.'
    }
}

function Copy-FolderContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring((Resolve-Path $Source).Path.Length).TrimStart('\')
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$ZipPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    $timestamp = [DateTimeOffset]::Parse('2020-01-01T00:00:00Z')
    try {
        Get-ChildItem -LiteralPath $Source -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                $relative = $_.FullName.Substring((Resolve-Path $Source).Path.Length).TrimStart('\').Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $input = [System.IO.File]::OpenRead($_.FullName)
                try {
                    $output = $entry.Open()
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                } finally {
                    $input.Dispose()
                }
            }
    } finally {
        $archive.Dispose()
    }
}

function Invoke-CrxPacker {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$KeyPath,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $raw = & node $packer --zip $ZipPath --key $KeyPath --out $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "CRX packing failed for $ZipPath"
    }
    return $raw | ConvertFrom-Json
}

function Set-ChromiumManifestKey {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$PublicKeyBase64
    )

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $manifest.key = $PublicKeyBase64
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
}

function Get-FileUri {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    return ([System.Uri]::new($resolved)).AbsoluteUri
}

function Set-PolicyValue {
    param(
        [Parameter(Mandatory)][string]$KeyPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value,
        [ValidateSet('String', 'MultiString', 'DWord')]
        [string]$Type = 'String'
    )

    if ($WhatIf) {
        Write-Output "WHAT IF: $KeyPath\$Name = $Value"
        return
    }

    try {
        if (-not (Test-Path -LiteralPath $KeyPath)) {
            New-Item -Path $KeyPath -Force | Out-Null
        }
        if ($Type -eq 'MultiString') {
            New-ItemProperty -Path $KeyPath -Name $Name -Value @($Value) -PropertyType MultiString -Force | Out-Null
        } elseif ($Type -eq 'DWord') {
            New-ItemProperty -Path $KeyPath -Name $Name -Value ([int]$Value) -PropertyType DWord -Force | Out-Null
        } else {
            New-ItemProperty -Path $KeyPath -Name $Name -Value $Value -PropertyType String -Force | Out-Null
        }
    } catch [System.UnauthorizedAccessException] {
        throw "Access denied writing '$KeyPath'. Re-run this script from an elevated PowerShell window: Start-Process PowerShell -Verb RunAs; then run tools\force-install-extensions.ps1."
    }
}

function Set-ChromiumPolicies {
    param(
        [Parameter(Mandatory)][ValidateSet('Chrome', 'Edge')][string]$Name,
        [Parameter(Mandatory)][string]$ExtensionId,
        [Parameter(Mandatory)][string]$UpdateUrl
    )

    $policyRoot = if ($Name -eq 'Chrome') {
        'HKCU:\Software\Policies\Google\Chrome'
    } else {
        'HKCU:\Software\Policies\Microsoft\Edge'
    }

    $forceList = Join-Path $policyRoot 'ExtensionInstallForcelist'
    $allowList = Join-Path $policyRoot 'ExtensionInstallAllowlist'
    $settingsKey = Join-Path $policyRoot 'ExtensionSettings'

    Set-PolicyValue -KeyPath $forceList -Name '1' -Value "$ExtensionId;$UpdateUrl"
    Set-PolicyValue -KeyPath $allowList -Name '1' -Value $ExtensionId

    $settings = @{
        $ExtensionId = [ordered]@{
            installation_mode   = 'force_installed'
            update_url          = $UpdateUrl
            override_update_url = $true
            toolbar_state       = 'force_shown'
        }
    }
    Set-PolicyValue -KeyPath $settingsKey -Name 'ExtensionSettings' -Value ($settings | ConvertTo-Json -Compress -Depth 10)
}

function Set-FirefoxPolicies {
    param(
        [Parameter(Mandatory)][string]$XpiPath,
        [Parameter(Mandatory)][string]$ExtensionId
    )

    $extensionsRoot = 'HKCU:\Software\Policies\Mozilla\Firefox\Extensions'
    $installKey = Join-Path $extensionsRoot 'Install'
    $lockedKey = Join-Path $extensionsRoot 'Locked'

    $uri = Get-FileUri -Path $XpiPath
    Set-PolicyValue -KeyPath $installKey -Name '1' -Value $uri -Type MultiString
    Set-PolicyValue -KeyPath $lockedKey -Name '1' -Value $ExtensionId -Type MultiString
}

function Update-ChromiumNativeAllowList {
    param(
        [Parameter(Mandatory)][string]$ExtensionId
    )

    $hostExeCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\NovaDownload\NovaDownload.NativeHost.exe'),
        (Join-Path $env:ProgramFiles 'NovaDownload\NovaDownload.NativeHost.exe'),
        (Join-Path $repo "src\NovaDownload.NativeHost\bin\Release\net9.0-windows\win-x64\NovaDownload.NativeHost.exe")
    )
    $hostExe = $hostExeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $hostExe) {
        throw 'NovaDownload.NativeHost.exe was not found. Build the app before forcing extension install.'
    }

    & $hostExe --register | Out-Null

    $manifestPath = Join-Path $env:LOCALAPPDATA 'NovaDownload\NativeMessaging\com.novadownload.host.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Chromium native messaging manifest not found: $manifestPath"
    }

    if (-not $WhatIf) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifest.allowed_origins = @("chrome-extension://$ExtensionId/")
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    }

    Write-Output "Native messaging allow-list -> chrome-extension://$ExtensionId/"
}

Ensure-ReleasePackages

New-Item -ItemType Directory -Path $WorkingRoot -Force | Out-Null

$summary = New-Object System.Collections.Generic.List[object]

if ($Browser -contains 'Chrome' -or $Browser -contains 'Edge' -or $Browser -contains 'All') {
    $keyPath = Join-Path $WorkingRoot 'chromium-key.pem'
    if (-not (Test-Path -LiteralPath $keyPath)) {
        if ($WhatIf) {
            Write-Output "WHAT IF: create signing key at $keyPath"
        } else {
            & node -e "const fs=require('fs');const c=require('crypto');if(!fs.existsSync(process.argv[1])){const {privateKey}=c.generateKeyPairSync('rsa',{modulusLength:2048,privateKeyEncoding:{type:'pkcs8',format:'pem'},publicKeyEncoding:{type:'spki',format:'pem'}});fs.writeFileSync(process.argv[1],privateKey);}" $keyPath
            if ($LASTEXITCODE -ne 0) { throw 'Unable to generate Chromium signing key.' }
        }
    }

    $packageRoot = Join-Path $WorkingRoot 'chromium-extension'
    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }
    Copy-FolderContents -Source (Join-Path $releaseRoot 'chrome-extension') -Destination $packageRoot

    $unsignedZip = Join-Path $WorkingRoot 'chromium-unsigned.zip'
    New-DeterministicZip -Source $packageRoot -ZipPath $unsignedZip

    $probeCrx = Join-Path $WorkingRoot 'chromium-probe.crx'
    $probe = Invoke-CrxPacker -ZipPath $unsignedZip -KeyPath $keyPath -OutputPath $probeCrx

    Set-ChromiumManifestKey -ManifestPath (Join-Path $packageRoot 'manifest.json') -PublicKeyBase64 $probe.publicKeyBase64

    $signedZip = Join-Path $WorkingRoot 'chromium.zip'
    New-DeterministicZip -Source $packageRoot -ZipPath $signedZip

    $crxPath = Join-Path $WorkingRoot 'novadownload-chromium.crx'
    $crxInfo = Invoke-CrxPacker -ZipPath $signedZip -KeyPath $keyPath -OutputPath $crxPath

    $extensionId = $crxInfo.chromiumExtensionId
    $updateXml = Join-Path $WorkingRoot 'novadownload-chromium-update.xml'
    if (-not $WhatIf) {
        $xml = @"
<?xml version='1.0' encoding='UTF-8'?>
<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
  <app appid='$extensionId'>
    <updatecheck codebase='$(Get-FileUri -Path $crxPath)' version='1.0.0' />
  </app>
</gupdate>
"@
        Set-Content -LiteralPath $updateXml -Value $xml -Encoding UTF8
    }
    $updateUrl = Get-FileUri -Path $updateXml

    # Policies must be written first: the native messaging manifest is switched to the new CRX id
    # only after the browser policy can actually install that CRX.
    $chromiumBrowsers = if ($Browser -contains 'All') { @('Chrome', 'Edge') } else { $Browser | Where-Object { $_ -in @('Chrome', 'Edge') } }
    foreach ($name in $chromiumBrowsers) {
        Set-ChromiumPolicies -Name $name -ExtensionId $extensionId -UpdateUrl $updateUrl
        $summary.Add([pscustomobject]@{
            Browser     = $name
            Action      = 'Force install via policy'
            ExtensionId = $extensionId
            UpdateUrl   = $updateUrl
            CrxPath     = $crxPath
        })
    }

    Update-ChromiumNativeAllowList -ExtensionId $extensionId
}

if ($Browser -contains 'Firefox' -or $Browser -contains 'All') {
    $xpiPath = Join-Path $WorkingRoot 'novadownload-firefox.xpi'
    if (-not $WhatIf) {
        Copy-Item -LiteralPath (Join-Path $releaseRoot 'firefox-extension.zip') -Destination $xpiPath -Force
    }

    $firefoxId = 'novadownload@vanta.local'
    Set-FirefoxPolicies -XpiPath $xpiPath -ExtensionId $firefoxId
    $summary.Add([pscustomobject]@{
        Browser     = 'Firefox'
        Action      = 'Install + Lock via policy'
        ExtensionId = $firefoxId
        UpdateUrl   = (Get-FileUri -Path $xpiPath)
        CrxPath     = $xpiPath
    })
}

if ($summary.Count -eq 0) {
    throw 'No supported browser was selected.'
}

Write-Output 'NovaDownload forced extension installation'
Write-Output '------------------------------------------'
$summary | Format-List

Write-Output 'Restart the target browser so the policy is read. Verify:'
foreach ($row in $summary) {
    Write-Output "  $($row.Browser): $($row.ExtensionId)"
}
Write-Output '  Edge: edge://extensions'
Write-Output '  Chrome: chrome://extensions'
Write-Output '  Firefox: about:addons'
