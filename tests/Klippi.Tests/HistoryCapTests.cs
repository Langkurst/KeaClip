using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

// history.json is rewritten on every download; with "keep forever" it holds only the newest records.
public sealed class HistoryCapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Klippi-history-cap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Adding_past_the_cap_drops_only_the_oldest_records()
    {
        Directory.CreateDirectory(_root);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Saved out of date order, as a hand edit or an older version could leave them.
        var saved = Enumerable.Range(0, JsonLocalStore.MaxHistoryEntries).Reverse().Select(i => Entry("old-" + i, start.AddMinutes(i))).ToArray();
        await File.WriteAllTextAsync(Path.Combine(_root, "history.json"), JsonSerializer.Serialize(saved));
        var store = new JsonLocalStore(_root);

        await store.AddHistoryAsync(Entry("new", start.AddYears(1)));

        var history = await new JsonLocalStore(_root).LoadHistoryAsync();
        Assert.Equal(JsonLocalStore.MaxHistoryEntries, history.Count);
        Assert.Equal("new", history[0].Id);
        Assert.DoesNotContain(history, entry => entry.Id == "old-0"); // the oldest one went
        Assert.Contains(history, entry => entry.Id == "old-1");
    }

    [Fact]
    public async Task Below_the_cap_nothing_is_dropped()
    {
        var store = new JsonLocalStore(_root);
        for (var i = 0; i < 3; i++) await store.AddHistoryAsync(Entry("item-" + i, DateTimeOffset.UtcNow.AddMinutes(-i)));
        Assert.Equal(3, (await store.LoadHistoryAsync()).Count);
    }

    private static HistoryEntry Entry(string id, DateTimeOffset date) => new(id, "Title", null, null, date, id + ".mp4", "VideoAudio", "Best available", 1, "Finished");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
