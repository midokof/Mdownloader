using NovaDownload.BrowserIntegration.Deduplication;

namespace NovaDownload.UnitTests.Browser;

public class RequestDeduplicatorTests
{
    [Fact]
    public void The_First_Sighting_Is_Admitted_And_A_Repeat_Is_Not()
    {
        var dedup = new RequestDeduplicator();

        Assert.True(dedup.TryBegin("id:abc|https://example.com/a.mp4", out var first));
        Assert.Null(first);

        Assert.False(dedup.TryBegin("id:abc|https://example.com/a.mp4", out var second));
        Assert.Null(second);
    }

    [Fact]
    public void The_Attached_Download_Id_Is_Returned_For_Repeats()
    {
        var dedup = new RequestDeduplicator();
        var id = Guid.NewGuid();
        const string key = "id:abc|https://example.com/a.mp4";

        Assert.True(dedup.TryBegin(key, out _));
        dedup.Attach(key, id);

        Assert.Equal(id, dedup.GetDownloadId(key));
        Assert.False(dedup.TryBegin(key, out var existing));
        Assert.Equal(id, existing);
    }

    [Fact]
    public async Task Entries_Expire_After_The_Window()
    {
        var dedup = new RequestDeduplicator(TimeSpan.FromMilliseconds(60));

        Assert.True(dedup.TryBegin("url:https://example.com/a.mp4", out _));
        Assert.False(dedup.TryBegin("url:https://example.com/a.mp4", out _));

        await Task.Delay(120);

        Assert.True(dedup.TryBegin("url:https://example.com/a.mp4", out _));
    }

    [Fact]
    public void Keys_Are_Built_From_The_Request_Id_And_The_Url()
    {
        Assert.Equal("id:abc|https://example.com/a.mp4",
            RequestDeduplicator.BuildKey("abc", "https://example.com/a.mp4"));
        Assert.Equal("url:https://example.com/a.mp4",
            RequestDeduplicator.BuildKey(null, "https://example.com/a.mp4"));
        Assert.Equal("url:https://example.com/a.mp4",
            RequestDeduplicator.BuildKey("  ", "https://example.com/a.mp4"));
    }

    [Fact]
    public void Distinct_Request_Ids_For_The_Same_Url_Are_Distinct_Requests()
    {
        var dedup = new RequestDeduplicator();

        Assert.True(dedup.TryBegin(RequestDeduplicator.BuildKey("one", "https://example.com/a.mp4"), out _));
        Assert.True(dedup.TryBegin(RequestDeduplicator.BuildKey("two", "https://example.com/a.mp4"), out _));
    }

    [Fact]
    public void The_Table_Is_Bounded()
    {
        var dedup = new RequestDeduplicator(capacity: 8);

        for (var i = 0; i < 40; i++)
            dedup.TryBegin($"id:{i}|https://example.com/{i}.mp4", out _);

        Assert.True(dedup.Count <= 8, $"capacity was exceeded: {dedup.Count}");
    }

    [Fact]
    public void Clearing_Forgets_Everything()
    {
        var dedup = new RequestDeduplicator();
        dedup.TryBegin("id:a|https://example.com/a.mp4", out _);
        dedup.Attach("id:a|https://example.com/a.mp4", Guid.NewGuid());

        dedup.Clear();

        Assert.Equal(0, dedup.Count);
        Assert.True(dedup.TryBegin("id:a|https://example.com/a.mp4", out _));
    }
}
