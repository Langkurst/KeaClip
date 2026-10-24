namespace Klippi.Core;

public static class AppIdentity
{
    public const string DisplayName = "KeaClip";
    public const string Version = "0.5.1"; // Keep equal to <Version> in Directory.Build.props (a test checks).
    public const string Protocol = "keaclip";
#if KEACLIP_PERSONAL
    public const string Edition = "Personal";
#else
    public const string Edition = "Public";
#endif
    /// <summary>Watch links upload to the owner's own storage, so only the owner's Personal build offers them.</summary>
    public const bool WatchLinksIncluded = Edition == "Personal";
    /// <summary>The "Support KeaClip" page (Ko-fi). Empty hides the button until the page exists.</summary>
    public const string SupportUrl = "https://ko-fi.com/keaclip";
    /// <summary>Where testers and users can write to the developer.</summary>
    public const string ContactEmail = "keaclip@proton.me";
#if KEACLIP_BETA
    public const bool IsBeta = true;
#else
    public const bool IsBeta = false;
#endif
}

public enum DownloadMode { VideoAudio, Audio, VideoOnly, Archive }
public enum MediaContainer { Auto, Mp4, Mkv, WebM }
public enum AudioFormat { Original, Mp3, Aac, Opus, Flac, Wav, Ogg }
public enum VideoCodec { Original, H264, Hevc, Av1 }
public enum EncoderPreference { Auto, Gpu, Cpu }
public enum ClipMode { Lossless, Exact }
public enum JobState { Queued, Running, Paused, Cancelled, Failed, Finished }
public enum JobStage { Queued, Analysing, Downloading, Merging, Converting, Compressing, WritingMetadata, Verifying, Uploading, Finished, Cancelled, Failed }
public enum FrameShape { Original, Vertical, Square }
public enum CompressionPreset { Off, Light, Balanced, Strong, VerySmall, TargetSize }
public enum CloseBehavior { Exit, Tray, Ask }

public sealed record MediaFormat(string Id, string? Extension, int? Width, int? Height, double? Fps, string? VideoCodec, string? AudioCodec, long? SizeBytes, double? AudioKbps, string? Language);
public sealed record PlaylistEntry(string Id, string Title, Uri? Url, TimeSpan? Duration);
public sealed record MediaInfo(string Id, string Title, Uri Url, string? Creator, string? Source, TimeSpan? Duration, Uri? Thumbnail, DateTimeOffset? PublishedAt, string? Description, IReadOnlyList<MediaFormat> Formats, IReadOnlyList<PlaylistEntry> Entries, string? MetadataJson)
{
    public bool IsPlaylist => Entries.Count > 0;
}

public sealed record DownloadOptions
{
    public required Uri Url { get; init; }
    public required string OutputDirectory { get; init; }
    public DownloadMode Mode { get; init; } = DownloadMode.VideoAudio;
    public int? MaxHeight { get; init; }
    public bool SmallestFile { get; init; }
    public MediaContainer Container { get; init; } = MediaContainer.Auto;
    public AudioFormat AudioFormat { get; init; } = AudioFormat.Original;
    public int AudioKbps { get; init; } = 320;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Original;
    public int? Fps { get; init; }
    public bool HdrToSdr { get; init; }
    public string? AudioLanguage { get; init; }
    public string? SubtitleLanguages { get; init; }
    public bool AutomaticCaptions { get; init; }
    public bool EmbedThumbnail { get; init; }
    public bool EmbedMetadata { get; init; } = true;
    public string? Browser { get; init; }
    public string? PlaylistItems { get; init; }
    public bool ReplaceExisting { get; init; }
    // Set by the duplicate card's Replace: the saved file this download overwrites. The download
    // is kept only when its finished file is exactly this path; a request that would write any
    // other file (another format) fails with nothing saved and the saved file unchanged.
    public string? ReplacePath { get; init; }
    public bool ArchiveZip { get; init; }
    public string? NamingTemplate { get; init; }
    /// <summary>Save only this part of the media (both set, start before end); null saves all of it.</summary>
    public TimeSpan? SectionStart { get; init; }
    public TimeSpan? SectionEnd { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasSection => SectionStart is not null && SectionEnd is not null;
}

public sealed record JobProgress(JobStage Stage, double? Percent = null, string? Message = null, double? BytesPerSecond = null, TimeSpan? Eta = null);
public sealed record DownloadResult(string FilePath, MediaInfo? Media, long SizeBytes, string? ArchiveDirectory = null, string? ArchiveZipPath = null);
/// <param name="Interactive">A short call the user is waiting for (analysis, probes, version checks). It runs at
/// normal priority; long downloads and exports stay below normal so games and other foreground work keep the CPU.</param>
public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory = null, TimeSpan? Timeout = null, int OutputLimit = 4_194_304, bool Interactive = false);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record ProbeInfo(string FilePath, TimeSpan Duration, long SizeBytes, bool HasVideo, bool HasAudio, int? Width, int? Height, double? Fps, string? VideoCodec, string? AudioCodec, int? SampleRate, int? Channels, string? TechnicalJson = null);
public sealed record ExportOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public TimeSpan? Start { get; init; }
    public TimeSpan? End { get; init; }
    public ClipMode ClipMode { get; init; } = ClipMode.Exact;
    public bool AudioOnly { get; init; }
    public AudioFormat AudioFormat { get; init; } = AudioFormat.Mp3;
    public int AudioKbps { get; init; } = 192;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.H264;
    public EncoderPreference Encoder { get; init; } = EncoderPreference.Auto;
    public CompressionPreset Compression { get; init; } = CompressionPreset.Off;
    public long? TargetBytes { get; init; }
    public int? MaxHeight { get; init; }
    public int? Fps { get; init; }
    public bool HdrToSdr { get; init; }
    /// <summary>Crop the picture to a phone shape (Vertical 9:16 for TikTok, Shorts and Reels) or a square; Original keeps it.</summary>
    public FrameShape Shape { get; init; } = FrameShape.Original;
    /// <summary>Where the crop sits along the side that's cut: 0 = left (or top), 0.5 = centre, 1 = right (or bottom).</summary>
    public double ShapePosition { get; init; } = 0.5;
    public double Volume { get; init; } = 1;
    public bool Normalize { get; init; }
    public bool TrimSilence { get; init; }
    public bool Mono { get; init; }
    public int? SampleRate { get; init; }
    public TimeSpan FadeIn { get; init; }
    public TimeSpan FadeOut { get; init; }
    public bool DeleteOriginalAfterSuccess { get; init; }
    public bool ReplaceExisting { get; init; }
    public bool DiscordSoundboard { get; init; }
    /// <summary>A looping, silent GIF of the selected range (at most 60 s; 15 fps, at most 480 px wide).</summary>
    public bool Gif { get; init; }
    public double SoundboardMaxSeconds { get; init; } = 5.2;
    public long SoundboardTargetBytes { get; init; } = 500_000;
}
public sealed record ExportResult(string FilePath, ProbeInfo Probe, bool OriginalDeleted, string? Warning = null);
public sealed record EncoderInfo(string Name, string Description, bool IsHardware);
public sealed record ExportEstimate(TimeSpan Duration, long? EstimatedBytes, int? VideoKbps, string Explanation);
public sealed record ShareResult(string ShareUrl, string FolderId, string FileId, double? FreeDownloadHours, DateTimeOffset UploadedAt, DateTimeOffset? EstimatedFreeUntil, string Sha256, string? Warning);
public sealed record ComponentInfo(string Name, string? Path, string? Version, bool Ready, string? Error = null);
// PlaylistItems is the entry position for an item of a multi-video post (whose items share the
// post's address), so the duplicate check can tell item 2 from item 1. Records saved before it
// existed load with null, which means "the whole link", as before.
public sealed record HistoryEntry(string Id, string Title, string? SourceUrl, string? Source, DateTimeOffset Date, string FilePath, string Mode, string Quality, long SizeBytes, string Status, string? PlaylistItems = null);

public sealed record AppSettings
{
    public string DownloadDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "KeaClip");
    public string? AudioDirectory { get; init; }
    public string? ClipsDirectory { get; init; }
    public string? ArchivesDirectory { get; init; }
    public bool DetectClipboard { get; init; } = true;
    /// <summary>The Editor lists new game clips and captures from the Videos folder (only listed, never opened or sent).</summary>
    public bool ShowRecentRecordings { get; init; } = true;
    public string Theme { get; init; } = "System";
    public int ConcurrentDownloads { get; init; } = 1;
    public int? HistoryRetentionDays { get; init; }
    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.Exit;
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool NotifyCompleted { get; init; }
    public string PreferredRetention { get; init; } = "Longest Free";
    public bool ReducedMotion { get; init; }
    public double SoundboardMaxSeconds { get; init; } = 5.2;
    public long SoundboardTargetBytes { get; init; } = 500_000;
    public string PresetCheckedDate { get; init; } = "2026-10-07";
    // The starting value of the Download page's "Share when finished" switch. Off by default: a
    // share is an upload, so it only happens after a download the user chose to share.
    public bool ShareWhenFinished { get; init; }
    /// <summary>Ask the release feed once a day whether a newer KeaClip exists. Nothing installs without Update now.</summary>
    public bool CheckForUpdates { get; init; } = true;
    /// <summary>Another https release feed (keaclip-update.json); null uses the default GitHub release feed. Not shown in the app.</summary>
    public string? UpdateFeedUrl { get; init; }
}

/// <summary>
/// One unfinished download saved so it survives a restart. Running jobs are saved as waiting, so they
/// start again. Only links that identify the media without private values are saved: a link whose
/// sign-in, session or signature values would be lost is kept out and only counted (LinkWithheld).
/// </summary>
public sealed record SavedQueueJob
{
    public string Title { get; init; } = "";
    public DownloadOptions? Options { get; init; }
    /// <summary>The job had failed; it comes back failed, with its reason and Retry, instead of starting again.</summary>
    public bool Failed { get; init; }
    public string? Error { get; init; }
    public bool ShareWhenFinished { get; init; }
    public bool EditAfter { get; init; }
    /// <summary>For a Replace download: the history record of the file it overwrites.</summary>
    public string? SupersedesRecordId { get; init; }
    public bool LinkWithheld { get; init; }
}

public sealed record SavedQueue
{
    public int Version { get; init; } = 1;
    /// <summary>The queue was paused (Pause queue): restored jobs wait instead of starting.</summary>
    public bool Paused { get; init; }
    public IReadOnlyList<SavedQueueJob?> Jobs { get; init; } = [];
}

public sealed class MediaOperationException(string userMessage, string? details = null, Exception? inner = null) : Exception(userMessage, inner)
{
    public string? Details { get; } = details;
}
