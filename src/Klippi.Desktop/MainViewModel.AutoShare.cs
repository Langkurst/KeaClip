using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

/// <summary>Where a "Share when finished" upload for one finished download stands.</summary>
public enum AutoShareState { None, Waiting, Uploading, NeedsConfirmation, Failed }

// Share when finished. The Download page switch (its starting value is a Settings default, off
// unless chosen) marks the jobs queued while it is on. When such a job finishes successfully, its
// file goes through the same encrypted File.Kiwi share as the Share buttons, in the Share lane, one
// upload at a time: an automatic share waits for any upload already running instead of failing.
// A very large file is never uploaded automatically; it waits for the usual confirmation. The
// finished link is copied to the clipboard and kept in memory only (with its decryption secret),
// so Copy link on the queue and Library rows works for the rest of the session; it is never
// written to history, the queue file, logs or diagnostics. A failed share leaves the download as
// it is and offers Retry share. Nothing here ever starts a download.
public sealed partial class MainViewModel
{
    private sealed class AutoShare(string path, string title)
    {
        public string Path { get; } = path;
        public string Title { get; } = title;
        public AutoShareState State { get; set; } = AutoShareState.Waiting;
        public string Reason { get; set; } = "";
    }

    private readonly HashSet<Guid> _shareAfter = [];
    private readonly Dictionary<Guid, AutoShare> _autoShares = [];
    private readonly Queue<Guid> _autoShareLine = new();
    // Full share links (with their secret) by file, for this session only.
    private readonly Dictionary<string, string> _shareLinks = new(StringComparer.OrdinalIgnoreCase);
    private bool _shareWhenFinished, _shareWhenFinishedDefault, _autoSharing;

    /// <summary>Download page: share the downloads queued now once they finish.</summary>
    public bool ShareWhenFinished { get => _shareWhenFinished; set => SetProperty(ref _shareWhenFinished, value); }

    /// <summary>Settings: the Download page switch's starting value. Changing it also sets the switch.</summary>
    public bool ShareWhenFinishedDefault
    {
        get => _shareWhenFinishedDefault;
        set { if (SetProperty(ref _shareWhenFinishedDefault, value)) ShareWhenFinished = value; }
    }

    internal AutoShareState AutoShareStateFor(Guid jobId) => _autoShares.TryGetValue(jobId, out var share) ? share.State : AutoShareState.None;
    internal string AutoShareReasonFor(Guid jobId) => _autoShares.TryGetValue(jobId, out var share) ? share.Reason : "";

    /// <summary>The full share link made for this file in this session, or null.</summary>
    internal string? ShareLinkFor(string? path) => path is not null && LinkKey(path) is { } key && _shareLinks.TryGetValue(key, out var link) ? link : null;

    /// <summary>Copies the file's share link (Copy link on a queue or Library row).</summary>
    public Task CopyShareLinkAsync(string? path) => ShareLinkFor(path) is { } link
        ? CopyToClipboardAsync(link, "Share link copied. Keep the full link private; it expires.")
        : Task.CompletedTask;

    private void RecordShareLink(string path, string link)
    {
        if (LinkKey(path) is not { } key) return;
        _shareLinks[key] = link;
        // A share made by hand (for example after the large-file confirmation) settles that file's automatic share too.
        foreach (var (id, share) in _autoShares.ToArray())
            if (share.State != AutoShareState.Uploading && LinkKey(share.Path) == key) { _autoShares.Remove(id); RaiseAutoShare(id); }
        foreach (var row in Queue) if (row.FilePath is { } file && LinkKey(file) == key) row.ShareChanged();
        foreach (var row in History.Concat(_libraryRows?.Values ?? Enumerable.Empty<HistoryRow>()).Distinct()) if (LinkKey(row.Entry.FilePath) == key) row.ShareLinkChanged();
    }

    private static string? LinkKey(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return null; }
    }

    private void RequestAutoShare(Guid jobId, string title, string path)
    {
        if (_disposed) return;
        _autoShares[jobId] = new AutoShare(path, title);
        _autoShareLine.Enqueue(jobId);
        RaiseAutoShare(jobId);
        if (!_autoSharing) _ = RunAutoSharesAsync();
    }

    /// <summary>Retry share on a finished download whose automatic share failed.</summary>
    public void RetryAutoShare(Guid jobId)
    {
        if (!_autoShares.TryGetValue(jobId, out var share) || share.State != AutoShareState.Failed) return;
        RequestAutoShare(jobId, share.Title, share.Path);
    }

    // One automatic upload at a time, in the order the downloads finished. Each waits for the Share lane
    // (a share the user started, or the previous automatic one) and then claims it before anything else can.
    private async Task RunAutoSharesAsync()
    {
        _autoSharing = true;
        try
        {
            while (!_disposed && _autoShareLine.TryDequeue(out var jobId))
            {
                if (!_autoShares.TryGetValue(jobId, out var share) || share.State != AutoShareState.Waiting) continue;
                while (!CanShare && !_disposed) await _lanes[OperationLane.Share].Finished;
                if (_disposed) return;
                await AutoShareAsync(jobId, share);
            }
        }
        catch (Exception error) { CaptureDiagnostics(error); }
        finally { _autoSharing = false; }
    }

    private async Task AutoShareAsync(Guid jobId, AutoShare share)
    {
        if (LargeShareBytes(share.Path) is { } bytes)
        {
            share.State = AutoShareState.NeedsConfirmation; RaiseAutoShare(jobId);
            AskToConfirmLargeShare(share.Path, bytes);
            Status = $"{share.Title} was downloaded. It is large ({Size(bytes)}), so it was not shared automatically: choose Share on it in your queue to upload it anyway.";
            return;
        }
        share.State = AutoShareState.Uploading; RaiseAutoShare(jobId);
        ShareResult? result = null; Exception? failure = null;
        // The lane is free (checked on this thread just before), so this claims it at once.
        await RunAsync(OperationLane.Share, async ct =>
        {
            try { result = await ShareCoreAsync(share.Path, automatic: true, ct); }
            catch (Exception error) { failure = error; throw; }
        });
        if (_disposed) return;
        if (result is not null)
        {
            _autoShares.Remove(jobId); RaiseAutoShare(jobId);
            Status = await TryCopyToClipboardAsync(result.ShareUrl)
                ? $"Shared: link copied. {share.Title} has an expiring File.Kiwi link; keep the full link private."
                : $"Shared: {share.Title} has an expiring File.Kiwi link, but the clipboard is busy in another app. Choose Copy link on it in your queue.";
            return;
        }
        share.State = AutoShareState.Failed;
        share.Reason = failure switch
        {
            OperationCanceledException => "The upload was cancelled.",
            null => "The upload could not start.",
            _ => FriendlyError(failure),
        };
        RaiseAutoShare(jobId);
        Status = $"{share.Title} was downloaded, but it could not be shared: {share.Reason} Choose Retry share on it in your queue.";
    }

    private void RaiseAutoShare(Guid jobId) { if (_rows.TryGetValue(jobId, out var row)) row.ShareChanged(); }
}
