using System.Diagnostics;
using System.IO;

namespace NovaDownload.App.Services;

public static class ShellHelper
{
    public static void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else OpenFolder(Path.GetDirectoryName(path));
        }
        catch { }
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
