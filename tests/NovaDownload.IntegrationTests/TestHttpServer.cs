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
    private readonly int _chunkDelayMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<string> _requests = new();
    private readonly List<string> _ranges = new();
    private readonly List<string> _ifRanges = new();
    private readonly List<string> _authorizations = new();
    private readonly object _payloadGate = new();

    private byte[] _payload;
    private int _failuresLeft;
    private int _active;
    private int _maxActive;

    public string ETag { get; set; } = "\"fixture-v1\"";
    public string FileName { get; set; } = "fixture.bin";
    public bool SupportsRange { get; set; } = true;

    /// <summary>Answers with <c>Transfer-Encoding: chunked</c> and no Content-Length.</summary>
    public bool UseChunked { get; set; }

    /// <summary>When false the response carries no Content-Disposition at all.</summary>
    public bool SendContentDisposition { get; set; } = true;

    /// <summary>When set, every request except <c>/redirected</c> is answered with a 302 pointing here.</summary>
    public string? RedirectTo { get; set; }

    /// <summary>Number of requests answered with 503 before the server starts behaving.</summary>
    public int FailuresBeforeSuccess
    {
        get => Volatile.Read(ref _failuresLeft);
        set => Volatile.Write(ref _failuresLeft, value);
    }

    /// <summary>Highest number of requests handled at the same time: proof that ranges ran in parallel.</summary>
    public int MaxConcurrentRequests => Volatile.Read(ref _maxActive);

    public byte[] Payload
    {
        get { lock (_payloadGate) return _payload; }
        set { lock (_payloadGate) _payload = value; }
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string Url => $"{BaseUrl}/{FileName}";

    public IReadOnlyList<string> RangeHeaders
    {
        get { lock (_ranges) return _ranges.ToArray(); }
    }

    public IReadOnlyList<string> IfRangeHeaders
    {
        get { lock (_ifRanges) return _ifRanges.ToArray(); }
    }

    public IReadOnlyList<string> AuthorizationHeaders
    {
        get { lock (_authorizations) return _authorizations.ToArray(); }
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
        var active = Interlocked.Increment(ref _active);
        InterlockedMax(ref _maxActive, active);

        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream).ConfigureAwait(false);
                if (head == null) return;

                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                lock (_requests) _requests.Add(lines[0]);

                var target = lines[0].Split(' ') is [_, var path, ..] ? path : "/";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }

                if (headers.TryGetValue("Authorization", out var authorization))
                    lock (_authorizations) _authorizations.Add(authorization);

                if (FailuresBeforeSuccess > 0)
                {
                    Interlocked.Decrement(ref _failuresLeft);
                    await WriteAsync(stream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                        .ConfigureAwait(false);
                    return;
                }

                if (RedirectTo != null && !target.StartsWith("/redirected", StringComparison.Ordinal))
                {
                    await WriteAsync(stream,
                        $"HTTP/1.1 302 Found\r\nLocation: {RedirectTo}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                        .ConfigureAwait(false);
                    return;
                }

                byte[] payload;
                lock (_payloadGate) payload = _payload;

                var start = 0L;
                var end = (long)payload.Length - 1;
                var partial = false;

                if (SupportsRange && headers.TryGetValue("Range", out var range))
                {
                    lock (_ranges) _ranges.Add(range);

                    var match = Regex.Match(range, @"bytes=(\d+)-(\d*)");
                    if (match.Success)
                    {
                        start = long.Parse(match.Groups[1].Value);
                        if (match.Groups[2].Value.Length > 0) end = long.Parse(match.Groups[2].Value);
                        end = Math.Min(end, payload.Length - 1);
                        partial = true;
                    }
                }

                if (headers.TryGetValue("If-Range", out var ifRange))
                    lock (_ifRanges) _ifRanges.Add(ifRange);

                var count = end - start + 1;
                if (count <= 0)
                {
                    await WriteAsync(stream,
                            "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                        .ConfigureAwait(false);
                    return;
                }

                var sb = new StringBuilder();
                sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: application/octet-stream\r\n");
                if (!UseChunked) sb.Append($"Content-Length: {count}\r\n");
                else sb.Append("Transfer-Encoding: chunked\r\n");
                if (SupportsRange) sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append($"ETag: {ETag}\r\n");
                if (SendContentDisposition) sb.Append($"Content-Disposition: attachment; filename={FileName}\r\n");
                if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{payload.Length}\r\n");
                sb.Append("Connection: close\r\n\r\n");

                await WriteAsync(stream, sb.ToString()).ConfigureAwait(false);

                if (UseChunked)
                {
                    var chunk = new byte[16384];
                    var offset = start;
                    while (offset <= end)
                    {
                        var size = (int)Math.Min(chunk.Length, end - offset + 1);
                        Buffer.BlockCopy(payload, (int)offset, chunk, 0, size);

                        await WriteAsync(stream, $"{size:x}\r\n").ConfigureAwait(false);
                        await stream.WriteAsync(chunk.AsMemory(0, size), _cts.Token).ConfigureAwait(false);
                        await WriteAsync(stream, "\r\n").ConfigureAwait(false);

                        offset += size;
                        if (_chunkDelayMs > 0) await Task.Delay(_chunkDelayMs, _cts.Token).ConfigureAwait(false);
                    }

                    await WriteAsync(stream, "0\r\n\r\n").ConfigureAwait(false);
                }
                else
                {
                    var buffer = new byte[65536];
                    var offset = start;
                    while (offset <= end)
                    {
                        var size = (int)Math.Min(buffer.Length, end - offset + 1);
                        Buffer.BlockCopy(payload, (int)offset, buffer, 0, size);
                        await stream.WriteAsync(buffer.AsMemory(0, size), _cts.Token).ConfigureAwait(false);
                        offset += size;
                        if (_chunkDelayMs > 0) await Task.Delay(_chunkDelayMs, _cts.Token).ConfigureAwait(false);
                    }
                }

                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // The client hung up: a fixture server does not care.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private static Task WriteAsync(NetworkStream stream, string text) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
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
