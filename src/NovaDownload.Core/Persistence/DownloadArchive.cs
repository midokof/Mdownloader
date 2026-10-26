using System.Text.Json;
using System.Text.Json.Serialization;
using NovaDownload.Core.Models;

namespace NovaDownload.Core.Persistence;

/// <summary>
/// The download list, as JSON. One file, written through a temporary file and moved into place so a
/// crash mid-write cannot leave a truncated archive that reads back as an empty list.
/// </summary>
public sealed class DownloadArchive
{
    /// <summary>Forces the folder for tests and portable runs; same variable the engines honour.</summary>
    public const string DataDirectoryVariable = "NOVADOWNLOAD_DATA_DIR";

    public const string FileName = "downloads.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    public DownloadArchive(string? directory = null)
    {
        DirectoryPath = ResolveDirectory(directory);
        System.IO.Directory.CreateDirectory(DirectoryPath);
        FilePath = System.IO.Path.Combine(DirectoryPath, FileName);
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    /// <summary>Last write failure, for the diagnostics log. Null after a successful write.</summary>
    public string? LastError { get; private set; }

    public static string ResolveDirectory(string? explicitDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return System.IO.Path.GetFullPath(explicitDirectory);

        var forced = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(forced))
            return System.IO.Path.GetFullPath(forced);

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaDownload");
    }

    /// <summary>
    /// Reads the archive. A missing, empty or unreadable file is an empty list: a broken archive must
    /// never stop the application from starting.
    /// </summary>
    public IReadOnlyList<DownloadRecord> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<DownloadRecord>();

                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DownloadRecord>();

                var file = JsonSerializer.Deserialize<ArchiveFile>(json, Options);
                if (file?.Items == null) return Array.Empty<DownloadRecord>();

                return file.Items.Where(record => record.Id != Guid.Empty).ToArray();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LastError = ex.Message;
                return Array.Empty<DownloadRecord>();
            }
        }
    }

    /// <summary>Writes the archive atomically. A failed write leaves the previous file intact.</summary>
    public void Save(IEnumerable<DownloadRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var file = new ArchiveFile
        {
            UpdatedAt = DateTime.UtcNow,
            Items = records.ToList()
        };

        lock (_gate)
        {
            var temporary = FilePath + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(file, Options));
                File.Move(temporary, FilePath, overwrite: true);
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = ex.Message;
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private sealed class ArchiveFile
    {
        public int Version { get; set; } = 1;
        public DateTime UpdatedAt { get; set; }
        public List<DownloadRecord> Items { get; set; } = new();
    }
}
