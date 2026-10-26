using NovaDownload.Core.Enums;
using NovaDownload.Core.Models;

namespace NovaDownload.Core.Models;

/// <summary>
/// The persisted shape of one list row. It is deliberately flat and human-readable: the file is a
/// user-visible archive of what was downloaded, not an engine snapshot.
/// </summary>
public sealed class DownloadRecord
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long ProgressBytes { get; set; }
    public DownloadStatus Status { get; set; }
    public TransferKind Kind { get; set; }
    public DownloadCategory Category { get; set; }
    public string CustomCategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastTriedAt { get; set; }
    public string? InfoHash { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Captures a live row for the archive.</summary>
    public static DownloadRecord From(DownloadItem item) => new()
    {
        Id = item.Id,
        Url = item.Url,
        FileName = item.FileName,
        DestinationPath = item.DestinationPath,
        FileSize = item.FileSize,
        ProgressBytes = item.ProgressBytes,
        Status = item.Status,
        Kind = item.Kind,
        Category = item.Category,
        CustomCategory = item.CustomCategory,
        Description = item.Description,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        LastTriedAt = item.LastTriedAt,
        InfoHash = item.InfoHash,
        ErrorMessage = item.ErrorMessage
    };

    /// <summary>
    /// Rebuilds a display row. A record that still says "en cours" cannot be in progress after a
    /// restart — the engine would have restored it with its own state — so it comes back as paused
    /// rather than claiming to run.
    /// </summary>
    public DownloadItem ToItem()
    {
        var status = Status is DownloadStatus.Downloading or DownloadStatus.Queued
            ? DownloadStatus.Paused
            : Status;

        var item = new DownloadItem
        {
            Id = Id,
            Url = Url,
            FileName = FileName,
            DestinationPath = DestinationPath,
            FileSize = FileSize,
            ProgressBytes = ProgressBytes,
            Status = status,
            Kind = Kind,
            Category = Category,
            CustomCategory = CustomCategory,
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            LastTriedAt = LastTriedAt,
            InfoHash = InfoHash,
            ErrorMessage = ErrorMessage,
            IsRestored = true
        };

        if (item.Category == DownloadCategory.Other && !string.IsNullOrEmpty(item.FileName))
            item.Category = Common.DownloadClassifier.Classify(item.FileName, item.IsTorrent);

        return item;
    }

    /// <summary>Copies the choices the engine cannot know back onto a live row restored by an engine.</summary>
    public void ApplyPreferencesTo(DownloadItem item)
    {
        item.Description = Description;
        item.Category = Category == DownloadCategory.Other
            ? Common.DownloadClassifier.Classify(item.FileName, item.IsTorrent)
            : Category;
        item.CustomCategory = CustomCategory;
        item.LastTriedAt = LastTriedAt ?? item.LastTriedAt;
    }
}
