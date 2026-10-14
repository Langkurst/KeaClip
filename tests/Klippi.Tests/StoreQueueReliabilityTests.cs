using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;
namespace Klippi.Tests;

public sealed class StoreQueueReliabilityTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Klippi-storequeue-" + Guid.NewGuid().ToString("N"));
    public StoreQueueReliabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string History => Path.Combine(_root, "history.json");
    private static HistoryEntry Entry(string id, DateTimeOffset? date = null) => new(id, "Title " + id, null, null, date ?? DateTimeOffset.UtcNow, "C:/media/" + id + ".mp4", "Video", "Best", 4, "Finished");

    [Fact] public async Task UnreadableHistoryWithoutBackupIsKeptAsideAndANewHistoryStarts()
    {
        var store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("a"));
        Assert.False(File.Exists(History + ".backup"));
        await File.WriteAllTextAsync(History, "[{\"Id\":\"a\",");
        Assert.Empty(await store.LoadHistoryAsync());
        var kept = Assert.Single(Directory.GetFiles(_root, "history.json.corrupt-*"));
        Assert.Equal("[{\"Id\":\"a\",", await File.ReadAllTextAsync(kept));
        Assert.Contains("history", store.LastWarning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFileName(kept), store.LastWarning!);
        await store.ClearHistoryAsync(); await store.AddHistoryAsync(Entry("b")); await store.PruneHistoryAsync(30);
        Assert.Equal("b", Assert.Single(await new JsonLocalStore(_root).LoadHistoryAsync()).Id);
        Assert.Single(Directory.GetFiles(_root, "history.json.corrupt-*"));
    }
    [Fact] public async Task UnreadablePrimaryAndBackupDoNotBlockClearAddOrPrune()
    {
        var store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("a")); await store.AddHistoryAsync(Entry("b"));
        await File.WriteAllTextAsync(History, "x"); await File.WriteAllTextAsync(History + ".backup", "y");
        await store.ClearHistoryAsync();
        Assert.Empty(await store.LoadHistoryAsync());
        Assert.Equal("x", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_root, "history.json.corrupt-*"))));
        Assert.Equal("y", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_root, "history.json.backup.corrupt-*"))));
        await store.AddHistoryAsync(Entry("c")); await store.PruneHistoryAsync(30);
        Assert.Equal("c", Assert.Single(await new JsonLocalStore(_root).LoadHistoryAsync()).Id);
    }
    [Fact] public async Task MissingHistoryWithUnreadableBackupStartsFreshAndKeepsTheBackup()
    {
        await File.WriteAllTextAsync(History + ".backup", "{broken");
        var store = new JsonLocalStore(_root);
        await store.AddHistoryAsync(Entry("a"));
        Assert.Equal("a", Assert.Single(await store.LoadHistoryAsync()).Id);
        Assert.Equal("{broken", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_root, "history.json.backup.corrupt-*"))));
    }
    [Fact] public async Task LockedHistoryIsNotMistakenForDamageAndIsNeverMoved()
    {
        var store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("a"));
        await using (new FileStream(History, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => store.LoadHistoryAsync());
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
        Assert.Equal("a", Assert.Single(await store.LoadHistoryAsync()).Id);
    }
    [Fact] public async Task UnreadableSettingsAreStillReportedAndLeftInPlace()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "broken"); await File.WriteAllTextAsync(History, "broken");
        var store = new JsonLocalStore(_root);
        Assert.Empty(await store.LoadHistoryAsync());
        await Assert.ThrowsAsync<MediaOperationException>(() => store.LoadSettingsAsync());
        Assert.Equal("broken", await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
        Assert.Empty(Directory.GetFiles(_root, "settings.json.corrupt-*"));
    }
    [Fact] public async Task GoodHistoryBackupIsStillPreferredOverStartingFresh()
    {
        var store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("a")); await store.AddHistoryAsync(Entry("b"));
        await File.WriteAllTextAsync(History, "broken");
        Assert.Equal("a", Assert.Single(await store.LoadHistoryAsync()).Id);
        Assert.Contains("recovered", store.LastWarning!);
        Assert.Contains("history", store.LastWarning!, StringComparison.OrdinalIgnoreCase);
        await store.AddHistoryAsync(Entry("c"));
        Assert.Equal(2, (await new JsonLocalStore(_root).LoadHistoryAsync()).Count);
        Assert.Equal("broken", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_root, "history.json.corrupt-*"))));
    }
    [Theory][InlineData(1_000_000)][InlineData(int.MaxValue)][InlineData(36_500)]
    public async Task HugeRetentionNeverThrowsAndKeepsHistory(int days)
    {
        var store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("old", DateTimeOffset.UtcNow.AddYears(-50))); await store.AddHistoryAsync(Entry("new"));
        await store.PruneHistoryAsync(days);
        Assert.Equal(2, (await store.LoadHistoryAsync()).Count);
    }
    [Fact] public async Task RetentionIsBoundedWhenSaving()
    {
        var store = new JsonLocalStore(_root);
        await Assert.ThrowsAsync<MediaOperationException>(() => store.SaveSettingsAsync(new() { HistoryRetentionDays = 36_501 }));
        await Assert.ThrowsAsync<MediaOperationException>(() => store.SaveSettingsAsync(new() { HistoryRetentionDays = 0 }));
        await store.SaveSettingsAsync(new() { HistoryRetentionDays = 36_500 });
        Assert.Equal(36_500, (await store.LoadSettingsAsync()).HistoryRetentionDays);
        await Assert.ThrowsAsync<MediaOperationException>(() => store.PruneHistoryAsync(0));
    }
    [Fact] public async Task RecoveryNoticeReachesCallersThroughTheInterfaceOnce()
    {
        ILocalStore store = new JsonLocalStore(_root);
        await store.SaveSettingsAsync(new() { Theme = "Dark" }); await store.SaveSettingsAsync(new() { Theme = "Light" });
        Assert.Null(store.TakeWarning());
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{corrupt");
        Assert.Equal("Dark", (await store.LoadSettingsAsync()).Theme);
        Assert.Contains("settings", store.LastWarning!);
        var notice = store.TakeWarning();
        Assert.Contains("settings", notice!); Assert.Contains("recovered", notice!);
        Assert.Null(store.TakeWarning()); Assert.Null(store.LastWarning);
        await File.WriteAllTextAsync(History, "broken");
        await store.LoadHistoryAsync();
        Assert.Contains("new one was started", store.TakeWarning()!);
        ILocalStore plain = new MemoryStore();
        Assert.Null(plain.LastWarning); Assert.Null(plain.TakeWarning());
        Assert.Equal(36_500, ILocalStore.MaxHistoryRetentionDays);
    }
    [Fact] public async Task PruneStillRemovesOnlyExpiredRecords()
    {
        var store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("old", DateTimeOffset.UtcNow.AddDays(-40))); await store.AddHistoryAsync(Entry("new"));
        await store.PruneHistoryAsync(30);
        Assert.Equal("new", Assert.Single(await store.LoadHistoryAsync()).Id);
    }
    [Fact] public async Task NoticesAreKeptUntilTakenSoOneDoesNotHideAnother()
    {
        var settings = Path.Combine(_root, "settings.json");
        ILocalStore store = new JsonLocalStore(_root);
        await store.SaveSettingsAsync(new() { Theme = "Dark" }); await store.SaveSettingsAsync(new() { Theme = "Light" }); await store.AddHistoryAsync(Entry("a"));
        await File.WriteAllTextAsync(settings, "{corrupt"); await File.WriteAllTextAsync(History, "broken");
        // The startup order: settings (recovered from backup), then prune and load history (set aside, no backup).
        Assert.Equal("Dark", (await store.LoadSettingsAsync()).Theme); Assert.Equal("Dark", (await store.LoadSettingsAsync()).Theme);
        await store.PruneHistoryAsync(30); Assert.Empty(await store.LoadHistoryAsync());
        var notice = store.TakeWarning()!;
        Assert.Contains("saved settings", notice); Assert.Contains("recovered", notice);
        Assert.Contains("download history", notice); Assert.Contains("new one was started", notice);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(notice, "saved settings"));
        Assert.Null(store.TakeWarning());
    }
    [Fact] public async Task PartlyLockedHistoryPairIsSetAsideWholeOrNotAtAll()
    {
        var store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("a")); await store.AddHistoryAsync(Entry("b"));
        await File.WriteAllTextAsync(History, "x"); await File.WriteAllTextAsync(History + ".backup", "y");
        // A sync client or scanner holding only the backup: it can be read but not moved.
        await using (new FileStream(History + ".backup", FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => store.LoadHistoryAsync());
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
        Assert.Equal("x", await File.ReadAllTextAsync(History)); Assert.Equal("y", await File.ReadAllTextAsync(History + ".backup"));
        Assert.Null(store.LastWarning);
        Assert.Empty(await store.LoadHistoryAsync());
        var primary = Assert.Single(Directory.GetFiles(_root, "history.json.corrupt-*")); var backup = Assert.Single(Directory.GetFiles(_root, "history.json.backup.corrupt-*"));
        Assert.Equal("x", await File.ReadAllTextAsync(primary)); Assert.Equal("y", await File.ReadAllTextAsync(backup));
        var notice = store.TakeWarning()!;
        Assert.Contains(Path.GetFileName(primary), notice); Assert.Contains(Path.GetFileName(backup), notice);
    }
    [Fact] public async Task RetentionAboveTheMaximumIsNamedWhenSavingAndLoadsAsTheMaximum()
    {
        var store = new JsonLocalStore(_root);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => store.SaveSettingsAsync(new() { HistoryRetentionDays = 36_501 }));
        Assert.Contains("retention", error.Message, StringComparison.OrdinalIgnoreCase); Assert.Contains("36500", error.Message);
        // An older version could save any positive value; it must not block saving unrelated settings later.
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new AppSettings { HistoryRetentionDays = 1_000_000, Theme = "Dark" }));
        var loaded = await store.LoadSettingsAsync();
        Assert.Equal(ILocalStore.MaxHistoryRetentionDays, loaded.HistoryRetentionDays); Assert.Equal("Dark", loaded.Theme);
        await store.SaveSettingsAsync(loaded with { Theme = "Light" });
        var saved = await new JsonLocalStore(_root).LoadSettingsAsync();
        Assert.Equal("Light", saved.Theme); Assert.Equal(ILocalStore.MaxHistoryRetentionDays, saved.HistoryRetentionDays);
    }
    [Fact] public async Task TheSameDamageIsReportedOnceUntilASaveRepairsIt()
    {
        // The desktop takes notices after every operation, and a damaged primary with a good backup is recovered on every
        // read until something saves, so each reload must not bring the same notice back.
        ILocalStore store = new JsonLocalStore(_root); await store.AddHistoryAsync(Entry("a")); await store.AddHistoryAsync(Entry("b"));
        await File.WriteAllTextAsync(History, "broken");
        Assert.Equal("a", Assert.Single(await store.LoadHistoryAsync()).Id);
        Assert.Contains("recovered", store.TakeWarning()!);
        Assert.Equal("a", Assert.Single(await store.LoadHistoryAsync()).Id); await store.LoadHistoryAsync();
        Assert.Null(store.TakeWarning()); Assert.Null(store.LastWarning);
        // A save repairs the file; damage found after that is a new episode and is reported again, once.
        await store.AddHistoryAsync(Entry("c")); Assert.Null(store.TakeWarning());
        Assert.Equal(2, (await store.LoadHistoryAsync()).Count); Assert.Null(store.TakeWarning());
        await File.WriteAllTextAsync(History, "broken again");
        Assert.Equal("a", Assert.Single(await store.LoadHistoryAsync()).Id);
        Assert.Contains("recovered", store.TakeWarning()!);
        await store.LoadHistoryAsync(); Assert.Null(store.TakeWarning());
        // A missing primary is recovered from its backup on every read too; settings follow the same rule.
        File.Delete(History); await store.LoadHistoryAsync();
        Assert.Contains("missing", store.TakeWarning()!);
        await store.LoadHistoryAsync(); Assert.Null(store.TakeWarning());
        await store.SaveSettingsAsync(new() { Theme = "Dark" }); await store.SaveSettingsAsync(new() { Theme = "Light" });
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{corrupt");
        Assert.Equal("Dark", (await store.LoadSettingsAsync()).Theme); Assert.Contains("settings", store.TakeWarning()!);
        Assert.Equal("Dark", (await store.LoadSettingsAsync()).Theme); Assert.Null(store.TakeWarning());
        // A fresh store (the next launch) reports damage that is still there, once.
        ILocalStore next = new JsonLocalStore(_root);
        await next.LoadSettingsAsync(); await next.LoadHistoryAsync(); await next.LoadSettingsAsync(); await next.LoadHistoryAsync();
        var notice = next.TakeWarning()!;
        Assert.Contains("settings", notice); Assert.Contains("history", notice);
        Assert.Null(next.TakeWarning());
    }
    [Fact] public async Task NullHistoryRecordsDoNotBlockAddOrPrune()
    {
        await File.WriteAllTextAsync(History, "[null]");
        var store = new JsonLocalStore(_root);
        Assert.Empty(await store.LoadHistoryAsync());
        await store.AddHistoryAsync(Entry("a")); await store.PruneHistoryAsync(30); await store.RemoveHistoryAsync("missing");
        Assert.Equal("a", Assert.Single(await new JsonLocalStore(_root).LoadHistoryAsync()).Id);
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*")); Assert.Null(store.LastWarning);
    }

    [Fact] public async Task RetryIsIdempotentAndKeepsTheJobsIdentityAndPlace()
    {
        var fake = new ScriptedDownloads(); fake.Outcomes.Enqueue(Outcome.Fail); fake.Outcomes.Enqueue(Outcome.Succeed);
        await using var queue = new DownloadQueue(fake, new MemoryStore());
        var failed = queue.Enqueue(Options(), "clip");
        await Assert.ThrowsAsync<MediaOperationException>(() => failed.Completion.WaitAsync(Wait));
        queue.Pause();
        var later = queue.Enqueue(Options(), "later");
        var retried = queue.Retry(failed.Id);
        Assert.NotNull(retried);
        Assert.Null(queue.Retry(failed.Id));
        Assert.Equal(failed.Id, retried!.Id); Assert.Equal("clip", retried.Title); Assert.Same(failed.Options, retried.Options);
        Assert.Equal([failed.Id, later.Id], queue.Snapshot.Select(j => j.Id));
        Assert.Equal(JobState.Queued, queue.Snapshot[0].State); Assert.Equal(JobState.Failed, failed.State);
        queue.Resume();
        Assert.Equal("test.mp4", (await retried.Completion.WaitAsync(Wait)).FilePath);
        Assert.Null(queue.Retry(failed.Id));
        await later.Completion.WaitAsync(Wait);
        Assert.Equal(3, fake.Starts);
        Assert.Equal(2, queue.Snapshot.Count);
    }
    [Fact] public async Task RetryRefusesJobsThatAreQueuedRunningOrFinished()
    {
        var fake = new ScriptedDownloads(); fake.Outcomes.Enqueue(Outcome.Block); fake.Outcomes.Enqueue(Outcome.Succeed);
        await using var queue = new DownloadQueue(fake, new MemoryStore());
        var running = queue.Enqueue(Options(), "running"); var queued = queue.Enqueue(Options(), "queued");
        await fake.Started.Task.WaitAsync(Wait);
        Assert.Null(queue.Retry(running.Id)); Assert.Null(queue.Retry(queued.Id)); Assert.Null(queue.Retry(Guid.NewGuid()));
        fake.Release.TrySetResult();
        await Task.WhenAll(running.Completion, queued.Completion).WaitAsync(Wait);
        Assert.Null(queue.Retry(running.Id));
        Assert.Equal(2, queue.Snapshot.Count); Assert.Equal(2, fake.Starts);
    }
    [Fact] public async Task CancelledJobCanBeRetriedOnce()
    {
        var fake = new ScriptedDownloads(); fake.Outcomes.Enqueue(Outcome.Succeed);
        await using var queue = new DownloadQueue(fake, new MemoryStore()); queue.Pause();
        var job = queue.Enqueue(Options(), "cancelled"); queue.Cancel(job.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(Wait));
        var retried = queue.Retry(job.Id); Assert.Null(queue.Retry(job.Id));
        queue.Resume();
        await retried!.Completion.WaitAsync(Wait);
        Assert.Equal(JobState.Finished, Assert.Single(queue.Snapshot).State); Assert.Equal(1, fake.Starts);
    }
    [Fact] public async Task RemovingARunningJobCancelsItAndDropsTheRowOnceItStops()
    {
        var fake = new ScriptedDownloads(); fake.Outcomes.Enqueue(Outcome.SlowCancel);
        await using var queue = new DownloadQueue(fake, new MemoryStore());
        var job = queue.Enqueue(Options(), "running");
        await fake.Started.Task.WaitAsync(Wait);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<(JobState State, bool Pending, bool Listed)>();
        queue.JobChanged += j => changes.Enqueue((j.State, j.RemovalPending, queue.Snapshot.Any(s => s.Id == j.Id)));
        queue.Remove(job.Id);
        await fake.CancellationSeen.Task.WaitAsync(Wait);
        Assert.Contains(queue.Snapshot, j => j.Id == job.Id);
        Assert.True(job.RemovalPending); Assert.Contains((JobState.Running, true, true), changes);
        fake.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(Wait));
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.DoesNotContain(queue.Snapshot, j => j.Id == job.Id);
        Assert.Null(queue.Retry(job.Id));
        await queue.DisposeAsync();
        Assert.Contains((JobState.Cancelled, true, false), changes);
    }
    [Fact] public async Task RemovingAQueuedJobReleasesItsAwaitersAndNeverStartsIt()
    {
        var fake = new ScriptedDownloads();
        await using var queue = new DownloadQueue(fake, new MemoryStore()); queue.Pause();
        var job = queue.Enqueue(Options(), "queued");
        queue.Remove(job.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(Wait));
        Assert.Empty(queue.Snapshot); queue.Resume(); Assert.Equal(0, fake.Starts);
    }
    [Fact] public async Task RemovingAJobWhoseDownloadAlreadyReturnedLetsItsHistoryFinish()
    {
        var store = new GatedHistory();
        await using var queue = new DownloadQueue(new ScriptedDownloads(), store);
        var job = queue.Enqueue(Options(), "saved");
        await store.Entered.Task.WaitAsync(Wait);
        queue.Remove(job.Id);
        Assert.True(job.RemovalPending); Assert.Contains(queue.Snapshot, j => j.Id == job.Id);
        store.Release.TrySetResult();
        Assert.Equal("test.mp4", (await job.Completion.WaitAsync(Wait)).FilePath);
        // The file is durable and recorded, so it stays findable in the Library while the row leaves as asked.
        Assert.Equal(JobState.Finished, job.State); Assert.Null(job.Error);
        Assert.Equal(job.Id.ToString(), Assert.Single(store.Saved).Id);
        Assert.DoesNotContain(queue.Snapshot, j => j.Id == job.Id);
    }
    [Fact] public async Task RemovalThatCancelsHistoryKeepsTheFinishedRowAndItsError()
    {
        var fake = new ScriptedDownloads(); fake.Outcomes.Enqueue(Outcome.IgnoreCancel);
        var store = new GatedHistory(); store.Release.TrySetResult();
        await using var queue = new DownloadQueue(fake, store);
        var job = queue.Enqueue(Options(), "finished late");
        await fake.Started.Task.WaitAsync(Wait);
        // Remove reaches the job just before its download returns, so the cancellation lands on the history record instead.
        queue.Remove(job.Id); Assert.True(job.RemovalPending);
        fake.Release.TrySetResult();
        Assert.Equal("test.mp4", (await job.Completion.WaitAsync(Wait)).FilePath);
        Assert.Equal(JobState.Finished, job.State); Assert.Contains("history", job.Error!); Assert.Empty(store.Saved);
        Assert.Contains(queue.Snapshot, j => j.Id == job.Id); Assert.False(job.RemovalPending);
        queue.Remove(job.Id);
        Assert.DoesNotContain(queue.Snapshot, j => j.Id == job.Id);
    }

    private static DownloadOptions Options() => new() { Url = new("https://youtube.com/watch?v=public"), OutputDirectory = Path.GetTempPath() };
    private enum Outcome { Succeed, Fail, Block, SlowCancel, IgnoreCancel }
    private sealed class ScriptedDownloads : IDownloadService
    {
        public System.Collections.Concurrent.ConcurrentQueue<Outcome> Outcomes { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
        public async Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Starts); Started.TrySetResult();
            switch (Outcomes.TryDequeue(out var outcome) ? outcome : Outcome.Succeed)
            {
                case Outcome.Fail: await Task.Yield(); throw new MediaOperationException("Network went away.");
                case Outcome.Block: await Release.Task.WaitAsync(cancellationToken); break;
                case Outcome.SlowCancel:
                    // Like a real process: the token fires, but the download only stops after cleanup finishes.
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { CancellationSeen.TrySetResult(); await Release.Task; throw; }
                    break;
                // The file was already promoted when the token fired, so the download still returns its result.
                case Outcome.IgnoreCancel: await Release.Task; break;
                default: await Task.Yield(); break;
            }
            return new("test.mp4", null, 10);
        }
        public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
    private sealed class MemoryStore : ILocalStore
    {
        private readonly List<HistoryEntry> _history = [];
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken = default) { lock (_history) return Task.FromResult<IReadOnlyList<HistoryEntry>>(_history.ToArray()); }
        public Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default) { lock (_history) { _history.RemoveAll(x => x.Id == entry.Id); _history.Add(entry); } return Task.CompletedTask; }
        public Task RemoveHistoryAsync(string id, CancellationToken cancellationToken = default) { lock (_history) _history.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
        public Task ClearHistoryAsync(CancellationToken cancellationToken = default) { lock (_history) _history.Clear(); return Task.CompletedTask; }
        public Task PruneHistoryAsync(int? retentionDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    // History saving that waits for the test and honours cancellation like JsonLocalStore's lock and writes do.
    private sealed class GatedHistory : ILocalStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<HistoryEntry> Saved { get; } = new();
        public async Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); Saved.Enqueue(entry);
        }
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>(Saved.ToArray());
        public Task RemoveHistoryAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PruneHistoryAsync(int? retentionDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
