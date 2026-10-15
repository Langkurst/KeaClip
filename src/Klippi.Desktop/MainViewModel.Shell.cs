using Klippi.Core;

namespace Klippi.Desktop;

// Shell-level presentation state: page headings and navigation badges.
public sealed partial class MainViewModel
{
    public string PageSubtitle => SelectedTab switch
    {
        1 => "Downloads keep running while you explore the rest of the app.",
        2 => "Everything you've saved or exported. Removing a record never deletes the file.",
        3 => "Trim, extract audio, compress and share. Your original stays safe by default.",
        4 => "Folders, appearance, components and privacy, all stored on this device.",
        _ => "Paste a link you're allowed to save, pick what to keep, and download.",
    };

    public int ActiveQueueCount => Queue.Count(row => row.IsActive);
    public bool HasActiveQueue => ActiveQueueCount > 0;

    /// <summary>
    /// Work that exiting now would stop, in plain words ("2 downloads", "an export"). Downloads
    /// are not kept across restarts, an upload cannot resume and a component task is cut short,
    /// so each of these is named before the app closes. A link check or an editor opening a file
    /// loses nothing and is not listed.
    /// </summary>
    public IReadOnlyList<string> RunningWorkDescriptions
    {
        get
        {
            var work = new List<string>(4);
            var downloads = ActiveQueueCount;
            if (downloads > 0) work.Add(downloads == 1 ? "1 download" : $"{downloads} downloads");
            else if (IsLaneBusy(OperationLane.Download) && !IsAnalysing) work.Add("downloads being added to your queue");
            if (IsExporting) work.Add("an export");
            if (IsSharing || IsLaneBusy(OperationLane.Share)) work.Add(_sharingWatchLink ? "a watch link upload" : "a file.kiwi upload");
            if (IsDownloadingUpdate) work.Add("an app update download");
            // Loading settings at startup is not interrupted work (_initialized and _settingsUnreadable are both set once it ends).
            if (IsLaneBusy(OperationLane.Maintenance) && (_initialized || _settingsUnreadable))
                work.Add(IsCleaningTemporaryFiles ? "temporary-file cleanup" : IsComponentTaskRunning ? "a media component task" : "a settings task");
            return work;
        }
    }

    /// <summary>Everything closing now would stop or lose: running work, then unsaved settings. Empty when the app can close at once.</summary>
    public IReadOnlyList<string> CloseWarnings
    {
        get
        {
            var warnings = new List<string>(RunningWorkDescriptions);
            if (UnsavedSettingsCount is var unsaved and > 0) warnings.Add(unsaved == 1 ? "1 unsaved settings change" : $"{unsaved} unsaved settings changes");
            return warnings;
        }
    }

    /// <summary>One sentence for the close prompt: what exiting now stops and loses.</summary>
    public string CloseWarningText
    {
        get
        {
            var work = RunningWorkDescriptions; var unsaved = UnsavedSettingsCount;
            var parts = new List<string>(2);
            if (work.Count > 0) parts.Add("stops " + JoinPlain(work));
            if (unsaved > 0) parts.Add("loses " + (unsaved == 1 ? "1 unsaved settings change" : $"{unsaved} unsaved settings changes"));
            return parts.Count == 0 ? "Keeping it in the tray lets queued downloads finish. Exit stops any active work."
                : "Exiting now " + string.Join(", and ", parts) + "."
                + (work.Count > 0 ? " Keep in tray lets the work finish." : "")
                + (unsaved > 0 && CanSaveSettings ? " Save and exit keeps your settings." : "");
        }
    }

    private static string JoinPlain(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>Records an unexpected UI error for diagnostics instead of crashing.</summary>
    public void ReportUnexpectedError(Exception error)
    {
        CaptureDiagnostics(error);
        Status = "Something went wrong, but your work is safe. Details are in Settings, Diagnostics.";
    }

    /// <summary>Moves the playhead by a time offset, clamped to the open media.</summary>
    public void SeekBy(TimeSpan offset)
    {
        if (_probe is not { Duration.TotalSeconds: > 0 } probe) return;
        Seek(PlaybackPosition + offset.TotalSeconds / probe.Duration.TotalSeconds);
    }

    private void NotifyQueueBadge()
    {
        OnPropertyChanged(nameof(ActiveQueueCount));
        OnPropertyChanged(nameof(HasActiveQueue));
    }
}
