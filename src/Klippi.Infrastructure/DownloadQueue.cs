using Klippi.Core;
namespace Klippi.Infrastructure;

public sealed class QueueJob
{
    internal readonly TaskCompletionSource<DownloadResult> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly CancellationTokenSource Cancellation = new();
    // A retry keeps the Id, so state callers key by it (rows, edit-after, thumbnails) carries over to the new attempt.
    public Guid Id { get; internal init; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required DownloadOptions Options { get; init; }
    public JobState State { get; internal set; } = JobState.Queued;
    public JobProgress Progress { get; internal set; } = new(JobStage.Queued);
    public DownloadResult? Result { get; internal set; }
    public string? Error { get; internal set; }
    // Remove was asked for while running: the job is stopping and leaves Snapshot as soon as it settles, unless it finished
    // with an error to show (its history could not be saved), in which case it stays listed and this returns to false.
    public bool RemovalPending { get; internal set; }
    public Task<DownloadResult> Completion => Source.Task;
}

public sealed class DownloadQueue(IDownloadService downloads, ILocalStore store, int maxConcurrent = 1) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<QueueJob> _jobs = [];
    private readonly HashSet<Task> _runningTasks = [];
    private int _maxConcurrent = Math.Clamp(maxConcurrent, 1, 8);
    private bool _paused, _disposed;
    public event Action<QueueJob>? JobChanged;
    public IReadOnlyList<QueueJob> Snapshot { get { lock (_sync) return _jobs.ToArray(); } }
    public bool IsPaused { get { lock (_sync) return _paused; } }
    public int MaxConcurrent { get { lock (_sync) return _maxConcurrent; } set { lock (_sync) _maxConcurrent = Math.Clamp(value, 1, 8); Pump(); } }
    public QueueJob Enqueue(DownloadOptions options, string title)
    {
        QueueJob job;
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); job = new QueueJob { Options = options, Title = title }; _jobs.Add(job); }
        Notify(job); Pump(); return job;
    }
    // Puts back a job saved by an earlier session at the end of the queue. A waiting job starts like any other (unless the
    // queue is paused); a failed one comes back failed with its reason, so it starts again only when Retry is chosen.
    public QueueJob Restore(DownloadOptions options, string title, string? failure = null)
    {
        QueueJob job;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            job = new QueueJob { Options = options, Title = title };
            if (failure is not null)
            {
                job.State = JobState.Failed; job.Error = failure; job.Progress = new(JobStage.Failed, Message: failure);
                job.Source.TrySetException(new MediaOperationException(failure)); _ = job.Source.Task.Exception;
            }
            _jobs.Add(job);
        }
        Notify(job); Pump(); return job;
    }
    public void Pause() { lock (_sync) _paused = true; }
    public void Resume() { lock (_sync) _paused = false; Pump(); }
    public void Cancel(Guid id)
    {
        QueueJob? job;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || job.State is JobState.Finished or JobState.Failed or JobState.Cancelled) return;
            if (job.State == JobState.Queued) MarkCancelled(job);
        }
        Signal(job); Notify(job); Pump();
    }
    // Re-queues a Failed or Cancelled job in its place under the same Id and returns the new attempt (its Completion tracks
    // that attempt; the old instance keeps its final state). Returns null for anything else (missing, removed, queued, running,
    // finished or already retried), so repeated clicks start at most one new download.
    public QueueJob? Retry(Guid id)
    {
        QueueJob retry;
        lock (_sync)
        {
            var index = _jobs.FindIndex(j => j.Id == id);
            if (index < 0 || _jobs[index] is not { State: JobState.Failed or JobState.Cancelled } old) return null;
            ObjectDisposedException.ThrowIf(_disposed, this);
            retry = new QueueJob { Id = old.Id, Title = old.Title, Options = old.Options };
            _jobs[index] = retry; old.Cancellation.Dispose();
        }
        Notify(retry); Pump(); return retry;
    }
    // Removes the job now unless it is running. A running job is marked RemovalPending (raising JobChanged) and leaves Snapshot
    // in the same step that settles it, before its Completion completes, followed by one more JobChanged. Its download is
    // cancelled, except once it has returned: the file is then already durable, so its history record is left to finish and
    // the file stays findable in the Library. A job that settles Finished with an error stays listed so the error is seen.
    public void Remove(Guid id)
    {
        QueueJob? job; bool changed = false, signal = false;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return;
            if (job.State == JobState.Running) { job.RemovalPending = changed = true; signal = job.Result is null; }
            else { if (job.State == JobState.Queued) { MarkCancelled(job); changed = signal = true; } _jobs.Remove(job); }
        }
        if (!changed) return;
        if (signal) Signal(job);
        Notify(job); Pump();
    }
    public void ClearCompleted() { lock (_sync) _jobs.RemoveAll(j => j.State is JobState.Finished or JobState.Cancelled); }
    public bool Move(Guid id, int direction)
    {
        lock (_sync)
        {
            var queued = _jobs.Where(j => j.State == JobState.Queued).ToArray();
            var index = Array.FindIndex(queued, j => j.Id == id); var target = index + Math.Sign(direction);
            if (index < 0 || target < 0 || target >= queued.Length) return false;
            var left = _jobs.IndexOf(queued[index]); var right = _jobs.IndexOf(queued[target]);
            (_jobs[left], _jobs[right]) = (_jobs[right], _jobs[left]); return true;
        }
    }
    private void Pump()
    {
        lock (_sync)
        {
            if (_disposed || _paused) return;
            while (_jobs.Count(j => j.State == JobState.Running) < _maxConcurrent)
            {
                var next = _jobs.FirstOrDefault(j => j.State == JobState.Queued);
                if (next is null) break;
                next.State = JobState.Running;
                var task = Task.Run(() => RunAsync(next)); _runningTasks.Add(task);
                _ = task.ContinueWith(t => { lock (_sync) _runningTasks.Remove(t); }, TaskScheduler.Default);
            }
        }
    }
    private async Task RunAsync(QueueJob job)
    {
        try
        {
            Notify(job);
            var result = await downloads.DownloadAsync(job.Options, new InlineProgress(progress => { lock (_sync) job.Progress = progress; Notify(job); }), job.Cancellation.Token).ConfigureAwait(false);
            // A returned result has already been verified and promoted to its final path.
            // Cancellation during optional bookkeeping must not hide that durable success.
            lock (_sync) job.Result = result;
            try
            {
                // Items of one post share the post's address; the entry position keeps them apart.
                var playlistItems = string.IsNullOrWhiteSpace(job.Options.PlaylistItems) ? null : job.Options.PlaylistItems.Trim();
                await store.AddHistoryAsync(new HistoryEntry(job.Id.ToString(), result.Media?.Title ?? job.Title, MediaValidation.SafeSourceUrl(job.Options.Url), result.Media?.Source, DateTimeOffset.UtcNow, result.FilePath, job.Options.Mode.ToString(), RecordedQuality(job.Options), result.SizeBytes, "Finished", playlistItems), job.Cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { job.Error = "Download finished, but history could not be saved: " + MediaValidation.Redact(ex.Message); }
            Settle(job, JobState.Finished, new(JobStage.Finished, 100));
            job.Source.TrySetResult(result);
        }
        catch (OperationCanceledException) { Settle(job, JobState.Cancelled, new(JobStage.Cancelled)); job.Source.TrySetCanceled(); }
        catch (Exception ex) { var error = MediaValidation.Redact(ex.Message); Settle(job, JobState.Failed, new(JobStage.Failed, Message: error), error); job.Source.TrySetException(ex); _ = job.Source.Task.Exception; }
        finally { Notify(job); Pump(); }
    }
    // The quality choice the record keeps, in the picker's own words, so the Library says
    // "Smallest file" for a download that asked for the smallest file instead of "Best quality".
    internal static string RecordedQuality(DownloadOptions options) => options.MaxHeight is { } height ? height + "p" : options.SmallestFile ? "Smallest available" : "Best available";
    // The final state and a pending removal land together, so a removed job is gone from Snapshot before Completion settles.
    // A finished download whose history could not be saved is kept: it is in neither the Library nor history otherwise.
    private void Settle(QueueJob job, JobState state, JobProgress progress, string? error = null)
    {
        lock (_sync)
        {
            job.State = state; job.Progress = progress; if (error is not null) job.Error = error;
            if (!job.RemovalPending) return;
            if (state == JobState.Finished && job.Error is not null) job.RemovalPending = false; else _jobs.Remove(job);
        }
    }
    private static void MarkCancelled(QueueJob job) { job.State = JobState.Cancelled; job.Progress = new(JobStage.Cancelled); job.Source.TrySetCanceled(); }
    // Signalled outside _sync: a download that stops inline must not raise JobChanged while the lock is held.
    private static void Signal(QueueJob job) { try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* Already retired by Retry or disposal. */ } }
    private void Notify(QueueJob job)
    {
        if (JobChanged is { } changed)
            foreach (Action<QueueJob> callback in changed.GetInvocationList())
                try { callback(job); } catch (Exception) { }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _jobs) { job.Cancellation.Cancel(); if (job.State == JobState.Queued) { job.State = JobState.Cancelled; job.Source.TrySetCanceled(); } }
            tasks = _runningTasks.ToArray();
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (_sync) foreach (var job in _jobs) job.Cancellation.Dispose();
    }
    private sealed class InlineProgress(Action<JobProgress> callback) : IProgress<JobProgress> { public void Report(JobProgress value) => callback(value); }
}
