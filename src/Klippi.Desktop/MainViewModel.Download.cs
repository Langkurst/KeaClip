using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Infrastructure.Engines;

namespace Klippi.Desktop;

// Download page presentation: the analysed media split into display pieces, inline
// notices (first-run setup, analysis problems, low disk space), whether each action is
// possible right now (and why not), and the counts shown by the playlist, batch and
// duplicate pickers. Every value is computed on read from the existing state;
// OnDownloadSourceChanged only re-announces derived values when one of their sources
// changes, so nothing polls and nothing runs while the page is idle.
public sealed partial class MainViewModel
{
    private const string BestQualityChoice = "Best available", SmallestQualityChoice = "Smallest available";
    private static readonly (string Key, string Name)[] KnownSites =
    [
        ("youtube", "YouTube"), ("twitch", "Twitch"), ("tiktok", "TikTok"), ("twitter", "X"), ("instagram", "Instagram"),
        ("soundcloud", "SoundCloud"), ("vimeo", "Vimeo"), ("bandcamp", "Bandcamp"), ("reddit", "Reddit"), ("facebook", "Facebook"),
        ("dailymotion", "Dailymotion"), ("bilibili", "Bilibili"), ("rumble", "Rumble"), ("archiveorg", "Internet Archive"), ("streamable", "Streamable"),
    ];

    private bool _downloadPresentationAttached;
    // One analysis attempt runs from IsAnalysing turning on until the operation ends; the
    // thumbnail, playlist entries and quality list arrive after the service call returns.
    private bool _analysisPending, _analysisCancelledByEdit;
    private string _analysisUrl = "";
    private MediaInfo? _mediaBeforeAnalysis;
    private string _analysisProblem = "";
    private bool _urlHintArmed;
    // Free space is read once per folder (and on refresh or when a download finishes),
    // not once per binding: DriveInfo can stall on a sleeping network or USB drive.
    private string? _spaceFolder;
    private long? _spaceBytes;
    private int _lastActiveQueueCount;
    private IRelayCommand? _clearUrlCommand, _dismissAnalysisProblemCommand, _useMkvCommand, _refreshSpaceCommand;

    // ----- Work gates -----
    // The page asks two questions of the shell's operation state: is link checking or queueing
    // busy, and can component setup run. Both read one place, so a shell that runs downloads and
    // maintenance in separate lanes only changes these definitions (and the property names that
    // re-announce them in OnDownloadSourceChanged).
    /// <summary>True while a link check or queueing step (or anything that blocks it) is running.</summary>
    private bool DownloadWorkRunning => !CanDownload;
    /// <summary>Cancels the link check or queueing step in progress.</summary>
    private void CancelDownloadWork() => CancelDownloadOperationCommand.Execute(null);
    /// <summary>Analyse and Try again: a link is entered and nothing blocks checking it.</summary>
    public bool CanCheckLink => !DownloadWorkRunning && HasUrl;
    /// <summary>Prepare components and Update downloader can start.</summary>
    public bool CanRunSetup => CanMaintain;

    public IRelayCommand ClearUrlCommand => _clearUrlCommand ??= new RelayCommand(() => Url = "");
    public IRelayCommand DismissAnalysisProblemCommand => _dismissAnalysisProblemCommand ??= new RelayCommand(() => SetAnalysisProblem(""));
    /// <summary>One-click fix for the WebM + H.264/HEVC conflict.</summary>
    public IRelayCommand UseMkvCommand => _useMkvCommand ??= new RelayCommand(() => { Container = MediaContainer.Mkv; OnPropertyChanged(nameof(Container)); RefreshFormatChoice(); });
    /// <summary>Reads the drive again (the shell's CheckSpaceCommand only re-announces AvailableSpace).</summary>
    public IRelayCommand RefreshSpaceCommand => _refreshSpaceCommand ??= new RelayCommand(() => { _spaceFolder = null; CheckSpaceCommand.Execute(null); RaiseStorage(); });

    // ----- Link field -----
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    /// <summary>
    /// Explains text that cannot be analysed. Quiet while a link is still being typed or can be
    /// fixed by adding https://; it appears after an attempt, when the field is left, or at once
    /// for something that clearly is not a web link (a file path, another scheme, several lines).
    /// </summary>
    public string UrlHint
    {
        get
        {
            var text = Url.Trim();
            if (text.Length == 0 || MediaValidation.TryMediaUrl(text, out _)) return "";
            if (text.Contains('\n') || text.Contains('\r')) return "Several links? Use Paste to review them one per line.";
            var scheme = Uri.TryCreate(text, UriKind.Absolute, out var parsed) ? parsed.Scheme : null;
            if (scheme == Uri.UriSchemeFile) return "That's a file on this PC. Use Open local media to edit it, or paste a web link here.";
            var foreign = scheme is not null && scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps;
            return _urlHintArmed || foreign ? "Use a complete web link, such as https://www.youtube.com/watch?v=…" : "";
        }
    }

    /// <summary>
    /// Called before Analyse, Download or Enter act on the field: a link typed without a scheme
    /// (youtube.com/watch?v=…) gets https:// so it can be analysed; anything else that cannot be
    /// a link arms the hint under the field. Returns true when the field holds a usable link.
    /// </summary>
    internal bool PrepareLinkForAction()
    {
        var text = Url.Trim();
        if (text.Length == 0) return false;
        if (MediaValidation.TryMediaUrl(text, out _)) return true;
        if (TrySchemelessLink(text, out var link)) { Url = link!.AbsoluteUri; return true; }
        ArmUrlHint();
        return false;
    }

    /// <summary>Leaving the field completes a scheme-less link or explains what is wrong.</summary>
    internal void CompleteLinkOnLeave()
    {
        var text = Url.Trim();
        if (text.Length == 0 || MediaValidation.TryMediaUrl(text, out _) || DownloadWorkRunning) return;
        if (TrySchemelessLink(text, out var link)) Url = link!.AbsoluteUri; else ArmUrlHint();
    }

    private void ArmUrlHint()
    {
        if (_urlHintArmed) return;
        _urlHintArmed = true; OnPropertyChanged(nameof(UrlHint));
    }

    private static bool TrySchemelessLink(string text, out Uri? link)
    {
        link = null;
        if (text.Contains("://", StringComparison.Ordinal) || text.Any(char.IsWhiteSpace) || text.StartsWith('/') || text.StartsWith('\\')) return false;
        if (!MediaValidation.TryMediaUrl("https://" + text, out var candidate) || !candidate!.Host.Contains('.', StringComparison.Ordinal)) return false;
        link = candidate;
        return true;
    }

    // ----- Just part of it: save only a time range of one analysed video (yt-dlp --download-sections) -----
    private bool _savePart;
    private string _partStart = "", _partEnd = "";
    /// <summary>Offered for one analysed video or audio with a known length; Archive always keeps the whole original.</summary>
    public bool CanSavePart => _media is { Duration: { TotalSeconds: >= 1 } } && !IsPlaylist && !IsArchiveDownload;
    public bool SavePart
    {
        get => _savePart;
        set
        {
            if (!SetProperty(ref _savePart, value)) return;
            // Turning it on starts from the whole length, so the boxes show what each end means.
            if (value && _media?.Duration is { } length && _partStart.Length == 0 && _partEnd.Length == 0) { PartStart = "0:00"; PartEnd = ClockText(length); }
            RaisePartState();
        }
    }
    public string PartStart { get => _partStart; set { if (SetProperty(ref _partStart, (value ?? "").Trim())) RaisePartState(); } }
    public string PartEnd { get => _partEnd; set { if (SetProperty(ref _partEnd, (value ?? "").Trim())) RaisePartState(); } }
    public bool ShowPartOptions => CanSavePart && SavePart;
    public bool PartHasProblem => ShowPartOptions && !TryGetPart(out _, out _, out _);
    /// <summary>"Saves 0:35 of 12:34", or what to fix.</summary>
    public string PartSummary => !ShowPartOptions ? "" : TryGetPart(out var start, out var end, out var problem) ? $"Saves {ClockText(end - start)} of {ClockText(_media!.Duration!.Value)}, from {ClockText(start)} to {ClockText(end)}." : problem;
    private void RaisePartState() { foreach (var name in (string[])[nameof(CanSavePart), nameof(ShowPartOptions), nameof(PartSummary), nameof(PartHasProblem)]) OnPropertyChanged(name); }

    internal bool TryGetPart(out TimeSpan start, out TimeSpan end, out string problem)
    {
        start = end = default; problem = "";
        var length = _media?.Duration ?? TimeSpan.Zero;
        if (!TryParseClock(_partStart, out start)) { problem = "Enter a start time like 45 or 1:20."; return false; }
        if (!TryParseClock(_partEnd, out end)) { problem = "Enter an end time like 2:05 or 1:02:03."; return false; }
        if (end > length + TimeSpan.FromSeconds(1)) { problem = $"The end is after the video ends ({ClockText(length)})."; return false; }
        if (end - start < TimeSpan.FromSeconds(0.5)) { problem = "The end must be at least half a second after the start."; return false; }
        if (end > length) end = length;
        return true;
    }

    /// <summary>Reads "45", "1:20", "1:02:03" and decimals such as "1:20.5" (seconds, minutes:seconds, hours:minutes:seconds).</summary>
    internal static bool TryParseClock(string? text, out TimeSpan time)
    {
        time = default;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length > 3 || parts.Any(part => part.Length == 0)) return false;
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || parts.Length > 1 && seconds >= 60) return false;
        var total = seconds;
        var scale = 60;
        for (var i = parts.Length - 2; i >= 0; i--, scale *= 60)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var whole) || i > 0 && whole >= 60) return false;
            total += whole * (double)scale;
        }
        if (total > TimeSpan.FromDays(2).TotalSeconds) return false;
        time = TimeSpan.FromSeconds(total);
        return true;
    }

    // ----- First run -----
    /// <summary>True once components were inspected and at least one still needs preparing.</summary>
    public bool NeedsComponentSetup => Components.Count > 0 && !ComponentsReady;
    /// <summary>Tools by the names their publishers use (component ids are lower case).</summary>
    internal static string ToolDisplayName(string id) => id switch { "ffmpeg" => "FFmpeg", "ffprobe" => "FFprobe", "deno" => "Deno", _ => id };
    public string MissingComponentsText
    {
        get
        {
            var missing = Components.Where(component => !component.Ready).Select(component => ToolDisplayName(component.Name)).ToArray();
            if (missing.Length == 0) return "";
            return "Still needed: " + (missing.Length == 1 ? missing[0] : string.Join(", ", missing[..^1]) + " and " + missing[^1]);
        }
    }
    /// <summary>The operation status without its percentage, which the animated counter already shows.</summary>
    public string OperationDetailText => string.Join(" · ", Status.Split(" · ").Where(part => !(part.EndsWith('%') && double.TryParse(part.AsSpan(0, part.Length - 1), NumberStyles.Float, CultureInfo.CurrentCulture, out _))));

    // ----- Media preview -----
    /// <summary>True for the whole analysis attempt, including the thumbnail and entries that follow the service call.</summary>
    public bool IsCheckingLink => IsAnalysing || _analysisPending;
    public bool ShowMediaPreview => _media is not null && !IsCheckingLink;
    public bool ShowMediaHint => _media is null && !IsCheckingLink && !HasAnalysisProblem && !HasBatch;
    public bool ShowPlaylistPicker => IsPlaylist && !IsCheckingLink;
    public string MediaByline
    {
        get
        {
            if (_media is not { } media) return "";
            var site = SiteName(media);
            return string.IsNullOrWhiteSpace(media.Creator) ? site : media.Creator + " · " + site;
        }
    }
    public string MediaDurationText => _media?.Duration is { } duration && duration > TimeSpan.Zero ? ClockText(duration) : "";
    public string MediaPublishedText => _media?.PublishedAt is { } date ? "Published " + date.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "";
    public string MediaResolutionText => VideoFormats().Select(format => format.Height ?? 0).DefaultIfEmpty(0).Max() is var height and > 0 ? "Up to " + HeightLabel(height) : "";
    public string MediaFrameRateText => VideoFormats().Select(format => format.Fps ?? 0).DefaultIfEmpty(0).Max() is var fps and >= 1 ? fps.ToString("0.##", CultureInfo.CurrentCulture) + " fps" : "";
    public string MediaAudioText => AudioStreams().Any() ? SourceAudioKbps is { } kbps ? $"Audio up to {kbps:0} kbps" : "Audio available" : "";
    public string MediaSizeRangeText
    {
        get
        {
            var sizes = (_media?.Formats ?? []).Where(format => format.SizeBytes is > 0).Select(format => format.SizeBytes!.Value).ToArray();
            if (sizes.Length == 0) return "";
            string low = CompactSize(sizes.Min()), high = CompactSize(sizes.Max());
            if (low == high) return low;
            var lowParts = low.Split(' '); var highParts = high.Split(' ');
            return lowParts.Length == 2 && highParts.Length == 2 && lowParts[1] == highParts[1] ? $"{lowParts[0]}–{high}" : $"{low}–{high}";
        }
    }
    public string PlaylistCountText => _media is not { IsPlaylist: true } playlist ? ""
        : !_playlistShape.MoreAvailable ? $"Playlist · {playlist.Entries.Count:N0} {(playlist.Entries.Count == 1 ? "entry" : "entries")}"
        : _playlistShape.Total is { } total && total > playlist.Entries.Count ? $"Playlist · {playlist.Entries.Count:N0} of {total:N0} entries"
        : $"Playlist · {playlist.Entries.Count:N0}+ entries";

    // ----- Analysis problems (shown inline under the link) -----
    public bool HasAnalysisProblem => _analysisProblem.Length > 0;
    /// <summary>The friendly reason without a first sentence that only repeats the title.</summary>
    public string AnalysisProblemText => ProblemKind(_analysisProblem) switch
    {
        "setup" => "KeaClip's media tools aren't ready yet. Prepare components once, then try again.",
        "browser-cookies" => WithoutLead(_analysisProblem, "Browser sign-in could not be read."),
        "sign-in" => WithoutLead(_analysisProblem, "Sign-in is required."),
        "private" => WithoutLead(_analysisProblem, "This video is private."),
        "changed-site" => WithoutLead(_analysisProblem, "This website may have changed."),
        "network" => WithoutLead(_analysisProblem, "Could not reach the media service."),
        _ => _analysisProblem,
    };
    public string AnalysisProblemTitle => ProblemKind(_analysisProblem) switch
    {
        "setup" => "Setup needed",
        "browser-cookies" => "Couldn't read your browser sign-in",
        "sign-in" => "Sign-in required",
        "private" => "This media is private",
        "unsupported" => "This link isn't supported",
        "changed-site" => "The site may have changed",
        "network" => "Can't reach the site",
        _ => "Couldn't analyse this link",
    };
    public bool AnalysisSuggestsSignIn => ProblemKind(_analysisProblem) is "sign-in" or "private" or "browser-cookies";
    public bool AnalysisSuggestsUpdate => ProblemKind(_analysisProblem) is "unsupported" or "changed-site";
    public bool AnalysisSuggestsSetup => ProblemKind(_analysisProblem) == "setup";

    // ----- Choices -----
    /// <summary>WebM cannot hold H.264 or HEVC video; the page blocks that choice instead of failing later in the queue.</summary>
    public bool HasFormatConflict => DownloadMode is DownloadMode.VideoAudio or DownloadMode.VideoOnly && Container == MediaContainer.WebM && VideoCodec is VideoCodec.H264 or VideoCodec.Hevc;
    public bool IsOriginalAudio => AudioFormat == AudioFormat.Original;
    /// <summary>A bitrate only matters for lossy conversions; Original, FLAC and WAV keep their own.</summary>
    public bool BitrateApplies => AudioFormat is AudioFormat.Mp3 or AudioFormat.Aac or AudioFormat.Opus or AudioFormat.Ogg;
    public string KeptBitrateText => AudioFormat == AudioFormat.Original
        ? SourceAudioKbps is { } kbps ? $"As the source · up to {kbps:0} kbps" : "As the source provides it"
        : "Lossless · no bitrate to choose";
    /// <summary>Audio mode's quality choice: the smallest audio stream instead of the best one.</summary>
    public bool PreferSmallestAudio
    {
        get => Quality == SmallestQualityChoice;
        set { var next = value ? SmallestQualityChoice : BestQualityChoice; if (Quality != next) Quality = next; }
    }

    // ----- Actions -----
    public bool HasPlaylistSelection => Playlist.Any(item => item.Selected);
    public bool CanStartDownload => !DownloadWorkRunning && HasUrl && !HasFormatConflict && (!IsPlaylist || HasPlaylistSelection);
    public bool CanDownloadAndEdit => CanStartDownload && !IsPlaylist;
    public bool CanQueueBatch => !DownloadWorkRunning && HasBatchSelection && !HasFormatConflict;
    public string DownloadTip => BlockedReason(playlistNeedsSelection: true) ?? (IsPlaylist ? "Adds the selected entries to your queue" : HasMedia ? "Adds it to your queue with these choices" : "Checks the link, then adds it to your queue with these choices");
    public string DownloadEditTip => IsPlaylist ? "Download & edit works on single links. Download the entries, then choose Edit on a finished item." : BlockedReason(playlistNeedsSelection: false) ?? "Download, then open it in the editor";
    public string QueueBatchTip => HasFormatConflict ? FormatConflictReason : !HasBatchSelection ? "Select at least one link first" : DownloadWorkRunning ? BusyReason : "Adds the selected links with the choices below";
    public string DownloadActionText
    {
        get
        {
            if (!IsPlaylist) return "Download";
            var selected = Playlist.Count(item => item.Selected);
            return selected == 0 ? "Download selected" : $"Download {selected:N0} selected";
        }
    }

    // ----- Pickers -----
    public string BatchCountText => BatchLinks.Count == 1 ? "1 link" : $"{BatchLinks.Count:N0} links";
    public string BatchSelectionText => $"{BatchLinks.Count(link => link.Selected):N0} selected";
    public bool HasBatchSelection => BatchLinks.Any(link => link.Selected);
    public string PlaylistSelectionText => $"{Playlist.Count(item => item.Selected):N0} of {Playlist.Count:N0} selected";
    public string DuplicateCountText => DuplicateChoices.Count == 1 ? "1 item" : $"{DuplicateChoices.Count:N0} items";

    // ----- Playlist structure -----
    /// <summary>
    /// What analysis learned about a playlist: which listed entries are lists themselves, and how
    /// much of the playlist the listing shows. Read once per analysis, off the UI thread.
    /// </summary>
    internal sealed record PlaylistShape(bool[] Lists, int Listed, int? Total, bool MoreAvailable)
    {
        public static PlaylistShape None { get; } = new([], 0, null, false);
        public bool IsList(int index) => index >= 0 && index < Lists.Length && Lists[index];
    }

    private PlaylistShape _playlistShape = PlaylistShape.None;

    internal static PlaylistShape ReadPlaylistShape(MediaInfo info)
    {
        if (!info.IsPlaylist) return PlaylistShape.None;
        (int Listed, int? Total, bool MoreAvailable) extent = (info.Entries.Count, null, false);
        try { extent = DownloadService.PlaylistExtent(info); }
        catch (MediaOperationException) { /* Unreadable metadata: show the listed entries without an extent. */ }
        return new(PlaylistListEntries(info), extent.Listed, extent.Total, extent.MoreAvailable);
    }

    /// <summary>
    /// Which listed entries are lists themselves (a channel's Videos tab, a playlist inside a
    /// channel), as <see cref="DownloadService.PlaylistEntryIsList"/> decides. That rule reads only
    /// the entry itself, so each entry is passed on its own instead of re-reading the whole
    /// listing once per entry (a 500-entry listing would otherwise be parsed 500 times).
    /// </summary>
    internal static bool[] PlaylistListEntries(MediaInfo info)
    {
        var lists = new bool[info.Entries.Count];
        if (lists.Length == 0 || string.IsNullOrWhiteSpace(info.MetadataJson)) return lists;
        try
        {
            using var json = JsonDocument.Parse(info.MetadataJson, new JsonDocumentOptions { MaxDepth = 64 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return lists;
            // Entries are the listing's objects in order (analysis skips anything else).
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                if (index >= lists.Length) break;
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var single = info with { Entries = [info.Entries[index]], MetadataJson = "{\"entries\":[" + entry.GetRawText() + "]}" };
                lists[index++] = DownloadService.PlaylistEntryIsList(single, 0);
            }
        }
        catch (Exception error) when (error is JsonException or MediaOperationException) { /* Unreadable metadata: every entry stays downloadable. */ }
        return lists;
    }

    /// <summary>The address and position to queue for one selected entry, or null when it has no usable address.</summary>
    private static (Uri Url, string? PlaylistItems)? PlaylistTarget(MediaInfo playlist, PlaylistChoice choice)
    {
        if (choice.Index >= 0)
        {
            try { return DownloadService.PlaylistEntryTarget(playlist, choice.Index); }
            catch (MediaOperationException) { /* Unreadable metadata: fall back to the entry's own address. */ }
        }
        return choice.Entry.Url is { } own ? (own, null) : null;
    }

    private static string PlaylistFoundStatus(MediaInfo info, PlaylistShape shape)
    {
        var found = !shape.MoreAvailable ? $"Playlist found: {info.Entries.Count:N0} {(info.Entries.Count == 1 ? "entry" : "entries")}."
            : shape.Total is { } total && total > info.Entries.Count ? $"Playlist found: showing the first {info.Entries.Count:N0} of {total:N0} entries."
            : $"Playlist found: showing the first {info.Entries.Count:N0} entries (the playlist is longer).";
        var lists = shape.Lists.Count(list => list);
        var note = lists == 0 ? ""
            : lists == info.Entries.Count ? (lists == 1 ? " It is a playlist itself: choose Open as playlist to see its videos." : " Each entry is a playlist itself: choose Open as playlist next to one to see its videos.")
            : $" {lists:N0} of them {(lists == 1 ? "is a playlist" : "are playlists")}: choose Open as playlist next to {(lists == 1 ? "it" : "one")} to see its videos.";
        return found + note + " Select the items you want; none are selected automatically.";
    }

    private string PlaylistExtentText()
    {
        if (_media is not { IsPlaylist: true } || !_playlistShape.MoreAvailable) return $"{Playlist.Count} available entries";
        return _playlistShape.Total is { } total && total > Playlist.Count ? $"First {Playlist.Count:N0} of {total:N0} entries shown" : $"First {Playlist.Count:N0} entries shown (the playlist is longer)";
    }

    /// <summary>
    /// Open as playlist on a playlist row: analyses that entry's own address, so a channel tab or a
    /// playlist inside a channel shows its videos to choose from. Nothing is queued.
    /// </summary>
    public Task OpenPlaylistEntryAsync(PlaylistChoice item)
    {
        if (ListAddress(item) is not { } url) { Status = "This entry has no link to open."; return Task.CompletedTask; }
        // Changing the link while a check runs would abandon it; ask first, as Analyse does.
        if (DownloadWorkRunning) { Status = LaneBusyMessage(OperationLane.Download); return Task.CompletedTask; }
        Url = url.AbsoluteUri; SelectedTab = 0;
        return AnalyseAsync();
    }

    /// <summary>The address a list entry opens as a playlist: its own web link, or null when it has none.</summary>
    private static Uri? ListAddress(PlaylistChoice item) => item.Entry.Url is { } url && MediaValidation.TryMediaUrl(url.AbsoluteUri, out var valid) ? valid : null;

    /// <summary>
    /// WebM cannot hold H.264 or HEVC video. The page disables its buttons for this; the guard makes
    /// sure no other path (keyboard, batch, a future caller) can queue a job that must fail.
    /// </summary>
    private void RejectFormatConflict()
    {
        if (HasFormatConflict) throw new MediaOperationException("WebM can't hold H.264 or HEVC video. Choose MKV, MP4 or Automatic.");
    }

    // ----- Storage -----
    public string FreeSpaceText => FreeBytes() is { } bytes ? CompactSize(bytes) + " free" : "Space unknown";
    /// <summary>
    /// Warns when the drive may not hold what the chosen mode and quality would download plus the
    /// temporary merge/convert copy. Only sizes the site reports are used; nothing is invented.
    /// </summary>
    public bool IsSpaceLow => EstimatedDownloadBytes() is { } estimate && FreeBytes() is { } free && free < estimate * 2;
    public string SpaceWarningText => IsSpaceLow && EstimatedDownloadBytes() is { } estimate && FreeBytes() is { } free
        ? $"Space may be tight: {CompactSize(free)} is free and this download is reported at about {CompactSize(estimate)}. Merging or converting needs about as much again while it works."
        : "";

    /// <summary>Starts re-announcing derived download-page values. Idempotent; called by the view and after analysis.</summary>
    internal void AttachDownloadPresentation()
    {
        if (_downloadPresentationAttached) return;
        _downloadPresentationAttached = true;
        PropertyChanged += OnDownloadSourceChanged;
    }

    partial void OnMediaAnalysed()
    {
        AttachDownloadPresentation();
        _analysisPending = false; _analysisCancelledByEdit = false; _mediaBeforeAnalysis = null;
        RaiseMediaPresentation();
    }

    /// <summary>Re-announces everything that depends on the container, codec or audio format pickers (plain properties).</summary>
    internal void RefreshFormatChoice()
    {
        // Clamp first, so the bitrate list never loses its selection while it changes.
        if (!Bitrates.Contains(AudioKbps)) AudioKbps = Bitrates.Where(kbps => kbps <= AudioKbps).DefaultIfEmpty(Bitrates[^1]).Max();
        OnPropertyChanged(nameof(Bitrates));
        OnPropertyChanged(nameof(HasFormatConflict)); OnPropertyChanged(nameof(IsOriginalAudio)); OnPropertyChanged(nameof(BitrateApplies)); OnPropertyChanged(nameof(KeptBitrateText));
        RaiseActionState();
    }

    private void OnDownloadSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Url):
                // Editing or clearing the link abandons a check of the previous one.
                if (_analysisPending && DownloadWorkRunning && !string.Equals(Url, _analysisUrl, StringComparison.Ordinal)) { _analysisCancelledByEdit = true; CancelDownloadWork(); }
                _urlHintArmed = false;
                SetAnalysisProblem("");
                OnPropertyChanged(nameof(HasUrl)); OnPropertyChanged(nameof(UrlHint));
                RaiseActionState();
                break;
            case nameof(HasMedia):
                RaiseMediaPresentation();
                // A new link starts as the whole thing again.
                if (_savePart || _partStart.Length + _partEnd.Length > 0) { _savePart = false; _partStart = ""; _partEnd = ""; foreach (var name in (string[])[nameof(SavePart), nameof(PartStart), nameof(PartEnd)]) OnPropertyChanged(name); }
                RaisePartState();
                break;
            case nameof(IsAnalysing):
                if (IsAnalysing) { _analysisPending = true; _analysisUrl = Url; _mediaBeforeAnalysis = _media; SetAnalysisProblem(""); }
                RaiseAnalysisState();
                break;
            case nameof(IsBusy):
                if (!DownloadWorkRunning) CompleteAnalysisAttempt();
                OnPropertyChanged(nameof(CanRunSetup));
                RaiseActionState();
                break;
            case nameof(Status) when !CanDownload || IsMaintaining:
                OnPropertyChanged(nameof(OperationDetailText));
                break;
            case nameof(ComponentsReady):
                OnPropertyChanged(nameof(NeedsComponentSetup)); OnPropertyChanged(nameof(MissingComponentsText));
                break;
            case nameof(DownloadMode):
                RaisePartState();
                RefreshFormatChoice(); RaiseSpaceWarning();
                break;
            case nameof(Quality):
                OnPropertyChanged(nameof(PreferSmallestAudio)); RaiseSpaceWarning();
                break;
            case nameof(AvailableSpace):
                RaiseStorage();
                break;
            case nameof(ActiveQueueCount) when ActiveQueueCount != _lastActiveQueueCount:
                // A download started or finished: free space has really changed.
                _lastActiveQueueCount = ActiveQueueCount; _spaceFolder = null; RaiseStorage();
                break;
            case nameof(BatchSummary):
            case nameof(HasBatch):
                OnPropertyChanged(nameof(ShowMediaHint));
                OnPropertyChanged(nameof(BatchCountText)); OnPropertyChanged(nameof(BatchSelectionText)); OnPropertyChanged(nameof(HasBatchSelection));
                RaiseActionState();
                break;
            case nameof(PlaylistSummary):
            case nameof(IsPlaylist):
                RaisePartState();
                OnPropertyChanged(nameof(PlaylistSelectionText)); OnPropertyChanged(nameof(DownloadActionText)); OnPropertyChanged(nameof(HasPlaylistSelection)); OnPropertyChanged(nameof(ShowPlaylistPicker));
                RaiseActionState();
                break;
            case nameof(HasDuplicates):
                OnPropertyChanged(nameof(DuplicateCountText));
                break;
        }
    }

    // A failed analysis keeps no new media; the status line already holds the friendly,
    // redacted reason, so the inline notice repeats it next to the link it belongs to.
    // Links that cannot be valid are caught earlier by UrlHint and never reach the service.
    private void CompleteAnalysisAttempt()
    {
        if (!_analysisPending) return;
        var analysed = _media is not null && !ReferenceEquals(_media, _mediaBeforeAnalysis);
        var cancelledByEdit = _analysisCancelledByEdit;
        var linkChanged = !string.Equals(Url, _analysisUrl, StringComparison.Ordinal);
        _analysisPending = false; _analysisCancelledByEdit = false; _mediaBeforeAnalysis = null;
        RaiseAnalysisState();
        if (cancelledByEdit) Status = HasUrl ? "Stopped checking the previous link. Analyse this one when you're ready." : "Link cleared. Paste another link when you're ready.";
        else if (!analysed && !linkChanged && !IsCancelledStatus(Status)) SetAnalysisProblem(Status);
    }

    private void SetAnalysisProblem(string message)
    {
        if (_analysisProblem == message) return;
        _analysisProblem = message;
        OnPropertyChanged(nameof(HasAnalysisProblem)); OnPropertyChanged(nameof(AnalysisProblemText)); OnPropertyChanged(nameof(AnalysisProblemTitle));
        OnPropertyChanged(nameof(AnalysisSuggestsSignIn)); OnPropertyChanged(nameof(AnalysisSuggestsUpdate)); OnPropertyChanged(nameof(AnalysisSuggestsSetup)); OnPropertyChanged(nameof(ShowMediaHint));
    }

    private void RaiseAnalysisState()
    {
        OnPropertyChanged(nameof(IsCheckingLink)); OnPropertyChanged(nameof(ShowMediaPreview)); OnPropertyChanged(nameof(ShowMediaHint)); OnPropertyChanged(nameof(ShowPlaylistPicker));
    }

    private void RaiseActionState()
    {
        OnPropertyChanged(nameof(CanCheckLink)); OnPropertyChanged(nameof(CanStartDownload)); OnPropertyChanged(nameof(CanDownloadAndEdit)); OnPropertyChanged(nameof(CanQueueBatch));
        OnPropertyChanged(nameof(DownloadTip)); OnPropertyChanged(nameof(DownloadEditTip)); OnPropertyChanged(nameof(QueueBatchTip));
    }

    private void RaiseMediaPresentation()
    {
        RaiseAnalysisState();
        OnPropertyChanged(nameof(MediaByline)); OnPropertyChanged(nameof(MediaDurationText)); OnPropertyChanged(nameof(MediaPublishedText));
        OnPropertyChanged(nameof(MediaResolutionText)); OnPropertyChanged(nameof(MediaFrameRateText)); OnPropertyChanged(nameof(MediaAudioText));
        OnPropertyChanged(nameof(MediaSizeRangeText)); OnPropertyChanged(nameof(PlaylistCountText)); OnPropertyChanged(nameof(DownloadActionText));
        OnPropertyChanged(nameof(PlaylistSelectionText)); OnPropertyChanged(nameof(HasPlaylistSelection)); OnPropertyChanged(nameof(KeptBitrateText));
        RaiseActionState(); RaiseSpaceWarning();
    }

    private void RaiseStorage() { OnPropertyChanged(nameof(FreeSpaceText)); RaiseSpaceWarning(); }
    private void RaiseSpaceWarning() { OnPropertyChanged(nameof(IsSpaceLow)); OnPropertyChanged(nameof(SpaceWarningText)); }

    // Storyboard and image-only formats (mhtml, vcodec "none") report small heights (45p, 90p)
    // that are not downloadable video qualities; only heights a real video stream offers become
    // height caps, highest first.
    internal static IEnumerable<int> QualityHeights(MediaInfo info) => info.Formats.Where(format => format.Height is > 0 && IsVideoFormat(format)).Select(format => format.Height!.Value).Distinct().OrderDescending();

    private IEnumerable<MediaFormat> VideoFormats() => (_media?.Formats ?? []).Where(IsVideoFormat);
    private IEnumerable<MediaFormat> AudioStreams() => (_media?.Formats ?? []).Where(format => IsRealCodec(format.AudioCodec));
    private double? SourceAudioKbps => AudioStreams().Select(format => format.AudioKbps ?? 0).DefaultIfEmpty(0).Max() is var kbps and >= 1 ? kbps : null;
    private static bool IsVideoFormat(MediaFormat format) => !string.Equals(format.Extension, "mhtml", StringComparison.OrdinalIgnoreCase) && !string.Equals(format.VideoCodec, "none", StringComparison.OrdinalIgnoreCase) && (format.Height is > 0 || IsRealCodec(format.VideoCodec));
    private static bool IsRealCodec(string? codec) => !string.IsNullOrWhiteSpace(codec) && !codec.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the chosen mode and quality would download, from reported sizes only: the audio
    /// stream for Audio, the video stream within the height cap for Video only, video plus a
    /// separate audio stream for Video + audio, and the best of both for Archive.
    /// </summary>
    private long? EstimatedDownloadBytes()
    {
        var sized = (_media?.Formats ?? []).Where(format => format.SizeBytes is > 0).ToArray();
        if (sized.Length == 0) return null;
        var smallest = DownloadMode != DownloadMode.Archive && Quality == SmallestQualityChoice;
        int? cap = DownloadMode != DownloadMode.Archive && Quality.EndsWith("p maximum", StringComparison.Ordinal) && int.TryParse(Quality.AsSpan(0, Quality.IndexOf('p')), NumberStyles.None, CultureInfo.InvariantCulture, out var height) ? height : null;
        var audioOnly = sized.Where(format => IsRealCodec(format.AudioCodec) && !IsVideoFormat(format)).ToArray();
        var video = sized.Where(IsVideoFormat).Where(format => cap is null || format.Height is null || format.Height <= cap).ToArray();
        MediaFormat? Pick(IEnumerable<MediaFormat> formats, bool pickSmallest) => (pickSmallest ? formats.OrderBy(Bytes) : formats.OrderByDescending(Bytes)).FirstOrDefault();
        static long Bytes(MediaFormat format) => format.SizeBytes!.Value;
        switch (DownloadMode)
        {
            case DownloadMode.Audio:
                return (Pick(audioOnly, smallest) ?? Pick(sized.Where(format => IsRealCodec(format.AudioCodec)), smallest)) is { } audio ? Bytes(audio) : null;
            case DownloadMode.VideoOnly:
                return Pick(video, smallest) is { } picture ? Bytes(picture) : null;
            case DownloadMode.Archive:
                var best = Pick(sized.Where(IsVideoFormat), false);
                var bestAudio = best is not null && IsRealCodec(best.AudioCodec) ? null : Pick(audioOnly, false);
                return best is null && bestAudio is null ? null : (best is null ? 0 : Bytes(best)) + (bestAudio is null ? 0 : Bytes(bestAudio));
            default:
                var chosen = Pick(video, smallest);
                if (chosen is null) return Pick(audioOnly, smallest) is { } fallback ? Bytes(fallback) : null;
                return Bytes(chosen) + (IsRealCodec(chosen.AudioCodec) ? 0 : Pick(audioOnly, smallest) is { } extra ? Bytes(extra) : 0);
        }
    }

    private long? FreeBytes()
    {
        var folder = SelectedOutputDirectory;
        if (string.Equals(_spaceFolder, folder, StringComparison.Ordinal)) return _spaceBytes;
        _spaceFolder = folder;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            var drive = string.IsNullOrEmpty(root) ? null : new DriveInfo(root);
            _spaceBytes = drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { _spaceBytes = null; }
        return _spaceBytes;
    }

    private static string ProblemKind(string message)
    {
        if (message.Length == 0) return "";
        if (message.Contains("is not ready", StringComparison.Ordinal)) return "setup";
        if (message.Contains("Browser sign-in could not be read", StringComparison.Ordinal)) return "browser-cookies";
        if (message.Contains("Sign-in is required", StringComparison.Ordinal)) return "sign-in";
        if (message.Contains("is private", StringComparison.Ordinal)) return "private";
        if (message.Contains("not supported by the installed downloader", StringComparison.Ordinal)) return "unsupported";
        if (message.Contains("website may have changed", StringComparison.Ordinal)) return "changed-site";
        if (message.Contains("Could not reach the media service", StringComparison.Ordinal)) return "network";
        return "other";
    }

    // A cancelled attempt is not a problem to explain ("Operation cancelled. …" or "Cancelled. …").
    private static bool IsCancelledStatus(string status) => status.StartsWith("Operation cancelled", StringComparison.Ordinal) || status.StartsWith("Cancelled", StringComparison.Ordinal);

    private static string WithoutLead(string message, string lead) => message.StartsWith(lead, StringComparison.Ordinal) && message.Length > lead.Length ? message[lead.Length..].TrimStart() : message;

    private string? BlockedReason(bool playlistNeedsSelection) =>
        !HasUrl ? "Paste or type a link first"
        : DownloadWorkRunning ? BusyReason
        : HasFormatConflict ? FormatConflictReason
        : playlistNeedsSelection && IsPlaylist && !HasPlaylistSelection ? "Select at least one playlist entry first"
        : null;

    private const string BusyReason = "Wait for the current task to finish, or cancel it";
    private const string FormatConflictReason = "WebM can't hold H.264 or HEVC video. Choose a compatible format first.";

    /// <summary>A friendly site name: the extractor key mapped to its brand, or the host without www.</summary>
    private static string SiteName(MediaInfo media)
    {
        var host = media.Url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? media.Url.Host[4..] : media.Url.Host;
        var key = media.Source?.Trim() ?? "";
        if (key.Length == 0 || key.Equals("Generic", StringComparison.OrdinalIgnoreCase)) return host;
        foreach (var (prefix, name) in KnownSites)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name;
        return key;
    }

    internal static string ClockText(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{time.Minutes}:{time.Seconds:00}";

    private static string HeightLabel(int height) => height switch { >= 4320 => "8K", >= 2160 => "4K", _ => height + "p" };

    internal static string CompactSize(long bytes) => bytes >= 1_000_000_000_000 ? $"{bytes / 1_000_000_000_000d:0.##} TB" : Size(bytes);
}
