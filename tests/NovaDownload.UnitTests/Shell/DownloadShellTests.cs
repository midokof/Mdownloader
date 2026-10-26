using NovaDownload.Core.Common;
using NovaDownload.Core.Enums;
using NovaDownload.Core.Localization;
using NovaDownload.Core.Models;
using NovaDownload.Core.Persistence;

namespace NovaDownload.UnitTests.Shell;

/// <summary>
/// The pieces the reference shell stands on: the category table behind the left pane and the Type
/// column, the list predicate behind the pane and the search box, and the JSON archive behind a
/// restart. They are tested together because they must agree: a row filed as "Video" has to be
/// found under "Video" and has to come back filed the same way.
/// </summary>
public class DownloadShellTests
{
    // ---- classifier ---------------------------------------------------------

    [Theory]
    [InlineData("archive.zip", DownloadCategory.Compressed)]
    [InlineData("backup.7Z", DownloadCategory.Compressed)]
    [InlineData("report.pdf", DownloadCategory.Documents)]
    [InlineData("track.FLAC", DownloadCategory.Music)]
    [InlineData("setup.exe", DownloadCategory.Programs)]
    [InlineData("episode.mkv", DownloadCategory.Video)]
    [InlineData("payload.xyz", DownloadCategory.Other)]
    [InlineData("README", DownloadCategory.Other)]
    [InlineData("", DownloadCategory.Other)]
    public void Extension_Decides_The_Category(string fileName, DownloadCategory expected) =>
        Assert.Equal(expected, DownloadClassifier.Classify(fileName, isTorrent: false));

    [Fact]
    public void Missing_Name_Is_Other() =>
        Assert.Equal(DownloadCategory.Other, DownloadClassifier.Classify(null, isTorrent: false));

    [Fact]
    public void Torrent_Kind_Wins_Over_The_Extension()
    {
        // A .mkv inside a torrent is still a torrent: the pane sorts by transfer, not by payload.
        Assert.Equal(DownloadCategory.Torrents, DownloadClassifier.Classify("episode.mkv", isTorrent: true));
        Assert.Equal(DownloadCategory.Torrents, DownloadClassifier.Classify("archive.zip", isTorrent: true));
    }

    [Fact]
    public void Every_Extension_Is_Filed_Exactly_Once()
    {
        var builtIn = new[]
        {
            DownloadCategory.Compressed, DownloadCategory.Documents, DownloadCategory.Music,
            DownloadCategory.Programs, DownloadCategory.Video
        };

        var all = builtIn.SelectMany(DownloadClassifier.Extensions).ToArray();

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Length, all.Length);
        Assert.All(builtIn, category => Assert.NotEmpty(DownloadClassifier.Extensions(category)));
    }

    [Fact]
    public void Torrents_Have_No_Extension_Table()
    {
        // Torrents are classified by kind; an extension list here would be a second, conflicting rule.
        Assert.Empty(DownloadClassifier.Extensions(DownloadCategory.Torrents));
        Assert.Empty(DownloadClassifier.Extensions(DownloadCategory.Other));
    }

    [Theory]
    [InlineData(DownloadCategory.Compressed)]
    [InlineData(DownloadCategory.Documents)]
    [InlineData(DownloadCategory.Music)]
    [InlineData(DownloadCategory.Programs)]
    [InlineData(DownloadCategory.Video)]
    [InlineData(DownloadCategory.Torrents)]
    [InlineData(DownloadCategory.Other)]
    public void Every_Category_Has_A_Label_In_Both_Languages(DownloadCategory category)
    {
        var key = DownloadClassifier.Key(category);

        Assert.False(string.IsNullOrWhiteSpace(Localizer.Catalog("en")[key]));
        Assert.False(string.IsNullOrWhiteSpace(Localizer.Catalog("fr")[key]));
    }

    // ---- filter -------------------------------------------------------------

    private static DownloadItem Row(DownloadStatus status, DownloadCategory category = DownloadCategory.Other,
        string custom = "", string name = "file.bin", string url = "https://example.test/file.bin",
        string folder = @"C:\Downloads", string description = "") => new()
    {
        FileName = name,
        Url = url,
        DestinationPath = Path.Combine(folder, name),
        Status = status,
        Category = category,
        CustomCategory = custom,
        Description = description
    };

    [Fact]
    public void All_Scope_Keeps_Every_Row()
    {
        Assert.True(DownloadFilter.MatchesScope(Row(DownloadStatus.Completed), DownloadScope.All,
            DownloadCategory.Other));
        Assert.True(DownloadFilter.MatchesScope(Row(DownloadStatus.Downloading), DownloadScope.All,
            DownloadCategory.Other));
        Assert.True(DownloadFilter.MatchesScope(Row(DownloadStatus.Failed), DownloadScope.All,
            DownloadCategory.Other));
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading, true)]
    [InlineData(DownloadStatus.Queued, true)]
    [InlineData(DownloadStatus.Paused, true)]
    [InlineData(DownloadStatus.Completed, false)]
    [InlineData(DownloadStatus.Canceled, false)]
    [InlineData(DownloadStatus.Failed, false)]
    public void Unfinished_Keeps_Everything_Not_Settled(DownloadStatus status, bool expected) =>
        Assert.Equal(expected, DownloadFilter.MatchesScope(Row(status), DownloadScope.Unfinished,
            DownloadCategory.Other));

    [Theory]
    [InlineData(DownloadStatus.Completed, true)]
    [InlineData(DownloadStatus.Failed, true)]
    [InlineData(DownloadStatus.Canceled, true)]
    [InlineData(DownloadStatus.Paused, false)]
    [InlineData(DownloadStatus.Downloading, false)]
    public void Finished_Keeps_Only_Settled_Rows(DownloadStatus status, bool expected) =>
        Assert.Equal(expected, DownloadFilter.MatchesScope(Row(status), DownloadScope.Finished,
            DownloadCategory.Other));

    [Fact]
    public void Queue_Scope_Keeps_Only_Queued_Rows()
    {
        Assert.True(DownloadFilter.MatchesScope(Row(DownloadStatus.Queued), DownloadScope.Queue,
            DownloadCategory.Other));
        Assert.False(DownloadFilter.MatchesScope(Row(DownloadStatus.Downloading), DownloadScope.Queue,
            DownloadCategory.Other));
        Assert.False(DownloadFilter.MatchesScope(Row(DownloadStatus.Paused), DownloadScope.Queue,
            DownloadCategory.Other));
    }

    [Fact]
    public void Category_Scope_Files_By_Type_And_Excludes_Custom_Rows()
    {
        var video = Row(DownloadStatus.Paused, DownloadCategory.Video);
        var custom = Row(DownloadStatus.Paused, DownloadCategory.Video, custom: "Cours");

        Assert.True(DownloadFilter.MatchesScope(video, DownloadScope.Category, DownloadCategory.Video));
        Assert.False(DownloadFilter.MatchesScope(video, DownloadScope.Category, DownloadCategory.Music));

        // A row given a custom category lives under that node only, not under every node of its type.
        Assert.False(DownloadFilter.MatchesScope(custom, DownloadScope.Category, DownloadCategory.Video));
    }

    [Fact]
    public void Custom_Scope_Matches_The_Name_Case_Insensitively()
    {
        var row = Row(DownloadStatus.Paused, custom: "Cours");

        Assert.True(DownloadFilter.MatchesScope(row, DownloadScope.Custom, DownloadCategory.Other, "cours"));
        Assert.False(DownloadFilter.MatchesScope(row, DownloadScope.Custom, DownloadCategory.Other, "Films"));
        Assert.False(DownloadFilter.MatchesScope(Row(DownloadStatus.Paused), DownloadScope.Custom,
            DownloadCategory.Other, "Cours"));
    }

    [Theory]
    [InlineData("movie", true)]
    [InlineData("MOVIE", true)]
    [InlineData("example.test", true)]
    [InlineData(@"C:\Downloads", true)]
    [InlineData("holiday", true)]
    [InlineData("absent", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    public void Search_Looks_At_Name_Url_Folder_And_Description(string? needle, bool expected)
    {
        var row = Row(DownloadStatus.Completed, name: "movie.mkv", url: "https://example.test/movie.mkv",
            folder: @"C:\Downloads", description: "Holiday footage");

        Assert.Equal(expected, DownloadFilter.MatchesSearch(row, needle));
    }

    [Fact]
    public void Matches_Applies_Scope_And_Search_Together()
    {
        var row = Row(DownloadStatus.Downloading, DownloadCategory.Video, name: "movie.mkv");

        Assert.True(DownloadFilter.Matches(row, DownloadScope.Unfinished, DownloadCategory.Video, "movie"));
        Assert.False(DownloadFilter.Matches(row, DownloadScope.Finished, DownloadCategory.Video, "movie"));
        Assert.False(DownloadFilter.Matches(row, DownloadScope.Unfinished, DownloadCategory.Video, "absent"));
    }

    // ---- archive ------------------------------------------------------------

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "nova-archive-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Archive_Round_Trip_Preserves_The_Row()
    {
        var directory = NewDirectory();
        try
        {
            var archive = new DownloadArchive(directory);
            var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var tried = created.AddMinutes(9);

            var record = new DownloadRecord
            {
                Id = Guid.NewGuid(),
                Url = "https://example.test/movie.mkv",
                FileName = "movie.mkv",
                DestinationPath = @"C:\Downloads\movie.mkv",
                FileSize = 4_194_304,
                ProgressBytes = 1_048_576,
                Status = DownloadStatus.Paused,
                Kind = TransferKind.Http,
                Category = DownloadCategory.Video,
                CustomCategory = "Cours",
                Description = "Episode 1",
                CreatedAt = created,
                UpdatedAt = tried,
                LastTriedAt = tried
            };

            archive.Save(new[] { record });

            var loaded = Assert.Single(new DownloadArchive(directory).Load());

            Assert.Equal(record.Id, loaded.Id);
            Assert.Equal(record.Url, loaded.Url);
            Assert.Equal(record.FileName, loaded.FileName);
            Assert.Equal(record.DestinationPath, loaded.DestinationPath);
            Assert.Equal(record.FileSize, loaded.FileSize);
            Assert.Equal(record.ProgressBytes, loaded.ProgressBytes);
            Assert.Equal(DownloadStatus.Paused, loaded.Status);
            Assert.Equal(DownloadCategory.Video, loaded.Category);
            Assert.Equal("Cours", loaded.CustomCategory);
            Assert.Equal("Episode 1", loaded.Description);
            Assert.Equal(tried, loaded.LastTriedAt);
            Assert.Null(archive.LastError);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Save_Leaves_No_Temporary_File_Behind()
    {
        var directory = NewDirectory();
        try
        {
            var archive = new DownloadArchive(directory);
            archive.Save(new[] { new DownloadRecord { Id = Guid.NewGuid(), FileName = "a.zip" } });

            Assert.True(File.Exists(archive.FilePath));
            Assert.False(File.Exists(archive.FilePath + ".tmp"));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Missing_Archive_Is_An_Empty_List()
    {
        var directory = NewDirectory();
        try
        {
            Assert.Empty(new DownloadArchive(directory).Load());
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Corrupt_Archive_Loads_Empty_And_Reports_The_Reason()
    {
        var directory = NewDirectory();
        try
        {
            var archive = new DownloadArchive(directory);
            File.WriteAllText(archive.FilePath, "{ this is not json");

            Assert.Empty(archive.Load());
            Assert.False(string.IsNullOrWhiteSpace(archive.LastError));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Row_That_Was_Running_Comes_Back_Paused()
    {
        var record = new DownloadRecord
        {
            Id = Guid.NewGuid(),
            FileName = "movie.mkv",
            Status = DownloadStatus.Downloading
        };

        var item = record.ToItem();

        // Nothing runs after a restart: the engine restores its own state, the view must not claim live.
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.True(item.IsRestored);
    }

    [Fact]
    public void Restored_Row_Reclassifies_An_Unfiled_Extension()
    {
        // Archives written before the category existed carry Other; the extension still decides.
        var item = new DownloadRecord
        {
            Id = Guid.NewGuid(),
            FileName = "setup.exe",
            Status = DownloadStatus.Completed,
            Category = DownloadCategory.Other
        }.ToItem();

        Assert.Equal(DownloadCategory.Programs, item.Category);
    }

    [Fact]
    public void Preferences_Are_Copied_Onto_A_Live_Engine_Row()
    {
        var tried = DateTime.UtcNow.AddMinutes(-3);
        var record = new DownloadRecord
        {
            Id = Guid.NewGuid(),
            FileName = "track.mp3",
            Category = DownloadCategory.Music,
            CustomCategory = "Cours",
            Description = "Lecture 4",
            LastTriedAt = tried
        };

        var live = new DownloadItem { FileName = "track.mp3" };
        record.ApplyPreferencesTo(live);

        Assert.Equal(DownloadCategory.Music, live.Category);
        Assert.Equal("Cours", live.CustomCategory);
        Assert.Equal("Lecture 4", live.Description);
        Assert.Equal(tried, live.LastTriedAt);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp folder is not a test failure.
        }
    }

    // ---- catalogs -----------------------------------------------------------

    [Fact]
    public void Both_Catalogs_Carry_The_Same_Keys()
    {
        var english = Localizer.Catalog("en").Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var french = Localizer.Catalog("fr").Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.Equal(english, french);
    }

    [Fact]
    public void Every_Label_Used_By_The_App_Resolves_In_Both_Languages()
    {
        // The catalogs are only as good as the keys the code actually asks for: a typo or a key
        // that was never added shows the raw key in the UI. This walks the sources and checks
        // every Localizer.Get/Format call and every {loc:Tr ...} markup against both catalogs.
        var root = FindRepositoryRoot();
        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

        Assert.NotEmpty(sources);

        var callPattern = new System.Text.RegularExpressions.Regex(
            "Localizer\\.(?:Get|Format|Plural)\\(\"([A-Za-z0-9_]+)\"");
        var markupPattern = new System.Text.RegularExpressions.Regex("\\{loc:Tr ([A-Za-z0-9_]+)\\}");

        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (System.Text.RegularExpressions.Match match in callPattern.Matches(text))
            {
                used.Add(match.Groups[1].Value);
            }

            // The markup form only counts in XAML: the .cs files mention it inside doc comments.
            if (!source.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (System.Text.RegularExpressions.Match match in markupPattern.Matches(text))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(used);

        var missing = new List<string>();
        foreach (var key in used)
        {
            if (!Localizer.Catalog("en").ContainsKey(key)) missing.Add($"en:{key}");
            if (!Localizer.Catalog("fr").ContainsKey(key)) missing.Add($"fr:{key}");
        }

        Assert.Empty(missing);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "NovaDownload.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void Every_Shell_Label_Exists_In_Both_Languages()
    {
        var prefixes = new[]
        {
            "Menu_", "Tool_", "Col_", "Cat_", "Ctx_", "Status_", "List_", "Dlg_", "Confirm_",
            "Opt_", "Speed_", "Sched_", "About_", "Help_", "Tray_", "Prompt_"
        };

        var keys = Localizer.Catalog("en").Keys
            .Where(key => prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.NotEmpty(keys);

        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(Localizer.Catalog("en")[key]), $"en:{key}");
            Assert.True(Localizer.Catalog("fr").TryGetValue(key, out var french), $"fr missing: {key}");
            Assert.False(string.IsNullOrWhiteSpace(french), $"fr blank: {key}");
        }
    }
}
