using NovaDownload.BrowserIntegration.Validation;

namespace NovaDownload.UnitTests.Browser;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("clip.mp4", "clip.mp4")]
    [InlineData("  spaced name.mp4  ", "spaced name.mp4")]
    [InlineData("report|final.mp4", "report_final.mp4")]
    [InlineData("what?.mp4", "what_.mp4")]
    [InlineData("tab\tname.mp4", "tabname.mp4")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("double  spaces.mp4", "double spaces.mp4")]
    public void Sanitize_Produces_Windows_Safe_Names(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.mp4")]
    [InlineData("NUL.txt")]
    [InlineData("LPT1.log")]
    public void Reserved_Device_Names_Are_Renamed(string input)
    {
        Assert.True(FileNameSanitizer.IsReserved(input));
        var sanitized = FileNameSanitizer.Sanitize(input);
        Assert.StartsWith("_", sanitized);
        Assert.False(FileNameSanitizer.IsReserved(sanitized));
    }

    [Theory]
    [InlineData("..\\windows\\system32\\evil.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("..")]
    [InlineData(" . . ")]
    public void Path_Traversal_Hints_Are_Detected(string input)
    {
        Assert.True(FileNameSanitizer.ContainsPathTraversal(input));
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("a b c.mp4")]
    [InlineData("movie (1).mp4")]
    public void Ordinary_Names_Are_Not_Traversal(string input)
    {
        Assert.False(FileNameSanitizer.ContainsPathTraversal(input));
    }

    [Fact]
    public void Empty_Input_Yields_Empty_Output()
    {
        Assert.Equal(string.Empty, FileNameSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, FileNameSanitizer.Sanitize("   "));
        Assert.Equal(string.Empty, FileNameSanitizer.Sanitize("..."));
    }

    [Fact]
    public void Long_Names_Are_Truncated_And_Keep_The_Extension()
    {
        var name = new string('a', 400) + ".mp4";
        var sanitized = FileNameSanitizer.Sanitize(name);

        Assert.True(sanitized.Length <= FileNameSanitizer.MaxFileNameLength,
            $"length was {sanitized.Length}");
        Assert.EndsWith(".mp4", sanitized);
    }

    [Fact]
    public void Absurdly_Long_Extension_Is_Dropped_Instead_Of_Eating_The_Name()
    {
        var name = new string('b', 200) + "." + new string('c', 40);
        var sanitized = FileNameSanitizer.Sanitize(name);

        Assert.True(sanitized.Length <= FileNameSanitizer.MaxFileNameLength);
        Assert.DoesNotContain(".cccccccc", sanitized);
    }

    [Fact]
    public void Sanitize_Is_Idempotent()
    {
        var once = FileNameSanitizer.Sanitize("  co:n*? .mp4  ");
        var twice = FileNameSanitizer.Sanitize(once);
        Assert.Equal(once, twice);
    }
}
