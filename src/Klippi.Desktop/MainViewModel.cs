using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;
using Avalonia.Media.Imaging;

namespace Klippi.Desktop;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DesktopServices _services;
    private readonly DownloadQueue _queue;
    private readonly HashSet<Guid> _editAfter = [];
    private readonly HashSet<Guid> _finishedHandled = [];
    // Replace jobs, each with the saved record it supersedes once it has overwritten that file.
    private readonly Dictionary<Guid, HistoryEntry> _supersededRecords = [];
    private AppSettings _settings = new();
    private List<HistoryEntry> _history = [];
    private Task? _shutdownTask;
    private bool _disposed;
    private bool _initialized;
    private int _selectedTab;
    private string _url = "";
    private string _status = "Ready. Paste a media link or open a local file.";
    private MediaInfo? _media;
    private string _historySearch = "";
    private ProbeInfo? _probe;
    private string _editorPath = "";
    private string _startTime = "00:00:00.000";
    private string _endTime = "00:00:00.000";
    private double _playbackPosition;
    private int _volume = 80;
    private IReadOnlyList<float> _waveform = [];
    private string _shareUrl = "";
    private string _shareInfo = "Sharing is optional. Only upload media you are allowed to share.";
    private string _estimateText = "Open a local file to estimate an export.";
    private bool _queuePaused;
    private bool _previewFullscreen;
    private string _theme = "System";
    private string _downloadDirectory = new AppSettings().DownloadDirectory;
    private string _audioDirectory = "", _clipsDirectory = "", _archivesDirectory = "", _jobDirectory = "";
    private DownloadMode _downloadMode = DownloadMode.VideoAudio;
    private int _concurrent = 1;
    private bool _detectClipboard = true;
    private bool _showRecentRecordings = true;
    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _reducedMotion;
    private bool? _systemReducedMotion;
    private bool _notifyCompleted;
    private CloseBehavior _closeBehavior;
    private string _retentionDays = "";
    private string _soundboardSeconds = "5.2";
    private string _soundboardBytes = "500000";
    private string _quality = "Best available";
    private CompressionPreset _compression;
    private string _targetSizeMb = "9.5";
    private string _attachmentPreset = "Custom target";
    private bool _audioOnly;
    private ClipMode _clipMode = ClipMode.Exact;
    private bool _soundboard;
    private bool _deleteOriginal;
    private double _exportVolume = 1;
    private bool _normalize, _trimSilence, _mono;
    private string _fadeIn = "0", _fadeOut = "0";
    private string _sampleRate = "Keep original";
    private Bitmap? _thumbnail;
    private EncoderPreference _encoder = EncoderPreference.Auto;
    private VideoCodec _exportCodec = VideoCodec.H264;
    private AudioFormat _exportAudioFormat = AudioFormat.Mp3;
    private int _exportAudioKbps = 192;
    private string _exportHeight = "Original";
    private string _exportFps = "Original";
    private bool _exportHdr;
    private string _diagnostics = "";
    private string _temporaryCleanupStatus = "Cleanup runs only when you choose Clean temporary files.";
    private string _rangeStart = "1", _rangeEnd = "1";
    private readonly Dictionary<string, Bitmap> _thumbnailCache = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Uri> _jobThumbnails = [];
    private string? _lastClipboardText;
    private CancellationTokenSource? _thumbnailLoad;
    private System.Threading.Timer? _historySearchDebounce;
    private readonly HttpClient _thumbnailHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public MainViewModel(DesktopServices services)
    {
        _services = services;
        _queue = new DownloadQueue(services.Downloads, services.Store, 1);
        _queue.JobChanged += QueueChanged;
        Playback.PropertyChanged += PlaybackChanged;
        AnalyseCommand = new AsyncRelayCommand(AnalyseAsync);
        DownloadCommand = new AsyncRelayCommand(() => QueueDownloadAsync(false));
        DownloadEditCommand = new AsyncRelayCommand(() => QueueDownloadAsync(true));
        PasteCommand = new AsyncRelayCommand(PasteAsync);
        QueueBatchCommand = new AsyncRelayCommand(QueueBatchAsync);
        SelectBatchCommand = new RelayCommand(() => { foreach (var item in BatchLinks) item.Selected = true; });
        ClearBatchSelectionCommand = new RelayCommand(() => { foreach (var item in BatchLinks) item.Selected = false; });
        DismissBatchCommand = new RelayCommand(() => { BatchLinks.Clear(); OnPropertyChanged(nameof(HasBatch)); OnPropertyChanged(nameof(BatchSummary)); });
        SelectRangeCommand = new RelayCommand(SelectPlaylistRange);
        CheckSpaceCommand = new RelayCommand(() => OnPropertyChanged(nameof(AvailableSpace)));
        OpenMediaCommand = new AsyncRelayCommand(async () => { var path = await _services.Platform.PickMediaAsync(); if (path is not null) await OpenEditorAsync(path); });
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ShareCommand = new AsyncRelayCommand(ShareEditorAsync);
        CopyShareCommand = new AsyncRelayCommand(async () => { if (!string.IsNullOrWhiteSpace(ShareUrl)) await CopyToClipboardAsync(ShareUrl, "Share link copied. Keep the full link private."); });
        OpenShareCommand = new RelayCommand(() => TryAction(() => { if (Uri.TryCreate(ShareUrl, UriKind.Absolute, out var uri) && ((uri.Scheme == "https" && uri.Host.Equals("file.kiwi", StringComparison.OrdinalIgnoreCase)) || IsWatchLinkUrl(uri)) && string.IsNullOrEmpty(uri.UserInfo)) _services.Platform.OpenUri(uri); else Status = "There is no valid share link to open yet."; }));
        ChooseFolderCommand = new AsyncRelayCommand(async () => { var folder = await _services.Platform.PickFolderAsync(); if (folder is not null) DownloadDirectory = folder; });
        ChooseJobFolderCommand = new AsyncRelayCommand(async () => { var folder = await _services.Platform.PickFolderAsync(); if (folder is not null) { JobDirectory = folder; Status = "This download will use the chosen folder. Your saved default is unchanged."; } });
        ResetJobFolderCommand = new RelayCommand(() => JobDirectory = "");
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        InspectComponentsCommand = new AsyncRelayCommand(() => RunAsync(OperationLane.Maintenance, InspectComponentsAsync));
        CleanTemporaryFilesCommand = new AsyncRelayCommand(CleanTemporaryFilesAsync, () => CanCleanTemporaryFiles);
        ProvisionCommand = new AsyncRelayCommand(() => RunComponentTaskAsync((service, progress, ct) => service.ProvisionAsync(progress, ct), "Components checked and ready where listed below."));
        UpdateDownloaderCommand = new AsyncRelayCommand(() => RunComponentTaskAsync((service, progress, ct) => service.UpdateDownloaderAsync(progress, ct), "Downloader update checked."));
        RollbackComponentsCommand = new AsyncRelayCommand(() => RunComponentTaskAsync((service, progress, ct) => service.RollbackAsync(progress, ct), "Previous media components verified and restored."));
        CopyDiagnosticsCommand = new AsyncRelayCommand(() => CopyToClipboardAsync(Diagnostics, "Redacted diagnostics copied."));
        SupportedSitesCommand = new AsyncRelayCommand(() => RunAsync(OperationLane.Maintenance, async ct => { var sites = await _services.Downloads.SupportedSitesAsync(ct); SupportedSitesText = string.Join(" · ", sites); OnPropertyChanged(nameof(SupportedSitesText)); Status = $"The installed downloader lists {sites.Count:N0} extractors. Support still depends on the site and content."; }));
        ClearHistoryCommand = new AsyncRelayCommand(() => RunAsync(OperationLane.Maintenance, async ct => { await _services.Store.ClearHistoryAsync(ct); await ReloadHistoryAsync(ct); Status = "History cleared. Your media files were kept."; }));
        PauseQueueCommand = new RelayCommand(() => { if (_queuePaused) _queue.Resume(); else _queue.Pause(); QueuePaused = !_queuePaused; PersistQueueIfChanged(); });
        ClearCompletedCommand = new RelayCommand(() => { _queue.ClearCompleted(); SynchroniseQueue(); });
        CancelOperationCommand = new RelayCommand(CancelCurrentOperations);
        PlayCommand = new RelayCommand(() => TryAction(Playback.Toggle));
        StopPlaybackCommand = new RelayCommand(() => TryAction(Playback.Stop));
        SetStartCommand = new RelayCommand(() => StartTime = MediaValidation.Timecode(Playback.Time));
        SetEndCommand = new RelayCommand(() => EndTime = MediaValidation.Timecode(Playback.Time));
        SoundboardCommand = new RelayCommand(ApplySoundboardPreset);
        UpdateEstimateCommand = new RelayCommand(UpdateEstimate);
        SelectPlaylistCommand = new RelayCommand(() => { foreach (var item in Playlist) item.Selected = true; });
        ClearPlaylistCommand = new RelayCommand(() => { foreach (var item in Playlist) item.Selected = false; });
    }
    public IPlaybackService Playback => _services.Playback;
    public event Action<string>? CompletionNotice;
    public string AppName => AppIdentity.DisplayName;
    public string AppVersion => "v" + AppIdentity.Version + (IsBeta ? " · beta" : AppIdentity.WatchLinksIncluded ? " · personal" : "");
    public string DataMode => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker")) ? "Portable settings beside the app" : "Settings saved locally on this device";
    public int SelectedTab { get => _selectedTab; set { SetProperty(ref _selectedTab, value); OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(PageSubtitle)); OnPropertyChanged(nameof(IsDownload)); OnPropertyChanged(nameof(IsQueue)); OnPropertyChanged(nameof(IsHistory)); OnPropertyChanged(nameof(IsEditor)); OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsBetaTest)); } }
    public bool IsDownload => SelectedTab == 0;
    public bool IsQueue => SelectedTab == 1;
    public bool IsHistory => SelectedTab == 2;
    public bool IsEditor => SelectedTab == 3;
    public bool IsSettings => SelectedTab == 4;
    public string PageTitle => SelectedTab switch { 1 => "Your queue", 2 => "Your library", 3 => "Edit a clip", 4 => "Make it yours", 5 => "Beta test", _ => "Save something worth keeping" };
    public string Url { get => _url; set { if (SetProperty(ref _url, value)) { _media = null; _playlistShape = PlaylistShape.None; _thumbnailLoad?.Cancel(); Thumbnail = null; Playlist.Clear(); FormatDetails.Clear(); OnPropertyChanged(nameof(HasMedia)); OnPropertyChanged(nameof(IsPlaylist)); OnPropertyChanged(nameof(MediaTitle)); OnPropertyChanged(nameof(MediaSummary)); OnPropertyChanged(nameof(SourceDetails)); OnPropertyChanged(nameof(PlaylistSummary)); } } }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool AnimateBusyIndicator => IsBusy && !EffectiveReducedMotion;
    // Which long operation is running, so pages can show skeletons and inline progress.
    private bool _analysing, _exporting, _sharing;
    private double? _operationPercent;
    public bool IsAnalysing { get => _analysing; private set => SetProperty(ref _analysing, value); }
    public bool IsExporting { get => _exporting; private set => SetProperty(ref _exporting, value); }
    public bool IsSharing { get => _sharing; private set => SetProperty(ref _sharing, value); }
    public double OperationPercent => _operationPercent ?? 0;
    public bool HasOperationPercent => _operationPercent is not null;
    public bool ComponentsReady => Components.Count > 0 && Components.All(c => c.Ready);
    private void SetOperationPercent(double? percent)
    {
        var clamped = percent is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : (double?)null;
        if (_operationPercent == clamped) return;
        var hadPercent = _operationPercent is not null; _operationPercent = clamped;
        OnPropertyChanged(nameof(OperationPercent)); if (hadPercent != (clamped is not null)) OnPropertyChanged(nameof(HasOperationPercent));
    }
    public bool HasMedia => _media is not null;
    public bool IsPlaylist => _media?.IsPlaylist ?? false;
    public string MediaTitle => _media?.Title ?? "Your next favourite, saved locally.";
    public string MediaSummary => _media is null ? "You choose the quality and output. Nothing downloads until you click Download." : $"{_media.Creator ?? "Unknown creator"} · {_media.Source ?? _media.Url.Host} · {(_media.Duration is { } time ? MediaValidation.Timecode(time) : "Duration unavailable")}";
    public string MediaDescription => _media?.Description ?? "";
    public Bitmap? Thumbnail { get => _thumbnail; private set => SetProperty(ref _thumbnail, value); }
    public string SourceDetails
    {
        get
        {
            if (_media is null) return "";
            var publication = _media.PublishedAt is { } date ? "Published " + date.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Publication date unavailable";
            var sizes = _media.Formats.Where(f => f.SizeBytes is > 0).Select(f => f.SizeBytes!.Value).ToArray();
            return publication + " · " + (sizes.Length > 0 ? $"Reported format sizes: {Size(sizes.Min())}–{Size(sizes.Max())}; final size depends on your choice." : "Source size unavailable; final size is checked after download.");
        }
    }
    public ObservableCollection<PlaylistChoice> Playlist { get; } = [];
    public ObservableCollection<BatchLinkChoice> BatchLinks { get; } = [];
    public ObservableCollection<PendingDownload> DuplicateChoices { get; } = [];
    public bool HasBatch => BatchLinks.Count > 0;
    public bool HasDuplicates => DuplicateChoices.Count > 0;
    public string BatchSummary => $"{BatchLinks.Count} links · {BatchLinks.Count(p => p.Selected)} selected. Nothing is queued until you choose Queue selected links.";
    public string PlaylistSummary => $"{PlaylistExtentText()} · {Playlist.Count(p => p.Selected)} selected. Total size is unavailable until individual entries are analysed.";
    public string RangeStart { get => _rangeStart; set => SetProperty(ref _rangeStart, value); }
    public string RangeEnd { get => _rangeEnd; set => SetProperty(ref _rangeEnd, value); }
    public string AvailableSpace
    {
        get { try { var path = Path.GetFullPath(SelectedOutputDirectory); var drive = new DriveInfo(Path.GetPathRoot(path)!); return drive.IsReady ? Size(drive.AvailableFreeSpace) + " available on this drive. Merging/exporting also needs temporary space." : "Drive space is unavailable."; } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return "Drive space is unavailable for this folder."; } }
    }
    public ObservableCollection<string> FormatDetails { get; } = [];
    public ObservableCollection<string> Qualities { get; } = ["Best available", "Smallest available"];
    public ObservableCollection<QueueRow> Queue { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<ComponentInfo> Components { get; } = [];
    public ObservableCollection<EncoderInfo> Encoders { get; } = [];
    public string SupportedSitesText { get; private set; } = "Click Supported sites to read the installed downloader's actual extractor list.";
    public bool QueuePaused { get => _queuePaused; private set { SetProperty(ref _queuePaused, value); OnPropertyChanged(nameof(PauseQueueLabel)); } }
    public string PauseQueueLabel => QueuePaused ? "Resume new starts" : "Pause new starts";
    // Typing filters after a short pause instead of rebuilding the list on every keystroke.
    public string HistorySearch { get => _historySearch; set { if (SetProperty(ref _historySearch, value)) { _historySearchDebounce?.Dispose(); _historySearchDebounce = new System.Threading.Timer(_ => Dispatcher.UIThread.Post(() => { if (!_disposed) FilterHistory(); }), null, 180, Timeout.Infinite); } } }
    public string Quality { get => _quality; set => SetProperty(ref _quality, value); }
    public DownloadMode DownloadMode { get => _downloadMode; set { if (SetProperty(ref _downloadMode, value)) { OnPropertyChanged(nameof(IsAudioDownload)); OnPropertyChanged(nameof(IsVideoDownload)); OnPropertyChanged(nameof(IsArchiveDownload)); OutputFolderChanged(); } } }
    public bool IsAudioDownload => DownloadMode == DownloadMode.Audio;
    public bool IsVideoDownload => DownloadMode is DownloadMode.VideoAudio or DownloadMode.VideoOnly;
    public bool IsArchiveDownload => DownloadMode == DownloadMode.Archive;
    public MediaContainer Container { get; set; } = MediaContainer.Auto;
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Original;
    private int _audioKbps = 320;
    public int AudioKbps { get => _audioKbps; set => SetProperty(ref _audioKbps, value); }
    public VideoCodec VideoCodec { get; set; } = VideoCodec.Original;
    public string DownloadFps { get; set; } = "Original";
    public bool HdrToSdr { get; set; }
    public string AudioLanguage { get; set; } = "";
    public string SubtitleLanguages { get; set; } = "";
    public bool AutomaticCaptions { get; set; }
    public bool EmbedThumbnail { get; set; }
    public bool EmbedMetadata { get; set; } = true;
    public string Browser { get; set; } = "None";
    public string NamingTemplate { get; set; } = "";
    public bool ReplaceExisting { get; set; }
    public bool ArchiveZip { get; set; }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public bool CanCleanTemporaryFiles => CanMaintain && _services.TemporaryWorkspaces is not null;
    public string TemporaryCleanupStatus { get => _temporaryCleanupStatus; private set => SetProperty(ref _temporaryCleanupStatus, value); }
    public IReadOnlyList<DownloadMode> DownloadModes { get; } = Enum.GetValues<DownloadMode>();
    public IReadOnlyList<MediaContainer> Containers { get; } = Enum.GetValues<MediaContainer>();
    public IReadOnlyList<AudioFormat> AudioFormats { get; } = Enum.GetValues<AudioFormat>();
    public IReadOnlyList<VideoCodec> VideoCodecs { get; } = Enum.GetValues<VideoCodec>();
    public IReadOnlyList<CompressionPreset> Compressions { get; } = Enum.GetValues<CompressionPreset>();
    public IReadOnlyList<EncoderPreference> EncoderPreferences { get; } = Enum.GetValues<EncoderPreference>();
    public IReadOnlyList<ClipMode> ClipModes { get; } = Enum.GetValues<ClipMode>();
    public IReadOnlyList<CloseBehavior> CloseBehaviors { get; } = Enum.GetValues<CloseBehavior>();
    public IReadOnlyList<string> Themes { get; } = ["System", "Dark", "Light"];
    public IReadOnlyList<string> Browsers { get; } = ["None", "Firefox", "Chrome", "Edge", "Brave", "Chromium", "Opera", "Vivaldi"];
    public IReadOnlyList<string> FpsChoices { get; } = ["Original", "24", "30", "60"];
    public IReadOnlyList<string> HeightChoices { get; } = ["Original", "2160", "1440", "1080", "720", "480"];
    public IReadOnlyList<string> SampleRates { get; } = ["Keep original", "44100", "48000"];
    private static readonly int[] AllBitrates = [64, 96, 128, 160, 192, 256, 320], OpusBitrates = [64, 96, 128, 160, 192, 256];
    /// <summary>libopus tops out at 256 kbps (the engine caps it), so Opus never offers a choice it cannot make.</summary>
    public IReadOnlyList<int> Bitrates => AudioFormat == AudioFormat.Opus ? OpusBitrates : AllBitrates;
    public IReadOnlyList<string> AttachmentPresets { get; } = ["Custom target", "10 MB", "20 MB", "25 MB", "50 MB", "100 MB", "500 MB"];
    public string AttachmentPreset { get => _attachmentPreset; set { if (SetProperty(ref _attachmentPreset, value) && int.TryParse(value.Split(' ')[0], out var mb)) { TargetSizeMb = (mb * .95).ToString("0.##", CultureInfo.InvariantCulture); Compression = CompressionPreset.TargetSize; ClipMode = ClipMode.Exact; Status = $"Target set below {mb} MB with a 5% size margin. The result is checked after export."; } } }
    public string Theme { get => _theme; set { if (SetProperty(ref _theme, value)) ApplyTheme(); } }
    public string DownloadDirectory { get => _downloadDirectory; set { if (SetProperty(ref _downloadDirectory, value)) OutputFolderChanged(); } }
    public string AudioDirectory { get => _audioDirectory; set { if (SetProperty(ref _audioDirectory, value)) OutputFolderChanged(); } }
    public string ClipsDirectory { get => _clipsDirectory; set => SetProperty(ref _clipsDirectory, value); }
    public string ArchivesDirectory { get => _archivesDirectory; set { if (SetProperty(ref _archivesDirectory, value)) OutputFolderChanged(); } }
    public string JobDirectory { get => _jobDirectory; set { if (SetProperty(ref _jobDirectory, value)) OutputFolderChanged(); } }
    private void OutputFolderChanged() { OnPropertyChanged(nameof(SelectedOutputDirectory)); OnPropertyChanged(nameof(AvailableSpace)); }
    public string SelectedOutputDirectory => !string.IsNullOrWhiteSpace(JobDirectory) ? JobDirectory : IsAudioDownload && !string.IsNullOrWhiteSpace(AudioDirectory) ? AudioDirectory : IsArchiveDownload && !string.IsNullOrWhiteSpace(ArchivesDirectory) ? ArchivesDirectory : DownloadDirectory;
    public int ConcurrentDownloads { get => _concurrent; set => SetProperty(ref _concurrent, Math.Clamp(value, 1, 8)); }
    public bool DetectClipboard { get => _detectClipboard; set => SetProperty(ref _detectClipboard, value); }
    public bool ShowRecentRecordings
    {
        get => _showRecentRecordings;
        set
        {
            if (!SetProperty(ref _showRecentRecordings, value)) return;
            // Off clears the list at once; on looks again only while the empty editor is showing.
            if (!value || IsEditor && !HasEditor) _ = RefreshRecentRecordingsAsync();
        }
    }
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }
    public bool ReducedMotion { get => _reducedMotion; set { if (SetProperty(ref _reducedMotion, value)) RefreshMotionIndicators(); } }
    public bool? SystemReducedMotion => _systemReducedMotion;
    public bool EffectiveReducedMotion => ReducedMotion || SystemReducedMotion == true;
    public string MotionPreferenceStatus => SystemReducedMotion switch { true => "Windows has animations turned off, so KeaClip keeps still too.", _ => "Turn this on to stop KeaClip's movement effects." };
    public bool NotifyCompleted { get => _notifyCompleted; set => SetProperty(ref _notifyCompleted, value); }
    public CloseBehavior CloseBehavior { get => _closeBehavior; set => SetProperty(ref _closeBehavior, value); }
    public string RetentionDays { get => _retentionDays; set => SetProperty(ref _retentionDays, value); }
    public string SoundboardSeconds { get => _soundboardSeconds; set => SetProperty(ref _soundboardSeconds, value); }
    public string SoundboardBytes { get => _soundboardBytes; set => SetProperty(ref _soundboardBytes, value); }
    public string EditorPath { get => _editorPath; private set { SetProperty(ref _editorPath, value); OnPropertyChanged(nameof(HasEditor)); OnPropertyChanged(nameof(EditorTitle)); } }
    public bool HasEditor => _probe is not null;
    public bool PreviewFullscreen { get => _previewFullscreen; set => SetProperty(ref _previewFullscreen, value); }
    public string EditorTitle => string.IsNullOrEmpty(EditorPath) ? "Make the good part yours." : Path.GetFileName(EditorPath);
    public string EditorSummary => _probe is null ? "Open a local video or audio file. Your original stays safe by default." : $"{MediaValidation.Timecode(_probe.Duration)} · {Size(_probe.SizeBytes)} · {_probe.VideoCodec ?? "Audio"} / {_probe.AudioCodec ?? "No audio"}";
    public string StartTime { get => _startTime; set { if (SetProperty(ref _startTime, value)) UpdateEstimate(); } }
    public string EndTime { get => _endTime; set { if (SetProperty(ref _endTime, value)) UpdateEstimate(); } }
    public double ClipStartRatio { get => _probe is { Duration.TotalSeconds: > 0 } && MediaValidation.TryTimecode(StartTime, out var time) ? time.TotalSeconds / _probe.Duration.TotalSeconds : 0; set { if (_probe is not null) StartTime = MediaValidation.Timecode(TimeSpan.FromSeconds(Math.Clamp(value, 0, 1) * _probe.Duration.TotalSeconds)); } }
    public double ClipEndRatio { get => _probe is { Duration.TotalSeconds: > 0 } && MediaValidation.TryTimecode(EndTime, out var time) ? time.TotalSeconds / _probe.Duration.TotalSeconds : 1; set { if (_probe is not null) EndTime = MediaValidation.Timecode(TimeSpan.FromSeconds(Math.Clamp(value, 0, 1) * _probe.Duration.TotalSeconds)); } }
    public IReadOnlyList<float> Waveform { get => _waveform; private set => SetProperty(ref _waveform, value); }
    public double PlaybackPosition { get => _playbackPosition; set { if (SetProperty(ref _playbackPosition, value)) Playback.Seek(value); } }
    public string PlaybackTime => MediaValidation.Timecode(Playback.Time);
    public string PlayLabel => Playback.IsPlaying ? "Pause" : "Play";
    public bool IsPlaying => Playback.IsPlaying;
    public int Volume { get => _volume; set { if (SetProperty(ref _volume, value)) Playback.SetVolume(value); } }
    // Choosing Video clip ends a soundboard preset: its limits are for audio clips only.
    public bool AudioOnly { get => _audioOnly; set { if (SetProperty(ref _audioOnly, value)) { if (!value) EndSoundboard("Soundboard limits are for audio clips, so they are off for this video clip."); RefreshExportBitrates(); UpdateEstimate(); } } }
    public ClipMode ClipMode { get => _clipMode; set { if (SetProperty(ref _clipMode, value)) { OnPropertyChanged(nameof(IsExactClip)); if (value == ClipMode.Lossless) Compression = CompressionPreset.Off; RefreshExportBitrates(); UpdateEstimate(); } } }
    public bool IsExactClip => ClipMode == ClipMode.Exact;
    public CompressionPreset Compression { get => _compression; set { if (SetProperty(ref _compression, value)) UpdateEstimate(); } }
    public string TargetSizeMb { get => _targetSizeMb; set { if (SetProperty(ref _targetSizeMb, value)) UpdateEstimate(); } }
    public EncoderPreference Encoder { get => _encoder; set { if (SetProperty(ref _encoder, value)) UpdateEstimate(); } }
    public VideoCodec ExportCodec { get => _exportCodec; set { if (SetProperty(ref _exportCodec, value)) UpdateEstimate(); } }
    // A soundboard sound is MP3 or Ogg; choosing another format ends the preset.
    public AudioFormat ExportAudioFormat { get => _exportAudioFormat; set { if (SetProperty(ref _exportAudioFormat, value)) { if (value is not (AudioFormat.Mp3 or AudioFormat.Ogg)) EndSoundboard($"Soundboard sounds are MP3 or Ogg, so the soundboard limits are off for {value}."); RefreshExportBitrates(); UpdateEstimate(); } } }
    public int ExportAudioKbps { get => _exportAudioKbps; set { if (SetProperty(ref _exportAudioKbps, value)) { if (_exportBitrateNote.Length > 0) { _exportBitrateNote = ""; OnPropertyChanged(nameof(ExportBitrateNote)); OnPropertyChanged(nameof(HasExportBitrateNote)); } UpdateEstimate(); } } }
    public string ExportHeight { get => _exportHeight; set { if (SetProperty(ref _exportHeight, value)) UpdateEstimate(); } }
    public string ExportFps { get => _exportFps; set { if (SetProperty(ref _exportFps, value)) UpdateEstimate(); } }
    public bool ExportHdr { get => _exportHdr; set { if (SetProperty(ref _exportHdr, value)) UpdateEstimate(); } }
    /// <summary>True when the open file is HDR (PQ or HLG), the only case HDR to SDR changes anything.</summary>
    public bool EditorSourceIsHdr => _probe is { } probe && MediaService.IsHdr(probe);
    public bool Normalize { get => _normalize; set { if (SetProperty(ref _normalize, value)) UpdateEstimate(); } }
    public bool TrimSilence { get => _trimSilence; set { if (SetProperty(ref _trimSilence, value)) UpdateEstimate(); } }
    public bool Mono { get => _mono; set { if (SetProperty(ref _mono, value)) { RefreshExportBitrates(); UpdateEstimate(); } } }
    public double ExportVolume { get => _exportVolume; set { if (SetProperty(ref _exportVolume, value)) UpdateEstimate(); } }
    public string FadeIn { get => _fadeIn; set { if (SetProperty(ref _fadeIn, value)) UpdateEstimate(); } }
    public string FadeOut { get => _fadeOut; set { if (SetProperty(ref _fadeOut, value)) UpdateEstimate(); } }
    public string SampleRate { get => _sampleRate; set { if (SetProperty(ref _sampleRate, value)) { RefreshExportBitrates(); UpdateEstimate(); } } }
    /// <summary>
    /// Soundboard limits for an audio clip. Turning them off (here, by choosing Video clip, another
    /// format or by opening a file) puts back what the preset replaced and the user has not
    /// changed since, so a later video clip is never silently mono 128 kbps.
    /// </summary>
    public bool Soundboard { get => _soundboard; set { if (SetProperty(ref _soundboard, value)) { if (!value) RestoreAfterSoundboard(); UpdateEstimate(); } } }
    public bool DeleteOriginal { get => _deleteOriginal; set => SetProperty(ref _deleteOriginal, value); }
    public string EstimateText { get => _estimateText; private set => SetProperty(ref _estimateText, value); }
    public string ShareUrl { get => _shareUrl; private set => SetProperty(ref _shareUrl, value); }
    public string ShareInfo { get => _shareInfo; private set => SetProperty(ref _shareInfo, value); }
    public IAsyncRelayCommand AnalyseCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand DownloadEditCommand { get; }
    public IAsyncRelayCommand PasteCommand { get; }
    public IAsyncRelayCommand QueueBatchCommand { get; }
    public IRelayCommand SelectBatchCommand { get; }
    public IRelayCommand ClearBatchSelectionCommand { get; }
    public IRelayCommand DismissBatchCommand { get; }
    public IRelayCommand SelectRangeCommand { get; }
    public IRelayCommand CheckSpaceCommand { get; }
    public IAsyncRelayCommand OpenMediaCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand ShareCommand { get; }
    public IAsyncRelayCommand CopyShareCommand { get; }
    public IRelayCommand OpenShareCommand { get; }
    public IAsyncRelayCommand ChooseFolderCommand { get; }
    public IAsyncRelayCommand ChooseJobFolderCommand { get; }
    public IRelayCommand ResetJobFolderCommand { get; }
    public IAsyncRelayCommand SaveSettingsCommand { get; }
    public IAsyncRelayCommand InspectComponentsCommand { get; }
    public IAsyncRelayCommand CleanTemporaryFilesCommand { get; }
    public IAsyncRelayCommand ProvisionCommand { get; }
    public IAsyncRelayCommand UpdateDownloaderCommand { get; }
    public IAsyncRelayCommand RollbackComponentsCommand { get; }
    public IAsyncRelayCommand CopyDiagnosticsCommand { get; }
    public IAsyncRelayCommand SupportedSitesCommand { get; }
    public IAsyncRelayCommand ClearHistoryCommand { get; }
    public IRelayCommand PauseQueueCommand { get; }
    public IRelayCommand ClearCompletedCommand { get; }
    public IRelayCommand CancelOperationCommand { get; }
    public IRelayCommand PlayCommand { get; }
    public IRelayCommand StopPlaybackCommand { get; }
    public IRelayCommand SetStartCommand { get; }
    public IRelayCommand SetEndCommand { get; }
    public IRelayCommand SoundboardCommand { get; }
    public IRelayCommand UpdateEstimateCommand { get; }
    public IRelayCommand SelectPlaylistCommand { get; }
    public IRelayCommand ClearPlaylistCommand { get; }

    // The Download page shows its own Prepare components button for this, so the status
    // names that action instead of sending people to Settings.
    internal const string FirstRunStatus = "One-time setup: choose Prepare components to get started.";
    public async Task InitializeAsync() => await RunAsync(OperationLane.Maintenance, async ct =>
    {
        RefreshPlatformPreferences();
        var problems = new List<string>();
        // Clipboard detection is only enabled from settings that really loaded; defaults
        // could switch it back on for someone who turned it off.
        _initialized = await LoadStartupStateAsync(ct, problems);
        await LoadWatchSetupAsync(ct);
        OnSettingsLoaded();
        // The queue comes back after settings (its concurrency) and history (Replace records) have loaded.
        var queueNotes = new List<string>();
        await RestoreQueueAsync(ct, queueNotes);
        var ready = ComponentsReady ? "Ready. Paste a link or open a local file." : FirstRunStatus;
        Status = problems.Count + queueNotes.Count == 0 ? ready
            : string.Join(" ", problems.Concat(queueNotes)) + (problems.Count == 0 && !ComponentsReady ? " " + FirstRunStatus : "");
        StartUpdates();
        StartDownloaderUpdates();
        OpenBetaTestOnFirstStart();
    });
    // Each step is independent: one unreadable file must not stop the others loading.
    private async Task<bool> LoadStartupStateAsync(CancellationToken ct, List<string> problems)
    {
        try { _settings = await _services.Store.LoadSettingsAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            CaptureDiagnostics(error); problems.Add("Your settings could not be read, so defaults are in use until you save. " + FriendlyError(error));
            try { await ReloadHistoryAsync(ct); } catch (Exception history) when (history is not OperationCanceledException) { CaptureDiagnostics(history); }
            await InspectComponentsAsync(ct);
            return false;
        }
        DownloadDirectory = _settings.DownloadDirectory; Theme = _settings.Theme; DetectClipboard = _settings.DetectClipboard; ShowRecentRecordings = _settings.ShowRecentRecordings; ConcurrentDownloads = _settings.ConcurrentDownloads;
        AudioDirectory = _settings.AudioDirectory ?? ""; ClipsDirectory = _settings.ClipsDirectory ?? ""; ArchivesDirectory = _settings.ArchivesDirectory ?? "";
        StartWithWindows = _settings.StartWithWindows; StartMinimized = _settings.StartMinimized; ReducedMotion = _settings.ReducedMotion; NotifyCompleted = _settings.NotifyCompleted; CloseBehavior = _settings.CloseBehavior;
        ShareWhenFinishedDefault = _settings.ShareWhenFinished; ShareWhenFinished = _settings.ShareWhenFinished; CheckForUpdates = _settings.CheckForUpdates;
        RetentionDays = _settings.HistoryRetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        SoundboardSeconds = _settings.SoundboardMaxSeconds.ToString(CultureInfo.InvariantCulture); SoundboardBytes = _settings.SoundboardTargetBytes.ToString(CultureInfo.InvariantCulture);
        _queue.MaxConcurrent = ConcurrentDownloads;
        try { await _services.Store.PruneHistoryAsync(_settings.HistoryRetentionDays, ct); await ReloadHistoryAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException) { CaptureDiagnostics(error); OnHistoryLoadFailed(error); problems.Add("Your history could not be read. New downloads still work; details are in Diagnostics."); }
        try { await InspectComponentsAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException) { CaptureDiagnostics(error); problems.Add("Media components could not be checked. Open Settings, Components to retry."); }
        return true;
    }
    public void AcceptLaunchArgument(string argument)
    {
        try
        {
            if (argument.StartsWith(AppIdentity.Protocol + ":", StringComparison.OrdinalIgnoreCase)) { Url = MediaValidation.ParseProtocol(argument).AbsoluteUri; SelectedTab = 0; Status = "Link received. Review it and choose Analyse or Download."; }
            else if (MediaValidation.TryMediaUrl(argument, out var uri)) { Url = uri!.AbsoluteUri; SelectedTab = 0; Status = "Link received. Nothing downloads automatically."; }
        }
        catch (Exception error) { Status = FriendlyError(error); }
    }
    public async Task DetectClipboardAsync()
    {
        if (!_initialized || !DetectClipboard || !CanDownload || !string.IsNullOrWhiteSpace(Url)) return;
        try
        {
            var text = await _services.Platform.ReadClipboardAsync();
            // Only a newly copied link is offered; a link the user already saw (and maybe
            // cleared on purpose) is not pasted back on every window activation.
            if (text == _lastClipboardText) return;
            _lastClipboardText = text;
            // A File.Kiwi share link holds its decryption key after '#'. It is the user's own upload,
            // not a download, and the key must never reach the Download box or the downloader.
            // Only links that look like video or audio are filled in; any link can still be pasted or typed.
            if (MediaValidation.TryMediaUrl(text, out var uri) && !MediaValidation.IsPrivateShareLink(uri!) && MediaValidation.LooksLikeMediaLink(uri!)) { Url = uri!.AbsoluteUri; Status = "Link detected in your clipboard. Review it before downloading."; }
        }
        catch { /* A busy clipboard does not interrupt the user's work. */ }
    }
    public async Task PasteAsync()
    {
        if (!CanDownload) { Status = "Wait for the current link check to finish, or cancel it, before pasting links."; return; }
        try
        {
            var text = await _services.Platform.ReadClipboardAsync(); var lines = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 100) { Status = "Paste up to 100 links at a time, one per line."; return; }
            if (lines.Length > 1)
            {
                var links = new List<Uri>(); foreach (var line in lines) { if (!MediaValidation.TryMediaUrl(line, out var link)) { Status = "Each pasted line must contain one complete http or https media link. No links were queued."; return; } if (!links.Contains(link!)) links.Add(link!); }
                BatchLinks.Clear(); foreach (var link in links) { var item = new BatchLinkChoice(link); item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(BatchSummary)); BatchLinks.Add(item); }
                SelectedTab = 0; OnPropertyChanged(nameof(HasBatch)); OnPropertyChanged(nameof(BatchSummary)); Status = "Review the pasted links and select what to queue. Playlist links need their own Analyse picker."; return;
            }
            _lastClipboardText = text;
            if (!MediaValidation.TryMediaUrl(text, out var uri)) { Status = "Copy a complete http or https media link first."; return; }
            BatchLinks.Clear(); OnPropertyChanged(nameof(HasBatch)); Url = uri!.AbsoluteUri; Status = "Link pasted. Choose Analyse to see the available formats.";
        }
        catch (Exception error) { Status = FriendlyError(error); }
    }
    public Task AnalyseAsync() => RunAsync(OperationLane.Download, AnalyseCoreAsync);
    private async Task AnalyseCoreAsync(CancellationToken ct)
    {
        if (!MediaValidation.TryMediaUrl(Url, out var uri)) throw new MediaOperationException("Enter a complete http or https media link.");
        var analysedUrl = Url;
        Status = "Checking the link and available formats…";
        MediaInfo info;
        PlaylistShape shape;
        IsAnalysing = true;
        var browser = BrowserValue();
        try
        {
            // The playlist structure is read from the same metadata off the UI thread, so a
            // long listing never stalls the window while the picker is built.
            (info, shape) = await Task.Run(async () => { var analysed = await _services.Downloads.AnalyseAsync(uri!, browser, ct); return (analysed, ReadPlaylistShape(analysed)); }, ct);
        }
        finally { IsAnalysing = false; }
        if (Url != analysedUrl) { Status = "The link changed. Analyse the new link before downloading."; return; }
        _media = info; _playlistShape = shape; OnPropertyChanged(nameof(HasMedia)); OnPropertyChanged(nameof(IsPlaylist)); OnPropertyChanged(nameof(MediaTitle)); OnPropertyChanged(nameof(MediaSummary)); OnPropertyChanged(nameof(MediaDescription)); OnPropertyChanged(nameof(SourceDetails));
        Playlist.Clear();
        for (var index = 0; index < info.Entries.Count; index++)
        {
            var item = new PlaylistChoice(info.Entries[index], index, shape.IsList(index), OpenPlaylistEntryAsync);
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PlaylistSummary));
            Playlist.Add(item);
        }
        RangeStart = "1"; RangeEnd = Playlist.Count.ToString(CultureInfo.InvariantCulture); OnPropertyChanged(nameof(PlaylistSummary));
        FormatDetails.Clear(); foreach (var format in info.Formats) FormatDetails.Add($"{format.Id} · {format.Extension ?? "?"} · {(format.Height is { } h ? h + "p" : "audio")} · {format.Fps?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"} fps · {format.VideoCodec ?? "no video"} / {format.AudioCodec ?? "no audio"} · {(format.SizeBytes is { } size ? Size(size) : "size unavailable")}");
        // Keep the user's choice (for example "Smallest available" picked before analysis);
        // the ComboBox can push null while its list is rebuilt, so remember it first. A height
        // cap this media cannot offer falls back to Best available. Media that lists no video
        // heights at all (a playlist, whose entries are only analysed when they download) cannot
        // rule a cap out, so the chosen cap stays and applies to every entry.
        var previousQuality = Quality;
        var heights = QualityHeights(info).Select(height => height + "p maximum").ToList();
        if (heights.Count == 0 && previousQuality is not null && previousQuality.EndsWith("p maximum", StringComparison.Ordinal)) heights.Add(previousQuality);
        Qualities.Clear(); Qualities.Add("Best available"); Qualities.Add("Smallest available"); foreach (var height in heights) Qualities.Add(height);
        Quality = previousQuality is not null && Qualities.Contains(previousQuality) ? previousQuality : "Best available";
        // The thumbnail is optional: it loads in the background once the lists are filled and
        // never delays the formats.
        _thumbnailLoad?.Cancel(); _thumbnailLoad = new CancellationTokenSource(); var thumbnailLoad = _thumbnailLoad.Token;
        _ = LoadThumbnailAsync(info.Thumbnail, thumbnailLoad, () => Url == analysedUrl && ReferenceEquals(_media, info));
        Status = info.IsPlaylist ? PlaylistFoundStatus(info, shape) : "Formats checked. Review your choices, then download.";
        OnMediaAnalysed();
    }
    public Task QueueDownloadAsync(bool editAfter) => RunAsync(OperationLane.Download, async ct =>
    {
        RejectFormatConflict();
        if (_media is null) await AnalyseCoreAsync(ct);
        if (_media is not { } media) return;
        var selected = media.IsPlaylist ? Playlist.Where(p => p.Selected).ToArray() : [];
        if (media.IsPlaylist && selected.Length == 0) throw new MediaOperationException("Select at least one playlist entry before downloading.");
        if (string.IsNullOrWhiteSpace(SelectedOutputDirectory)) throw new MediaOperationException("Choose an output folder in Settings.");
        if (editAfter && media.IsPlaylist) throw new MediaOperationException("Download selected entries first, then choose Edit on a finished item.");
        BeginQueueRequest();
        if (!media.IsPlaylist)
        {
            CompleteQueueChoice(QueueOrAsk(BuildDownloadOptions(media.Url), media.Title, editAfter, media.Thumbnail) == QueueOutcome.Queued ? 1 : 0, 0);
            return;
        }
        // An entry that is a list itself (a channel tab, a playlist in a channel) is never queued.
        // Chosen on its own, Download opens it so its videos can be picked, exactly as Open as
        // playlist does; chosen with videos, it is skipped with a note. Other entries are queued by
        // their own address, or, for items of one post (X, Instagram), by the post address plus
        // their position. Entries without a usable address are skipped and counted instead of
        // aborting halfway.
        var listChoices = selected.Where(p => p.IsList).ToArray();
        if (listChoices.Length == selected.Length && listChoices is [{ } onlyList] && ListAddress(onlyList) is { } listUrl)
        {
            Url = listUrl.AbsoluteUri; SelectedTab = 0;
            await AnalyseCoreAsync(ct);
            if (Url == listUrl.AbsoluteUri && _media is not null) Status = "That entry is a playlist itself, so it was opened instead of queued. " + Status;
            return;
        }
        var lists = listChoices.Count(p => ListAddress(p) is not null);
        var wanted = selected.Where(p => !p.IsList).Select(p => (Choice: p, p.Entry.Title)).ToArray();
        var targets = await Task.Run(() => wanted.Select(item => (Target: PlaylistTarget(media, item.Choice), item.Title)).ToArray(), ct);
        // A list entry without an address cannot be opened either: it is counted with the others
        // that have no usable link.
        int queued = 0, unsupported = listChoices.Length - lists;
        foreach (var (target, title) in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (target is not { } item) { unsupported++; continue; }
            if (QueueOrAsk(BuildDownloadOptions(item.Url) with { PlaylistItems = item.PlaylistItems }, title, false, null) == QueueOutcome.Queued) queued++;
        }
        CompleteQueueChoice(queued, unsupported, lists);
    });
    public Task QueueBatchAsync() => RunAsync(OperationLane.Download, ct =>
    {
        RejectFormatConflict();
        if (string.IsNullOrWhiteSpace(SelectedOutputDirectory)) throw new MediaOperationException("Choose an output folder before queueing links.");
        var selected = BatchLinks.Where(p => p.Selected).ToArray(); if (selected.Length == 0) throw new MediaOperationException("Select at least one pasted link before choosing Queue.");
        BeginQueueRequest(); var queued = 0;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = QueueOrAsk(BuildDownloadOptions(item.Url), item.Url.Host + " · pasted link", false, null);
            if (outcome == QueueOutcome.Queued) queued++;
            // A link held back by a download with other settings stays in the list for later.
            if (outcome != QueueOutcome.Conflict) BatchLinks.Remove(item);
        }
        OnPropertyChanged(nameof(HasBatch)); OnPropertyChanged(nameof(BatchSummary)); CompleteQueueChoice(queued, 0); return Task.CompletedTask;
    });
    private void SelectPlaylistRange()
    {
        if (!int.TryParse(RangeStart, out var start) || !int.TryParse(RangeEnd, out var end) || start < 1 || end < start || end > Playlist.Count) { Status = "Choose a range within the playlist: first entry is 1, end must follow start."; return; }
        for (var index = 0; index < Playlist.Count; index++) Playlist[index].Selected = index >= start - 1 && index < end;
        OnPropertyChanged(nameof(PlaylistSummary));
    }
    // What one request did with each link it was asked to queue: added it, held it on a duplicate
    // card, found the same request still in the queue, or found a queued download with other
    // settings that would write the same file.
    private enum QueueOutcome { Queued, Asked, AlreadyQueued, Conflict }
    // Queues the download, or holds it for a duplicate choice. Items of one post share the post's
    // address, so the entry position (PlaylistItems) is part of every duplicate key: item 2 of a post
    // is never a duplicate of item 1.
    private QueueOutcome QueueOrAsk(DownloadOptions options, string title, bool editAfter, Uri? thumbnail)
    {
        var item = PlaylistItemKey(options.PlaylistItems);
        var outcome = ActiveQueueMatch(options);
        PendingDownload? card = null;
        if (outcome is null)
        {
            var safeUrl = MediaValidation.SafeSourceUrl(options.Url);
            // Cheap comparisons first; only a matching record touches the disk. Mode and format must
            // match too: saved audio is no reason to stop a video download, and a saved MP3 is no
            // reason to stop a FLAC one.
            var mode = options.Mode.ToString();
            // SafeStoredSourceUrl re-normalises links saved by older versions so they still match.
            var duplicate = _history.FirstOrDefault(h => h.Mode == mode && PlaylistItemKey(h.PlaylistItems) == item && SavedInRequestedFormat(h.FilePath, options) && MediaValidation.SafeStoredSourceUrl(h.SourceUrl) == safeUrl && File.Exists(h.FilePath));
            // Without a history record, the expected output file itself is checked. The name comes
            // from the engine, so long titles (shortened before " [id]") and templates match exactly.
            if (duplicate is null && item is null && _media is { IsPlaylist: false } info && info.Url == options.Url && ExpectedOutputName(info, options) is { } name && Directory.Exists(options.OutputDirectory))
            {
                var candidate = ExistingOutputCandidates(options.OutputDirectory, name, options).FirstOrDefault(File.Exists);
                if (candidate is not null) duplicate = new("existing-output", info.Title, safeUrl, "Existing output", DateTimeOffset.Now, candidate, options.Mode.ToString(), "Existing file", new FileInfo(candidate).Length, "Saved");
            }
            if (duplicate is not null) card = new(this, options, title, editAfter, thumbnail, duplicate, ShareWhenFinished);
            outcome = card is null ? QueueOutcome.Queued : QueueOutcome.Asked;
        }
        // Asking again for a link replaces its waiting card, whatever the new request decides: the
        // card always holds the latest request's format, mode, quality, folder and Download & edit
        // choice, so Download again and Replace never act on settings that are no longer selected.
        var waiting = DuplicateChoices.FirstOrDefault(p => SameMediaLink(p.Options.Url, options.Url) && PlaylistItemKey(p.Options.PlaylistItems) == item);
        if (card is not null && waiting is not null) DuplicateChoices[DuplicateChoices.IndexOf(waiting)] = card;
        else if (card is not null) DuplicateChoices.Add(card);
        else if (waiting is not null) DuplicateChoices.Remove(waiting);
        if (card is not null || waiting is not null) OnPropertyChanged(nameof(HasDuplicates));
        switch (outcome)
        {
            case QueueOutcome.Queued: Enqueue(options, title, editAfter, thumbnail, ShareWhenFinished); break;
            case QueueOutcome.Asked: _askedThisRequest++; break;
            case QueueOutcome.AlreadyQueued: _skippedActive++; break;
            case QueueOutcome.Conflict: _conflictsThisRequest++; break;
        }
        return outcome.Value;
    }
    /// <summary>
    /// A download still in the queue for the same media, folder and name. The jobs are in memory, so
    /// the exact link is compared, not the reduced link history stores (which can merge two files of
    /// one download page). The same request again is never fetched twice. A request with other
    /// settings that could write the same file (another quality, Video only, MP4 next to Automatic)
    /// would fail on that file or overwrite it, so it is held back; one that writes a different file
    /// (FLAC after MP3, MKV after MP4) is queued.
    /// </summary>
    private QueueOutcome? ActiveQueueMatch(DownloadOptions options)
    {
        var item = PlaylistItemKey(options.PlaylistItems);
        var active = _queue.Snapshot.Where(j => j.State is JobState.Queued or JobState.Running or JobState.Paused && SameMediaLink(j.Options.Url, options.Url) && PlaylistItemKey(j.Options.PlaylistItems) == item
            && j.Options.NamingTemplate == options.NamingTemplate && string.Equals(j.Options.OutputDirectory, options.OutputDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Any(j => SameOutputOptions(j.Options, options))) return QueueOutcome.AlreadyQueued;
        if (active.Any(j => DownloadService.OutputExtensions(j.Options).Intersect(DownloadService.OutputExtensions(options), StringComparer.OrdinalIgnoreCase).Any())) return QueueOutcome.Conflict;
        return null;
    }
    /// <summary>
    /// The same media link: exactly the same address, or two addresses whose stored forms match when
    /// nothing that may choose the media was dropped from either (only tracking, share or playback
    /// values), so a second share link of one video still counts. download.php?file=a and ?file=b
    /// stay different, because their stored forms are not exact.
    /// </summary>
    internal static bool SameMediaLink(Uri first, Uri second) =>
        string.Equals(first.AbsoluteUri, second.AbsoluteUri, StringComparison.Ordinal)
        || MediaValidation.SafeSourceUrlIsExact(first) && MediaValidation.SafeSourceUrlIsExact(second) && MediaValidation.SafeSourceUrl(first) == MediaValidation.SafeSourceUrl(second);
    private static readonly Uri AnyLink = new("https://localhost/");
    /// <summary>
    /// Every choice that changes the file a download writes is the same. Choices a mode ignores
    /// (a container for audio, a bitrate for video) and those that do not change the file (the
    /// browser for sign-in, replacement) are left out.
    /// </summary>
    internal static bool SameOutputOptions(DownloadOptions first, DownloadOptions second) =>
        OutputShape(first) == OutputShape(second) && DownloadService.OutputExtensions(first).SequenceEqual(DownloadService.OutputExtensions(second), StringComparer.OrdinalIgnoreCase);
    private static DownloadOptions OutputShape(DownloadOptions options)
    {
        var audio = options.Mode == DownloadMode.Audio;
        var video = options.Mode is DownloadMode.VideoAudio or DownloadMode.VideoOnly;
        var archive = options.Mode == DownloadMode.Archive;
        return options with
        {
            Url = AnyLink, OutputDirectory = "", PlaylistItems = null, Browser = null, ReplaceExisting = false, ReplacePath = null,
            EmbedThumbnail = !archive && options.EmbedThumbnail, EmbedMetadata = !archive && options.EmbedMetadata,
            Container = MediaContainer.Auto,
            AudioFormat = audio ? options.AudioFormat : AudioFormat.Original,
            AudioKbps = audio && options.AudioFormat != AudioFormat.Original ? options.AudioKbps : 0,
            VideoCodec = video ? options.VideoCodec : VideoCodec.Original,
            MaxHeight = video ? options.MaxHeight : null, Fps = video ? options.Fps : null, HdrToSdr = video && options.HdrToSdr,
            NamingTemplate = string.IsNullOrWhiteSpace(options.NamingTemplate) ? null : options.NamingTemplate,
        };
    }
    /// <summary>
    /// "Already saved" means the same mode and format (USER_GUIDE). A request that names one format
    /// (MP3, FLAC, MP4) matches only a saved file in that format, so a FLAC request is never answered
    /// with the saved MP3. Automatic and Original can finish as any of their formats, so any saved
    /// file of the mode matches them; an archive matches any saved archive of the media.
    /// </summary>
    internal static bool SavedInRequestedFormat(string savedPath, DownloadOptions options) =>
        DownloadService.OutputExtensions(options) is not [var only] || string.Equals(Path.GetExtension(savedPath), only, StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// The saved files the existing-output probe looks for: exactly the final paths the chosen mode,
    /// audio format and container can write (DownloadService.PotentialOutputPaths), so an MP3 request
    /// is never stopped by a saved video of the same title. An archive always goes into a new folder;
    /// only an existing archive of the same media (its folder and the media inside) asks first.
    /// </summary>
    internal static IReadOnlyList<string> ExistingOutputCandidates(string directory, string name, DownloadOptions options)
    {
        if (options.Mode != DownloadMode.Archive) return DownloadService.PotentialOutputPaths(directory, name, options);
        var archive = Path.Combine(directory, name + " Archive");
        return [.. new[] { ".mp4", ".mkv", ".webm", ".mov", ".m4a", ".mp3", ".opus", ".ogg", ".flac", ".wav", ".aac" }.Select(extension => Path.Combine(archive, name + extension))];
    }
    /// <summary>
    /// The export dialog's suggestion: "&lt;file&gt; - clip", or the first free "(2)", "(3)" in the folder it
    /// opens in, so a second clip of the same file never starts by offering to overwrite the first.
    /// </summary>
    internal static string FreeClipName(string? source, string extension, string? folder)
    {
        var stem = MediaValidation.FriendlyFileName(string.IsNullOrWhiteSpace(source) ? "clip" : source, 110) + " - clip";
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return stem;
        var name = stem;
        for (var number = 2; File.Exists(Path.Combine(folder, name + "." + extension)) && number < 10_000; number++) name = $"{stem} ({number})";
        return name;
    }
    /// <summary>
    /// The name for "Download again": the saved file's name with the first free " (n)", checked
    /// against every path this request could write ("Title [id] (2).mp3", or "(3)" when "(2)" is
    /// taken), instead of an opaque "Copy 20261012-130745-1a2b3c4d - Title".
    /// </summary>
    internal static string FreeCopyName(PendingDownload item)
    {
        var options = item.Options;
        var stem = Path.GetFileNameWithoutExtension(item.Existing.FilePath);
        if (string.IsNullOrWhiteSpace(stem)) stem = item.Title;
        // Downloading a copy again counts on from it: "Title (2)" becomes "Title (3)", not "Title (2) (2)".
        if (System.Text.RegularExpressions.Regex.Match(stem, @"^(.+) \((\d{1,4})\)$") is { Success: true } numbered) stem = numbered.Groups[1].Value;
        stem = MediaValidation.FriendlyFileName(stem.Replace('%', '_'), 120);
        for (var number = 2; number < 10_000; number++)
        {
            var name = $"{stem} ({number})";
            var taken = options.Mode == DownloadMode.Archive
                ? Directory.Exists(Path.Combine(options.OutputDirectory, name + " Archive"))
                : DownloadService.PotentialOutputPaths(options.OutputDirectory, name, options).Any(path => File.Exists(path) || Directory.Exists(path));
            if (!taken) return name;
        }
        return stem + " (" + Guid.NewGuid().ToString("N")[..8] + ")";
    }
    private static bool SameFilePath(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    /// <summary>The playlist position a job or record addresses, normalised so "2", " 2" and "2 " match; null for a whole link.</summary>
    internal static string? PlaylistItemKey(string? playlistItems) => string.IsNullOrWhiteSpace(playlistItems) ? null : playlistItems.Trim();
    // An invalid template is reported by the download itself; the probe simply has no name to check.
    private static string? ExpectedOutputName(MediaInfo info, DownloadOptions options)
    {
        try { return DownloadService.OutputBaseName(info, options.NamingTemplate, options); }
        catch (MediaOperationException) { return null; }
    }
    private QueueJob Enqueue(DownloadOptions options, string title, bool editAfter, Uri? thumbnail, bool shareAfter)
    {
        var job = _queue.Enqueue(options, title); if (editAfter) _editAfter.Add(job.Id); if (shareAfter) _shareAfter.Add(job.Id); if (thumbnail is not null) _jobThumbnails[job.Id] = thumbnail;
        return job;
    }
    // What the current request did, counted per request: a card left from an earlier request
    // never decides the status or page of a later one.
    private int _skippedActive, _askedThisRequest, _conflictsThisRequest;
    private void BeginQueueRequest() { _skippedActive = 0; _askedThisRequest = 0; _conflictsThisRequest = 0; }
    private void CompleteQueueChoice(int queued, int unsupported, int lists = 0)
    {
        SynchroniseQueue(); OnPropertyChanged(nameof(AvailableSpace));
        var asked = _askedThisRequest; var conflicts = _conflictsThisRequest;
        var notes = (lists > 0 ? $" {lists} selected {(lists == 1 ? "entry is a playlist itself and was" : "entries are playlists themselves and were")} not queued. Choose Open as playlist next to {(lists == 1 ? "it" : "one")} to see its videos." : "")
            + (unsupported > 0 ? $" {unsupported} entr{(unsupported == 1 ? "y has" : "ies have")} no downloadable link and {(unsupported == 1 ? "was" : "were")} skipped." : "")
            + (_skippedActive > 0 ? $" {_skippedActive} {(_skippedActive == 1 ? "item is" : "items are")} already in your queue." : "")
            + (conflicts > 0 ? $" {conflicts} {(conflicts == 1 ? "item was" : "items were")} not added because a download still in your queue, with other settings, saves to the same file. When it finishes, ask again to keep both or replace it." : "");
        if (asked == 0 && queued == 0 && _skippedActive == 0 && conflicts == 0)
        {
            // Nothing new was queued (every selected entry was a list or had no link): stay on this
            // page with the choices intact so the user can act on the explanation.
            SelectedTab = 0; Status = "Nothing was queued." + notes;
            return;
        }
        // "Folder for this download" applies to this request only.
        if (!string.IsNullOrWhiteSpace(JobDirectory)) JobDirectory = "";
        // This page stays open only when this request is waiting for a duplicate choice.
        SelectedTab = asked > 0 ? 0 : 1;
        // Choosing Download again for a link that is still downloading adds nothing: say so
        // instead of "Added to your queue" next to "already in your queue".
        Status = asked > 0 && queued > 0 ? $"Added {queued} to your queue. {asked} already saved {(asked == 1 ? "item needs" : "items need")} a choice below: Open existing, Download again, Replace or Skip." + notes
            : asked > 0 ? "Already saved media found. Choose Open existing, Download again, Replace or Skip before those items start." + notes
            : queued > 0 ? "Added to your queue. " + (ShareWhenFinished ? "It is shared with an encrypted File.Kiwi link when it finishes. " : "") + "Pause stops new starts; Cancel stops an active item." + notes
            : "Nothing new was queued." + notes;
    }
    public void ResolveDuplicate(PendingDownload item, string choice)
    {
        TryAction(() =>
        {
            if (!DuplicateChoices.Contains(item)) return;
            // A missing existing file keeps the choice open, so Download again is still offered.
            if (choice == "open")
            {
                try { _services.Platform.OpenFile(item.Existing.FilePath); }
                catch (FileNotFoundException) { Status = MissingFileStatus + " Choose Download again or Skip."; return; }
            }
            else if (choice == "again") Enqueue(item.Options with { ReplaceExisting = false, ReplacePath = null, NamingTemplate = FreeCopyName(item) }, item.Title, item.EditAfter, item.Thumbnail, item.ShareAfter);
            else if (choice == "replace")
            {
                // Offered only when this request writes exactly the saved file (PendingDownload.CanReplace).
                // The engine also refuses any other result, so a different format never lands next to it.
                if (!item.CanReplace) throw new MediaOperationException(item.ReplaceTip);
                var job = Enqueue(item.Options with { ReplaceExisting = true, ReplacePath = item.Existing.FilePath, OutputDirectory = Path.GetDirectoryName(item.Existing.FilePath)!, NamingTemplate = Path.GetFileNameWithoutExtension(item.Existing.FilePath).Replace('%', '_') }, item.Title, item.EditAfter, item.Thumbnail, item.ShareAfter);
                if (item.Existing.Id != "existing-output") _supersededRecords[job.Id] = item.Existing;
            }
            else if (choice != "skip") throw new MediaOperationException("Choose a valid duplicate action.");
            DuplicateChoices.Remove(item); OnPropertyChanged(nameof(HasDuplicates)); SynchroniseQueue(); Status = choice is "again" or "replace" ? "Your chosen duplicate action was queued." : "Existing media kept. No download started for that choice.";
        });
    }
    public Bitmap? QueueThumbnail(Guid id, QueueJob? job) => (job?.Result?.Media?.Thumbnail ?? (_jobThumbnails.TryGetValue(id, out var value) ? value : null)) is { } uri && _thumbnailCache.TryGetValue(uri.AbsoluteUri, out var image) ? image : null;
    public DownloadOptions BuildDownloadOptions(Uri uri)
    {
        int? height = Quality.EndsWith("p maximum", StringComparison.Ordinal) && int.TryParse(Quality.Split('p')[0], out var parsed) ? parsed : null;
        // Just part of it: only for the analysed link itself; a wrong time stops here with what to fix.
        TimeSpan? sectionStart = null, sectionEnd = null;
        if (SavePart && CanSavePart && _media?.Url == uri)
        {
            if (!TryGetPart(out var start, out var end, out var problem)) throw new MediaOperationException(problem);
            sectionStart = start; sectionEnd = end;
        }
        return new() { SectionStart = sectionStart, SectionEnd = sectionEnd, Url = uri, OutputDirectory = SelectedOutputDirectory, Mode = DownloadMode, MaxHeight = IsArchiveDownload ? null : height, SmallestFile = !IsArchiveDownload && Quality == "Smallest available", Container = IsArchiveDownload ? MediaContainer.Auto : Container, AudioFormat = AudioFormat, AudioKbps = AudioKbps, VideoCodec = IsArchiveDownload ? VideoCodec.Original : VideoCodec, Fps = IsArchiveDownload ? null : ParseChoice(DownloadFps), HdrToSdr = !IsArchiveDownload && HdrToSdr, AudioLanguage = Empty(AudioLanguage), SubtitleLanguages = Empty(SubtitleLanguages), AutomaticCaptions = AutomaticCaptions, EmbedThumbnail = EmbedThumbnail, EmbedMetadata = EmbedMetadata, Browser = BrowserValue(), ReplaceExisting = ReplaceExisting, ArchiveZip = IsArchiveDownload && ArchiveZip, NamingTemplate = Empty(NamingTemplate) };
    }
    private string? BrowserValue() => Browser == "None" ? null : Browser.ToLowerInvariant();
    private async Task LoadThumbnailAsync(Uri? uri, CancellationToken ct, Func<bool> isCurrent)
    {
        if (uri is null || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) return;
        try
        {
            if (_thumbnailCache.TryGetValue(uri.AbsoluteUri, out var cached)) { if (isCurrent()) Thumbnail = cached; return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _thumbnailHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 5_000_000 || !(response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)) return;
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token); using var memory = new MemoryStream(); var buffer = new byte[16384];
            while (true) { var read = await input.ReadAsync(buffer, deadline.Token); if (read == 0) break; if (memory.Length + read > 5_000_000) return; await memory.WriteAsync(buffer.AsMemory(0, read), deadline.Token); }
            var data = memory.ToArray();
            var image = await Task.Run(() => DecodeThumbnail(data), ct);
            if (image is null) return;
            if (_disposed) { image.Dispose(); return; }
            _thumbnailCache[uri.AbsoluteUri] = image; if (isCurrent()) Thumbnail = image; foreach (var row in Queue) row.ThumbnailChanged();
            while (_thumbnailCache.Count > 24) { var oldest = _thumbnailCache.First(p => !ReferenceEquals(p.Value, Thumbnail)); _thumbnailCache.Remove(oldest.Key); foreach (var row in Queue) row.ThumbnailChanged(); oldest.Value.Dispose(); }
        }
        catch (Exception) { /* Optional thumbnail failures or cancellation never block the media workflow. */ }
    }
    // SKCodec takes ownership of a managed stream and closes it when disposed, so the size
    // check reads its own stream and the decode gets a fresh one over the same bytes.
    // (Reusing one MemoryStream for both threw ObjectDisposedException, and the optional
    // thumbnail silently never appeared.) Oversized or undecodable images return null.
    // The widest preview frame is 296 DIP, 592 px at 200% scaling, so images decode up to
    // 640 px wide (never wider than the source, which would only spend memory).
    public const int ThumbnailDecodeMaxWidth = 640;
    public static Bitmap? DecodeThumbnail(byte[] data)
    {
        int decodeWidth;
        using (var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(data)))
        {
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || codec.Info.Width > 8192 || codec.Info.Height > 8192 || (long)codec.Info.Width * codec.Info.Height > 16_000_000) return null;
            decodeWidth = Math.Max(1, Math.Min(Math.Min(ThumbnailDecodeMaxWidth, codec.Info.Width), (int)(384d * codec.Info.Width / codec.Info.Height)));
        }
        using var stream = new MemoryStream(data, writable: false);
        return Bitmap.DecodeToWidth(stream, decodeWidth);
    }
    // Queue events arrive from worker threads several times a second per job. They are
    // coalesced into one UI pass per dispatcher cycle that touches only changed rows;
    // the list itself is only restructured when jobs were added, removed or reordered.
    private readonly object _queueEvents = new();
    private readonly HashSet<Guid> _dirtyJobs = [];
    private readonly Dictionary<Guid, QueueJob> _finishedJobs = [];
    private readonly Dictionary<Guid, QueueRow> _rows = [];
    private bool _queueFlushPosted;
    private void QueueChanged(QueueJob job)
    {
        lock (_queueEvents)
        {
            _dirtyJobs.Add(job.Id);
            if (job.State == JobState.Finished) _finishedJobs[job.Id] = job;
            if (_queueFlushPosted) return;
            _queueFlushPosted = true;
        }
        Dispatcher.UIThread.Post(FlushQueueChanges, DispatcherPriority.Background);
    }
    private async void FlushQueueChanges()
    {
        HashSet<Guid> dirty; QueueJob[] finished;
        lock (_queueEvents) { dirty = [.. _dirtyJobs]; _dirtyJobs.Clear(); finished = [.. _finishedJobs.Values]; _finishedJobs.Clear(); _queueFlushPosted = false; }
        if (_disposed) return;
        try
        {
            SynchroniseQueue(dirty);
            foreach (var job in finished) await HandleFinishedJobAsync(job);
        }
        catch (Exception error) { CaptureDiagnostics(error); Status = FriendlyError(error); }
    }
    private async Task HandleFinishedJobAsync(QueueJob job)
    {
        if (_disposed || job.State != JobState.Finished || !_finishedHandled.Add(job.Id)) return;
        // A finished Replace overwrote the saved file, and its new record describes it. The old
        // record would list the same file again with its old size and date, so it is removed.
        // Removing a record never touches the file.
        if (_supersededRecords.Remove(job.Id, out var superseded) && job.Result is { } replaced && SameFilePath(replaced.FilePath, superseded.FilePath))
            try { await _services.Store.RemoveHistoryAsync(superseded.Id); }
            catch (Exception error) { CaptureDiagnostics(error); }
        try { await ReloadHistoryAsync(CancellationToken.None); }
        catch (Exception error) { CaptureDiagnostics(error); Status = "Download finished. History could not refresh: " + FriendlyError(error); }
        if (NotifyCompleted) try { CompletionNotice?.Invoke(job.Title); } catch (Exception error) { CaptureDiagnostics(error); }
        if (job.Result?.Media?.Thumbnail is { } thumbnail && !_thumbnailCache.ContainsKey(thumbnail.AbsoluteUri)) _ = LoadThumbnailAsync(thumbnail, CancellationToken.None, () => false);
        // Share when finished: only ever after this successful download, and one upload at a time.
        if (_shareAfter.Remove(job.Id) && job.Result is { } shared) RequestAutoShare(job.Id, shared.Media?.Title is { Length: > 0 } title ? title : job.Title, shared.FilePath);
        if (_editAfter.Remove(job.Id) && job.Result is { } result) await RequestEditorOpenAsync(result.FilePath);
    }
    private void SynchroniseQueue() => SynchroniseQueue(null);
    private void SynchroniseQueue(IReadOnlySet<Guid>? dirty)
    {
        var snapshot = _queue.Snapshot;
        var structural = Queue.Count != snapshot.Count;
        for (var i = 0; !structural && i < snapshot.Count; i++) structural = Queue[i].Id != snapshot[i].Id;
        if (structural)
        {
            var ids = snapshot.Select(j => j.Id).ToHashSet();
            foreach (var id in _jobThumbnails.Keys.Where(id => !ids.Contains(id)).ToArray()) _jobThumbnails.Remove(id);
            _editAfter.RemoveWhere(id => !ids.Contains(id)); _shareAfter.RemoveWhere(id => !ids.Contains(id));
            for (var i = Queue.Count - 1; i >= 0; i--) if (!ids.Contains(Queue[i].Id)) { _rows.Remove(Queue[i].Id); Queue.RemoveAt(i); }
            for (var i = 0; i < snapshot.Count; i++)
            {
                var job = snapshot[i];
                if (!_rows.TryGetValue(job.Id, out var row)) { row = new QueueRow(job.Id, this); _rows[job.Id] = row; row.Update(job); Queue.Insert(i, row); continue; }
                if (!ReferenceEquals(Queue[i], row)) Queue.Move(Queue.IndexOf(row), i);
            }
        }
        foreach (var job in snapshot) if (dirty is null || dirty.Contains(job.Id)) _rows[job.Id].Update(job);
        NotifyQueueBadge();
        OnQueueSynchronised();
        PersistQueueIfChanged();
    }
    public void CancelJob(Guid id) => _queue.Cancel(id);
    // Retry keeps the job's id and place, so its edit-after flag and thumbnail carry over.
    public void RetryJob(Guid id) { TryAction(() => { if (_queue.Retry(id) is null) Status = "This download is already queued or running."; }); SynchroniseQueue(); }
    // A removed job must never open the editor, even if it finishes while stopping.
    public void RemoveJob(Guid id) { _editAfter.Remove(id); _shareAfter.Remove(id); _supersededRecords.Remove(id); TryAction(() => _queue.Remove(id)); SynchroniseQueue(); }
    public void MoveJob(Guid id, int direction) { _queue.Move(id, direction); SynchroniseQueue(); }
    public void OpenFile(string path) => FileAction(() => _services.Platform.OpenFile(path));
    public void RevealFile(string path) => FileAction(() => _services.Platform.RevealFile(path));
    internal const string MissingFileStatus = "This file is no longer available. It may have been moved, renamed or deleted.";
    // Open and Show file (Library, Queue, the editor's latest export, an existing duplicate)
    // say plainly when the file has gone instead of doing nothing.
    private void FileAction(Action action)
    {
        try { action(); }
        catch (FileNotFoundException) { Status = MissingFileStatus; }
        catch (Exception error) { Status = FriendlyError(error); }
    }
    public Task OpenEditorAsync(string path) => RunAsync(OperationLane.Editor, async ct =>
    {
        OnEditorOpening();
        Status = "Reading local media…";
        // The probe decides whether the file can be edited: until it succeeds the previous
        // file stays open untouched. Heavy work runs off the UI thread.
        var probe = await Task.Run(() => _services.Media.ProbeAsync(path, ct), ct);
        ct.ThrowIfCancellationRequested();
        // Switch in one step and drop the previous waveform, so the editor never shows the
        // new file's title over the old file's sound.
        Playback.Stop();
        // The soundboard preset belongs to the file it was applied to; a different file starts
        // without it (and gets back the audio settings it replaced).
        var soundboardEnded = !string.Equals(EditorPath, path, StringComparison.OrdinalIgnoreCase) && EndSoundboard(null);
        _probe = probe; EditorPath = path; AudioOnly = !probe.HasVideo;
        // The bitrates the encoder accepts can depend on this file's sample rate and channels.
        RefreshExportBitrates();
        // HDR to SDR only applies to HDR (PQ or HLG) sources; an SDR file has nothing to convert.
        if (!EditorSourceIsHdr) ExportHdr = false;
        OnPropertyChanged(nameof(EditorSourceIsHdr));
        DeleteOriginal = false; Waveform = [];
        OnPropertyChanged(nameof(HasEditor)); OnPropertyChanged(nameof(EditorSummary));
        StartTime = "00:00:00.000"; EndTime = MediaValidation.Timecode(probe.Duration);
        string? previewProblem = null, waveformProblem = null;
        try { Playback.Open(path); Playback.SetVolume(Volume); }
        catch (Exception error) { previewProblem = "Editor ready; preview could not open: " + FriendlyError(error); }
        SelectedTab = 3; UpdateEstimate();
        // The waveform and encoder list fill in afterwards; if they fail, the file stays fully
        // editable and exportable (neither is needed to cut or convert).
        if (probe.HasAudio)
        {
            // One decode gives every point the editor needs: the decode costs the same for any
            // point count, so a long file is not decoded a second time for its detail.
            var points = OpenWaveformPoints(probe.Duration);
            try { var waveform = await Task.Run(() => _services.Media.WaveformAsync(path, points, ct), ct); if (EditorPath == path) Waveform = waveform; }
            catch (Exception error) when (error is not OperationCanceledException) { CaptureDiagnostics(error); waveformProblem = "Editor ready; the waveform could not be drawn (" + FriendlyError(error) + "). You can still preview, trim and export."; }
        }
        // The first call test-encodes on each hardware encoder (well under a second normally, longer on a busy PC).
        if (Encoders.Count == 0) Status = "Checking hardware encoders…";
        try { var encoders = await Task.Run(() => _services.Media.EncodersAsync(ct), ct); if (EditorPath == path) { Encoders.Clear(); foreach (var encoder in encoders) Encoders.Add(encoder); } }
        catch (Exception error) when (error is not OperationCanceledException) { CaptureDiagnostics(error); }
        // Pages react after the overview waveform, so any detailed pass follows it.
        OnEditorOpened();
        Status = previewProblem ?? waveformProblem ?? "Editor ready. Preview and choose a range, then Export." + (soundboardEnded ? " The soundboard preset from the previous file is off." : "");
    });
    public ExportOptions BuildExportOptions(string output)
    {
        if (_probe is null) throw new MediaOperationException("Open a local file first.");
        if (!MediaValidation.TryTimecode(StartTime, out var start)) throw new MediaOperationException("The start time isn't a timecode. Use 05:48, 00:05:48 or 00:05:48.250.");
        if (!MediaValidation.TryTimecode(EndTime, out var end)) throw new MediaOperationException("The end time isn't a timecode. Use 05:48, 00:05:48 or 00:05:48.250.");
        if (start < TimeSpan.Zero || end <= start || end > _probe.Duration + TimeSpan.FromMilliseconds(1)) throw new MediaOperationException("Enter a valid range within the file: end must come after start.");
        if (IsExactClip && AudioOnly && ExportAudioFormat == AudioFormat.Original) throw new MediaOperationException("An exact audio clip is re-encoded, so choose an audio format such as MP3, AAC or Opus. Lossless keeps the original audio.");
        var fadeIn = TimeSpan.Zero; var fadeOut = TimeSpan.Zero;
        if (IsExactClip && (!MediaValidation.TryTimecode(FadeIn, out fadeIn) || !MediaValidation.TryTimecode(FadeOut, out fadeOut))) throw new MediaOperationException("Fade times must be valid non-negative seconds or timecodes.");
        long? target = null;
        if (IsExactClip && Compression == CompressionPreset.TargetSize)
        {
            if (!double.TryParse(TargetSizeMb, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mb) || !double.IsFinite(mb) || mb <= 0 || mb > 1_000_000) throw new MediaOperationException("Enter a positive target size in MB.");
            target = checked((long)(mb * 1_000_000));
        }
        return new() { InputPath = EditorPath, OutputPath = output, Start = start, End = end, ClipMode = ClipMode, AudioOnly = AudioOnly, AudioFormat = IsExactClip ? ExportAudioFormat : AudioFormat.Original, AudioKbps = ExportAudioKbps, VideoCodec = IsExactClip ? ExportCodec : VideoCodec.Original, Encoder = Encoder, Compression = Compression, TargetBytes = target, MaxHeight = IsExactClip ? ParseChoice(ExportHeight) : null, Fps = IsExactClip ? ParseChoice(ExportFps) : null, HdrToSdr = IsExactClip && ExportHdr, Volume = IsExactClip ? ExportVolume : 1, Normalize = IsExactClip && Normalize, TrimSilence = IsExactClip && TrimSilence, Mono = IsExactClip && Mono, SampleRate = IsExactClip ? ParseChoice(SampleRate) : null, FadeIn = IsExactClip ? fadeIn : TimeSpan.Zero, FadeOut = IsExactClip ? fadeOut : TimeSpan.Zero, DeleteOriginalAfterSuccess = DeleteOriginal, DiscordSoundboard = IsExactClip && Soundboard, SoundboardMaxSeconds = _settings.SoundboardMaxSeconds, SoundboardTargetBytes = _settings.SoundboardTargetBytes };
    }
    // One place decides the output container, so the pre-dialog check, the estimate
    // and the export agree.
    private string ExportExtension()
    {
        if (AudioOnly)
        {
            if (IsExactClip && ExportAudioFormat != AudioFormat.Original) return ExportAudioFormat == AudioFormat.Aac ? "m4a" : ExportAudioFormat.ToString().ToLowerInvariant();
            return _probe?.AudioCodec switch { "aac" => "m4a", "mp3" => "mp3", "opus" => "opus", "vorbis" => "ogg", "flac" => "flac", var codec when codec?.StartsWith("pcm_", StringComparison.Ordinal) == true => "wav", _ => throw new MediaOperationException("The original audio codec has no supported export container. Choose Exact and a regular audio format.") };
        }
        if (IsExactClip) return "mp4";
        // Lossless keeps the source container when it is a supported one; others copy into MKV.
        var source = Path.GetExtension(EditorPath).TrimStart('.').ToLowerInvariant();
        return source switch { "mp4" or "mkv" or "webm" or "mov" or "avi" => source, "m4v" => "mp4", _ => "mkv" };
    }
    public Task ExportAsync() => RunAsync(OperationLane.Editor, async ct =>
    {
        var extension = ExportExtension();
        var check = BuildExportOptions("validation." + extension);
        // Soundboard rules are checked before the save dialog, never after the user picked a name.
        if (check.DiscordSoundboard) MediaService.ValidateSoundboardChoice(check);
        var folder = Empty(ClipsDirectory) ?? DownloadDirectory;
        var output = await _services.Platform.SaveMediaAsync(FreeClipName(Path.GetFileNameWithoutExtension(EditorPath), extension, folder) + "." + extension, extension, folder);
        if (output is null) { Status = "Export cancelled before processing."; return; }
        var options = BuildExportOptions(output) with { ReplaceExisting = File.Exists(output) };
        if (options.DeleteOriginalAfterSuccess && _sharingPath is { } sharing && string.Equals(Path.GetFullPath(sharing), Path.GetFullPath(EditorPath), StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("This file is still uploading. Wait for the upload to finish before deleting the original.");
        var previewClosed = options.DeleteOriginalAfterSuccess;
        if (previewClosed) Playback.Close();
        Status = "Exporting your selection…";
        ExportResult? result = null;
        IsExporting = true;
        var progress = Progress(OperationLane.Editor);
        try { result = await Task.Run(() => _services.Media.ExportAsync(options, progress, ct), ct); }
        finally
        {
            progress.Close();
            IsExporting = false;
            // A failed or cancelled export, or one that kept the original, gets its preview back.
            if (previewClosed && result is not { OriginalDeleted: true } && File.Exists(EditorPath)) TryAction(() => { Playback.Open(EditorPath); Playback.SetVolume(Volume); });
        }
        var completed = $"Export verified: {Path.GetFileName(result.FilePath)} · {Size(result.Probe.SizeBytes)}" + (result.OriginalDeleted ? ". Original deleted as requested." : ". Original kept.") + (result.Warning is { } warning ? " " + MediaValidation.Redact(warning) : "");
        Status = completed;
        if (result.OriginalDeleted) { Playback.Stop(); _probe = null; EditorPath = ""; OnPropertyChanged(nameof(HasEditor)); OnPropertyChanged(nameof(EditorSummary)); }
        try
        {
            await _services.Store.AddHistoryAsync(new(Guid.NewGuid().ToString("N"), Path.GetFileName(result.FilePath), null, "Local export", DateTimeOffset.UtcNow, result.FilePath, options.AudioOnly ? "Audio Clip" : "Video Clip", "Verified export", result.Probe.SizeBytes, "Finished"), ct);
            await ReloadHistoryAsync(ct);
        }
        catch (Exception historyError) { Status = completed + " The export succeeded, but history could not be updated: " + (historyError is OperationCanceledException ? "history recording was cancelled." : MediaValidation.Redact(historyError.Message)); }
    });
    // ----- Export as GIF: the selected range as a silent, looping GIF (at most 60 s; 15 fps, at most 480 px wide) -----
    private IAsyncRelayCommand? _exportGifCommand;
    public IAsyncRelayCommand ExportGifCommand => _exportGifCommand ??= new AsyncRelayCommand(ExportGifAsync);
    public bool CanExportGif => EditorHasVideo && CanExport;
    public Task ExportGifAsync() => RunAsync(OperationLane.Editor, async ct =>
    {
        if (_probe is not { HasVideo: true }) throw new MediaOperationException("A GIF needs a picture. Open a video file first.");
        if (!MediaValidation.TryTimecode(StartTime, out var start) || !MediaValidation.TryTimecode(EndTime, out var end) || start < TimeSpan.Zero || end <= start || end > _probe.Duration + TimeSpan.FromMilliseconds(1)) throw new MediaOperationException("Choose the part to turn into a GIF first: the end must come after the start.");
        if (end - start > MediaService.MaxGifLength) throw new MediaOperationException("GIFs are limited to 60 seconds. Drag the handles closer together, or export a video clip instead.");
        var folder = Empty(ClipsDirectory) ?? DownloadDirectory;
        var output = await _services.Platform.SaveMediaAsync(FreeClipName(Path.GetFileNameWithoutExtension(EditorPath), "gif", folder) + ".gif", "gif", folder);
        if (output is null) { Status = "GIF cancelled before processing."; return; }
        var options = new ExportOptions { InputPath = EditorPath, OutputPath = output, Start = start, End = end, Gif = true, ReplaceExisting = File.Exists(output) };
        Status = "Making the GIF…";
        ExportResult result;
        IsExporting = true;
        var progress = Progress(OperationLane.Editor);
        try { result = await Task.Run(() => _services.Media.ExportAsync(options, progress, ct), ct); }
        finally { progress.Close(); IsExporting = false; }
        var completed = $"GIF ready: {Path.GetFileName(result.FilePath)} · {Size(result.Probe.SizeBytes)}. Original kept.";
        Status = completed;
        try
        {
            await _services.Store.AddHistoryAsync(new(Guid.NewGuid().ToString("N"), Path.GetFileName(result.FilePath), null, "Local export", DateTimeOffset.UtcNow, result.FilePath, "GIF", "Verified export", result.Probe.SizeBytes, "Finished"), ct);
            await ReloadHistoryAsync(ct);
        }
        catch (Exception historyError) { Status = completed + " The GIF was made, but history could not be updated: " + (historyError is OperationCanceledException ? "history recording was cancelled." : MediaValidation.Redact(historyError.Message)); }
    });

    private string? _sharingPath;
    private string _shareFileName = "", _shareProgressText = "";
    /// <summary>
    /// While an upload runs: the amount sent and any notice, e.g. "12.4 MB of 35 MB" or "12.4 MB of
    /// 35 MB · Connection lost. Retrying the encrypted upload in 5 s…". Empty otherwise.
    /// </summary>
    public string ShareProgressText { get => _shareProgressText; private set => SetProperty(ref _shareProgressText, value); }
    /// <summary>The file the current share link belongs to.</summary>
    public string ShareFileName { get => _shareFileName; private set => SetProperty(ref _shareFileName, value); }
    private Task ShareEditorAsync() => string.IsNullOrWhiteSpace(EditorPath) ? RunAsync(OperationLane.Share, _ => throw new MediaOperationException("Open or choose a local file first.")) : ShareFileAsync(EditorPath);
    /// <summary>
    /// Shares a local file. A very large one waits for confirmation first, because File.Kiwi gives an upload
    /// 36 hours to finish and an interrupted share cannot resume yet: choosing Share again for the
    /// same file (from any Share button), or <see cref="ConfirmLargeShareCommand"/>, starts it.
    /// </summary>
    public Task ShareFileAsync(string path)
    {
        // While another upload runs, the lane itself reports that; nothing needs confirming yet.
        if (!CanShare || LargeShareBytes(path) is not { } bytes) return StartShareAsync(path);
        if (!IsPendingLargeShare(path)) { AskToConfirmLargeShare(path, bytes); return Task.CompletedTask; }
        var waited = Stopwatch.GetElapsedTime(_largeShareAskedAt);
        // The second click of a double-click is not a decision; the prompt stays as it is.
        if (waited < LargeShareRepeatDelay) return Task.CompletedTask;
        // A prompt left for a long time is shown again rather than taken as agreed.
        if (waited > LargeShareConfirmWindow) { AskToConfirmLargeShare(path, bytes); return Task.CompletedTask; }
        return StartShareAsync(path);
    }
    private Task StartShareAsync(string path) => RunAsync(OperationLane.Share, ct => ShareCoreAsync(path, automatic: false, ct));
    // One upload in the Share lane. An automatic share (Share when finished) stays on the current page,
    // leaves another file's large-upload prompt alone and lets its caller report the result.
    private async Task<ShareResult> ShareCoreAsync(string path, bool automatic, CancellationToken ct)
    {
        if (IsExporting && DeleteOriginal && string.Equals(Path.GetFullPath(path), Path.GetFullPath(EditorPath), StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("The running export may delete this file. Wait for it to finish, then share.");
        if (!automatic || IsPendingLargeShare(path)) ClearLargeShareRequest();
        Status = "Encrypting and uploading to File.Kiwi…"; ShareUrl = ""; ShareFileName = Path.GetFileName(path); _shareLinkPath = path;
        ShareInfo = "Encrypting on this device, then uploading. Keep the app open until the link appears.";
        ShareResult result;
        IsSharing = true; _sharingPath = path; ShareProgressText = "";
        var progress = Progress(OperationLane.Share, FileLength(path));
        try { result = await Task.Run(() => _services.Sharing.ShareAsync(path, "Shared media", progress, ct), ct); }
        catch { ShareInfo = "The upload did not finish, so nothing was shared. You can try again."; throw; }
        finally { progress.Close(); IsSharing = false; _sharingPath = null; ShareProgressText = ""; }
        ShareUrl = result.ShareUrl;
        RecordShareLink(path, result.ShareUrl);
        ShareInfo = (result.FreeDownloadHours is { } hours ? $"Free to download for about {hours:0.##} hours" + (result.EstimatedFreeUntil is { } until ? $", until about {until.ToLocalTime():g}. " : ". ")
                : $"File.Kiwi did not say how long this link lasts; API uploads are usually free to download for {FreeDownloadHoursText(File.Exists(path) ? new FileInfo(path).Length : 0)}. ")
            + "After that the link stops working." + (result.Warning is { } warning ? " " + MediaValidation.Redact(warning) : "");
        if (!automatic) { SelectedTab = 3; Status = "Upload finished. Copy the full link to share; its decryption secret is not saved to history."; }
        return result;
    }

    // ----- Very large shares -----
    // File.Kiwi gives an upload 36 hours to finish and an interrupted share cannot be resumed yet. A file
    // of about 100 GB, or one that would take over 30 hours at a modest 1 MB/s, could run out of
    // time, so the upload starts only after the user confirms they understand that.
    internal const long LargeShareBytes100Gb = 100_000_000_000;
    private const double AssumedUploadBytesPerSecond = 1_000_000;
    private static readonly TimeSpan LongUploadThreshold = TimeSpan.FromHours(30);
    private string? _largeSharePath;
    private long _largeShareBytes, _largeShareAskedAt;
    private IAsyncRelayCommand? _confirmLargeShareCommand;
    private IRelayCommand? _cancelLargeShareCommand;
    /// <summary>The size from which a share asks for confirmation (100 GB; tests use a small value).</summary>
    internal long LargeShareThresholdBytes { get; set; } = LargeShareBytes100Gb;
    /// <summary>A repeat Share sooner than this after the prompt is a double-click, not a confirmation.</summary>
    internal TimeSpan LargeShareRepeatDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How long Share again confirms the prompt; after that the prompt is shown again.</summary>
    internal TimeSpan LargeShareConfirmWindow { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>True while a very large share waits for Upload anyway or Cancel.</summary>
    /// <summary>File.Kiwi's table of how long API uploads stay free to download (they differ from the website's).</summary>
    public const string ShareHoursUrl = "https://file.kiwi/api#free-download-hours-by-file-size";
    public IRelayCommand OpenShareHoursCommand => _openShareHours ??= new RelayCommand(() => _services.Platform.OpenUri(new Uri(ShareHoursUrl)));
    private IRelayCommand? _openShareHours;
    /// <summary>The published free download time for an API upload of this size: 24 h under 10 GB, 12 h to 20 GB, then 6 h.</summary>
    public static string FreeDownloadHoursText(long bytes) => bytes < 10_000_000_000 ? "24 hours" : bytes < 20_000_000_000 ? "12 hours" : "6 hours";

    public bool HasPendingLargeShare => _largeSharePath is not null;
    public string LargeShareTitle => _largeSharePath is null ? "" : $"Upload {Size(_largeShareBytes)}?";
    public string LargeShareWarning => _largeSharePath is null ? "" :
        $"{Path.GetFileName(_largeSharePath)} is {Size(_largeShareBytes)}. At about 1 MB/s the upload takes around {EstimatedUploadTime(_largeShareBytes).TotalHours:0} hours. File.Kiwi gives an upload 36 hours to finish, and an interrupted share can't be resumed yet, so a slow or interrupted upload has to start again from the beginning.";
    /// <summary>Starts the large share the user has confirmed.</summary>
    public IAsyncRelayCommand ConfirmLargeShareCommand => _confirmLargeShareCommand ??= new AsyncRelayCommand(() =>
    {
        if (_largeSharePath is not { } path) return Task.CompletedTask;
        ClearLargeShareRequest();
        return StartShareAsync(path);
    });
    /// <summary>Drops the large share; nothing is uploaded.</summary>
    public IRelayCommand CancelLargeShareCommand => _cancelLargeShareCommand ??= new RelayCommand(() =>
    {
        if (_largeSharePath is null) return;
        ClearLargeShareRequest();
        Status = "Large upload cancelled. Nothing was uploaded.";
    });
    internal static TimeSpan EstimatedUploadTime(long bytes) => TimeSpan.FromSeconds(bytes / AssumedUploadBytesPerSecond);
    internal static bool NeedsLargeShareConfirmation(long bytes, long thresholdBytes) => bytes >= thresholdBytes || EstimatedUploadTime(bytes) > LongUploadThreshold;
    private long? LargeShareBytes(string path) => FileLength(path) is { } bytes && NeedsLargeShareConfirmation(bytes, LargeShareThresholdBytes) ? bytes : null;
    private static long? FileLength(string path)
    {
        try { var file = new FileInfo(path); return file.Exists ? file.Length : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }
    private bool IsPendingLargeShare(string path)
    {
        if (_largeSharePath is not { } pending) return false;
        try { return string.Equals(Path.GetFullPath(pending), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return false; }
    }
    // The prompt stays where the user chose Share (Library, Queue or the editor), so the same
    // Share button confirms it; the action comes first because the status line may be trimmed.
    private void AskToConfirmLargeShare(string path, long bytes)
    {
        _largeSharePath = path; _largeShareBytes = bytes; _largeShareAskedAt = Stopwatch.GetTimestamp();
        RaiseLargeShare();
        Status = $"Large upload ({Size(bytes)}): choose Share again to upload it anyway. File.Kiwi gives an upload 36 hours to finish and an interrupted upload can't resume, so a slow upload may have to start over.";
    }
    private void ClearLargeShareRequest()
    {
        if (_largeSharePath is null) return;
        _largeSharePath = null; _largeShareBytes = 0;
        RaiseLargeShare();
    }
    private void RaiseLargeShare() { OnPropertyChanged(nameof(HasPendingLargeShare)); OnPropertyChanged(nameof(LargeShareTitle)); OnPropertyChanged(nameof(LargeShareWarning)); }
    private async Task ReloadHistoryAsync(CancellationToken ct) { _history = (await _services.Store.LoadHistoryAsync(ct)).OrderByDescending(h => h.Date).ToList(); FilterHistory(); OnHistoryReloaded(); }
    // One history-sync implementation (MainViewModel.Library.cs): rows are reused by record id
    // and small changes are applied in place, so a finished download inserts one row and every
    // other row keeps its container, file check and scroll position.
    private void FilterHistory() => SyncHistoryRows();
    public Task RemoveHistoryAsync(string id) => RunAsync(OperationLane.Maintenance, async ct => { await _services.Store.RemoveHistoryAsync(id, ct); await ReloadHistoryAsync(ct); Status = "History record removed. The media file was kept."; });
    public Task CopyPathAsync(string path) => CopyToClipboardAsync(path, "File path copied.");
    public Task CopySourceAsync(HistoryEntry entry) => entry.SourceUrl is { } url ? CopyToClipboardAsync(url, "Saved source link copied.") : Task.CompletedTask;
    // Another app can hold the clipboard; that must never crash or lose state.
    private async Task CopyToClipboardAsync(string value, string done)
    {
        if (await TryCopyToClipboardAsync(value)) Status = done;
        else Status = "The clipboard is busy in another app. Try copying again.";
    }
    private async Task<bool> TryCopyToClipboardAsync(string value)
    {
        // Text the app copied itself (a share link with its key, a source link) is never offered
        // back as a new download when the window is activated again.
        try { await _services.Platform.CopyAsync(value); _lastClipboardText = value; return true; }
        catch (Exception error) { CaptureDiagnostics(error); return false; }
    }
    public async Task RepeatDownloadAsync(HistoryEntry entry)
    {
        if (!MediaValidation.TryMediaUrl(entry.SourceUrl, out var url)) { Status = "This local export has no saved source link."; return; }
        // A check of another link gives way to this one, exactly as editing the link does; it is
        // waited for so this link is analysed instead of being refused as "still checking".
        // Work past its check (adding chosen entries to the queue) is never interrupted: the link
        // stays as it is and the status says to wait.
        if (DownloadWorkRunning && !IsAnalysing) { Status = LaneBusyMessage(OperationLane.Download); return; }
        Url = url!.AbsoluteUri; SelectedTab = 0;
        if (DownloadWorkRunning) { CancelDownloadWork(); await _lanes[OperationLane.Download].Finished; }
        if (Url != url.AbsoluteUri) return;
        Status = "Source link restored. Review the current formats, then choose Download.";
        await AnalyseAsync();
        // An item of a multi-video post was saved with its position: select that item again.
        if (PlaylistItemKey(entry.PlaylistItems) is not { } item || _media is not { IsPlaylist: true } media || Url != url.AbsoluteUri) return;
        if (Playlist.FirstOrDefault(choice => !choice.IsList && PlaylistItemKey(PlaylistTarget(media, choice)?.PlaylistItems) == item) is { } saved)
        {
            saved.Selected = true;
            Status = "Source link restored with the saved item selected. Review it, then choose Download.";
        }
    }
    public Task SaveSettingsAsync() => RunAsync(OperationLane.Maintenance, async ct =>
    {
        if (string.IsNullOrWhiteSpace(DownloadDirectory) || !Path.IsPathFullyQualified(DownloadDirectory)) throw new MediaOperationException("Choose a complete output folder path.");
        foreach (var folder in new[] { AudioDirectory, ClipsDirectory, ArchivesDirectory })
            if (!string.IsNullOrWhiteSpace(folder) && !Path.IsPathFullyQualified(folder)) throw new MediaOperationException("Separate output folders must be complete paths, or blank to use your default.");
        int? retention = null;
        if (!string.IsNullOrWhiteSpace(RetentionDays)) { if (!int.TryParse(RetentionDays, out var days) || days < 1 || days > ILocalStore.MaxHistoryRetentionDays) throw new MediaOperationException($"History retention must be blank or between 1 and {ILocalStore.MaxHistoryRetentionDays} days."); retention = days; }
        if (!double.TryParse(SoundboardSeconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 300 || !long.TryParse(SoundboardBytes, out var bytes) || bytes is < 1024 or > 100_000_000) throw new MediaOperationException("Enter sensible positive soundboard limits (up to 300 seconds and 100 MB).");
        var previous = _settings;
        var next = _settings with { DownloadDirectory = Path.GetFullPath(DownloadDirectory), AudioDirectory = Empty(AudioDirectory), ClipsDirectory = Empty(ClipsDirectory), ArchivesDirectory = Empty(ArchivesDirectory), Theme = Theme, ConcurrentDownloads = ConcurrentDownloads, DetectClipboard = DetectClipboard, ShowRecentRecordings = ShowRecentRecordings, StartWithWindows = StartWithWindows, StartMinimized = StartMinimized, ReducedMotion = ReducedMotion, NotifyCompleted = NotifyCompleted, ShareWhenFinished = ShareWhenFinishedDefault, CheckForUpdates = CheckForUpdates, CloseBehavior = CloseBehavior, HistoryRetentionDays = retention, SoundboardMaxSeconds = seconds, SoundboardTargetBytes = bytes };
        var startupChanged = next.StartWithWindows != previous.StartWithWindows || next.StartWithWindows && next.StartMinimized != previous.StartMinimized;
        if (startupChanged) _services.Platform.SetStartup(next.StartWithWindows, next.StartMinimized);
        try { await _services.Store.SaveSettingsAsync(next, ct); }
        catch (Exception error)
        {
            if (startupChanged) try { _services.Platform.SetStartup(previous.StartWithWindows, previous.StartMinimized); }
            catch (Exception rollback) { throw new MediaOperationException("Settings could not save, and Windows startup could not be restored. Review your startup choice and retry.", MediaValidation.RedactDiagnostics(error.Message + "\n" + rollback.Message), error); }
            throw;
        }
        _settings = next; _queue.MaxConcurrent = ConcurrentDownloads;
        try { await _services.Store.PruneHistoryAsync(retention, ct); await ReloadHistoryAsync(ct); Status = "Settings saved on this device."; }
        catch (Exception error) { CaptureDiagnostics(error); Status = "Settings saved. History maintenance could not finish; your media was kept."; }
    });
    // Shows the component service's own completion message ("yt-dlp is already up to
    // date (…)", which components were restored) instead of a fixed string.
    private Task RunComponentTaskAsync(Func<IComponentService, IProgress<JobProgress>, CancellationToken, Task> work, string fallback) => RunAsync(OperationLane.Maintenance, async ct =>
    {
        var lane = Progress(OperationLane.Maintenance);
        var progress = new FinishedMessageProgress(lane);
        try { await Task.Run(() => work(_services.Components, progress, ct), ct); }
        finally { lane.Close(); }
        await InspectComponentsAsync(ct);
        Status = progress.Message is { } message ? MediaValidation.Redact(message) : fallback;
    });
    private sealed class FinishedMessageProgress(IProgress<JobProgress> inner) : IProgress<JobProgress>
    {
        private volatile string? _message;
        public string? Message => _message;
        public void Report(JobProgress value) { if (value.Stage == JobStage.Finished && !string.IsNullOrWhiteSpace(value.Message)) _message = value.Message; inner.Report(value); }
    }
    private async Task InspectComponentsAsync(CancellationToken ct) { Components.Clear(); foreach (var component in await _services.Components.InspectAsync(ct)) Components.Add(component with { Error = MediaValidation.Redact(component.Error) }); OnPropertyChanged(nameof(ComponentsReady)); }
    public void RefreshPlatformPreferences()
    {
        if (_disposed) return;
        var preference = _services.Platform.GetReducedMotionPreference();
        if (SetProperty(ref _systemReducedMotion, preference, nameof(SystemReducedMotion))) { OnPropertyChanged(nameof(MotionPreferenceStatus)); RefreshMotionIndicators(); }
    }
    private void RefreshMotionIndicators() { OnPropertyChanged(nameof(EffectiveReducedMotion)); OnPropertyChanged(nameof(AnimateBusyIndicator)); foreach (var row in Queue) row.MotionChanged(); }
    public Task CleanTemporaryFilesAsync() => RunAsync(OperationLane.Maintenance, async ct =>
    {
        if (_services.TemporaryWorkspaces is not { } temporaryWorkspaces) { TemporaryCleanupStatus = Status = "Temporary-file cleanup is unavailable in this app session."; return; }
        TemporaryCleanupStatus = Status = "Checking app-owned temporary files… You can cancel this check.";
        try
        {
            var result = await temporaryWorkspaces.CleanAsync(ct);
            var cancelled = result.Cancelled || ct.IsCancellationRequested;
            var summary = (cancelled ? "Temporary-file cleanup cancelled." : "Temporary-file cleanup finished.") + $" Removed {result.DeletedFolders:N0} {(result.DeletedFolders == 1 ? "folder" : "folders")} ({Size(result.DeletedBytes)}). Kept {result.ActiveFolders:N0} active, {result.RecoveryFolders:N0} recovery and {result.SkippedFolders:N0} unverified folders.";
            Status = summary;
            var warnings = result.Warnings.Take(5).Select(warning => { var redacted = MediaValidation.RedactDiagnostics(warning); return redacted.Length > 240 ? redacted[..240] + "…" : redacted; }).ToArray();
            TemporaryCleanupStatus = summary + (warnings.Length == 0 ? "" : "\n" + string.Join("\n", warnings)) + (result.Warnings.Count > warnings.Length ? "\nMore items were kept; warning details are limited." : "");
            OnPropertyChanged(nameof(AvailableSpace));
        }
        catch (OperationCanceledException)
        {
            TemporaryCleanupStatus = Status = "Temporary-file cleanup cancelled. Some verified inactive folders may already have been removed. Active jobs and recovery copies are kept.";
        }
        catch (Exception error)
        {
            CaptureDiagnostics(error);
            TemporaryCleanupStatus = Status = "Temporary-file cleanup could not finish. Some verified inactive folders may already have been removed. Check the redacted diagnostics before trying again.";
        }
    });
    private void ApplyTheme() { if (Application.Current is { } app) app.RequestedThemeVariant = Theme switch { "Dark" => ThemeVariant.Dark, "Light" => ThemeVariant.Light, _ => ThemeVariant.Default }; }
    private void ApplySoundboardPreset()
    {
        // What the preset replaces is remembered once, so ending the preset can put it back.
        _soundboardRestore ??= new(ExportAudioFormat, ExportAudioKbps, Mono, SampleRate);
        AudioOnly = true; ExportAudioFormat = SoundboardFormat; ExportAudioKbps = SoundboardKbps; Mono = true; SampleRate = SoundboardSampleRate; Compression = CompressionPreset.Off; ClipMode = ClipMode.Exact; Soundboard = true;
        // The preset only ever shortens: a selection already within the limit keeps its out-point.
        if (_probe is not null && MediaValidation.TryTimecode(StartTime, out var start))
        {
            var limit = TimeSpan.FromSeconds(_settings.SoundboardMaxSeconds);
            var fits = MediaValidation.TryTimecode(EndTime, out var end) && end > start && end - start <= limit && end <= _probe.Duration + TimeSpan.FromMilliseconds(1);
            if (!fits) EndTime = MediaValidation.Timecode(TimeSpan.FromSeconds(Math.Min(_probe.Duration.TotalSeconds, start.TotalSeconds + _settings.SoundboardMaxSeconds)));
        }
        Status = $"Soundboard preset: at most {_settings.SoundboardMaxSeconds:0.##} seconds and {_settings.SoundboardTargetBytes:N0} bytes. Current defaults are editable in Settings; service limits can change.";
    }
    public void UpdateEstimate()
    {
        OnPropertyChanged(nameof(ClipStartRatio)); OnPropertyChanged(nameof(ClipEndRatio));
        if (_probe is null) return;
        try { var estimate = _services.Media.Estimate(_probe, BuildExportOptions("estimate." + ExportExtension())); EstimateText = "Estimate: " + (estimate.EstimatedBytes is { } bytes ? Size(bytes) + " · " : "size unavailable · ") + MediaValidation.Timecode(estimate.Duration) + ". " + estimate.Explanation; }
        catch (Exception error) { EstimateText = FriendlyError(error); }
    }
    public void Seek(double ratio) { PlaybackPosition = Math.Clamp(ratio, 0, 1); }
    private void PlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(IPlaybackService.Position)) SetProperty(ref _playbackPosition, Playback.Position, nameof(PlaybackPosition));
        OnPropertyChanged(nameof(PlaybackTime)); OnPropertyChanged(nameof(PlayLabel)); OnPropertyChanged(nameof(IsPlaying));
    }
    private void TryAction(Action action) { try { action(); } catch (Exception error) { Status = FriendlyError(error); } }
    private static string FriendlyError(Exception error) => error is MediaOperationException ? MediaValidation.Redact(error.Message) : error is FileNotFoundException ? "This file is no longer available. Choose another local file." : "Could not finish: " + MediaValidation.Redact(error.Message);
    private void CaptureDiagnostics(Exception error)
    {
        var details = error is MediaOperationException operation ? operation.Details : null;
        var text = $"{AppIdentity.DisplayName} {AppIdentity.Version}\nOS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" + string.Join("\n", Components.Select(c => c.Name + ": " + (c.Version ?? "unknown"))) + "\nError: " + error.Message + "\n" + details;
        Diagnostics = MediaValidation.RedactDiagnostics(text.Length > 8192 ? text[..8192] : text);
    }
    private static int? ParseChoice(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string Size(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.##} GB" : bytes >= 1_000_000 ? $"{bytes / 1_000_000d:0.##} MB" : $"{bytes / 1000d:0.##} KB";
    public void Dispose()
    {
        if (_disposed) return;
        // Jobs still waiting or running are saved as they are now, before closing cancels them.
        SaveQueueBeforeExit();
        _disposed = true; CancelAllLanes(); StopUpdates(); _thumbnailLoad?.Cancel(); _historySearchDebounce?.Dispose(); _queue.JobChanged -= QueueChanged;
        foreach (var job in _queue.Snapshot.Where(j => j.State is JobState.Running or JobState.Queued)) _queue.Cancel(job.Id);
        Playback.PropertyChanged -= PlaybackChanged; Playback.Dispose(); Thumbnail = null; foreach (var image in _thumbnailCache.Values) image.Dispose(); _thumbnailCache.Clear(); _thumbnailHttp.Dispose();
        _ = ShutdownAsync();
    }
    // Extension points for page-specific partial files; unimplemented hooks compile away.
    partial void OnMediaAnalysed();
    // Called once the editor lane has accepted an open, before the file is probed.
    partial void OnEditorOpening();
    partial void OnEditorOpened();
    partial void OnSettingsLoaded();
    partial void OnQueueSynchronised();
    // History reads report explicitly: with minimal list updates an empty history raises
    // no collection change, so pages must not infer "loaded" from the list.
    partial void OnHistoryReloaded();
    partial void OnHistoryLoadFailed(Exception error);
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync() { CancelAllLanes(); await Task.WhenAll(AllLanesFinished(), _queue.DisposeAsync().AsTask()); }
}
