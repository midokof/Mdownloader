# NovaDownload 1.0.0 — Windows x64

File: `NovaDownload-1.0.0-win-x64.exe`

## Requirements

- Windows 10 or Windows 11, 64-bit.
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) installed for x64.

This is a **framework-dependent, single-file** build. It needs the .NET 9 Desktop Runtime; it does not bundle the runtime.

## Run

Double-click the executable, or from PowerShell:

```powershell
.\NovaDownload-1.0.0-win-x64.exe
```

## Features

- HTTP/HTTPS downloads with resume support.
- Torrent and magnet downloads.
- Multi-file torrent file selection.
- Pause, resume, cancel, queue monitoring, and torrent telemetry.

## Notes

- The browser-extension native messaging host (`NovaDownload.NativeHost.exe`) is not bundled in this single-file release. Build or publish the full solution for browser-extension integration.
- Full source is included in the repository.
