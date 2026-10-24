namespace NovaDownload.NativeHost;

/// <summary>
/// Append-only diagnostics log. The native messaging protocol owns stdout, so troubleshooting
/// data goes to a file (and optionally stderr) instead.
/// </summary>
public sealed class HostLog
{
    private const long MaxBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Action<string>? _mirror;

    public HostLog(bool verbose = false)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NovaDownload", "logs");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "native-host.log");
        }
        catch
        {
            _path = null;
        }

        _mirror = verbose ? message => Console.Error.WriteLine($"[novadownload-host] {message}") : null;
    }

    public void Info(string message) => Write("info", message);

    public void Error(string message) => Write("error", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {message}";
        try { _mirror?.Invoke(message); } catch { }

        lock (_gate)
        {
            if (_path == null) return;
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var previous = _path + ".1";
                    File.Delete(previous);
                    File.Move(_path, previous);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never break a download.
            }
        }
    }
}
