# NovaDownload Architecture

## Goals
- Clean separation: Core (domain/models/contracts), DownloadEngine (network/queue/segments), Infrastructure (persistence/logging), BrowserIntegration (extension/native messaging), App (WPF UI + DI + MVVM)
- Testable, async-first, resilient
- Windows 10/11, .NET 9 (net9.0-windows for WPF)

## Layers
- NovaDownload.Core: DTOs, enums, interfaces (IDownloadService, IQueueService, IDownloadRepository, ISettingsService)
- NovaDownload.DownloadEngine: HttpDownloadClient, SegmentDownloader, QueueManager, RetryPolicy, SpeedCalculator
- NovaDownload.Infrastructure: SqliteDownloadRepository, SettingsRepository, Logger, FileSystem helpers
- NovaDownload.BrowserIntegration: Native host messaging, extension protocol, interception logic (explicit opt-in)
- NovaDownload.App: Views, ViewModels, Services (DI), Themes, Converters

## Data Model (initial)
DownloadItem: Id, Url, FileName, DestinationPath, FileSize, Status, ProgressBytes, SpeedBps, ETag, LastModified, SupportsRange, Segments[], CreatedAt, UpdatedAt, RetryCount

## Persistence
SQLite via Microsoft.Data.Sqlite + Dapper. Schema migrations on startup.

## Networking
HttpClient with sockets handler, per-request headers, Range requests, redirect handling, timeouts.

## Concurrency
Configurable simultaneous downloads and segments. CancellationTokenSource per download.

## Security
Path traversal protection, safe temp files, no auto-execution, explicit browser interception.
