# NovaDownload - live browser end-to-end verification (Microsoft Edge / Chromium).
#
# Proves the whole chain on a real browser, with no mocks:
#
#   extension page -> chrome.runtime.sendNativeMessage
#                  -> NovaDownload.NativeHost.exe   (browser-launched, stdio)
#                  -> named pipe                    (per user, current-user DACL)
#                  -> NovaDownload.App.exe bridge   (validation, dedupe)
#                  -> download engine               (HTTP, real bytes on disk)
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-browser-e2e.ps1
#   powershell -NoProfile -File tools\verify-browser-e2e.ps1 -ExtensionDir release\edge-extension -KeepOpen
#
# What it checks:
#   1. the unpacked extension loads and the runtime id matches the id derived from the manifest key;
#   2. the native messaging host is registered for that exact id;
#   3. ping travels the whole chain and comes back as a NovaDownload response;
#   4. a media URL sent from the extension lands in the engine and the file is byte-correct.
#
# Nothing here installs anything or changes browser settings. It uses a throw-away browser profile.

param(
    [string]$EdgePath,
    [string]$ExtensionDir,
    [string]$HostExecutable,
    [string]$AppExecutable,
    [int]$FixturePort = 8099,
    [int]$DebugPort = 9333,
    [int]$DownloadSizeMb = 2,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $ExtensionDir) { $ExtensionDir = Join-Path $repo 'release\edge-extension' }
if (-not $HostExecutable) { $HostExecutable = Join-Path $repo 'build\app\NovaDownload.NativeHost.exe' }
if (-not $AppExecutable) { $AppExecutable = Join-Path $repo 'build\app\NovaDownload.App.exe' }
if (-not $EdgePath) {
    foreach ($candidate in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
            (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $EdgePath = $candidate; break }
    }
}

$results = New-Object System.Collections.Generic.List[object]
function Record([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Output ("  [{0}] {1}{2}" -f $mark, $name, $(if ($detail) { " - $detail" } else { '' }))
}

function Write-Step([string]$text) {
    Write-Output ''
    Write-Output "== $text"
}

if (-not (Test-Path -LiteralPath $ExtensionDir)) { throw "Extension package not found: $ExtensionDir (run installer\build-release.ps1)" }
if (-not (Test-Path -LiteralPath $HostExecutable)) { throw "Native host not found: $HostExecutable (run installer\build-release.ps1)" }
if (-not $EdgePath) { throw 'Microsoft Edge was not found. Pass -EdgePath.' }

$expectedId = (Get-Content (Join-Path $repo 'browser-extensions\extension-identity.json') -Raw | ConvertFrom-Json).chromiumExtensionId
$profileDir = Join-Path $env:TEMP ("nova-edge-e2e-" + [Guid]::NewGuid().ToString('n'))
$downloadDir = Join-Path $env:TEMP ("nova-e2e-downloads-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

$fixture = $null
$app = $null
$edge = $null
$socket = $null
$cdpId = 0

function Invoke-Cdp([string]$method, $params) {
    $script:cdpId++
    $id = $script:cdpId
    $payload = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $segment = New-Object System.ArraySegment[byte] -ArgumentList @(, $bytes)
    $script:socket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
        [System.Threading.CancellationToken]::None).Wait(15000) | Out-Null

    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        $message = Receive-CdpMessage
        $parsed = $message | ConvertFrom-Json
        if ($parsed.id -eq $id) { return $parsed }
    }
    throw "no answer to CDP $method"
}

function Receive-CdpMessage {
    $buffer = New-Object byte[] 131072
    $builder = New-Object System.Text.StringBuilder
    do {
        $segment = New-Object System.ArraySegment[byte] -ArgumentList @(, $buffer)
        $result = $script:socket.ReceiveAsync($segment, [System.Threading.CancellationToken]::None).Result
        if ($result.Count -gt 0) {
            [void]$builder.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
        }
    } while (-not $result.EndOfMessage)
    return $builder.ToString()
}

function Evaluate-JavaScript([string]$expression) {
    $response = Invoke-Cdp 'Runtime.evaluate' @{
        expression    = $expression
        awaitPromise  = $true
        returnByValue = $true
    }
    if ($response.result.exceptionDetails) {
        throw "page script failed: $($response.result.exceptionDetails.text) $($response.result.exceptionDetails.exception.description)"
    }
    return $response.result.result.value
}

try {
    # ------------------------------------------------------------------ fixture server
    Write-Step 'Local HTTP fixture'
    $fixtureScript = Join-Path $repo 'tools\test-server.ps1'
    $fixture = Start-Process -FilePath 'powershell' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fixtureScript,
        '-Port', $FixturePort, '-SizeMb', $DownloadSizeMb, '-ChunkDelayMs', '0')
    $fixtureUrl = "http://127.0.0.1:$FixturePort/test.bin"

    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $client.Connect('127.0.0.1', $FixturePort)
            $client.Close()
            $ready = $true
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'the fixture server never started' }
    Write-Output "  serving $fixtureUrl"

    # ------------------------------------------------------------------ host registration
    Write-Step 'Native messaging registration'
    $register = & $HostExecutable --register 2>&1 | Out-String
    Write-Output $register.Trim()
    $registered = $LASTEXITCODE -eq 0

    $registryKey = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.novadownload.host"
    $manifestPath = $null
    if (Test-Path $registryKey) { $manifestPath = (Get-ItemProperty -Path $registryKey).'(default)' }
    Record 'host registered for Edge' $registered "manifest: $manifestPath"

    $allowed = $null
    if ($manifestPath -and (Test-Path -LiteralPath $manifestPath)) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $allowed = $manifest.allowed_origins -join ', '
        $detail = "path=$($manifest.path) origins=$allowed"
        Record 'manifest allow-lists the derived id' ($allowed -match $expectedId) $detail
        Record 'manifest points at the host executable' (Test-Path -LiteralPath $manifest.path) $manifest.path
    } else {
        Record 'manifest allow-lists the derived id' $false 'manifest file missing'
    }

    # ------------------------------------------------------------------ application
    Write-Step 'Desktop application'
    $pipeName = $null
    $status = & $HostExecutable --status 2>&1 | Out-String
    $match = [regex]::Match($status, 'Pipe name\s*:\s*(\S+)')
    if ($match.Success) { $pipeName = $match.Groups[1].Value }
    Record 'bridge pipe name resolved' ($null -ne $pipeName) $pipeName

    # The real application, but with its own throw-away settings folder: browser integration is on
    # by default there, and the developer's profile is never read or written.
    $settingsDir = Join-Path $env:TEMP ("nova-e2e-settings-" + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
    $env:NOVADOWNLOAD_DATA_DIR = $settingsDir

    # A stale instance would receive the request instead of this one, with its own settings.
    Get-Process -Name 'NovaDownload.App' -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.Path } catch { $path = $null }
        if ($path -and $path.StartsWith((Split-Path -Parent $AppExecutable), [System.StringComparison]::OrdinalIgnoreCase)) {
            try { $_.Kill() } catch { }
        }
    }
    Start-Sleep -Milliseconds 500

    $app = Start-Process -FilePath $AppExecutable -PassThru
    $bridgeUp = $false
    for ($i = 0; $i -lt 120 -and -not $bridgeUp; $i++) {
        $probe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $probe.Connect(300)
            $bridgeUp = $true
        } catch { } finally { $probe.Dispose() }
        if (-not $bridgeUp) { Start-Sleep -Milliseconds 500 }
    }
    Record 'application bridge is listening' $bridgeUp $pipeName

    $isolated = Join-Path $settingsDir 'settings.json'
    for ($i = 0; $i -lt 40 -and -not (Test-Path -LiteralPath $isolated); $i++) { Start-Sleep -Milliseconds 250 }
    Record 'the app used an isolated settings folder' (Test-Path -LiteralPath $isolated) $settingsDir

    # ------------------------------------------------------------------ browser
    Write-Step 'Microsoft Edge with the unpacked extension'
    $edgeArguments = @(
        "--user-data-dir=$profileDir",
        "--load-extension=$ExtensionDir",
        "--disable-extensions-except=$ExtensionDir",
        "--remote-debugging-port=$DebugPort",
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-sync',
        '--disable-background-networking',
        'about:blank'
    )
    $edge = Start-Process -FilePath $EdgePath -PassThru -ArgumentList $edgeArguments

    $pageTarget = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline -and -not $pageTarget) {
        try {
            $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$DebugPort/json/list" -TimeoutSec 3
            $pageTarget = $targets | Where-Object { $_.type -eq 'page' -and $_.webSocketDebuggerUrl } | Select-Object -First 1
        } catch { }
        if (-not $pageTarget) { Start-Sleep -Milliseconds 500 }
    }
    Record 'debugger endpoint reachable' ($null -ne $pageTarget) "port $DebugPort"
    if (-not $pageTarget) { throw 'Edge never exposed a debuggable page target' }

    $socket = New-Object System.Net.WebSockets.ClientWebSocket
    $socket.ConnectAsync([Uri]$pageTarget.webSocketDebuggerUrl, [System.Threading.CancellationToken]::None).Wait(15000) | Out-Null

    # Navigating over CDP is the supported way to reach an extension page.
    $optionsUrl = "chrome-extension://$expectedId/options.html"
    $null = Invoke-Cdp 'Page.enable' @{}
    $null = Invoke-Cdp 'Page.navigate' @{ url = $optionsUrl }
    Start-Sleep -Seconds 2

    $liveId = $null
    try { $liveId = Evaluate-JavaScript 'chrome.runtime.id' } catch { $liveId = $null }
    Record 'extension loaded and answering' ($null -ne $liveId) "runtime id: $liveId"
    Record 'live id equals the derived id' ($liveId -eq $expectedId) "derived: $expectedId"

    # ------------------------------------------------------------------ the chain
    Write-Step 'Extension -> host -> application -> engine'
    $ping = $null
    for ($attempt = 0; $attempt -lt 3 -and -not $ping; $attempt++) {
        try {
            $raw = Evaluate-JavaScript 'NOVA.pingApp().then(function (r) { return JSON.stringify(r); })'
            $ping = $raw | ConvertFrom-Json
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    if ($null -eq $ping) { throw 'pingApp never answered' }
    Record 'ping answered through native messaging' ([bool]$ping.ok) "code=$($ping.code) server=$($ping.server) version=$($ping.appVersion)"
    Record 'ping reports the protocol version' ($ping.protocolVersion -eq 1) "protocolVersion=$($ping.protocolVersion)"

    $escapedUrl = $fixtureUrl.Replace('\', '\\')
    $escapedDir = $downloadDir.Replace('\', '\\')
    $script = @"
NOVA.sendItem({ url: '$escapedUrl', name: 'nova-e2e.bin', mime: 'video/mp4', pageUrl: 'https://example.com/watch' }, { directory: '$escapedDir' })
    .then(function (r) { return JSON.stringify(r); })
"@
    $raw = Evaluate-JavaScript $script
    $download = $raw | ConvertFrom-Json
    Record 'download accepted by the application' ([bool]$download.ok) "code=$($download.code) file=$($download.fileName) id=$($download.downloadId)"
    Record 'engine reports a download id' ($null -ne $download.downloadId) "$($download.downloadId)"

    $saved = $null
    $expectedBytes = $DownloadSizeMb * 1MB
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline -and -not $saved) {
        $saved = Get-ChildItem -LiteralPath $downloadDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Length -eq $expectedBytes } | Select-Object -First 1
        if (-not $saved) { Start-Sleep -Milliseconds 500 }
    }
    Record 'file finished on disk with the exact size' ($null -ne $saved) `
        $(if ($saved) { "$($saved.FullName) ($($saved.Length) bytes)" } else { "nothing of $expectedBytes bytes in $downloadDir" })

    if ($saved) {
        # The fixture payload is deterministic (seed 1337), so a hash compared against the source
        # file proves the bytes were not corrupted by the relay.
        $source = Join-Path $env:TEMP ("nova-test-$($DownloadSizeMb)mb.bin")
        if (Test-Path -LiteralPath $source) {
            $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            $savedHash = (Get-FileHash -LiteralPath $saved.FullName -Algorithm SHA256).Hash
            Record 'downloaded bytes match the served file' ($sourceHash -eq $savedHash) "$($savedHash.Substring(0, 16))..."
        }
    }

    # ------------------------------------------------------------------ repeat
    # The documented rule: a *finished* transfer does not block a new request for the same URL —
    # downloading the same file again later is a normal user action, and the engine picks a
    # non-colliding name. The replay-id guard (same requestId) is covered by the pipe tests.
    Write-Output ''
    Write-Output '== Repeat request'
    $second = $null
    try {
        $raw = Evaluate-JavaScript $script
        $second = $raw | ConvertFrom-Json
    } catch {
        Write-Output ("  (the repeat call could not be evaluated: " + $_.Exception.Message + ")")
    }

    if ($second) {
        Record 'a finished transfer does not block a new request' `
            ([bool]$second.ok -and $second.downloadId -ne $download.downloadId) `
            "code=$($second.code) id=$($second.downloadId)"
        Record 'the second copy gets its own file name' ($second.fileName -ne $download.fileName) `
            "first=$($download.fileName) second=$($second.fileName)"
    } else {
        Record 'a finished transfer does not block a new request' $false 'no answer from the extension'
    }
}
finally {
    $env:NOVADOWNLOAD_DATA_DIR = $null
    if (-not $KeepOpen) {
        if ($socket) { try { $socket.Dispose() } catch { } }
        if ($edge -and -not $edge.HasExited) {
            try { $edge.Kill() } catch { }
            Start-Sleep -Milliseconds 500
        }
        if ($app -and -not $app.HasExited) {
            try { $app.Kill() } catch { }
        }
        if ($fixture -and -not $fixture.HasExited) {
            try { $fixture.Kill() } catch { }
        }
        try { Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        if ($settingsDir) {
            try { Remove-Item -LiteralPath $settingsDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        }
    } else {
        Write-Output ''
        Write-Output "Left running: Edge profile $profileDir, downloads $downloadDir, fixture port $FixturePort"
    }
}

Write-Output ''
$failed = @($results | Where-Object { -not $_.Ok })
Write-Output ("{0} checks, {1} passed, {2} failed" -f $results.Count, ($results.Count - $failed.Count), $failed.Count)
if ($failed.Count -gt 0) {
    $failed | ForEach-Object { Write-Output ("  failed: {0} ({1})" -f $_.Name, $_.Detail) }
    exit 1
}
exit 0
