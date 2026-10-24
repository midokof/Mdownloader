using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaDownload.IntegrationTests;

/// <summary>
/// Minimal HTTP/1.1 fixture server over a raw socket: no URL ACL, no admin rights,
/// deterministic throttling, and it records the Range headers it received.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _payload;
    private readonly int _chunkDelayMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<string> _requests = new();
    private readonly List<string> _ranges = new();

    public string ETag { get; set; } = "\"fixture-v1\"";
    public string FileName { get; set; } = "fixture.bin";
    public bool SupportsRange { get; set; } = true;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string Url => $"{BaseUrl}/{FileName}";

    public IReadOnlyList<string> RangeHeaders
    {
        get { lock (_ranges) return _ranges.ToArray(); }
    }

    public int RequestCount
    {
        get { lock (_requests) return _requests.Count; }
    }

    public TestHttpServer(byte[] payload, int chunkDelayMs = 0)
    {
        _payload = payload;
        _chunkDelayMs = chunkDelayMs;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream).ConfigureAwait(false);
                if (head == null) return;

                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                lock (_requests) _requests.Add(lines[0]);

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }

                var start = 0L;
                var end = (long)_payload.Length - 1;
                var partial = false;

                if (SupportsRange && headers.TryGetValue("Range", out var range))
                {
                    lock (_ranges) _ranges.Add(range);

                    var match = Regex.Match(range, @"bytes=(\d+)-(\d*)");
                    if (match.Success)
                    {
                        start = long.Parse(match.Groups[1].Value);
                        if (match.Groups[2].Value.Length > 0) end = long.Parse(match.Groups[2].Value);
                        end = Math.Min(end, _payload.Length - 1);
                        partial = true;
                    }
                }

                var count = end - start + 1;
                if (count <= 0)
                {
                    var notFound = Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(notFound).ConfigureAwait(false);
                    return;
                }

                var sb = new StringBuilder();
                sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: application/octet-stream\r\n");
                sb.Append($"Content-Length: {count}\r\n");
                if (SupportsRange) sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append($"ETag: {ETag}\r\n");
                sb.Append($"Content-Disposition: attachment; filename={FileName}\r\n");
                if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{_payload.Length}\r\n");
                sb.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);

                var buffer = new byte[65536];
                var offset = start;
                while (offset <= end)
                {
                    var size = (int)Math.Min(buffer.Length, end - offset + 1);
                    Buffer.BlockCopy(_payload, (int)offset, buffer, 0, size);
                    await stream.WriteAsync(buffer.AsMemory(0, size), _cts.Token).ConfigureAwait(false);
                    offset += size;
                    if (_chunkDelayMs > 0) await Task.Delay(_chunkDelayMs, _cts.Token).ConfigureAwait(false);
                }

                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[16384];
        var used = 0;

        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used)).ConfigureAwait(false);
            if (read == 0) return null;
            used += read;

            var text = Encoding.ASCII.GetString(buffer, 0, used);
            if (text.Contains("\r\n\r\n")) return text;
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
