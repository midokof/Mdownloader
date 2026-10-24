using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NovaDownload.Core.Enums;

namespace NovaDownload.App.Converters;

/// <summary>
/// Maps a <see cref="DownloadStatus"/> to a themed brush.
/// ConverterParameter "bg" returns the soft surface variant, anything else the strong colour.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is DownloadStatus s ? s : DownloadStatus.Queued;
        var soft = string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase);

        var key = status switch
        {
            DownloadStatus.Completed => soft ? "SuccessSoftBrush" : "SuccessBrush",
            DownloadStatus.Downloading or DownloadStatus.Queued => soft ? "PrimarySoftBrush" : "PrimaryBrush",
            DownloadStatus.Paused => soft ? "WarningSoftBrush" : "WarningBrush",
            DownloadStatus.Failed => soft ? "ErrorSoftBrush" : "ErrorBrush",
            _ => soft ? "SurfaceAltBrush" : "TextMutedBrush"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
