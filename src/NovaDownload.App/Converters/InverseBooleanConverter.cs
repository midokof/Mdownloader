using System.Globalization;
using System.Windows.Data;

namespace NovaDownload.App.Converters;

/// <summary>Inverts a boolean: "enabled while not unlimited" is the only use, kept explicit.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : true;
}
