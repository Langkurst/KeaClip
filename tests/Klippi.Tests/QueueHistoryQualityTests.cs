using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

// The Library labels a record by the quality choice the download was queued with. A download
// that asked for the smallest file used to be recorded as "Best available", so the Library
// called it "Best quality".
public sealed class QueueHistoryQualityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Klippi-history-quality-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Each_quality_choice_is_recorded_in_the_pickers_words()
    {
        var store = new JsonLocalStore(_root);
        var options = new DownloadOptions { Url = new("https://example.com/video"), OutputDirectory = _root };
        await using (var queue = new DownloadQueue(new InstantDownloads(), store))
        {
            await queue.Enqueue(options with { SmallestFile = true }, "smallest").Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.Enqueue(options with { MaxHeight = 720 }, "capped").Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.Enqueue(options, "best").Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var history = await store.LoadHistoryAsync();
        Assert.Equal("Smallest available", history.Single(entry => entry.Title == "smallest").Quality);
        Assert.Equal("720p", history.Single(entry => entry.Title == "capped").Quality);
        Assert.Equal("Best available", history.Single(entry => entry.Title == "best").Quality);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class InstantDownloads : IDownloadService
    {
        public Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadResult("video.mp4", null, 10));
        public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
