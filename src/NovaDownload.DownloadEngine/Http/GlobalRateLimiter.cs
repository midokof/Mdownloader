// language: C#, file: GlobalRateLimiter.cs, target: .NET 9
namespace NovaDownload.DownloadEngine.Http;

/// <summary>
/// One budget shared by every transfer and every segment. A token bucket refilled from wall clock
/// time, so eight parallel range requests cannot each consume the whole allowance: the sum is what
/// the user asked for. Set <see cref="BytesPerSecond"/> to 0 to lift the limit.
/// </summary>
public sealed class GlobalRateLimiter
{
    private readonly object _gate = new();
    private readonly object _tickGate = new();

    private double _tokens;
    private long _lastTick = Environment.TickCount64;
    private long _bytesPerSecond;

    /// <summary>Current allowance in bytes per second; 0 means unlimited.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_gate) return _bytesPerSecond;
        }
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = Math.Max(0, value);
                _tokens = 0;
                _lastTick = Environment.TickCount64;
            }
        }
    }

    public bool IsLimited
    {
        get
        {
            lock (_gate) return _bytesPerSecond > 0;
        }
    }

    /// <summary>
    /// Blocks until <paramref name="bytes"/> may leave the pipe. Called after every read, before the
    /// bytes are written, so the disk never runs ahead of the allowance.
    /// </summary>
    public async Task AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return;

        while (true)
        {
            int waitMs;

            lock (_gate)
            {
                var rate = _bytesPerSecond;
                if (rate <= 0) return;

                var now = Environment.TickCount64;
                var elapsedMs = Math.Max(0, now - _lastTick);
                _lastTick = now;

                // One second of burst, but never less than a single chunk or the wait would become
                // a busy loop on very small allowances.
                var capacity = Math.Max(bytes, rate);
                _tokens = Math.Min(capacity, _tokens + elapsedMs / 1000.0 * rate);

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                waitMs = (int)Math.Ceiling((bytes - _tokens) / rate * 1000.0);
            }

            // Several readers refill concurrently; each one waits a short slice and retries so a
            // limit change or a cancellation is noticed quickly.
            await Task.Delay(Math.Clamp(waitMs, 1, 100), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Bytes a caller may move right now without waiting; used by the tests and the UI meter.</summary>
    public double AvailableTokens
    {
        get
        {
            lock (_gate) return _tokens;
        }
    }
}
