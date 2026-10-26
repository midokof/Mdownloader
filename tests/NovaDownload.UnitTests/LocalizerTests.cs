using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;

namespace NovaDownload.UnitTests;

public class LocalizerTests
{
    [Fact]
    public void Auto_Follows_The_Os_Display_Language()
    {
        Localizer.Apply("Auto");

        var expected = Localizer.OsCulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase)
            ? "fr"
            : "en";

        Assert.Equal("Auto", Localizer.Preference);
        Assert.Equal(expected, Localizer.Language);
    }

    [Fact]
    public void Unsupported_Preference_Falls_Back_To_English()
    {
        Localizer.Apply("de");
        Assert.Equal("en", Localizer.Language);

        // A token that is not a catalog still resolves, never throws.
        Localizer.Apply("klingon");
        Assert.Equal("en", Localizer.Language);

        Localizer.Apply(null);
        Assert.Equal("Auto", Localizer.Preference);
    }

    [Fact]
    public void Unknown_Key_Returns_The_Key_Instead_Of_Throwing()
    {
        Localizer.Apply("en");
        Assert.Equal("No_Such_Key", Localizer.Get("No_Such_Key"));
        Assert.Equal("No_Such_Key", Localizer.Format("No_Such_Key", 1));
    }

    [Fact]
    public void Switch_Raises_LanguageChanged()
    {
        Localizer.Apply("en");
        var raised = 0;
        void Handler(object? sender, EventArgs args) => raised++;

        Localizer.LanguageChanged += Handler;
        try
        {
            Localizer.Apply("fr");
        }
        finally
        {
            Localizer.LanguageChanged -= Handler;
        }

        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData(0, "0 o")]
    [InlineData(512, "512 o")]
    [InlineData(1536, "1,5 Ko")]
    [InlineData(1048576, "1 Mo")]
    [InlineData(1610612736, "1,5 Go")]
    public void French_Uses_French_Units_And_Decimal_Comma(long input, string expected)
    {
        Localizer.Apply("fr");
        Assert.Equal(expected, Formatters.Bytes(input));
    }

    [Fact]
    public void French_Formats_Speed_And_Eta()
    {
        Localizer.Apply("fr");

        Assert.Equal("0 o/s", Formatters.Speed(0));
        Assert.Equal("2 Ko/s", Formatters.Speed(2048));
        Assert.Equal("1 s", Formatters.Eta(1000, 1000));
        Assert.Equal("1 min 30 s", Formatters.Eta(90_000, 1000));
        Assert.Equal("2 h 0 min", Formatters.Eta(7_200_000, 1000));
        Assert.Equal("—", Formatters.Eta(0, 0));
    }

    [Fact]
    public void English_Stays_Pinned_While_French_Is_Active_Elsewhere()
    {
        Localizer.Apply("en");
        Assert.Equal("1.5 KB", Formatters.Bytes(1536));
        Assert.Equal("1m 30s", Formatters.Eta(90_000, 1000));

        Localizer.Apply("fr");
        Assert.Equal("1,5 Ko", Formatters.Bytes(1536));
    }

    [Fact]
    public void Language_Switch_Refreshes_Localized_Item_Text()
    {
        Localizer.Apply("en");

        var item = new DownloadItem { FileSize = 1_048_576, Status = DownloadStatus.Completed };
        Assert.Equal("Completed", item.StatusText);
        Assert.Equal("1 MB", item.SizeText);
        Assert.StartsWith("Finished ", item.MetricPrimary);

        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Localizer.Apply("fr");
        item.RefreshLocalizedText();

        Assert.Equal("Terminé", item.StatusText);
        Assert.Equal("1 Mo", item.SizeText);
        Assert.StartsWith("Terminé ", item.MetricPrimary);
        Assert.Contains(nameof(DownloadItem.StatusText), changed);
        Assert.Contains(nameof(DownloadItem.MetricPrimary), changed);
    }

    [Fact]
    public void Plural_Follows_Each_Language_Rule()
    {
        Localizer.Apply("en");
        Assert.Equal("1 of 1 transfer", Localizer.Plural("Downloads_SummaryOne", "Downloads_Summary", 1, 1, 1));
        Assert.Equal("0 of 4 transfers", Localizer.Plural("Downloads_SummaryOne", "Downloads_Summary", 0, 0, 4));
        Assert.Equal("3 of 4 transfers", Localizer.Plural("Downloads_SummaryOne", "Downloads_Summary", 3, 3, 4));

        // French counts zero as singular too.
        Localizer.Apply("fr");
        Assert.Equal("1 sur 1 transfert", Localizer.Plural("Downloads_SummaryOne", "Downloads_Summary", 1, 1, 1));
        Assert.Equal("0 sur 4 transfert", Localizer.Plural("Downloads_SummaryOne", "Downloads_Summary", 0, 0, 4));
        Assert.Equal("3 sur 4 transferts", Localizer.Plural("Downloads_SummaryOne", "Downloads_Summary", 3, 3, 4));
    }

    [Fact]
    public void Every_Status_Has_A_Translation()
    {
        Localizer.Apply("fr");

        foreach (var status in Enum.GetValues<DownloadStatus>())
        {
            var text = new DownloadItem { Status = status }.StatusText;
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual("Unknown", text);
        }
    }
}
