using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Infrastructure.Engines;

namespace Klippi.Desktop;

// Editor page presentation state: media facts, loading/empty states, the structured size
// estimate, mute, export/share progress flags and quick actions for the latest export.
// Derived values are raised from one PropertyChanged listener that attaches the first time
// the editor view binds (or the editor opens), so pure view-model tests are unaffected.
public sealed partial class MainViewModel
{
    private static readonly string[] EditorVideoExtensions = [".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v"];
    private static readonly string[] EditorAudioExtensions = [".mp3", ".m4a", ".flac", ".wav", ".ogg", ".opus", ".aac"];
    private const string ExportHistorySource = "Local export", NoAttachmentPreset = "Custom target";
    private const double AudioArtworkAspect = 2.75, DetailWaveformPointsPerSecond = 12;
    private const int MaxWaveformPoints = 4096, RecentScanLimit = 30, RecentShown = 4;
    private bool _editorHooksAttached, _muted;
    private bool _editorLoading, _openingEditor, _waveformLoading, _editorOpenLatched;
    private ExportEstimate? _editorEstimate;
    private string _soundboardLimitText = "";
    private DateTimeOffset _editorOpenedAt = DateTimeOffset.MaxValue;
    private HistoryEntry? _lastExport;
    private IReadOnlyList<EditorRecentItem> _recentMedia = [];
    private object? _listsHistory;
    private int _listsGeneration;
    private ProbeInfo? _aspectProbe;
    private double _previewAspect = 16d / 9d;
    private CancellationTokenSource? _waveformDetail;
    private ProbeInfo? _waveformDetailFailed;
    private int _editorSession;
    private IRelayCommand? _toggleMuteCommand, _openLastExportCommand, _revealLastExportCommand;
    private IAsyncRelayCommand? _shareLastExportCommand, _editLastExportCommand;

    partial void OnEditorOpened()
    {
        EnsureEditorHooks();
        _editorOpenedAt = DateTimeOffset.UtcNow;
        _editorSession++; OnPropertyChanged(nameof(EditorSession));
        // Opening media re-applies the volume; keep a chosen mute in effect.
        if (_muted) TryAction(() => Playback.SetVolume(0));
        RaiseEditorMedia();
        RefreshEditorEstimate();
        QueueEditorLists(force: true);
        StartDetailedWaveform();
    }

    // ===== Media facts =====

    public TimeSpan EditorDuration => _probe?.Duration ?? TimeSpan.Zero;
    public string EditorDurationText => MediaValidation.Timecode(EditorDuration);
    /// <summary>The transport clock (m:ss, or h:mm:ss for long files).</summary>
    public string PlaybackClockText => ClockText(Playback.Time, EditorDuration);
    private string _shownClock = "";
    // Called every frame while playing; the text (and its layout) changes only when the shown second does.
    private void RefreshPlaybackClock()
    {
        var clock = PlaybackClockText;
        if (clock == _shownClock) return;
        _shownClock = clock; OnPropertyChanged(nameof(PlaybackClockText));
    }
    public string DurationClockText => ClockText(EditorDuration, EditorDuration);

    /// <summary>Pages are a 1040px column; with a file open the Editor widens so the video gets a proper size on big screens.</summary>
    public double PageMaxWidth => IsEditor && HasEditor ? 1440 : 1040;

    // Whole seconds: the clock reads like a player's (m:ss, or h:mm:ss for long files). The trim fields keep tenths.
    private static string ClockText(TimeSpan time, TimeSpan duration)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return duration.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }
    public bool EditorHasVideo => _probe?.HasVideo ?? false;
    public bool EditorHasAudio => _probe?.HasAudio ?? false;
    /// <summary>Changes every time a file finishes opening (also the same file again), so the view can reset the timeline.</summary>
    public int EditorSession => _editorSession;
    /// <summary>Explains a disabled output tile; null (no tooltip) while the tile is available.</summary>
    public string? VideoOutputTip => _probe is { HasVideo: false } ? "This file has no video track, so only an audio clip can be made." : null;
    public string? AudioOutputTip => _probe is { HasAudio: false } ? "This file has no audio track, so only a video clip can be made." : null;
    public bool IsAudioOnlyMedia => _probe is { HasVideo: false };

    /// <summary>
    /// The displayed shape of the video (rotation applied, clamped between 9:16 and 21:9) so portrait
    /// clips get a taller frame; a short banner for audio artwork so it does not waste space.
    /// </summary>
    public double PreviewAspect
    {
        get
        {
            if (_probe is not { } probe) return 16d / 9d;
            if (!probe.HasVideo) return AudioArtworkAspect;
            if (!ReferenceEquals(probe, _aspectProbe)) { _aspectProbe = probe; _previewAspect = DisplayAspect(probe); }
            return _previewAspect;
        }
    }

    private static double DisplayAspect(ProbeInfo probe)
    {
        if (probe is not { Width: > 0, Height: > 0 }) return 16d / 9d;
        var ratio = (double)probe.Width.Value / probe.Height.Value;
        if (IsQuarterTurn(probe.TechnicalJson)) ratio = 1 / ratio;
        return Math.Clamp(ratio, 9d / 16d, 21d / 9d);
    }

    // Phone videos are often stored landscape with a 90° display rotation (stream side data or a rotate tag).
    private static bool IsQuarterTurn(string? technicalJson)
    {
        if (string.IsNullOrEmpty(technicalJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(technicalJson);
            if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return false;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var type) || type.GetString() != "video") continue;
                if (stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var cover) && cover.ValueKind == JsonValueKind.Number && cover.GetInt32() == 1) continue;
                double? rotation = null;
                if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
                    foreach (var item in sideData.EnumerateArray())
                        if (item.TryGetProperty("rotation", out var value) && value.ValueKind == JsonValueKind.Number) rotation = value.GetDouble();
                if (rotation is null && stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var tag) && double.TryParse(tag.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tagged)) rotation = tagged;
                return rotation is { } degrees && Math.Abs(Math.Round(degrees / 90) % 2) == 1;
            }
        }
        catch (JsonException) { }
        return false;
    }

    public IReadOnlyList<string> EditorFacts
    {
        get
        {
            if (_probe is not { } probe) return [];
            var facts = new List<string>(5) { CompactDuration(probe.Duration) };
            if (probe is { HasVideo: true, Width: > 0, Height: > 0 }) facts.Add($"{probe.Width}×{probe.Height}");
            if (probe is { HasVideo: true, Fps: > 0 }) facts.Add(probe.Fps.Value.ToString("0.##", CultureInfo.InvariantCulture) + " fps");
            var codecs = string.Join(" · ", new[] { probe.HasVideo ? CodecName(probe.VideoCodec) : null, probe.HasAudio ? CodecName(probe.AudioCodec) : null }.Where(c => !string.IsNullOrEmpty(c)));
            if (codecs.Length > 0) facts.Add(codecs);
            facts.Add(Size(probe.SizeBytes));
            return facts;
        }
    }

    public string EditorAudioText
    {
        get
        {
            if (_probe is not { } probe) return "";
            if (!probe.HasAudio) return "No audio track";
            var parts = new List<string>(3) { CodecName(probe.AudioCodec) ?? "Audio" };
            if (probe.SampleRate is > 0) parts.Add((probe.SampleRate.Value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + " kHz");
            if (probe.Channels is > 0) parts.Add(probe.Channels switch { 1 => "Mono", 2 => "Stereo", var channels => channels + " channels" });
            return string.Join(" · ", parts);
        }
    }

    private static string? CodecName(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" => null,
        "h264" => "H.264", "hevc" or "h265" => "HEVC", "av1" => "AV1", "vp9" => "VP9", "vp8" => "VP8", "mpeg4" => "MPEG-4", "prores" => "ProRes",
        "aac" => "AAC", "mp3" => "MP3", "opus" => "Opus", "vorbis" => "Vorbis", "flac" => "FLAC", "ac3" => "AC-3", "eac3" => "E-AC-3", "alac" => "ALAC",
        var name when name.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        var name => name.ToUpperInvariant(),
    };

    private static string CompactDuration(TimeSpan time)
    {
        if (time.TotalSeconds < 60) return time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        return time.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}") : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    private void RaiseEditorMedia()
    {
        foreach (var name in (string[])[nameof(EditorDuration), nameof(EditorDurationText), nameof(DurationClockText), nameof(PlaybackClockText), nameof(EditorHasVideo), nameof(EditorHasAudio), nameof(VideoOutputTip), nameof(AudioOutputTip), nameof(IsAudioOnlyMedia), nameof(PreviewAspect), nameof(EditorFacts), nameof(EditorAudioText), nameof(WaveformPlaceholder), nameof(ShowShareCard), nameof(CanShareEditorFile), nameof(ShowShareOriginalInstead), nameof(HasRecentMedia), nameof(SelectionDurationText), nameof(IsSelectionValid), nameof(CanExport), nameof(ShowPreviewSurface), nameof(ShowOriginalDeletedNote)])
            OnPropertyChanged(name);
    }

    // ===== Loading and empty states =====

    /// <summary>True while <see cref="OpenEditorAsync"/> probes the file and builds the waveform.</summary>
    public bool IsEditorLoading { get { EnsureEditorHooks(); return _editorLoading; } }
    public bool IsOpeningEditor { get { EnsureEditorHooks(); return _openingEditor; } }
    public bool IsWaveformLoading { get { EnsureEditorHooks(); return _waveformLoading; } }
    public bool AnimateEditorLoading => (_openingEditor || _waveformLoading) && !EffectiveReducedMotion;
    public string WaveformPlaceholder => _waveformLoading ? "" : _probe is { HasAudio: false } ? "No audio track · drag the handles to trim" : "";

    // OpenEditorAsync announces itself through this hook once its lane is taken. The loading state
    // lasts for the whole open, whatever the status line says meanwhile (copy notices, history
    // errors), and ends when the editor lane is free again.
    partial void OnEditorOpening()
    {
        EnsureEditorHooks();
        _editorOpenLatched = true;
        CancelDetailedWaveform();
        RefreshEditorLoadingState();
    }

    private void RefreshEditorLoadingState()
    {
        if (CanEdit) _editorOpenLatched = false;
        var loading = _editorOpenLatched;
        var changed = SetProperty(ref _editorLoading, loading, nameof(IsEditorLoading));
        changed |= SetProperty(ref _openingEditor, loading && !HasEditor, nameof(IsOpeningEditor));
        changed |= SetProperty(ref _waveformLoading, loading && HasEditor, nameof(IsWaveformLoading));
        if (changed) { OnPropertyChanged(nameof(AnimateEditorLoading)); OnPropertyChanged(nameof(WaveformPlaceholder)); }
    }

    public IReadOnlyList<EditorRecentItem> RecentMedia { get { EnsureEditorHooks(); return _recentMedia; } }

    // ----- Your recordings: the newest game clips and captures in Videos, listed (never opened or sent) while the
    // editor is empty, so a fresh clip is one click from the trim timeline. -----
    // Only the real app supplies the Videos folder (DesktopServices); tests and other hosts read nothing unless they set one.
    private Func<string>? _recordingsFolder;
    internal Func<string> RecordingsFolder { get => _recordingsFolder ??= () => _services.RecordingsRoot ?? ""; set => _recordingsFolder = value; }
    private IReadOnlyList<EditorRecentItem> _recentRecordings = [];
    private int _recordingsScan;
    public IReadOnlyList<EditorRecentItem> RecentRecordings { get { EnsureEditorHooks(); return _recentRecordings; } }
    public bool HasRecentRecordings => !HasEditor && _recentRecordings.Count > 0;

    /// <summary>Looks for new recordings off the UI thread; a newer scan replaces an older one.</summary>
    public async Task RefreshRecentRecordingsAsync()
    {
        var scan = ++_recordingsScan; var folder = RecordingsFolder(); var now = DateTimeOffset.UtcNow;
        IReadOnlyList<Klippi.Infrastructure.RecentRecording> found;
        // Turned off in Settings: the Videos folder is not even read.
        if (!ShowRecentRecordings) found = [];
        else try { found = await Task.Run(() => Klippi.Infrastructure.RecentRecordings.Find(folder, now)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { found = []; }
        if (scan != _recordingsScan || _disposed) return;
        _recentRecordings = found.Select(recording =>
        {
            var path = recording.Path;
            var local = recording.Modified.ToLocalTime();
            var when = local.Date == DateTimeOffset.Now.Date ? "Today " + local.ToString("h:mm tt", CultureInfo.CurrentCulture).ToLowerInvariant() : local.ToString("d MMM", CultureInfo.CurrentCulture);
            return new EditorRecentItem(Path.GetFileNameWithoutExtension(path), $"{recording.Source} · {Size(recording.SizeBytes)} · {when}", false, new AsyncRelayCommand(() => OpenEditorAsync(path)));
        }).ToArray();
        OnPropertyChanged(nameof(RecentRecordings)); OnPropertyChanged(nameof(HasRecentRecordings));
    }
    public bool HasRecentMedia => !HasEditor && _recentMedia.Count > 0;

    // ===== Preview =====

    public string? PreviewError => Playback.LastError;
    public bool HasPreviewError => !string.IsNullOrEmpty(Playback.LastError);
    /// <summary>The native video surface shows for video files whose preview works; otherwise the frame explains why.</summary>
    public bool ShowPreviewSurface => !IsAudioOnlyMedia && !HasPreviewError;

    // ===== Selection =====

    public bool IsSelectionValid => _probe is not null && MediaValidation.TryTimecode(StartTime, out var start) && MediaValidation.TryTimecode(EndTime, out var end) && start >= TimeSpan.Zero && end > start && end <= _probe.Duration + TimeSpan.FromMilliseconds(1);
    public string SelectionDurationText => IsSelectionValid && MediaValidation.TryTimecode(StartTime, out var start) && MediaValidation.TryTimecode(EndTime, out var end) ? Views.EditorTimeConverter.Shown(MediaValidation.Timecode(end - start)) : "Check range";

    // ===== Mute =====

    public bool IsMuted => _muted;
    public bool ShowMutedIcon => _muted || Volume == 0;
    public string MuteLabel => ShowMutedIcon ? "Unmute" : "Mute";
    public IRelayCommand ToggleMuteCommand => _toggleMuteCommand ??= new RelayCommand(ToggleMute);

    private void ToggleMute()
    {
        EnsureEditorHooks();
        _muted = !_muted;
        if (!_muted && Volume == 0) Volume = 60; // Unmuting a zero volume should be audible.
        TryAction(() => Playback.SetVolume(_muted ? 0 : Volume));
        OnPropertyChanged(nameof(IsMuted)); OnPropertyChanged(nameof(ShowMutedIcon)); OnPropertyChanged(nameof(MuteLabel));
    }

    // ===== Export panel =====

    /// <summary>Export needs a valid range and the track the chosen output keeps.</summary>
    public bool CanExport => CanEdit && IsSelectionValid && (AudioOnly ? EditorHasAudio : EditorHasVideo);
    public bool IsTargetSizeCompression => Compression == CompressionPreset.TargetSize;
    public string ExportButtonText => IsExporting ? "Exporting…" : (HasLastExport ? "Export another " : "Export ") + (AudioOnly ? "audio clip" : "video clip");
    /// <summary>After an export, sharing the result becomes the main action, so Export steps back.</summary>
    public bool IsExportPrimary => !HasLastExport;
    public bool HasEditorEstimate => _editorEstimate is not null;
    public bool HasEstimatedSize => _editorEstimate?.EstimatedBytes is not null;
    public bool HasUnknownSize => _editorEstimate is { EstimatedBytes: null };
    public bool HasEstimateProblem => _probe is not null && _editorEstimate is null;
    public string EstimateOriginalText => _probe is null ? "—" : Size(_probe.SizeBytes);
    public string EstimateOutputText => _editorEstimate?.EstimatedBytes is { } bytes ? "≈ " + Size(bytes) : "Known after export";
    public string EstimateDetailText => _editorEstimate is { } estimate ? ClockText(estimate.Duration, EditorDuration) + " selected" + (string.IsNullOrWhiteSpace(estimate.Explanation) ? "" : " · " + estimate.Explanation) : "";
    public string EstimateQualityText => !IsExactClip ? "Lossless copy" : AudioOnly && Compression is not (CompressionPreset.Off or CompressionPreset.TargetSize) ? AudioPresetQualityText() : Compression switch
    {
        CompressionPreset.Off => "Full quality",
        CompressionPreset.TargetSize => $"Target {TargetSizeMb} MB",
        CompressionPreset.VerySmall => "Very small",
        var preset => preset + " compression",
    };

    /// <summary>
    /// An audio clip has no video to compress: a preset only lowers the audio bitrate, so the chip
    /// names the bitrate it really uses (WAV and FLAC have none and stay lossless).
    /// </summary>
    private string AudioPresetQualityText()
    {
        if (ExportAudioFormat is AudioFormat.Wav or AudioFormat.Flac) return "Lossless audio";
        var kbps = MediaService.AudioPresetKbps(Compression) is { } preset ? Math.Min(preset, ExportAudioKbps) : ExportAudioKbps;
        return kbps.ToString(CultureInfo.InvariantCulture) + " kbps audio";
    }

    private void RefreshEditorEstimate()
    {
        ExportEstimate? estimate = null;
        if (_probe is not null)
        {
            try { estimate = _services.Media.Estimate(_probe, BuildExportOptions("estimate.mp4")); }
            catch (Exception) { /* EstimateText already carries the friendly validation message. */ }
        }
        _editorEstimate = estimate;
        foreach (var name in (string[])[nameof(HasEditorEstimate), nameof(HasEstimatedSize), nameof(HasUnknownSize), nameof(HasEstimateProblem), nameof(EstimateOriginalText), nameof(EstimateOutputText), nameof(EstimateDetailText), nameof(EstimateQualityText)])
            OnPropertyChanged(name);
    }

    // A messaging preset only describes the settings it applied; once compression or the target
    // changes by other means, the picker returns to "no preset" so it never shows a stale size.
    // ----- Fit for (one-click size targets) -----
    /// <summary>The size target in MB while a messaging preset is active (0 otherwise), so the Fit for buttons can show which one is on.</summary>
    public int FitTargetMb => AttachmentPreset != NoAttachmentPreset && int.TryParse(AttachmentPreset.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mb) ? mb : 0;
    private IRelayCommand<string>? _fitForCommand;
    /// <summary>Discord 10, Email 25, Nitro Basic 50, Nitro 500: an exact cut aimed just under that size (same 5% margin as the Messaging target).</summary>
    public IRelayCommand<string> FitForCommand => _fitForCommand ??= new RelayCommand<string>(megabytes =>
    {
        var preset = megabytes + " MB";
        if (!HasEditor || !AttachmentPresets.Contains(preset)) return;
        AttachmentPreset = AttachmentPreset == preset ? NoAttachmentPreset : preset;
        if (AttachmentPreset == NoAttachmentPreset) { Compression = CompressionPreset.Off; Status = "Size target off. The clip keeps its own size."; }
    });

    private void ResetStaleAttachmentPreset(string changed)
    {
        if (AttachmentPreset == NoAttachmentPreset || !int.TryParse(AttachmentPreset.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mb)) return;
        var stale = changed switch
        {
            nameof(Compression) => Compression != CompressionPreset.TargetSize,
            nameof(ClipMode) => !IsExactClip,
            nameof(TargetSizeMb) => TargetSizeMb != (mb * .95).ToString("0.##", CultureInfo.InvariantCulture),
            _ => false,
        };
        if (stale) AttachmentPreset = NoAttachmentPreset;
    }

    public string SoundboardLimitText { get { EnsureEditorHooks(); return _soundboardLimitText; } }

    private void RefreshSoundboardText()
    {
        var text = $"Preset keeps clips under {_settings.SoundboardMaxSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s and {Size(_settings.SoundboardTargetBytes)}. Adjust the limits in Settings if Discord changes them.";
        SetProperty(ref _soundboardLimitText, text, nameof(SoundboardLimitText));
    }

    public bool ExportProgressIndeterminate => IsExporting && !HasExportPercent && !EffectiveReducedMotion;
    public bool ShareProgressIndeterminate => IsSharing && !HasSharePercent && !EffectiveReducedMotion;

    // ===== Latest export (this editing session) =====

    public bool HasLastExport => _lastExport is not null;
    public string LastExportName => _lastExport?.Title ?? "";
    public string LastExportDetail => _lastExport is { } entry ? $"{Size(entry.SizeBytes)} · {entry.Mode} · verified" : "";
    /// <summary>The editor closes only when a verified export deleted its original as requested.</summary>
    public bool ShowOriginalDeletedNote => HasLastExport && !HasEditor;
    public IRelayCommand OpenLastExportCommand => _openLastExportCommand ??= new RelayCommand(() => { if (_lastExport is { } entry) OpenFile(entry.FilePath); });
    public IRelayCommand RevealLastExportCommand => _revealLastExportCommand ??= new RelayCommand(() => { if (_lastExport is { } entry) RevealFile(entry.FilePath); });
    public IAsyncRelayCommand ShareLastExportCommand => _shareLastExportCommand ??= new AsyncRelayCommand(() => _lastExport is { } entry ? ShareFileAsync(entry.FilePath) : ShareEditorAsync());
    public IAsyncRelayCommand EditLastExportCommand => _editLastExportCommand ??= new AsyncRelayCommand(() => _lastExport is { } entry ? OpenEditorAsync(entry.FilePath) : Task.CompletedTask);

    // ===== Share card =====

    public bool HasShareUrl => !string.IsNullOrEmpty(ShareUrl);
    /// <summary>The share card (and the output column) also stays for an export whose original was deleted, for a Library share and while a large share waits for confirmation.</summary>
    public bool ShowShareCard => HasEditor || HasLastExport || HasShareUrl || IsSharing || HasPendingLargeShare;
    /// <summary>Share the open file directly (no export from this session to offer instead).</summary>
    public bool CanShareEditorFile => HasEditor && !HasLastExport;
    public bool ShowShareOriginalInstead => HasEditor && HasLastExport;

    /// <summary>"Share link for clip.mp4", so a link kept from an earlier file never reads as the open file's.</summary>
    public string ShareLinkLabel => string.IsNullOrEmpty(ShareFileName) ? "Share link" : "Share link for " + ShareFileName;
    /// <summary>The kept link opens another file than the one in the editor (an earlier file or a Library share).</summary>
    public bool ShareLinkIsForOtherFile => HasShareUrl && HasEditor && _shareLinkPath is { } linked && !SamePath(linked, EditorPath);
    public string ShareLinkOtherFileText => ShareLinkIsForOtherFile ? $"This link opens {ShareFileName}, not the file open in the editor. Use Share to make a link for this file." : "";
    private string? _shareLinkPath;
    private static bool SamePath(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
    }
    private void RaiseShareLink() { OnPropertyChanged(nameof(ShareLinkLabel)); OnPropertyChanged(nameof(ShareLinkIsForOtherFile)); OnPropertyChanged(nameof(ShareLinkOtherFileText)); }

    // ===== Soundboard preset =====

    private const AudioFormat SoundboardFormat = AudioFormat.Mp3;
    private const int SoundboardKbps = 128;
    private const string SoundboardSampleRate = "48000";
    /// <summary>The audio settings the soundboard preset replaced, put back when the preset ends.</summary>
    private sealed record SoundboardRestore(AudioFormat Format, int Kbps, bool Mono, string SampleRate);
    private SoundboardRestore? _soundboardRestore;

    /// <summary>
    /// Ends the soundboard limits (Video clip chosen, another format, another file opened).
    /// Returns whether they were on. A note, when given, goes to the status line.
    /// </summary>
    private bool EndSoundboard(string? note)
    {
        if (!_soundboard) { _soundboardRestore = null; return false; }
        Soundboard = false;
        if (note is not null) Status = note;
        return true;
    }

    /// <summary>Puts back each audio setting the preset replaced, unless the user has changed it since.</summary>
    private void RestoreAfterSoundboard()
    {
        if (_soundboardRestore is not { } restore) return;
        _soundboardRestore = null;
        if (ExportAudioFormat == SoundboardFormat) ExportAudioFormat = restore.Format;
        if (ExportAudioKbps == SoundboardKbps) ExportAudioKbps = restore.Kbps;
        if (Mono) Mono = restore.Mono;
        if (SampleRate == SoundboardSampleRate) SampleRate = restore.SampleRate;
    }

    // ===== Export audio bitrates =====

    private IReadOnlyList<int> _exportBitrates = AllBitrates;
    private string _exportBitrateNote = "";
    /// <summary>The editor's audio bitrates: only those the chosen format's encoder accepts for the channels and sample rate it will use.</summary>
    public IReadOnlyList<int> ExportBitrates => _exportBitrates;
    /// <summary>Says when a bitrate was moved to one the encoder accepts; empty otherwise.</summary>
    public string ExportBitrateNote => _exportBitrateNote;
    public bool HasExportBitrateNote => _exportBitrateNote.Length > 0;

    private void RefreshExportBitrates()
    {
        var limits = AudioBitrateLimits.None;
        if (_probe is { } probe && IsExactClip)
        {
            var options = new ExportOptions { InputPath = EditorPath, OutputPath = "bitrates.mp4", ClipMode = ClipMode, AudioOnly = AudioOnly, AudioFormat = ExportAudioFormat, AudioKbps = ExportAudioKbps, Mono = Mono, SampleRate = ParseChoice(SampleRate) };
            try { limits = MediaService.AudioBitrateLimitsFor(probe, options); }
            catch (Exception error) when (error is MediaOperationException or ArgumentException) { limits = AudioBitrateLimits.None; }
        }
        IReadOnlyList<int> list = AllBitrates.Where(kbps => kbps >= limits.MinKbps && kbps <= limits.MaxKbps).ToArray();
        if (list.Count == 0) list = [Math.Clamp(limits.MaxKbps, 8, 320)];
        if (!_exportBitrates.SequenceEqual(list)) { _exportBitrates = list; OnPropertyChanged(nameof(ExportBitrates)); }
        var note = "";
        var kbps = _exportAudioKbps;
        if (kbps > limits.MaxKbps || kbps < limits.MinKbps)
        {
            var chosen = kbps > limits.MaxKbps ? list.Where(value => value <= limits.MaxKbps).DefaultIfEmpty(list[0]).Max() : list.Where(value => value >= limits.MinKbps).DefaultIfEmpty(list[^1]).Min();
            note = $"{limits.Description} accepts {limits.MinKbps.ToString(CultureInfo.InvariantCulture)}–{limits.MaxKbps.ToString(CultureInfo.InvariantCulture)} kbps, so the bitrate is now {chosen.ToString(CultureInfo.InvariantCulture)} kbps instead of {kbps.ToString(CultureInfo.InvariantCulture)}.";
            ExportAudioKbps = chosen;
        }
        if (_exportBitrateNote != note) { _exportBitrateNote = note; OnPropertyChanged(nameof(ExportBitrateNote)); OnPropertyChanged(nameof(HasExportBitrateNote)); }
    }

    /// <summary>
    /// The waveform points an open asks for: the overview, or straight away the detail a longer
    /// file gets (about 12 per second), since one decode serves any point count.
    /// </summary>
    internal static int OpenWaveformPoints(TimeSpan duration) => (int)Math.Clamp(Math.Max(600, Math.Round(duration.TotalSeconds * DetailWaveformPointsPerSecond)), 16, MaxWaveformPoints);

    // ===== Detailed waveform =====

    // OpenEditorAsync draws a 600-point overview. Longer files get a second, detailed pass in the
    // background (about 12 points per second, up to the service limit) so zooming shows real
    // transients; the overview stays usable meanwhile and the view keeps its zoom when it arrives.
    // Any other operation (export, share, another open) cancels it first, so it never competes
    // with real work or holds the file open while an export deletes the original; it resumes
    // once that operation ends and the file is still open.
    private void StartDetailedWaveform()
    {
        CancelDetailedWaveform();
        if (_disposed || _probe is not { HasAudio: true } probe || ReferenceEquals(probe, _waveformDetailFailed) || string.IsNullOrEmpty(EditorPath)) return;
        var points = (int)Math.Clamp(Math.Round(probe.Duration.TotalSeconds * DetailWaveformPointsPerSecond), 16, MaxWaveformPoints);
        if (points <= Waveform.Count * 1.25) return;
        var cancellation = new CancellationTokenSource();
        _waveformDetail = cancellation;
        _ = LoadDetailedWaveformAsync(EditorPath, probe, points, cancellation);
    }

    private void CancelDetailedWaveform()
    {
        if (_waveformDetail is not { } cancellation) return;
        _waveformDetail = null;
        cancellation.Cancel(); // The load disposes its own source when it finishes.
    }

    private async Task LoadDetailedWaveformAsync(string path, ProbeInfo probe, int points, CancellationTokenSource cancellation)
    {
        try
        {
            var detailed = await _services.Media.WaveformAsync(path, points, cancellation.Token);
            if (cancellation.IsCancellationRequested || _disposed || !ReferenceEquals(_probe, probe) || detailed.Count <= Waveform.Count) return;
            Waveform = detailed;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // The overview waveform stays; extra detail is optional, so do not retry this file.
            if (!cancellation.IsCancellationRequested) { _waveformDetailFailed = probe; CaptureDiagnostics(error); }
        }
        finally
        {
            if (ReferenceEquals(_waveformDetail, cancellation)) _waveformDetail = null;
            cancellation.Dispose();
        }
    }

    // ===== Change routing =====

    private void EnsureEditorHooks()
    {
        if (_editorHooksAttached || _disposed) return;
        _editorHooksAttached = true;
        PropertyChanged += OnEditorStateChanged;
        Playback.PropertyChanged += OnEditorPlaybackChanged;
        History.CollectionChanged += (_, _) => QueueEditorLists();
        RefreshEditorLoadingState();
        RefreshSoundboardText();
        QueueEditorLists(force: true);
    }

    private void OnEditorPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(IPlaybackService.LastError)) return;
        OnPropertyChanged(nameof(PreviewError)); OnPropertyChanged(nameof(HasPreviewError)); OnPropertyChanged(nameof(ShowPreviewSurface));
        if (HasPreviewError && HasEditor) Status = "Preview could not play this file. Trimming and export still work.";
    }

    private void OnEditorStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HasEditor) or nameof(SelectedTab)) OnPropertyChanged(nameof(PageMaxWidth));
        // However the clock was refreshed (a new file, a seek), remember what is on screen now.
        if (e.PropertyName == nameof(PlaybackClockText)) _shownClock = PlaybackClockText;
        switch (e.PropertyName)
        {
            case nameof(HasEditor) or nameof(EditorSummary):
                if (!HasEditor) CancelDetailedWaveform();
                RaiseEditorMedia(); RefreshEditorLoadingState(); RefreshEditorEstimate();
                OnPropertyChanged(nameof(HasRecentRecordings));
                break;
            case nameof(SelectedTab) when IsEditor && !HasEditor:
                _ = RefreshRecentRecordingsAsync();
                break;
            case nameof(IsBusy):
                RefreshEditorLoadingState(); OnPropertyChanged(nameof(CanExport));
                if (!CanEdit) CancelDetailedWaveform();
                else if (HasEditor && _waveformDetail is null) StartDetailedWaveform();
                break;
            case nameof(Status):
                RefreshSoundboardText();
                break;
            case nameof(StartTime) or nameof(EndTime):
                OnPropertyChanged(nameof(SelectionDurationText)); OnPropertyChanged(nameof(IsSelectionValid)); OnPropertyChanged(nameof(CanExport));
                break;
            case nameof(AttachmentPreset):
                OnPropertyChanged(nameof(FitTargetMb));
                break;
            case nameof(CanExport) or nameof(EditorHasVideo):
                OnPropertyChanged(nameof(CanExportGif));
                break;
            case nameof(ClipMode) or nameof(Compression) or nameof(TargetSizeMb):
                ResetStaleAttachmentPreset(e.PropertyName!);
                OnPropertyChanged(nameof(IsTargetSizeCompression)); RefreshEditorEstimate();
                break;
            case nameof(EstimateText):
                RefreshEditorEstimate();
                break;
            case nameof(AudioOnly):
                OnPropertyChanged(nameof(ExportButtonText)); OnPropertyChanged(nameof(CanExport)); RefreshEditorEstimate();
                break;
            case nameof(Volume):
                // Moving the slider is an explicit volume choice, so it also ends mute.
                if (_muted) { _muted = false; OnPropertyChanged(nameof(IsMuted)); }
                OnPropertyChanged(nameof(ShowMutedIcon)); OnPropertyChanged(nameof(MuteLabel));
                break;
            case nameof(ShareUrl):
                OnPropertyChanged(nameof(HasShareUrl)); OnPropertyChanged(nameof(ShowShareCard)); RaiseShareLink();
                break;
            case nameof(ShareFileName) or nameof(EditorPath):
                RaiseShareLink();
                break;
            case nameof(IsSharing):
                OnPropertyChanged(nameof(ShowShareCard)); OnPropertyChanged(nameof(ShareProgressIndeterminate));
                break;
            case nameof(HasPendingLargeShare):
                OnPropertyChanged(nameof(ShowShareCard));
                break;
            case nameof(IsExporting) or nameof(HasExportPercent) or nameof(HasSharePercent) or nameof(EffectiveReducedMotion):
                OnPropertyChanged(nameof(ExportButtonText)); OnPropertyChanged(nameof(ExportProgressIndeterminate)); OnPropertyChanged(nameof(ShareProgressIndeterminate)); OnPropertyChanged(nameof(AnimateEditorLoading));
                break;
        }
    }

    // History reloads replace the list; Library searches only refill the visible rows from the same
    // list, so they never trigger a rescan. File checks run off the UI thread (slow or sleeping
    // drives must not freeze the window) and only the newest scan is applied.
    private void QueueEditorLists(bool force = false)
    {
        if (_disposed || !force && ReferenceEquals(_history, _listsHistory)) return;
        _listsHistory = _history;
        var generation = ++_listsGeneration;
        var openedAt = _editorOpenedAt;
        var candidates = new List<(HistoryEntry Entry, bool IsAudio)>(RecentScanLimit);
        foreach (var entry in _history)
        {
            if (candidates.Count >= RecentScanLimit) break;
            var extension = Path.GetExtension(entry.FilePath);
            var isAudio = EditorAudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
            if (isAudio || EditorVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) candidates.Add((entry, isAudio));
        }
        _ = ScanEditorListsAsync(candidates, openedAt, generation);
    }

    private async Task ScanEditorListsAsync(List<(HistoryEntry Entry, bool IsAudio)> candidates, DateTimeOffset openedAt, int generation)
    {
        // The scan runs on the thread pool; the await resumes on the UI thread's context, so the
        // background work never touches the dispatcher itself.
        var (recent, last) = await Task.Run(() =>
        {
            var found = new List<(HistoryEntry Entry, bool IsAudio)>(RecentShown);
            HistoryEntry? export = null;
            foreach (var (entry, isAudio) in candidates)
            {
                if (found.Count >= RecentShown && (export is not null || entry.Date < openedAt)) break;
                bool exists;
                try { exists = File.Exists(entry.FilePath); } catch (Exception) { exists = false; }
                if (!exists) continue;
                if (export is null && entry.Source == ExportHistorySource && entry.Date >= openedAt) export = entry;
                if (found.Count < RecentShown) found.Add((entry, isAudio));
            }
            return (found, export);
        });
        if (!_disposed && generation == _listsGeneration) ApplyEditorLists(recent, last);
    }

    private void ApplyEditorLists(List<(HistoryEntry Entry, bool IsAudio)> recent, HistoryEntry? last)
    {
        _recentMedia = recent.Select(item =>
        {
            var (entry, isAudio) = item;
            var exported = entry.Source == ExportHistorySource;
            var detail = $"{(exported ? "Exported clip" : "Saved")} · {Size(entry.SizeBytes)} · {entry.Date.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
            var path = entry.FilePath;
            return new EditorRecentItem(entry.Title, detail, isAudio, new AsyncRelayCommand(() => OpenEditorAsync(path)));
        }).ToArray();
        _lastExport = last;
        foreach (var name in (string[])[nameof(RecentMedia), nameof(HasRecentMedia), nameof(HasLastExport), nameof(LastExportName), nameof(LastExportDetail), nameof(CanShareEditorFile), nameof(ShowShareOriginalInstead), nameof(ShowShareCard), nameof(ShowOriginalDeletedNote), nameof(IsExportPrimary), nameof(ExportButtonText)])
            OnPropertyChanged(name);
    }
}

/// <summary>A recent local file the empty editor offers to reopen.</summary>
public sealed record EditorRecentItem(string Title, string Detail, bool IsAudio, IAsyncRelayCommand EditCommand)
{
    public string EditLabel => "Edit " + Title;
}
