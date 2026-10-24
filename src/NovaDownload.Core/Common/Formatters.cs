using System.Globalization;
using NovaDownload.Core.Localization;

namespace NovaDownload.Core.Common;

/// <summary>
/// Human-readable byte, speed and duration formatting. Culture comes from
/// <see cref="Localizer"/>, so units and decimal separators follow the UI language.
/// </summary>
public static class Formatters
{
    private static readonly string[] UnitKeys = { "Unit_B", "Unit_KB", "Unit_MB", "Unit_GB", "Unit_TB" };

    public static string Bytes(long bytes)
    {
        var unit = Localizer.Get("Unit_B");
        if (bytes <= 0) return "0 " + unit;

        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < UnitKeys.Length - 1)
        {
            value /= 1024;
            index++;
        }

        unit = Localizer.Get(UnitKeys[index]);
        return index == 0
            ? string.Format(Localizer.Culture, "{0} {1}", (long)value, unit)
            : string.Format(Localizer.Culture, "{0:0.#} {1}", value, unit);
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond <= 0
            ? "0 " + Localizer.Get("Unit_B") + Localizer.Get("Unit_PerSecond")
            : Bytes((long)bytesPerSecond) + Localizer.Get("Unit_PerSecond");

    public static string Eta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 1 || remainingBytes <= 0) return Localizer.Get("Common_Dash");

        var seconds = (long)(remainingBytes / bytesPerSecond);
        if (seconds < 60) return Localizer.Format("Eta_Seconds", seconds);
        if (seconds < 3600) return Localizer.Format("Eta_Minutes", seconds / 60, seconds % 60);
        if (seconds < 86400) return Localizer.Format("Eta_Hours", seconds / 3600, (seconds % 3600) / 60);
        return Localizer.Format("Eta_Days", seconds / 86400, (seconds % 86400) / 3600);
    }

    /// <summary>Time of day for finished/paused stamps, 24-hour and culture-neutral.</summary>
    public static string TimeOfDay(DateTime utc) =>
        utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
