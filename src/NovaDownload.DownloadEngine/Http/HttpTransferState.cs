// language: C#, file: HttpTransferState.cs, target: .NET 9
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaDownload.DownloadEngine.Http;

/// <summary>Per-download options that are not part of the shared engine configuration.</summary>
public sealed record HttpDownloadRequest
{
    public HttpDownloadRequest(string url, string destinationDirectory, string? fileName = null)
    {
        Url = url;
        DestinationDirectory = destinationDirectory;
        FileName = fileName;
    }

    public string Url { get; }

    public string DestinationDirectory { get; }

    /// <summary>Pinned by the caller; when null the engine takes Content-Disposition, then the URL.</summary>
    public string? FileName { get; }

    /// <summary>Optional HTTP Basic user name, applied to every request of this transfer.</summary>
    public string? UserName { get; init; }

    /// <summary>Optional HTTP Basic password. Never written to disk.</summary>
    public string? Password { get; init; }
}

/// <summary>One byte range of a transfer and how much of it is already on disk.</summary>
public sealed class HttpSegmentState
{
    public int Index { get; set; }

    /// <summary>First byte of the range, absolute in the final file.</summary>
    public long Start { get; set; }

    /// <summary>Last byte of the range, absolute and inclusive. Negative when the size is unknown.</summary>
    public long End { get; set; }

    /// <summary>Bytes of this range already written. Always less than or equal to the range length.</summary>
    public long Written { get; set; }

    [JsonIgnore]
    public long Length => End < Start ? -1 : End - Start + 1;
}

/// <summary>
/// Everything needed to continue a transfer after the process died: where the bytes are, what the
/// remote object looked like when they were fetched, and how far each range got.
/// </summary>
public sealed class HttpTransferState
{
    public int Version { get; set; } = 1;

    public Guid Id { get; set; }

    public string Url { get; set; } = string.Empty;

    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>Partial file. Always <see cref="DestinationPath"/> + ".npart".</summary>
    public string TempPath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>True when the caller named the file, so Content-Disposition must not override it.</summary>
    public bool PinnedName { get; set; }

    /// <summary>Full size announced by the server; 0 while unknown (chunked response).</summary>
    public long TotalSize { get; set; }

    public bool SupportsRange { get; set; }

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public string? UserName { get; set; }

    /// <summary>Kept in memory only: a resume after restart asks for the password again.</summary>
    [JsonIgnore]
    public string? Password { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<HttpSegmentState> Segments { get; set; } = new();

    [JsonIgnore]
    public long WrittenBytes
    {
        get
        {
            var total = 0L;
            foreach (var segment in Segments) total += segment.Written;
            return total;
        }
    }

    [JsonIgnore]
    public bool IsPlanned => Segments.Count > 0;
}

/// <summary>
/// One JSON file per transfer, written through a temporary file and moved into place so a crash
/// can never leave a half-written state that would be read back as truth.
/// </summary>
public sealed class HttpTransferStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();

    public HttpTransferStateStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public string PathFor(Guid id) => Path.Combine(Directory, id.ToString("n") + ".json");

    public void Save(HttpTransferState state)
    {
        var target = PathFor(state.Id);
        var temporary = target + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(state, Options);
            lock (_gate)
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A state file that cannot be written costs a resume, never the transfer itself.
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // ignored: the next save retries
            }
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            try
            {
                var path = PathFor(id);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ignored
            }
        }
    }

    /// <summary>Reads every state file, skipping the ones that are corrupt or belong to another version.</summary>
    public IReadOnlyList<HttpTransferState> LoadAll()
    {
        var loaded = new List<HttpTransferState>();

        string[] files;
        try
        {
            files = System.IO.Directory.GetFiles(Directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return loaded;
        }

        foreach (var file in files)
        {
            try
            {
                var state = JsonSerializer.Deserialize<HttpTransferState>(File.ReadAllText(file), Options);
                if (state == null || state.Version != 1 || state.Id == Guid.Empty) continue;
                if (string.IsNullOrWhiteSpace(state.Url) || string.IsNullOrWhiteSpace(state.TempPath)) continue;
                loaded.Add(state);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt file is not a reason to refuse the other resumes.
            }
        }

        return loaded.OrderBy(s => s.CreatedAt).ToArray();
    }
}
