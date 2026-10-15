using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klippi.Desktop;

/// <summary>What kind of media a history record holds; drives the row's icon tile.</summary>
public enum HistoryKind { Video, Audio, VideoClip, AudioClip, Archive }

/// <summary>Whether a record's media is still on disk. <see cref="Unknown"/> until the background check returns.</summary>
public enum HistoryFileState { Unknown, Present, Missing }

// Library row presentation: kind tile, a meta line ordered so trimming cuts the least
// important facts first, a status pill only for exceptional states, row-specific tips
// that explain disabled actions, and an honest three-state file check. Checks run off the UI thread (a disconnected network
// drive can block File.Exists for seconds) and only for rows realised on screen,
// because the getters below are only read by bindings of visible rows.
public sealed partial class HistoryRow : ObservableObject
{
    private static readonly ConcurrentDictionary<string, Task<FileProbe>> FileChecks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] FileStateProperties =
    [
        nameof(FileState), nameof(IsMissing), nameof(IsAvailable), nameof(CanReveal), nameof(ShowRepeatPrimary), nameof(ShowOpenPrimary),
        nameof(ShowStatusPill), nameof(StatusLabel), nameof(StatusTip), nameof(OpenTip), nameof(RevealTip), nameof(EditTip), nameof(ShareTip), nameof(AccessibleSummary),
    ];
    // Unknown -> Present changes nothing visible; only these need re-reading.
    private static readonly string[] QuietFileStateProperties = [nameof(FileState), nameof(StatusLabel), nameof(StatusTip), nameof(AccessibleSummary)];

    private HistoryFileState _fileState;
    private bool _fileChecked, _folderExists;
    private int _fileGeneration;
    private IAsyncRelayCommand? _removeRecordCommand;

    /// <summary>The library that shows this row; set when the row enters <see cref="MainViewModel.History"/>.</summary>
    internal MainViewModel? Library { get; set; }

    /// <summary>
    /// Removes this record through the library's local path, which does not hold the app-wide
    /// busy state (no Cancel button, no header reflow, works while a download runs). Falls back
    /// to <see cref="RemoveCommand"/> when the row is not shown by a library. The file is kept.
    /// </summary>
    public IAsyncRelayCommand RemoveRecordCommand => _removeRecordCommand ??= new AsyncRelayCommand(() => Library is { } library ? library.RemoveHistoryRecordAsync(this) : RemoveCommand.ExecuteAsync(null));

    /// <summary>The saved title, or the file name when a record has none.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Entry.Title) ? Path.GetFileName(Entry.FilePath) is { Length: > 0 } name ? name : "Untitled media" : Entry.Title;

    public HistoryKind Kind => Entry.Mode switch
    {
        var mode when mode.Contains("clip", StringComparison.OrdinalIgnoreCase) => mode.Contains("audio", StringComparison.OrdinalIgnoreCase) ? HistoryKind.AudioClip : HistoryKind.VideoClip,
        var mode when mode.Equals("Audio", StringComparison.OrdinalIgnoreCase) => HistoryKind.Audio,
        var mode when mode.Equals("Archive", StringComparison.OrdinalIgnoreCase) => HistoryKind.Archive,
        _ => HistoryKind.Video,
    };
    public bool IsVideoKind => Kind == HistoryKind.Video;
    public bool IsAudioKind => Kind == HistoryKind.Audio;
    public bool IsClipKind => Kind is HistoryKind.VideoClip or HistoryKind.AudioClip;
    public bool IsArchiveKind => Kind == HistoryKind.Archive;
    private bool IsAudioOnly => Kind is HistoryKind.Audio or HistoryKind.AudioClip;

    public string KindLabel => Entry.Mode switch
    {
        "VideoAudio" or "Video" => "Video",
        "VideoOnly" => "Video only",
        "Audio" => "Audio",
        "Archive" => "Archive",
        _ when Kind == HistoryKind.AudioClip => "Audio clip",
        _ when Kind == HistoryKind.VideoClip => "Video clip",
        _ => Entry.Mode,
    };

    /// <summary>A friendly quality, or nothing when it says nothing useful (a video height on an audio record, an export marker).</summary>
    public string QualityLabel => Entry.Quality switch
    {
        "Best available" => "Best quality",
        "Smallest available" => "Smallest file",
        "Verified export" or "Existing file" or "Original" => "",
        var quality when IsAudioOnly && IsVideoHeight(quality) => "",
        var quality => quality ?? "",
    };

    public string SizeLabel => Entry.SizeBytes > 0 ? MainViewModel.Size(Entry.SizeBytes) : "";
    public string SourceLabel => string.IsNullOrWhiteSpace(Entry.Source) || Entry.Source is "Local export" or "Existing output" ? "" : Entry.Source;

    public string DateLabel
    {
        get
        {
            var local = Entry.Date.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            var time = local.ToString("t", CultureInfo.CurrentCulture);
            if (local.Date == today) return "Today " + time;
            if (local.Date == today.AddDays(-1)) return "Yesterday " + time;
            return local.ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>
    /// Date · kind · size · quality · source, most important first: the row trims from the end, so a
    /// narrow window loses the source and then the quality before it ever cuts the size. Empty parts are skipped.
    /// </summary>
    public string MetaLine => Join(DateLabel, KindLabel, SizeLabel, QualityLabel, SourceLabel);

    public string DetailTip => DisplayTitle + Environment.NewLine + Entry.FilePath + Environment.NewLine + "Saved " + Entry.Date.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    public HistoryFileState FileState { get { EnsureFileState(); return _fileState; } }
    public bool IsMissing => FileState == HistoryFileState.Missing;
    /// <summary>Optimistic while the check runs; Open and Show file re-check the file anyway.</summary>
    public bool IsAvailable => !IsMissing;
    /// <summary>Show file stays useful for a missing file whose folder still exists: it opens that folder.</summary>
    public bool CanReveal => !IsMissing || _folderExists;
    /// <summary>A missing file with a known source swaps the primary Open action for Download again.</summary>
    public bool ShowRepeatPrimary => IsMissing && HasSource;
    public bool ShowOpenPrimary => !ShowRepeatPrimary;
    /// <summary>The pill is for exceptions only (a missing file, an unfinished record); a normal saved row stays quiet.</summary>
    public bool ShowStatusPill => IsMissing || !IsFinished;
    public string StatusLabel => FileState switch
    {
        HistoryFileState.Missing => "File missing",
        _ when !IsFinished => string.IsNullOrWhiteSpace(Entry.Status) ? "Unfinished" : Entry.Status,
        HistoryFileState.Present => IsClipKind ? "Exported" : "Saved",
        _ => "",
    };
    public string StatusTip => FileState switch
    {
        HistoryFileState.Missing => HasSource ? "The file is no longer at its saved location. Download it again or remove this record." : "The file is no longer at its saved location. You can remove this record; nothing else is deleted.",
        _ when !IsFinished => "This record did not finish saving.",
        _ => IsClipKind ? "Exported and verified on this device." : "Saved on this device.",
    };

    public string OpenTip => IsMissing ? "File missing · " + MissingAdvice : "Open";
    public string RevealTip => !IsMissing ? "Show file in folder" : _folderExists ? "File missing · Open the folder it was saved in" : "File missing · Its folder is gone too";
    public string EditTip => IsMissing ? "File missing · " + MissingAdvice : "Edit in KeaClip";
    public string ShareTip => IsMissing ? "File missing · " + MissingAdvice : "Share with file.kiwi";
    private string MissingAdvice => HasSource ? "Download it again or remove the record" : "Remove the record from More actions";

    /// <summary>What a screen reader hears after the title: any status, then the meta line.</summary>
    public string AccessibleSummary => StatusLabel is { Length: > 0 } status ? status + ". " + MetaLine : MetaLine;

    private bool IsFinished => Entry.Status.Equals("Finished", StringComparison.OrdinalIgnoreCase);

    /// <summary>Clears every cached existence check (the library page calls this when it is shown again).</summary>
    public static void ForgetFileChecks() => FileChecks.Clear();

    /// <summary>
    /// Re-checks this row's file on its next read. The last known state stays on screen
    /// until the new result arrives, so a re-check never flickers.
    /// </summary>
    public void InvalidateFileState()
    {
        if (!_fileChecked) return;
        _fileChecked = false; _fileGeneration++;
        Raise(FileStateProperties);
    }

    /// <summary>Checks the file again now and returns whether it is still on disk.</summary>
    public async Task<bool> RevalidateAsync()
    {
        var generation = ++_fileGeneration; _fileChecked = true;
        var check = CheckAsync(FilePathKey); FileChecks[FilePathKey] = check;
        var probe = await check.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => { if (generation == _fileGeneration) Apply(probe, notify: true); });
        return probe.Exists;
    }

    private string FilePathKey => Entry.FilePath ?? "";

    private void EnsureFileState()
    {
        if (_fileChecked) return;
        _fileChecked = true;
        var check = FileChecks.GetOrAdd(FilePathKey, CheckAsync);
        // A cached result is applied in place: every dependent binding is being read
        // right now (first realisation or InvalidateFileState), so no event is needed.
        if (check.IsCompletedSuccessfully) { Apply(check.Result, notify: false); return; }
        _ = CompleteCheckAsync(check, _fileGeneration);
    }

    // Results always land on the UI thread, whatever thread the check finished on.
    private async Task CompleteCheckAsync(Task<FileProbe> check, int generation)
    {
        var probe = await check.ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => { if (generation == _fileGeneration) Apply(probe, notify: true); });
    }

    private void Apply(FileProbe probe, bool notify)
    {
        var state = probe.Exists ? HistoryFileState.Present : HistoryFileState.Missing;
        if (state == _fileState && probe.FolderExists == _folderExists) return;
        var wasMissing = _fileState == HistoryFileState.Missing;
        _fileState = state; _folderExists = probe.FolderExists;
        if (notify) Raise(wasMissing || state == HistoryFileState.Missing ? FileStateProperties : QuietFileStateProperties);
    }

    private void Raise(string[] properties)
    {
        foreach (var property in properties) OnPropertyChanged(property);
    }

    private static Task<FileProbe> CheckAsync(string path) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(path)) return new FileProbe(false, false);
        if (File.Exists(path)) return new FileProbe(true, true);
        try { return new FileProbe(false, Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder && Directory.Exists(folder)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return new FileProbe(false, false); }
    });

    private static bool IsVideoHeight(string? quality) => quality is { Length: > 1 } && quality[^1] is 'p' or 'P' && quality.AsSpan(0, quality.Length - 1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string Join(params string[] parts)
    {
        var count = 0; foreach (var part in parts) if (!string.IsNullOrWhiteSpace(part)) count++;
        if (count == 0) return "";
        var kept = new string[count]; var i = 0;
        foreach (var part in parts) if (!string.IsNullOrWhiteSpace(part)) kept[i++] = part;
        return string.Join(" · ", kept);
    }

    private readonly record struct FileProbe(bool Exists, bool FolderExists);
}
