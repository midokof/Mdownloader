namespace NovaDownload.BrowserIntegration.Deduplication;

/// <summary>
/// Remembers the requests seen in a short window so a double click, a replayed message or a
/// retried native message does not queue the same transfer twice.
/// </summary>
public sealed class RequestDeduplicator
{
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public DateTime SeenAt { get; init; }
        public Guid? DownloadId { get; set; }
    }

    public RequestDeduplicator(TimeSpan? window = null, int capacity = 256)
    {
        _window = window ?? TimeSpan.FromSeconds(20);
        _capacity = Math.Max(8, capacity);
    }

    /// <summary>Registered id for a key, when the transfer has already been created.</summary>
    public Guid? GetDownloadId(string key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.DownloadId : null;
        }
    }

    /// <summary>
    /// Returns true when this is the first sighting of <paramref name="key"/> inside the window.
    /// False means the caller must not start a second transfer.
    /// </summary>
    public bool TryBegin(string key, out Guid? existingDownloadId)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            Prune(now);

            if (_entries.TryGetValue(key, out var entry) && now - entry.SeenAt <= _window)
            {
                existingDownloadId = entry.DownloadId;
                return false;
            }

            if (_entries.Count >= _capacity)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.SeenAt).First();
                _entries.Remove(oldest.Key);
            }

            _entries[key] = new Entry { SeenAt = now };
            existingDownloadId = null;
            return true;
        }
    }

    /// <summary>Attaches the engine id to a key so a repeat can answer with the existing transfer.</summary>
    public void Attach(string key, Guid downloadId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry)) entry.DownloadId = downloadId;
            else _entries[key] = new Entry { SeenAt = DateTime.UtcNow, DownloadId = downloadId };
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Builds the dedupe key: the extension-provided id when present, the URL otherwise.</summary>
    public static string BuildKey(string? requestId, string? url) =>
        string.IsNullOrWhiteSpace(requestId) ? $"url:{url}" : $"id:{requestId}|{url}";

    private void Prune(DateTime now)
    {
        if (_entries.Count == 0) return;
        var expired = _entries.Where(pair => now - pair.Value.SeenAt > _window).Select(pair => pair.Key).ToArray();
        foreach (var key in expired) _entries.Remove(key);
    }
}
