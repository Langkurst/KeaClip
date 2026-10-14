using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

// The saved queue file and putting saved jobs back into the queue.
public sealed class QueuePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Klippi-saved-queue-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task The_queue_file_round_trips_and_keeps_a_backup_of_the_previous_copy()
    {
        var store = new JsonLocalStore(_root);
        Assert.Null(await store.LoadQueueAsync());
        var first = new SavedQueue { Paused = true, Jobs = [Job("https://example.com/a", "A"), new SavedQueueJob { LinkWithheld = true }] };
        await store.SaveQueueAsync(first);
        var second = new SavedQueue { Jobs = [Job("https://example.com/b", "B") with { Failed = true, Error = "Stopped.", ShareWhenFinished = true, EditAfter = true, SupersedesRecordId = "record-1" }] };
        await store.SaveQueueAsync(second);

        var loaded = await new JsonLocalStore(_root).LoadQueueAsync();
        Assert.NotNull(loaded);
        Assert.False(loaded.Paused);
        var job = Assert.Single(loaded.Jobs)!;
        Assert.Equal("B", job.Title); Assert.Equal("https://example.com/b", job.Options!.Url.AbsoluteUri); Assert.Equal(_root, job.Options.OutputDirectory);
        Assert.True(job.Failed); Assert.Equal("Stopped.", job.Error); Assert.True(job.ShareWhenFinished); Assert.True(job.EditAfter); Assert.Equal("record-1", job.SupersedesRecordId);
        Assert.True(File.Exists(Path.Combine(_root, "queue.json.backup")));
        Assert.Null(store.TakeWarning());
    }

    [Fact]
    public async Task A_damaged_queue_file_is_recovered_from_its_backup_with_a_notice()
    {
        var store = new JsonLocalStore(_root);
        await store.SaveQueueAsync(new SavedQueue { Jobs = [Job("https://example.com/a", "A")] });
        await store.SaveQueueAsync(new SavedQueue { Jobs = [Job("https://example.com/b", "B")] });
        await File.WriteAllTextAsync(Path.Combine(_root, "queue.json"), "{ damaged");

        var reader = new JsonLocalStore(_root);
        var loaded = await reader.LoadQueueAsync();
        Assert.Equal("A", Assert.Single(loaded!.Jobs)!.Title);
        Assert.Contains("Your saved queue could not be read", reader.TakeWarning());
    }

    [Fact]
    public async Task An_unreadable_queue_file_is_set_aside_not_deleted_and_reported_once()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "queue.json"), "not json at all");
        await File.WriteAllTextAsync(Path.Combine(_root, "queue.json.backup"), "[1, 2");

        var store = new JsonLocalStore(_root);
        Assert.Null(await store.LoadQueueAsync());
        var notice = store.TakeWarning();
        Assert.Contains("Your saved queue could not be read, so a new one was started", notice);
        Assert.Single(Directory.GetFiles(_root, "queue.json.corrupt-*"));
        Assert.Single(Directory.GetFiles(_root, "queue.json.backup.corrupt-*"));
        Assert.Equal("not json at all", await File.ReadAllTextAsync(Directory.GetFiles(_root, "queue.json.corrupt-*")[0]));

        Assert.Null(await store.LoadQueueAsync());
        Assert.Null(store.TakeWarning());
        // The empty queue can be saved again afterwards.
        await store.SaveQueueAsync(new SavedQueue());
        Assert.Empty((await store.LoadQueueAsync())!.Jobs);
    }

    [Fact]
    public async Task Restored_waiting_jobs_wait_while_paused_and_then_start_in_order_within_the_limit()
    {
        var downloads = new RecordingDownloads();
        await using var queue = new DownloadQueue(downloads, new JsonLocalStore(_root), 1);
        queue.Pause();
        var a = queue.Restore(Options("https://example.com/a"), "A");
        var b = queue.Restore(Options("https://example.com/b"), "B");
        Assert.Equal(JobState.Queued, a.State); Assert.Equal(JobState.Queued, b.State);
        Assert.Empty(downloads.Started);

        queue.Resume();
        await downloads.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["https://example.com/a"], downloads.Started);
        downloads.Release.TrySetResult();
        await Task.WhenAll(a.Completion, b.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["https://example.com/a", "https://example.com/b"], downloads.Started);
    }

    [Fact]
    public async Task A_restored_failure_stays_failed_until_Retry()
    {
        var downloads = new RecordingDownloads(); downloads.Release.TrySetResult();
        await using var queue = new DownloadQueue(downloads, new JsonLocalStore(_root), 1);
        var failed = queue.Restore(Options("https://example.com/a"), "A", "The site refused the request.");
        Assert.Equal(JobState.Failed, failed.State); Assert.Equal("The site refused the request.", failed.Error);
        await Assert.ThrowsAsync<MediaOperationException>(() => failed.Completion);
        await Task.Delay(50);
        Assert.Empty(downloads.Started);

        var retry = queue.Retry(failed.Id);
        Assert.NotNull(retry);
        await retry.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["https://example.com/a"], downloads.Started);
    }

    private SavedQueueJob Job(string url, string title) => new() { Title = title, Options = Options(url) };
    private DownloadOptions Options(string url) => new() { Url = new Uri(url), OutputDirectory = _root };

    private sealed class RecordingDownloads : IDownloadService
    {
        private readonly object _sync = new();
        private readonly List<string> _started = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> Started { get { lock (_sync) return _started.ToArray(); } }
        public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            lock (_sync) _started.Add(options.Url.AbsoluteUri);
            FirstStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new("test.mp4", null, 10);
        }
        public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
