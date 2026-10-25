        public async Task<DownloadItem> StartDownloadAsync(string url, string destinationDirectory, IReadOnlyCollection<TorrentFileInfo> selectedFiles, string? fileName = null, CancellationToken cancellationToken = default)
        {
            // The original overload without selectedFiles continues to work for backward compatibility
            return await StartDownloadAsync(url, destinationDirectory, fileName, cancellationToken);
        }
