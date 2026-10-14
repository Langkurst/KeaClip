using System.Text.Json.Nodes;
using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

// Items of an X/Instagram post share the post's address and are downloaded by position
// (DownloadOptions.PlaylistItems). History keeps that position so the duplicate check can tell
// item 2 from item 1; records written before it existed still load.
public sealed class PlaylistItemHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Klippi-playlist-items-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_finished_post_item_records_its_position_and_a_whole_link_records_none()
    {
        var store = new JsonLocalStore(_root);
        await using (var queue = new DownloadQueue(new InstantDownloads(), store))
        {
            var options = new DownloadOptions { Url = new("https://x.com/kea/status/1"), OutputDirectory = _root, PlaylistItems = " 2 " };
            await queue.Enqueue(options, "item").Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.Enqueue(options with { PlaylistItems = null }, "whole").Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var history = await store.LoadHistoryAsync();
        Assert.Equal("2", history.Single(entry => entry.Title == "item").PlaylistItems);
        Assert.Null(history.Single(entry => entry.Title == "whole").PlaylistItems);
        Assert.All(history, entry => Assert.Equal("https://x.com/kea/status/1", entry.SourceUrl));
    }

    [Fact]
    public async Task History_saved_before_positions_existed_still_loads_and_new_records_round_trip()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "history.json"), """
            [{"Id":"old","Title":"Saved","SourceUrl":"https://example.com/v","Source":"Example","Date":"2026-10-01T00:00:00+00:00","FilePath":"saved.mp4","Mode":"VideoAudio","Quality":"Best available","SizeBytes":12,"Status":"Finished"}]
            """);
        var store = new JsonLocalStore(_root);
        var old = Assert.Single(await store.LoadHistoryAsync());
        Assert.Equal("old", old.Id); Assert.Null(old.PlaylistItems); Assert.Equal(12, old.SizeBytes);
        await store.AddHistoryAsync(new("new", "Item", "https://x.com/kea/status/1", "Twitter", DateTimeOffset.UtcNow, "item.mp4", "VideoAudio", "Best available", 5, "Finished", "3"));
        var reloaded = await new JsonLocalStore(_root).LoadHistoryAsync();
        Assert.Equal("3", reloaded.Single(entry => entry.Id == "new").PlaylistItems); Assert.Null(reloaded.Single(entry => entry.Id == "old").PlaylistItems);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "history.json")))!.AsArray();
        Assert.Equal("3", saved.Single(node => node!["Id"]!.GetValue<string>() == "new")!["PlaylistItems"]!.GetValue<string>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class InstantDownloads : IDownloadService
    {
        public Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadResult("item.mp4", null, 10));
        public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
