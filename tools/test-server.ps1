param(
    [int]$Port = 8099,
    [int]$SizeMb = 32,
    [int]$ChunkDelayMs = 20
)

$file = Join-Path $env:TEMP "nova-test-$($SizeMb)mb.bin"
if (-not (Test-Path $file)) {
    $fs = [System.IO.File]::Create($file)
    $chunk = New-Object byte[] (1024 * 1024)
    (New-Object System.Random 1337).NextBytes($chunk)
    for ($i = 0; $i -lt $SizeMb; $i++) { $fs.Write($chunk, 0, $chunk.Length) }
    $fs.Dispose()
}

$total = (Get-Item $file).Length
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Output "serving $file ($([math]::Round($total / 1MB, 1)) MB) on http://127.0.0.1:$Port/test.bin"

while ($listener.IsListening) {
    try { $ctx = $listener.GetContext() } catch { break }
    $req = $ctx.Request
    $res = $ctx.Response

    $start = 0
    $end = $total - 1
    $range = $req.Headers["Range"]
    if ($range -and ($range -match "bytes=(\d+)-(\d*)")) {
        $start = [long]$matches[1]
        if ($matches[2] -ne "") { $end = [long]$matches[2] }
        $res.StatusCode = 206
        $res.AddHeader("Content-Range", "bytes $start-$end/$total")
    }

    $count = $end - $start + 1
    $res.AddHeader("Accept-Ranges", "bytes")
    $res.AddHeader("ETag", "`"nova-fixture-v1`"")
    $res.AddHeader("Cache-Control", "no-store")
    $res.ContentType = "application/octet-stream"
    $res.AddHeader("Content-Disposition", "attachment; filename=nova-test-$($SizeMb)mb.bin")
    $res.ContentLength64 = $count

    $fs = [System.IO.File]::OpenRead($file)
    $fs.Position = $start
    $buf = New-Object byte[] 65536
    $out = $res.OutputStream
    $remaining = $count
    try {
        while ($remaining -gt 0) {
            $toRead = [Math]::Min($buf.Length, $remaining)
            $read = $fs.Read($buf, 0, $toRead)
            if ($read -le 0) { break }
            $out.Write($buf, 0, $read)
            $out.Flush()
            $remaining -= $read
            if ($ChunkDelayMs -gt 0) { Start-Sleep -Milliseconds $ChunkDelayMs }
        }
    } catch { }
    finally {
        $fs.Dispose()
        try { $out.Close(); $res.Close() } catch { }
    }
}
