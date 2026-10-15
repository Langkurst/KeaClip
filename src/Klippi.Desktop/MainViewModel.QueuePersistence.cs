using Avalonia.Threading;
using Klippi.Core;
using Klippi.Infrastructure;

namespace Klippi.Desktop;

// The queue survives a restart. Waiting, running and failed jobs are saved (with their options and
// the choices made for them: share or edit when finished, the record a Replace supersedes) in the
// app-owned queue file next to the settings and history, whenever that list changes; progress
// updates never write. Running jobs are saved as waiting, so they start again next time. Finished
// jobs are in the history and cancelled ones were stopped on purpose, so neither is kept.
//
// Privacy: only a link that names the media without private values is saved, in the same reduced
// form the history keeps (MediaValidation.SafeSourceUrl). A link that needs a sign-in, session or
// signature value to work is never written; the file only counts it, so the next start can say why
// it is missing. Cookies and passwords are never part of a job (sign-in is read from the browser
// when the download runs).
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _queueSaveGate = new(1, 1);
    private bool _queueRestoreDone, _queuePersistenceBlocked, _queueSaveFailureShown;
    // What the queue file holds (or is being written with): null until the first save, and after a failed save so the next change retries.
    private (Guid Id, bool Failed, QueueJob Job)[]? _savedQueueShape;
    private bool _savedQueuePaused;
    private long _queueSaveVersion, _queueWrittenVersion;
    private Task _queueSaveTask = Task.CompletedTask;

    /// <summary>The latest queue-file write (tests wait for it).</summary>
    internal Task QueueSaveTask => _queueSaveTask;

    private static bool KeepsAcrossRestart(QueueJob job) => !job.RemovalPending && job.State is JobState.Queued or JobState.Running or JobState.Paused or JobState.Failed;

    /// <summary>Writes the queue file when the saved list (jobs, order, failures or the pause) has changed.</summary>
    private void PersistQueueIfChanged()
    {
        if (!_queueRestoreDone || _queuePersistenceBlocked || _disposed) return;
        if (QueueToSave() is not { } queue) return;
        var version = ++_queueSaveVersion;
        _queueSaveTask = Task.Run(() => WriteQueueAsync(queue, version));
    }

    private SavedQueue? QueueToSave()
    {
        var jobs = _queue.Snapshot.Where(KeepsAcrossRestart).ToArray();
        var shape = jobs.Select(job => (job.Id, job.State == JobState.Failed, job)).ToArray();
        if (_savedQueueShape is { } saved && _savedQueuePaused == _queuePaused && shape.SequenceEqual(saved)) return null;
        _savedQueueShape = shape; _savedQueuePaused = _queuePaused;
        return new SavedQueue { Paused = _queuePaused, Jobs = jobs.Select(SavedJob).ToArray() };
    }

    private SavedQueueJob SavedJob(QueueJob job)
    {
        if (!MediaValidation.SafeSourceUrlIsExact(job.Options.Url)) return new SavedQueueJob { LinkWithheld = true };
        var failed = job.State == JobState.Failed;
        return new SavedQueueJob
        {
            Title = job.Title,
            Options = job.Options with { Url = new Uri(MediaValidation.SafeSourceUrl(job.Options.Url)) },
            Failed = failed,
            Error = failed ? MediaValidation.Redact(job.Error) : null,
            ShareWhenFinished = _shareAfter.Contains(job.Id),
            EditAfter = _editAfter.Contains(job.Id),
            SupersedesRecordId = _supersededRecords.TryGetValue(job.Id, out var record) ? record.Id : null,
        };
    }

    // Writes run one at a time; a write that has been overtaken by a newer list is skipped, so an older list never lands last.
    private async Task WriteQueueAsync(SavedQueue queue, long version)
    {
        await _queueSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (version <= _queueWrittenVersion) return;
            await _services.Store.SaveQueueAsync(queue).ConfigureAwait(false);
            _queueWrittenVersion = version;
        }
        catch (Exception error) { Dispatcher.UIThread.Post(() => ReportQueueSaveFailure(error)); }
        finally { _queueSaveGate.Release(); }
    }

    private void ReportQueueSaveFailure(Exception error)
    {
        if (_disposed) return;
        _savedQueueShape = null; // The next change tries again with the whole list.
        CaptureDiagnostics(error);
        if (_queueSaveFailureShown) return;
        _queueSaveFailureShown = true;
        Status = "Your queue could not be saved, so unfinished downloads may not come back after a restart. " + FriendlyError(error);
    }

    // Closing does not wait for background work, so the final list is written now (briefly blocking) before jobs are cancelled.
    private void SaveQueueBeforeExit()
    {
        if (!_queueRestoreDone || _queuePersistenceBlocked) return;
        try
        {
            if (QueueToSave() is { } queue)
            {
                var version = ++_queueSaveVersion;
                _queueSaveTask = Task.Run(() => WriteQueueAsync(queue, version));
            }
            _queueSaveTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception error) { CaptureDiagnostics(error); }
    }

    /// <summary>
    /// Puts back the jobs saved by the last session, in their order. Waiting (and interrupted) jobs start as usual within the
    /// concurrency limit unless the queue was paused; failed jobs come back failed with their reason and Retry. A job whose
    /// folder is gone, or whose link was not saved, is dropped and counted in <paramref name="notes"/>.
    /// </summary>
    private async Task RestoreQueueAsync(CancellationToken ct, List<string> notes)
    {
        SavedQueue? saved;
        try { saved = await _services.Store.LoadQueueAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Damaged content is set aside by the store (and reported there). This is a file that could not be opened at
            // all (locked or no access): it is left untouched, and this session never writes over it.
            CaptureDiagnostics(error); _queuePersistenceBlocked = true;
            notes.Add("Your saved queue could not be opened, so it was left as it is. Downloads you add now will not come back after a restart.");
            return;
        }
        var entries = saved?.Jobs.OfType<SavedQueueJob>().ToArray() ?? [];
        // A sleeping network or USB drive can stall a folder check, so they run off the UI thread.
        var folders = await Task.Run(() => entries.Select(entry => entry.Options?.OutputDirectory is { } folder && FolderExists(folder)).ToArray(), ct);
        int missingFolder = 0, withheld = 0, unreadable = (saved?.Jobs.Count ?? 0) - entries.Length;
        var accepted = new List<(SavedQueueJob Entry, DownloadOptions Options)>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.LinkWithheld) { withheld++; continue; }
            if (entry.Options is not { Url: { } link } options || !MediaValidation.TryMediaUrl(link.OriginalString, out var url) || string.IsNullOrWhiteSpace(options.OutputDirectory) || !Path.IsPathFullyQualified(options.OutputDirectory)) { unreadable++; continue; }
            if (!folders[i]) { missingFolder++; continue; }
            accepted.Add((entry, options with { Url = url! }));
        }
        var paused = saved?.Paused == true && accepted.Any(item => !item.Entry.Failed);
        if (paused && !_queuePaused) { _queue.Pause(); QueuePaused = true; }
        foreach (var (entry, options) in accepted)
        {
            var title = string.IsNullOrWhiteSpace(entry.Title) ? options.Url.Host + " · saved download" : entry.Title.Trim();
            var failure = !entry.Failed ? null : MediaValidation.Redact(entry.Error) is { Length: > 0 } reason ? reason : "This download stopped before KeaClip closed. Retry starts it again.";
            // Restore starts a waiting job straight away; its choices are attached before the UI thread can handle any of its events.
            var job = _queue.Restore(options, title, failure);
            if (entry.ShareWhenFinished) _shareAfter.Add(job.Id);
            if (entry.EditAfter) _editAfter.Add(job.Id);
            if (entry.SupersedesRecordId is { } recordId && _history.FirstOrDefault(record => record.Id == recordId) is { } superseded) _supersededRecords[job.Id] = superseded;
        }
        _queueRestoreDone = true;
        // Saves the queue as it now is, so dropped entries are reported once and then forgotten.
        SynchroniseQueue();
        if (accepted.Count > 0)
            notes.Add($"Restored {Count(accepted.Count, "download")} from your last session." + (paused ? " Your queue is paused: press Resume queue to continue." : ""));
        if (missingFolder > 0)
            notes.Add($"{Count(missingFolder, "saved download")} {(missingFolder == 1 ? "was" : "were")} not restored because {(missingFolder == 1 ? "its save folder is" : "their save folders are")} gone. Add {(missingFolder == 1 ? "it" : "them")} again with a folder that exists.");
        if (withheld > 0)
            notes.Add($"{Count(withheld, "saved download")} {(withheld == 1 ? "was" : "were")} not restored because {(withheld == 1 ? "its link holds" : "their links hold")} private sign-in or session values, which KeaClip never saves. Add {(withheld == 1 ? "it" : "them")} again.");
        if (unreadable > 0)
            notes.Add($"{Count(unreadable, "saved download")} could not be read and {(unreadable == 1 ? "was" : "were")} skipped.");
    }

    private static bool FolderExists(string folder)
    {
        try { return Directory.Exists(folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static string Count(int count, string noun) => count == 1 ? "1 " + noun : count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " " + noun + "s";
}
