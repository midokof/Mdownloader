// language: C#, file: TorrentTransferState.cs, target: .NET 9
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaDownload.DownloadEngine.Torrenting;

/// <summary>
/// Everything needed to rejoin a swarm after the application closed: what was asked for, where it
/// goes, and which files the user picked. The pieces themselves are on disk, in MonoTorrent's own
/// layout, so a resume costs a hash check and nothing more.
/// </summary>
public sealed class TorrentTransferState
{
    public int Version { get; set; } = 1;

    public Guid Id { get; set; }

    /// <summary>What the row shows and what a magnet resumes from: the magnet URI, the .torrent URL or path.</summary>
    public string Source { get; set; } = string.Empty;

    public string DestinationDirectory { get; set; } = string.Empty;

    /// <summary>Row title: the torrent name before the metadata arrived, then the real one.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Local copy of the .torrent, kept so a resume never depends on the original file or URL still
    /// existing. Null for magnets, whose metadata is cached by the engine itself.
    /// </summary>
    public string? TorrentFilePath { get; set; }

    /// <summary>Torrent-relative paths the user kept. Empty means the whole torrent.</summary>
    public List<string> SelectedPaths { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One JSON file per torrent, written through a temporary file and moved into place: a half-written
/// state must never be read back as truth, or the resume would re-add the wrong selection.
/// </summary>
public sealed class TorrentTransferStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();

    public TorrentTransferStateStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public string PathFor(Guid id) => System.IO.Path.Combine(Directory, id.ToString("n") + ".json");

    public void Save(TorrentTransferState state)
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

    /// <summary>Reads every state file, skipping the corrupt ones instead of failing the whole restore.</summary>
    public IReadOnlyList<TorrentTransferState> LoadAll()
    {
        var loaded = new List<TorrentTransferState>();

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
                var state = JsonSerializer.Deserialize<TorrentTransferState>(File.ReadAllText(file), Options);
                if (state == null || state.Version != 1 || state.Id == Guid.Empty) continue;
                if (string.IsNullOrWhiteSpace(state.Source)) continue;
                if (string.IsNullOrWhiteSpace(state.DestinationDirectory)) continue;
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
