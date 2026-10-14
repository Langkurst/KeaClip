using System.Globalization;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Klippi.Core;

namespace Klippi.Infrastructure.Engines;

public sealed partial class DownloadService(IProcessRunner runner, IComponentService components, ITemporaryWorkspaceService? temporaryWorkspaces = null) : IDownloadService
{
    private readonly MediaService mediaService = new(runner, components, temporaryWorkspaces);
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "firefox", "chrome", "edge", "brave", "chromium", "opera", "vivaldi", "safari" };
    // Playlist analysis lists at most this many entries (see KNOWN_LIMITATIONS).
    // One more is requested, so a longer list is reported as cut short.
    internal const int PlaylistLimit = 500;
    private const int NameLimit = 140;
    private const string PlaylistChoiceMessage = "Choose the playlist entries first. Each selected entry is downloaded as its own job.";
    // A missing template field prints as NA, which is not JSON; |null keeps
    // every line readable. "p" is yt-dlp's own progress object for one stream.
    private const string ProgressTemplate = "download:KC_PROGRESS:{\"v\":%(info.vcodec|null)j,\"a\":%(info.acodec|null)j,\"f\":%(info.format_id|null)j,\"p\":%(progress)j}";
    // Printed once before transfer: the streams that will be fetched in order,
    // so separate video and audio downloads become one steady percentage.
    private const string PlanTemplate = "before_dl:KC_PLAN:{\"f\":%(requested_formats.:.format_id|null)j,\"v\":%(requested_formats.:.vcodec|null)j,\"a\":%(requested_formats.:.acodec|null)j,\"s\":%(requested_formats.:.filesize|null)j,\"e\":%(requested_formats.:.filesize_approx|null)j}";

    public async Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default)
    {
        ValidateUrl(url);
        using var json = await AnalyseJsonAsync(url, browser, ["--flat-playlist", "--playlist-end", (PlaylistLimit + 1).ToString(EngineSupport.Invariant)], cancellationToken);
        return ParseInfo(json.RootElement, url);
    }

    private async Task<JsonDocument> AnalyseJsonAsync(Uri url, string? browser, IEnumerable<string> selection, CancellationToken cancellationToken)
    {
        var arguments = Common(browser);
        arguments.AddRange(["--dump-single-json", "--skip-download"]);
        arguments.AddRange(selection);
        arguments.AddRange(["--", url.AbsoluteUri]);
        var result = await runner.RunAsync(new ProcessRequest(EngineSupport.Required(components, "yt-dlp"), arguments, Timeout: TimeSpan.FromMinutes(3), OutputLimit: 16_777_216, Interactive: true), cancellationToken: cancellationToken);
        EngineSupport.Success(result, "URL analysis");
        return EngineSupport.Json(result.StandardOutput, "URL analysis");
    }

    // Posts with several videos (X, Instagram) give every item the post's own
    // address, so such an item is addressed by its position in the post.
    private async Task<MediaInfo> AnalyseItemAsync(Uri url, string? browser, int item, CancellationToken cancellationToken)
    {
        using var json = await AnalyseJsonAsync(url, browser, ["--yes-playlist", "--playlist-items", item.ToString(EngineSupport.Invariant)], cancellationToken);
        var root = json.RootElement;
        if (!IsPlaylistResult(root))
            return item == 1 ? ParseInfo(root, url) : throw new MediaOperationException("This link now contains a single item. Analyse it again before downloading.");
        var entries = root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).ToArray() : [];
        if (entries.Length != 1) throw new MediaOperationException("The selected entry is no longer available. Analyse the link again and choose an entry.");
        if (IsPlaylistResult(entries[0])) throw new MediaOperationException(PlaylistChoiceMessage);
        return ParseInfo(entries[0], url);
    }
    private static bool IsPlaylistResult(JsonElement root) => root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("entries", out _) || EngineSupport.Text(root, "_type") is "playlist" or "multi_video");

    public async Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(options);
        var item = PlaylistItem(options.PlaylistItems);
        progress?.Report(new JobProgress(JobStage.Analysing, Message: "Checking the selected media"));
        MediaInfo info;
        if (item is { } position) info = await AnalyseItemAsync(options.Url, options.Browser, position, cancellationToken);
        else
        {
            info = await AnalyseAsync(options.Url, options.Browser, cancellationToken);
            using var analysed = EngineSupport.Json(info.MetadataJson ?? "{}", "URL analysis");
            if (IsPlaylistResult(analysed.RootElement))
            {
                // A list or post holding exactly one item needs no choice.
                if (!analysed.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 1) throw new MediaOperationException(PlaylistChoiceMessage);
                item = 1;
                info = await AnalyseItemAsync(options.Url, options.Browser, 1, cancellationToken);
            }
        }
        if (PreservingWebM(options) && info.Formats.Count > 0 && !info.Formats.Any(format => string.IsNullOrWhiteSpace(format.VideoCodec) || WebMVideoCodec(format.VideoCodec))) throw new MediaOperationException("This source has no available WebM-compatible video streams. Choose Automatic or MKV to preserve it, or choose AV1 to convert it.");
        if (PreservingWebM(options) && options.Mode == DownloadMode.VideoAudio && info.Formats.Count > 0 && !info.Formats.Any(format => string.IsNullOrWhiteSpace(format.AudioCodec) || format.AudioCodec is "opus" or "vorbis")) throw new MediaOperationException("This source has no available WebM-compatible audio streams. Choose Automatic or MKV to preserve it, or choose AV1 to convert it.");
        // Only an explicit "none" proves silence; an unreported codec may still carry sound.
        var silentSource = info.Formats.Count > 0 && info.Formats.All(format => string.Equals(format.AudioCodec, "none", StringComparison.OrdinalIgnoreCase));
        if (silentSource && options.Mode == DownloadMode.Audio) throw new MediaOperationException("This media has no sound track, so there is no audio to save. Choose Video only instead.");
        var outputDirectory = EngineSupport.Absolute(options.OutputDirectory);
        var name = FileName(info, options.NamingTemplate) + SectionSuffix(options);
        // The final name is known now; a conflict found only after the transfer wastes it.
        CheckExistingOutput(outputDirectory, name, options);
        var replaceTarget = ReplaceTarget(options, outputDirectory, name);
        using var workspace = EngineSupport.CreateWorkspace(outputDirectory, temporaryWorkspaces);
        var staging = workspace.Path;
        var retainRecovery = false;
        var notices = new List<string>();
        var tracker = new DownloadProgressTracker();
        try
        {
            workspace.RegisterDownloadBaseName(name);
            // yt-dlp's metadata step writes chapters to "<name>.meta" next to the media.
            // It is removed when the step succeeds; after a failure the job folder must
            // still be recognised as this job's own, or cleanup keeps every stream.
            workspace.RegisterPayload(name + ".meta");
            var arguments = BuildArguments(options, staging, name, item, info, notices);
            foreach (var notice in notices) progress?.Report(new JobProgress(JobStage.Analysing, Message: notice));
            var diagnostics = new Queue<string>();
            string? reportedOutput = null;
            var reportedFiles = 0;
            var unreadableReport = false;
            void Receive(string line, bool standardError)
            {
                if (!standardError && line.StartsWith("KC_FILE:", StringComparison.Ordinal))
                {
                    reportedFiles++;
                    try { reportedOutput = line.Length < 32768 ? JsonSerializer.Deserialize<string>(line[8..]) : null; }
                    catch (JsonException) { unreadableReport = true; }
                    return;
                }
                // In quiet mode (implied by --print) yt-dlp writes the post-processing
                // template to standard error and download progress to standard output.
                if (line.StartsWith("KC_", StringComparison.Ordinal))
                {
                    tracker.Read(line, progress);
                    return;
                }
                // Keep a bounded tail: a failure is described by the last error lines.
                if (standardError) lock (diagnostics) { diagnostics.Enqueue(line.Length > 4000 ? line[..4000] : line); if (diagnostics.Count > 200) diagnostics.Dequeue(); }
            }
            var result = await runner.RunAsync(new ProcessRequest(EngineSupport.Required(components, "yt-dlp"), arguments, staging, TimeSpan.FromHours(12), 1_048_576), line => Receive(line, false), line => Receive(line, true), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string diagnosticText;
            lock (diagnostics) diagnosticText = diagnostics.Count > 0 ? string.Join('\n', diagnostics) : result.StandardError;
            var diagnosticLines = EngineSupport.DiagnosticLines(diagnosticText);
            if (result.ExitCode != 0)
            {
                // yt-dlp prints KC_FILE only after the media passed every post-processing
                // step and was moved into place. A failed optional subtitle track still
                // sets a failure exit code, but must not discard that complete media.
                if (reportedFiles == 1 && reportedOutput is not null && File.Exists(reportedOutput) && OptionalTrackFailuresOnly(diagnosticLines))
                    notices.Add(SubtitleNotice);
                else EngineSupport.Success(result with { StandardError = diagnosticText }, "Download");
            }
            // With --ignore-errors a subtitle track the site refused is only a warning.
            if (!notices.Contains(SubtitleNotice) && diagnosticLines.Any(line => line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) && line.Contains("unable to download video subtitles", StringComparison.OrdinalIgnoreCase)))
                notices.Add(SubtitleNotice);
            if (reportedFiles > 1) throw new MediaOperationException("The downloader produced more than one item, so nothing was saved. Analyse the link again and choose a single entry.");
            var primary = ResolveOutput(staging, reportedOutput ?? (unreadableReport ? StagedOutput(staging, name) : null));
            progress?.Report(new JobProgress(JobStage.Verifying, Message: "Checking the downloaded file"));
            var probe = await mediaService.ProbeAsync(primary, cancellationToken);
            if (options.Mode == DownloadMode.VideoOnly && probe.HasVideo && probe.HasAudio)
                (primary, probe) = await RemoveAudioAsync(workspace, name, primary, probe, progress, cancellationToken);
            CheckMode(probe, options.Mode, silentSource, notices);
            if (options.Mode == DownloadMode.Archive)
                return await FinishArchiveAsync(workspace, outputDirectory, name, primary, info, probe, options.ArchiveZip, notices, progress, cancellationToken);
            var container = EffectiveContainer(options);
            var video = options.Mode is DownloadMode.VideoAudio or DownloadMode.VideoOnly;
            if (video && Conversion(options, probe) is { } conversion)
            {
                progress?.Report(new JobProgress(JobStage.Converting, Message: conversion.Reason));
                var extension = container switch { MediaContainer.Mkv => ".mkv", MediaContainer.WebM => ".webm", _ => ".mp4" };
                var converted = Path.Combine(staging, name + ".converted" + extension);
                workspace.RegisterPayload(Path.GetFileName(converted));
                // The conversion covers the whole download, so the chapters, cover and
                // attachments yt-dlp embedded stay where the container holds them. Only the
                // video was asked to change: audio the container accepts is copied, and
                // audio it cannot hold (AAC in WebM) is still converted.
                var exported = await mediaService.ExportAsync(new ExportOptions { InputPath = primary, OutputPath = converted, VideoCodec = options.VideoCodec, MaxHeight = options.MaxHeight, Fps = conversion.Fps, HdrToSdr = conversion.Hdr }, PreserveExtras.All, new ExportProgress(progress), cancellationToken);
                // Such as a cover that could not be carried over; the completion message says so.
                if (exported.Warning is { } exportNote) notices.Add(exportNote);
                NoteLostCover(probe, exported.Probe, notices);
                primary = exported.FilePath;
                probe = exported.Probe;
                // VideoOnly downloads contain no audio, so conversion cannot invent an audio track.
                CheckMode(probe, options.Mode, silentSource);
            }
            else if (video && container != MediaContainer.Auto)
            {
                if (container == MediaContainer.WebM && (probe.VideoCodec is not ("vp8" or "vp9" or "av1") || probe.HasAudio && probe.AudioCodec is not ("opus" or "vorbis"))) throw new MediaOperationException("These source streams cannot be preserved in WebM. Choose Automatic or MKV, or choose AV1 to convert them.");
                if (!MatchesContainer(primary, probe, container))
                {
                    progress?.Report(new JobProgress(JobStage.Merging, Message: "Putting the source streams in the selected container without re-encoding"));
                    var remuxed = Path.Combine(staging, name + ".container" + ContainerExtension(container));
                    workspace.RegisterPayload(Path.GetFileName(remuxed));
                    // A stream copy already keeps the audio; the extras would otherwise be dropped.
                    var exported = await mediaService.ExportAsync(new ExportOptions { InputPath = primary, OutputPath = remuxed, ClipMode = ClipMode.Lossless, VideoCodec = VideoCodec.Original, AudioFormat = AudioFormat.Original }, PreserveExtras.All, new ExportProgress(progress), cancellationToken);
                    if (exported.Warning is { } remuxNote) notices.Add(remuxNote);
                    NoteLostCover(probe, exported.Probe, notices);
                    primary = exported.FilePath;
                    probe = exported.Probe;
                    CheckMode(probe, options.Mode, silentSource);
                }
            }
            if (video && container != MediaContainer.Auto && !MatchesContainer(primary, probe, container)) throw new MediaOperationException("The completed file did not match the selected container. No output was saved. Choose another format or check the components.");
            var destination = Path.Combine(outputDirectory, name + Path.GetExtension(primary));
            if (replaceTarget is not null && !SamePath(destination, replaceTarget)) throw new MediaOperationException(ReplacedFormatMismatch(destination, replaceTarget));
            var sidecars = Directory.EnumerateFiles(staging).Where(path => SidecarExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())).ToArray();
            var outputs = new List<(string Staged, string Output)> { (primary, destination) };
            outputs.AddRange(sidecars.Select(path => (path, Path.Combine(outputDirectory, Path.GetFileName(path)))));
            cancellationToken.ThrowIfCancellationRequested();
            PromoteOwnedOutputs(outputs, options.ReplaceExisting, workspace, out retainRecovery);
            progress?.Report(new JobProgress(JobStage.Finished, 100, notices.Count == 0 ? "Download complete and checked" : "Download complete and checked. " + string.Join(" ", notices)));
            return new DownloadResult(destination, Lean(info), new FileInfo(destination).Length);
        }
        finally
        {
            if (retainRecovery) workspace.PreserveForRecovery();
            else
            {
                OwnStreamFiles(workspace, name, tracker.PlannedFormats);
                EngineSupport.Cleanup(workspace, outputDirectory);
            }
        }
    }

    // yt-dlp names each separately downloaded stream "<name>.f<format id>.<ext>".
    // The workspace recognises numeric IDs (YouTube) on its own, but sites such as
    // X or Vimeo use IDs like "http-720p". After a failed merge or a cancelled
    // transfer those streams would keep the whole job folder in the output folder.
    // Only this job's own name with a format ID from its plan is registered, and
    // only a few names: the workspace receipt holds a bounded list, and one
    // fragment at a time is on disk because fragments download one by one.
    private static void OwnStreamFiles(ITemporaryWorkspace workspace, string name, IReadOnlyList<string> formats)
    {
        if (formats.Count == 0) return;
        try
        {
            var streams = Directory.EnumerateFiles(workspace.Path).Take(256).Select(Path.GetFileName).OfType<string>()
                .Where(file => formats.Any(format => StreamFile(file, name + ".f" + format + "."))).Take(16).ToArray();
            foreach (var file in streams) workspace.RegisterPayload(file);
        }
        // Best effort: cleanup then keeps the folder, as it did before.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or MediaOperationException) { }
    }
    private static bool StreamFile(string file, string prefix)
    {
        if (!file.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = file[prefix.Length..];
        var dot = rest.IndexOf('.');
        var extension = dot < 0 ? rest : rest[..dot];
        return MediaExtensions.Contains("." + extension) && StreamTail().IsMatch(dot < 0 ? "" : rest[dot..]);
    }

    // An export inside a download reports its own completion, but the job is not
    // finished until the result is checked and saved; a Finished stage here would
    // briefly mark the queue row complete. It is shown as verification instead.
    private sealed class ExportProgress(IProgress<JobProgress>? outer) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => outer?.Report(value.Stage == JobStage.Finished ? new JobProgress(JobStage.Verifying, Message: "Checking the converted file") : value);
    }

    // Finished jobs stay in the queue. Nothing reads their full engine metadata or
    // format lists, which can be megabytes per item, so they are not retained.
    private static MediaInfo Lean(MediaInfo info) => info with { MetadataJson = null, Formats = [], Entries = [] };

    private const string SubtitleNotice = "Some optional subtitle or caption tracks could not be downloaded.";
    // Subtitles download before the media. A retried subtitle failure is reported as
    // "ERROR: [download] Got error: ..." and the media still completes; any other
    // error (post-processing, merging, conversion) keeps the job failed.
    private static bool OptionalTrackFailuresOnly(IReadOnlyList<string> diagnostics)
    {
        var errors = diagnostics.Where(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)).ToArray();
        return errors.Length > 0 && errors.All(line => !line.Contains("postprocessing", StringComparison.OrdinalIgnoreCase) && (line.StartsWith("ERROR: [download]", StringComparison.OrdinalIgnoreCase) || line.Contains("subtitle", StringComparison.OrdinalIgnoreCase)));
    }

    // Used only when the KC_FILE line could not be read: accept exactly one
    // finished media file carrying this job's name in its own staging folder.
    private static string? StagedOutput(string staging, string name)
    {
        var candidates = Directory.EnumerateFiles(staging).Where(path =>
        {
            var file = Path.GetFileName(path);
            return file.StartsWith(name + ".", StringComparison.Ordinal) && MediaExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && file.Count(character => character == '.') == name.Count(character => character == '.') + 1;
        }).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static void CheckExistingOutput(string directory, string name, DownloadOptions options)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in PotentialOutputPaths(directory, name, options))
            if (Directory.Exists(path) || !options.ReplaceExisting && File.Exists(path)) throw new MediaOperationException("An output file with this name already exists in the chosen folder. Choose another filename or explicitly allow file replacement. Nothing was downloaded.");
    }

    /// <summary>
    /// The final media paths a download with these options could write for <paramref name="name"/>.
    /// Only these can collide: an MP3 download never conflicts with a saved video of the same title.
    /// Archive mode always creates a new folder, so it has none.
    /// </summary>
    public static IReadOnlyList<string> PotentialOutputPaths(string directory, string name, DownloadOptions options)
        => [.. OutputExtensions(options).Select(extension => Path.Combine(directory, name + extension))];

    /// <summary>
    /// The extensions a download with these options can finish with: exactly one for a chosen audio
    /// format or container (or a codec, which makes MP4 under Automatic), several for Original audio
    /// or an Automatic container, and none for an archive, which always goes into a new folder.
    /// </summary>
    public static IReadOnlyList<string> OutputExtensions(DownloadOptions options)
    {
        if (options.Mode == DownloadMode.Archive) return [];
        return options.Mode == DownloadMode.Audio
            ? options.AudioFormat switch { AudioFormat.Mp3 => [".mp3"], AudioFormat.Aac => [".m4a"], AudioFormat.Opus => [".opus"], AudioFormat.Flac => [".flac"], AudioFormat.Wav => [".wav"], AudioFormat.Ogg => [".ogg"], _ => [".m4a", ".mp3", ".opus", ".ogg", ".flac", ".wav", ".aac"] }
            : EffectiveContainer(options) switch { MediaContainer.Mp4 => [".mp4"], MediaContainer.Mkv => [".mkv"], MediaContainer.WebM => [".webm"], _ => [".mp4", ".mkv", ".webm"] };
    }

    // FileName reads the media only to fill template fields, and a literal name has none.
    private static readonly MediaInfo LiteralNameInfo = new("", "", new Uri("https://localhost/"), null, null, null, null, null, null, [], [], null);
    /// <summary>
    /// The output name a download gets when Replace passes a saved file's own name as the template:
    /// percent signs become underscores and the engine's character and length rules apply. Null
    /// when the engine would refuse that name.
    /// </summary>
    public static string? LiteralOutputName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return FileName(LiteralNameInfo, name.Replace('%', '_')); }
        catch (MediaOperationException) { return null; }
    }

    // Replace may overwrite only the saved file it names. A request that cannot write that exact
    // path is refused before the transfer, and a finished file with another extension is refused
    // before anything is saved, so Replace never leaves a second file behind (or overwrites a
    // different one) while the user believes the saved file was replaced.
    private static string? ReplaceTarget(DownloadOptions options, string outputDirectory, string name)
    {
        if (options.ReplacePath is null) return null;
        var target = EngineSupport.Absolute(options.ReplacePath);
        if (!PotentialOutputPaths(outputDirectory, name, options).Any(path => SamePath(path, target)))
            throw new MediaOperationException("Replace was stopped because this download would not save to the same file name and format. Nothing was downloaded and the saved file is unchanged. Choose Download again to keep both.");
        return target;
    }
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static string ReplacedFormatMismatch(string produced, string target) =>
        $"This download produced {Path.GetExtension(produced).ToLowerInvariant()} instead of {Path.GetExtension(target).ToLowerInvariant()}, so it could not replace the saved file. Nothing was saved and the saved file is unchanged. Choose Download again to keep both.";

    // Some sites offer only combined streams. Video only then removes the audio
    // track with a stream copy; the picture, cover and chapters stay untouched.
    private async Task<(string Path, ProbeInfo Probe)> RemoveAudioAsync(ITemporaryWorkspace workspace, string name, string primary, ProbeInfo probe, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new JobProgress(JobStage.Converting, Message: "Removing the audio track without re-encoding the video"));
        var output = Path.Combine(workspace.Path, name + ".video" + Path.GetExtension(primary));
        workspace.RegisterPayload(Path.GetFileName(output));
        var arguments = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-i", primary, "-map", "0", "-map", "-0:a", "-dn", "-c", "copy", "-map_metadata", "0", "-map_chapters", "0" };
        if (Path.GetExtension(primary).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov") arguments.AddRange(["-movflags", "+faststart"]);
        arguments.AddRange(["-n", output]);
        var result = await runner.RunAsync(new ProcessRequest(EngineSupport.Required(components, "ffmpeg"), arguments, workspace.Path, TimeSpan.FromMinutes(Math.Min(720, 10 + probe.Duration.TotalMinutes)), 262_144), cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EngineSupport.Success(result, "Audio removal");
        return (output, await mediaService.ProbeAsync(output, cancellationToken));
    }

    private sealed record ConversionPlan(string Reason, bool Hdr, int? Fps);
    // Decided from the downloaded streams, not only from the options: a source
    // that already has the chosen codec, is already SDR or already has an
    // acceptable frame rate is kept as it is instead of being re-encoded.
    private static ConversionPlan? Conversion(DownloadOptions options, ProbeInfo probe)
    {
        var target = options.VideoCodec switch { VideoCodec.H264 => "h264", VideoCodec.Hevc => "hevc", VideoCodec.Av1 => "av1", _ => null };
        // WebM holds only Opus or Vorbis audio. When the video already matches the
        // chosen AV1 but the audio does not fit, the conversion still has to run;
        // the copy remux would otherwise refuse the streams.
        var audioMisfit = target is not null && EffectiveContainer(options) == MediaContainer.WebM && probe.HasAudio && probe.AudioCodec is not ("opus" or "vorbis");
        var codec = target is not null && (!string.Equals(probe.VideoCodec, target, StringComparison.OrdinalIgnoreCase) || audioMisfit);
        var hdr = options.HdrToSdr && IsHdr(probe);
        // 29.97 fps meets a 30 fps choice; 59.94 or 60 fps does not.
        var fps = options.Fps is { } limit && probe.Fps is { } actual && actual > limit + Math.Max(0.5, limit * 0.02) ? options.Fps : null;
        if (!codec && !hdr && fps is null) return null;
        return new(hdr ? "Converting HDR video to SDR" : codec ? "Creating the selected video format" : "Converting to the selected frame rate", hdr, fps);
    }
    internal static bool IsHdr(ProbeInfo probe)
    {
        if (probe.TechnicalJson is null) return false;
        using var json = EngineSupport.Json(probe.TechnicalJson, "Media inspection");
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return false;
        foreach (var stream in streams.EnumerateArray())
        {
            if (EngineSupport.Text(stream, "codec_type") != "video" || stream.TryGetProperty("disposition", out var disposition) && EngineSupport.Integer(disposition, "attached_pic") == 1) continue;
            // PQ (HDR10, HDR10+, most Dolby Vision) or HLG. Untagged video is SDR.
            return EngineSupport.Text(stream, "color_transfer") is "smpte2084" or "arib-std-b67";
        }
        return false;
    }

    private static void PromoteOutputs(IReadOnlyList<(string Staged, string Output)> outputs, bool replace, out bool retainRecovery)
        => PromoteOwnedOutputs(outputs, replace, null, out retainRecovery);

    private static void PromoteOwnedOutputs(IReadOnlyList<(string Staged, string Output)> outputs, bool replace, ITemporaryWorkspace? workspace, out bool retainRecovery)
    {
        retainRecovery = false;
        // Validate the entire set before touching any destination. A directory
        // cannot be replaced by a media file, even when file replacement is chosen.
        foreach (var item in outputs)
        {
            if (Directory.Exists(item.Output) || !replace && File.Exists(item.Output)) throw new MediaOperationException("An output or subtitle file already exists. Choose another filename or explicitly allow file replacement.");
            if (!File.Exists(item.Staged) || new FileInfo(item.Staged).Length == 0) throw new MediaOperationException("The engine did not create a complete output or subtitle file.");
        }
        var backups = new List<(string Backup, string Output)>();
        var plannedBackups = new List<(string Backup, string Output)>();
        var promoted = new List<(string Staged, string Output)>();
        // Persist every exact backup name before moving any original. A receipt
        // write/limit failure must leave all existing destination files intact.
        foreach (var item in outputs)
        {
            if (!replace || !File.Exists(item.Output)) continue;
            var backup = Path.Combine(Path.GetDirectoryName(item.Staged)!, ".restore-" + Guid.NewGuid().ToString("N"));
            workspace?.RegisterPayload(Path.GetFileName(backup));
            plannedBackups.Add((backup, item.Output));
        }
        try
        {
            foreach (var item in plannedBackups)
            {
                File.Move(item.Output, item.Backup);
                backups.Add(item);
            }
            foreach (var item in outputs)
            {
                // Never overwrite a new file created after the preflight check.
                File.Move(item.Staged, item.Output);
                promoted.Add(item);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            foreach (var item in promoted.AsEnumerable().Reverse())
                try { File.Move(item.Output, item.Staged); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { retainRecovery = true; }
            foreach (var item in backups.AsEnumerable().Reverse())
                try { File.Move(item.Backup, item.Output); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { retainRecovery = true; }
            if (retainRecovery)
                throw new MediaOperationException("The output could not be saved completely. Recovery files were preserved in " + Path.GetFileName(Path.GetDirectoryName(outputs[0].Staged)) + " inside the chosen output folder. Review these files before retrying.");
            throw new MediaOperationException("The output could not be saved. Existing files were restored. Choose another filename or folder and retry.");
        }
    }

    public async Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(new ProcessRequest(EngineSupport.Required(components, "yt-dlp"), ["--ignore-config", "--list-extractors"], Timeout: TimeSpan.FromSeconds(30), OutputLimit: 1_048_576, Interactive: true), cancellationToken: cancellationToken);
        EngineSupport.Success(result, "Supported-site lookup");
        return result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => line.Length < 200).Distinct().Order(StringComparer.OrdinalIgnoreCase).Take(5000).ToArray();
    }

    private List<string> Common(string? browser)
    {
        var arguments = new List<string> { "--ignore-config", "--no-cache-dir", "--no-colors", "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3" };
        if (!string.IsNullOrWhiteSpace(browser))
        {
            if (!Browsers.Contains(browser)) throw new MediaOperationException("Choose a supported browser from the authentication list.");
            arguments.AddRange(["--cookies-from-browser", browser.ToLowerInvariant()]);
        }
        if (components.Find("deno") is { } deno) arguments.AddRange(["--js-runtimes", "deno:" + deno]);
        if (components.Find("ffmpeg") is { } ffmpeg) arguments.AddRange(["--ffmpeg-location", Path.GetDirectoryName(ffmpeg)!]);
        return arguments;
    }
    private List<string> BuildArguments(DownloadOptions options, string staging, string name, int? playlistItem, MediaInfo info, List<string> notices)
    {
        EngineSupport.Required(components, "ffmpeg");
        EngineSupport.Required(components, "ffprobe");
        if (options.Url.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || options.Url.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) || options.Url.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) EngineSupport.Required(components, "deno");
        var arguments = Common(options.Browser);
        // An item of a post is fetched through the post itself. Without
        // --no-write-playlist-metafiles an archive would hold the post's info.json
        // under the item's name and skip the item's own metadata as "already present".
        arguments.AddRange(playlistItem is { } position ? ["--yes-playlist", "--playlist-items", position.ToString(EngineSupport.Invariant), "--no-write-playlist-metafiles"] : ["--no-playlist"]);
        // --print implies quiet mode in yt-dlp. Force the structured progress
        // templates on so callbacks describe real work and remain cancellable.
        // --ignore-errors turns a subtitle track the site refuses (HTTP 404/429) into a
        // warning; without it that track aborts the whole item before the media
        // downloads. It also lets yt-dlp continue after a failed merge or conversion,
        // into later steps that then fail on a missing file, so it is passed only
        // when subtitle or caption tracks are requested. Media, merge and
        // post-processing failures still print ERROR and exit 1, and the job fails
        // unless every error is an optional subtitle track.
        if (options.Mode == DownloadMode.Archive || !string.IsNullOrWhiteSpace(options.SubtitleLanguages) || options.AutomaticCaptions) arguments.Add("--ignore-errors");
        arguments.AddRange(["--newline", "--progress", "--progress-delta", "0.2", "--no-overwrites", "--windows-filenames", "--restrict-filenames", "--concurrent-fragments", "1", "--paths", staging, "--output", name + ".%(ext)s", "--progress-template", ProgressTemplate, "--progress-template", "postprocess:KC_POST:%(progress)j", "--print", PlanTemplate, "--print", "after_move:KC_FILE:%(filepath)j", "--no-simulate", "--postprocessor-args", "ffmpeg:-threads 2"]);
        var video = options.Mode is DownloadMode.VideoAudio or DownloadMode.VideoOnly;
        var container = EffectiveContainer(options);
        // '?' keeps formats whose height or language the site did not report;
        // without it such sources fail with "requested format is not available".
        // Frame rate is a sort preference, not a filter: a cap would silently turn
        // a 1080p source offered only at 60 fps into 480p. Faster video is
        // converted after download instead.
        var height = options.MaxHeight is { } max ? $"[height<=?{max}]" : "";
        var language = !string.IsNullOrWhiteSpace(options.AudioLanguage) ? $"[language^=?{options.AudioLanguage}]" : "";
        var selector = options.Mode switch
        {
            DownloadMode.Audio => $"ba{language}/b{language}",
            // Sites such as TikTok or Twitch offer only combined streams; their
            // audio track is removed after download.
            DownloadMode.VideoOnly => $"bv{height}/bv*{height}",
            _ => $"bv*{height}+ba{language}/b{height}{language}"
        };
        if (PreservingWebM(options))
        {
            // '?' keeps formats with unknown extractor metadata available;
            // ffprobe validates their real codecs before any copy remux.
            const string videoCodec = "[vcodec~=?'^(vp8|vp9|vp09|av1|av01)']";
            const string audioCodec = "[acodec~=?'^(opus|vorbis)$']";
            selector = options.Mode == DownloadMode.VideoOnly ? $"bv{height}{videoCodec}/bv*{height}{videoCodec}" : $"bv{height}{videoCodec}+ba{language}{audioCodec}/b{height}{language}{videoCodec}{audioCodec}";
        }
        arguments.AddRange(["--format", selector]);
        // Only part of it: yt-dlp fetches just that range and FFmpeg cuts on exact frames (keyframes are forced at the cuts).
        if (options.HasSection) arguments.AddRange(["--download-sections", SectionArgument(options.SectionStart!.Value, options.SectionEnd!.Value), "--force-keyframes-at-cuts"]);
        var preferences = new List<string>();
        if (video)
        {
            preferences.Add(options.Fps is { } fps ? "fps:" + fps.ToString(EngineSupport.Invariant) : "fps");
            // Prefer a native SDR rendition; HDR is converted only when no SDR exists.
            if (options.HdrToSdr) preferences.Add("hdr:SDR");
            // Prefer a native stream in the chosen codec so no re-encode is needed.
            // A frame-rate change re-encodes anyway, and H.264 is fast to convert.
            var codec = options.VideoCodec switch { VideoCodec.H264 => "vcodec:h264", VideoCodec.Hevc => "vcodec:h265", VideoCodec.Av1 => "vcodec:av01", _ => options.Fps is not null && container != MediaContainer.WebM ? "vcodec:h264" : null };
            if (codec is not null) preferences.Add(codec);
            if (options.VideoCodec != VideoCodec.Original && container == MediaContainer.Mp4) preferences.Add("acodec:aac");
        }
        if (options.SmallestFile) arguments.AddRange(["--format-sort", string.Join(',', new[] { "+size", "+br", "+res" }.Concat(preferences.Where(preference => preference != "fps")))]);
        else if (preferences.Count > 1 || preferences is [not "fps"]) arguments.AddRange(["--format-sort", string.Join(',', new[] { "lang", "quality", "res" }.Concat(preferences))]);
        if (options.Mode == DownloadMode.Audio)
        {
            var audioFormat = options.AudioFormat switch { AudioFormat.Original => "best", AudioFormat.Mp3 => "mp3", AudioFormat.Aac => "m4a", AudioFormat.Opus => "opus", AudioFormat.Flac => "flac", AudioFormat.Wav => "wav", AudioFormat.Ogg => "vorbis", _ => throw new MediaOperationException("That audio format is not supported.") };
            arguments.AddRange(["--extract-audio", "--audio-format", audioFormat]);
            // libvorbis refuses fixed 256 and 320 kbps targets for mono sources.
            // Quality-based VBR suits any channel count.
            if (options.AudioFormat == AudioFormat.Ogg) arguments.AddRange(["--audio-quality", VorbisQuality(options.AudioKbps)]);
            // libopus accepts at most 256 kbps per channel, so 320 kbps fails for a mono
            // source after the whole transfer. 256 kbps is already beyond transparent.
            else if (options.AudioFormat == AudioFormat.Opus) arguments.AddRange(["--audio-quality", Math.Min(options.AudioKbps, 256).ToString(EngineSupport.Invariant) + "K"]);
            else if (options.AudioFormat != AudioFormat.Original) arguments.AddRange(["--audio-quality", options.AudioKbps.ToString(EngineSupport.Invariant) + "K"]);
        }
        else if (video)
        {
            // A codec change merges into the target container when the streams fit,
            // otherwise into MKV, which accepts any stream, before conversion.
            var merge = options.VideoCodec != VideoCodec.Original ? container switch { MediaContainer.Mkv => "mkv", MediaContainer.WebM => "webm/mkv", _ => "mp4/mkv" } : container == MediaContainer.Auto ? null : container.ToString().ToLowerInvariant();
            if (merge is not null) arguments.AddRange(["--merge-output-format", merge]);
            // yt-dlp remuxes a single-file download before it embeds metadata and the
            // cover, so the chosen container keeps chapters and cover art. WebM cannot
            // hold a cover, so for Automatic a WebM file becomes MKV when one is wanted.
            if (options.VideoCodec == VideoCodec.Original && container is MediaContainer.Mp4 or MediaContainer.Mkv) arguments.AddRange(["--remux-video", container.ToString().ToLowerInvariant()]);
            else if (options.EmbedThumbnail && container != MediaContainer.WebM) arguments.AddRange(["--remux-video", "webm>mkv"]);
        }
        if (options.EmbedMetadata && options.Mode != DownloadMode.Archive) arguments.Add("--embed-metadata");
        if (options.EmbedThumbnail && options.Mode != DownloadMode.Archive)
        {
            // Embedding into a format without cover support fails only after the
            // whole transfer, so the cover is skipped there with a notice instead.
            if (options.Mode == DownloadMode.Audio && options.AudioFormat == AudioFormat.Wav) notices.Add("WAV files cannot hold a cover image, so the cover was not embedded.");
            else if (video && container == MediaContainer.WebM) notices.Add("WebM files cannot hold a cover image, so the cover was not embedded.");
            else
            {
                arguments.Add("--embed-thumbnail");
                // yt-dlp turns a WebP thumbnail into PNG for MP4, but attaches it to MKV
                // as WebP. FFmpeg reads that as a plain attachment, not as the cover, so a
                // later conversion to MP4 lost it. Matroska covers should be JPEG or PNG
                // anyway, so WebP becomes PNG for every video; JPEG and PNG stay as they are.
                if (video) arguments.AddRange(["--convert-thumbnails", "webp>png"]);
            }
        }
        if (options.Mode == DownloadMode.Archive)
        {
            arguments.AddRange(["--write-info-json", "--write-description", "--write-thumbnail", "--write-subs", "--sub-langs", ArchiveSubtitleLanguages(options, info)]);
            if (options.AutomaticCaptions) arguments.Add("--write-auto-subs");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(options.SubtitleLanguages)) arguments.AddRange(["--write-subs", "--sub-langs", options.SubtitleLanguages]);
            if (options.AutomaticCaptions) arguments.Add("--write-auto-subs");
        }
        arguments.AddRange(["--", options.Url.AbsoluteUri]);
        return arguments;
    }
    // A cover the downloaded file had but the converted or remuxed file lacks is named,
    // whatever the reason: a picture format the export does not carry, such as a WebP
    // attachment, or a container that cannot hold a cover. MediaService's own note for a
    // cover it could not extract uses the same words, so the message names it once.
    private const string CoverLostNote = "The cover picture could not be carried over.";
    private static void NoteLostCover(ProbeInfo source, ProbeInfo result, List<string> notices)
    {
        if (HasCover(source) && !HasCover(result) && !notices.Any(notice => notice.Contains(CoverLostNote, StringComparison.Ordinal))) notices.Add(CoverLostNote);
    }
    // FFprobe shows an MP4 cover, and a Matroska JPEG or PNG attachment, as a picture
    // stream marked attached_pic; a Matroska WebP picture stays an image attachment.
    private static bool HasCover(ProbeInfo probe)
    {
        if (probe.TechnicalJson is null) return false;
        using var json = EngineSupport.Json(probe.TechnicalJson, "Media inspection");
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return false;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object) continue;
            var type = EngineSupport.Text(stream, "codec_type");
            if (type == "video" && stream.TryGetProperty("disposition", out var disposition) && EngineSupport.Integer(disposition, "attached_pic") == 1) return true;
            if (type == "attachment" && stream.TryGetProperty("tags", out var tags) && EngineSupport.Text(tags, "mimetype") is { } mimetype && mimetype.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    // A chosen codec with Automatic produces MP4, as conversions always have.
    private static MediaContainer EffectiveContainer(DownloadOptions options) => options.Mode is DownloadMode.VideoAudio or DownloadMode.VideoOnly && options.Container == MediaContainer.Auto && options.VideoCodec != VideoCodec.Original ? MediaContainer.Mp4 : options.Container;
    private static bool PreservingWebM(DownloadOptions options) => options.Mode is DownloadMode.VideoAudio or DownloadMode.VideoOnly && options.Container == MediaContainer.WebM && options.VideoCodec == VideoCodec.Original;
    // yt-dlp's 0 (best) to 10 scale maps onto libvorbis -q:a 10 to 0. The table
    // holds libvorbis's nominal stereo bitrate for each quality step.
    private static string VorbisQuality(int kbps)
    {
        int[] nominal = [64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 500];
        var quality = 0;
        for (var step = 0; step < nominal.Length; step++) if (kbps >= nominal[step]) quality = step;
        return (10 - quality).ToString(EngineSupport.Invariant);
    }
    // Archive keeps every manual subtitle track. Automatic captions are limited to
    // the original-language track: YouTube lists over 100 machine translations,
    // and "all" would request every one of them. Live chat replays are excluded.
    private static string ArchiveSubtitleLanguages(DownloadOptions options, MediaInfo info)
    {
        var requested = (options.SubtitleLanguages ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!options.AutomaticCaptions) return string.Join(',', (requested.Count > 0 ? requested : ["all"]).Append("-live_chat"));
        using var metadata = EngineSupport.Json(info.MetadataJson ?? "{}", "URL analysis");
        var root = metadata.RootElement;
        var languages = requested.Count > 0 ? requested : TrackKeys(root, "subtitles").ToList();
        var automatic = TrackKeys(root, "automatic_captions").ToArray();
        var originals = automatic.Where(key => key.EndsWith("-orig", StringComparison.Ordinal)).ToList();
        if (originals.Count == 0 && EngineSupport.Text(root, "language") is { } spoken && automatic.Contains(spoken, StringComparer.Ordinal)) originals.Add(spoken);
        if (originals.Count == 0) originals.Add(".*-orig");
        return string.Join(',', languages.Concat(originals).Append("-live_chat").Distinct(StringComparer.Ordinal));
    }
    private static IEnumerable<string> TrackKeys(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var tracks) && tracks.ValueKind == JsonValueKind.Object
        ? tracks.EnumerateObject().Select(track => track.Name).Where(key => key != "live_chat" && TrackKey().IsMatch(key)).Take(200).ToArray() : [];
    private static bool WebMVideoCodec(string? codec) => codec is not null && new[] { "vp8", "vp9", "vp09", "av1", "av01" }.Any(prefix => codec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    private static string ContainerExtension(MediaContainer container) => container switch { MediaContainer.Mp4 => ".mp4", MediaContainer.Mkv => ".mkv", MediaContainer.WebM => ".webm", _ => throw new MediaOperationException("Choose an explicit media container.") };
    private static bool MatchesContainer(string path, ProbeInfo probe, MediaContainer container)
    {
        if (!Path.GetExtension(path).Equals(ContainerExtension(container), StringComparison.OrdinalIgnoreCase) || probe.TechnicalJson is null) return false;
        using var metadata = EngineSupport.Json(probe.TechnicalJson, "Container inspection");
        var format = metadata.RootElement.TryGetProperty("format", out var value) ? EngineSupport.Text(value, "format_name") : null;
        var names = format?.Split(',') ?? [];
        if (container == MediaContainer.Mp4 && !names.Contains("mp4") || container != MediaContainer.Mp4 && !names.Contains("matroska") && !names.Contains("webm")) return false;
        // ffprobe groups MOV/MP4 and Matroska/WebM under shared demuxers.
        // Inspect only the bounded file header to distinguish the actual format.
        try
        {
            using var file = File.OpenRead(path);
            var header = new byte[(int)Math.Min(4096, file.Length)];
            file.ReadExactly(header);
            if (container == MediaContainer.Mp4)
            {
                if (header.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(header) < 16 || !header.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return false;
                var brand = Encoding.ASCII.GetString(header, 8, 4);
                return brand != "qt  " && !brand.StartsWith("3g", StringComparison.Ordinal);
            }
            if (header.Length < 5 || !header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return false;
            var index = 4;
            if (!ReadEbmlNumber(header, ref index, false, out var headerSize) || headerSize > (ulong)(header.Length - index)) return false;
            var end = index + (int)headerSize;
            while (index < end)
            {
                if (!ReadEbmlNumber(header.AsSpan(0, end), ref index, true, out var id) || !ReadEbmlNumber(header.AsSpan(0, end), ref index, false, out var size) || size > (ulong)(end - index)) return false;
                if (id == 0x4282) return header.AsSpan(index, (int)size).SequenceEqual(container == MediaContainer.WebM ? "webm"u8 : "matroska"u8);
                index += (int)size;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new MediaOperationException("The completed file's container could not be checked. No output was saved."); }
    }
    private static bool ReadEbmlNumber(ReadOnlySpan<byte> bytes, ref int index, bool keepMarker, out ulong value)
    {
        value = 0;
        if (index >= bytes.Length || bytes[index] == 0) return false;
        var marker = 0x80;
        var count = 1;
        while ((bytes[index] & marker) == 0) { marker >>= 1; count++; }
        if (count > 8 || count > bytes.Length - index) return false;
        value = keepMarker ? bytes[index] : (ulong)(bytes[index] & (marker - 1));
        index++;
        for (var offset = 1; offset < count; offset++) value = value << 8 | bytes[index++];
        return true;
    }
    private static void ValidateUrl(Uri url)
    {
        if (url is null || !MediaValidation.TryMediaUrl(url.ToString(), out _)) throw new MediaOperationException("Paste a valid http or https media link.");
    }
    private static void Validate(DownloadOptions options)
    {
        ValidateUrl(options.Url);
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Container) || !Enum.IsDefined(options.AudioFormat) || !Enum.IsDefined(options.VideoCodec)) throw new MediaOperationException("Choose a supported download option.");
        if (options.Mode is DownloadMode.VideoAudio or DownloadMode.VideoOnly && options.Container == MediaContainer.WebM && options.VideoCodec is VideoCodec.H264 or VideoCodec.Hevc) throw new MediaOperationException("WebM cannot contain H.264 or HEVC video. Choose AV1, or choose MP4, MKV or Automatic for that codec.");
        if (options.MaxHeight is < 64 or > 16384 || options.Fps is < 1 or > 240 || options.AudioKbps is < 8 or > 1024) throw new MediaOperationException("Choose a valid quality, frame rate and audio bitrate.");
        if (options.AudioLanguage is { Length: > 100 } || options.AudioLanguage is { } language && !Language().IsMatch(language) || options.SubtitleLanguages is { Length: > 500 } || options.SubtitleLanguages is { } subtitles && !Languages().IsMatch(subtitles)) throw new MediaOperationException("Use valid language codes for audio and subtitles.");
        if (options.Mode == DownloadMode.Archive && (options.VideoCodec != VideoCodec.Original || options.HdrToSdr || options.MaxHeight is not null || options.Fps is not null || options.SmallestFile || options.Container != MediaContainer.Auto)) throw new MediaOperationException("Archive mode preserves the highest available source streams. Clear resolution, frame-rate, smallest-file, container and conversion choices; create a compatible copy afterwards.");
        if (options.ArchiveZip && options.Mode != DownloadMode.Archive) throw new MediaOperationException("ZIP packaging is available in Archive mode.");
        if ((options.SectionStart is null) != (options.SectionEnd is null)) throw new MediaOperationException("Choose both a start and an end for the part to save.");
        if (options.HasSection)
        {
            if (options.Mode == DownloadMode.Archive) throw new MediaOperationException("Archive mode keeps the whole original. Choose Video + audio, Audio only or Video only to save just part of it.");
            if (!string.IsNullOrWhiteSpace(options.PlaylistItems)) throw new MediaOperationException("Saving just part works on a single video, not a playlist entry.");
            if (options.SectionStart < TimeSpan.Zero || options.SectionEnd - options.SectionStart < TimeSpan.FromSeconds(0.5) || options.SectionEnd > TimeSpan.FromDays(2)) throw new MediaOperationException("The part to save must start before it ends and be at least half a second long.");
        }
        if (!string.IsNullOrWhiteSpace(options.PlaylistItems) && PlaylistItem(options.PlaylistItems) is null) throw new MediaOperationException("Use the playlist picker to queue individual entries.");
    }
    // One entry number, as queued by the playlist picker for an item that has
    // no address of its own. Ranges and lists are not accepted.
    private static int? PlaylistItem(string? value) => value is { } text && PlaylistPosition().IsMatch(text.Trim()) ? int.Parse(text.Trim(), EngineSupport.Invariant) : null;
    private static void CheckMode(ProbeInfo probe, DownloadMode mode, bool silentSource = false, List<string>? notices = null)
    {
        if (mode == DownloadMode.VideoAudio && probe.HasVideo && !probe.HasAudio)
        {
            // The analysis proved the source has no sound at all (for example an
            // animated GIF post), so the video itself is the complete result.
            if (!silentSource) throw new MediaOperationException("The downloaded video has no sound track. If this media is silent, choose Video only and download it again.");
            notices?.Add("This media has no sound, so the video was saved without an audio track.");
            return;
        }
        if (mode == DownloadMode.Audio && (!probe.HasAudio || probe.HasVideo) || mode == DownloadMode.VideoOnly && (!probe.HasVideo || probe.HasAudio) || mode == DownloadMode.VideoAudio && (!probe.HasVideo || !probe.HasAudio)) throw new MediaOperationException("The downloaded file did not contain the requested audio/video tracks.");
    }
    private static string FileName(MediaInfo info, string? template)
    {
        var suppliedTemplate = !string.IsNullOrWhiteSpace(template);
        if (suppliedTemplate)
        {
            var name = template!;
            if (name.Length > 500 || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl)) throw new MediaOperationException("Use a filename template without folder paths.");
            var literal = name.Replace("%(title)s", "").Replace("%(id)s", "").Replace("%(uploader)s", "").Replace("%(upload_date)s", "").Replace("%(ext)s", "");
            if (literal.Contains('%')) throw new MediaOperationException("Filename templates support title, id, uploader, upload_date and ext only.");
            name = name.Replace("%(title)s", info.Title, StringComparison.Ordinal).Replace("%(id)s", info.Id, StringComparison.Ordinal).Replace("%(uploader)s", info.Creator ?? "Unknown", StringComparison.Ordinal).Replace("%(upload_date)s", info.PublishedAt?.ToString("yyyyMMdd", EngineSupport.Invariant) ?? "Unknown", StringComparison.Ordinal).Replace("%(ext)s", "", StringComparison.Ordinal).TrimEnd('.');
            // Percent signs in a title must never be interpreted as yt-dlp template syntax.
            // Replace passes an existing file's name as the template; a name of up to
            // 140 characters must come back unchanged, or Replace would write a copy.
            return FitName(WholeCharacters(name).Replace('%', '_'));
        }
        // A long title is shortened before the " [id]" suffix, which keeps names
        // unique. Cuts fall between whole characters: half of an emoji would make
        // yt-dlp report the finished file as unreadable JSON.
        var suffix = " [" + Fit(WholeCharacters(info.Id).Replace('%', '_'), 64) + "]";
        var title = WholeCharacters(info.Title).Replace('%', '_');
        if (title.Length + suffix.Length > NameLimit) title = Fit(MediaValidation.FriendlyFileName(title, int.MaxValue), NameLimit - suffix.Length).TrimEnd();
        return FitName((title + suffix).TrimEnd('.'));
    }
    // Cleans the name first and then cuts it between whole characters, so a
    // reserved-name prefix ("_CON.") is counted before the cut and the final
    // FriendlyFileName never shortens the result again by UTF-16 units.
    private static string FitName(string name) => MediaValidation.FriendlyFileName(Fit(MediaValidation.FriendlyFileName(name, int.MaxValue), NameLimit), NameLimit);
    /// <summary>The output name, without extension, that a download of this media uses.</summary>
    public static string OutputBaseName(MediaInfo info, string? namingTemplate = null, DownloadOptions? options = null) => FileName(info, namingTemplate) + SectionSuffix(options);
    /// <summary>" (0-45 to 1-20)" for a part of the media, so it never takes the name of the whole download.</summary>
    public static string SectionSuffix(DownloadOptions? options) => options is { HasSection: true } ? $" ({Clock(options.SectionStart!.Value)} to {Clock(options.SectionEnd!.Value)})" : "";
    private static string Clock(TimeSpan time) => time.TotalHours >= 1
        ? string.Create(EngineSupport.Invariant, $"{(int)time.TotalHours}-{time.Minutes:00}-{time.Seconds:00}")
        : string.Create(EngineSupport.Invariant, $"{time.Minutes}-{time.Seconds:00}");
    /// <summary>yt-dlp section syntax with millisecond precision, for example "*45.000-80.500".</summary>
    internal static string SectionArgument(TimeSpan start, TimeSpan end) => string.Create(EngineSupport.Invariant, $"*{start.TotalSeconds:0.000}-{end.TotalSeconds:0.000}");
    // Replaces unpaired UTF-16 surrogates, which no file system name or JSON reader accepts.
    private static string WholeCharacters(string text)
    {
        var characters = text.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (char.IsHighSurrogate(characters[index]) && index + 1 < characters.Length && char.IsLowSurrogate(characters[index + 1])) { index++; continue; }
            if (char.IsSurrogate(characters[index])) characters[index] = '_';
        }
        return new string(characters);
    }
    private static string Fit(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var end = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var next = elements.ElementIndex + elements.GetTextElement().Length;
            if (next > maxLength) break;
            end = next;
        }
        return text[..end];
    }
    private static string ResolveOutput(string staging, string? reported)
    {
        if (reported is null) throw new MediaOperationException("The downloader did not report a completed output file.");
        var full = EngineSupport.Absolute(reported);
        if (!string.Equals(Path.GetDirectoryName(full), EngineSupport.Absolute(staging), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new MediaOperationException("The downloader reported an output outside its job folder.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new MediaOperationException("The downloaded output was an unexpected file link.");
        return full;
    }
    private async Task<DownloadResult> FinishArchiveAsync(ITemporaryWorkspace workspace, string directory, string name, string primary, MediaInfo info, ProbeInfo probe, bool packageZip, IReadOnlyList<string> notices, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var staging = workspace.Path;
        progress?.Report(new JobProgress(JobStage.WritingMetadata, Message: "Writing source information and checksums"));
        var reserved = new[] { "source.json", "technical.json", "archive.json", "archive-info.txt", "SHA256SUMS.txt", "archive.zip", "archive.zip.sha256" };
        foreach (var file in reserved) workspace.RegisterPayload(file);
        if (reserved.Any(file => File.Exists(Path.Combine(staging, file)) || Directory.Exists(Path.Combine(staging, file)))) throw new MediaOperationException("A downloaded file conflicts with an archive metadata filename. Choose another filename and retry.");
        foreach (var path in Directory.EnumerateFiles(staging))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new MediaOperationException("A downloaded archive file was an unexpected file link.");
        foreach (var jsonPath in Directory.EnumerateFiles(staging, "*.info.json"))
            await File.WriteAllTextAsync(jsonPath, EngineSupport.SafeMetadata(await File.ReadAllTextAsync(jsonPath, cancellationToken)), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "source.json"), info.MetadataJson is { } metadata ? EngineSupport.SafeMetadata(metadata) : "{}", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "technical.json"), probe.TechnicalJson is { } technical ? EngineSupport.SafeMetadata(technical) : "{}", cancellationToken);
        var versions = (await components.InspectAsync(cancellationToken)).ToDictionary(component => component.Name, component => component.Version);
        var archivedAt = DateTimeOffset.UtcNow;
        // Keeps public media identifiers (such as ?v=) and drops share, tracking
        // and signed query values. Scrubbing every query would lose the media ID.
        // A direct CDN link becomes the reference itself, so the signatures and the
        // viewer's address that some CDNs put in the path, and a password-like p=,
        // are removed exactly as the history link policy removes them.
        var reference = MediaValidation.ScrubPublicSourceUrl(new Uri(EngineSupport.PublicSourceUrl(info.Url)));
        await File.WriteAllTextAsync(Path.Combine(staging, "archive.json"), JsonSerializer.Serialize(new { SchemaVersion = 2, Application = AppIdentity.DisplayName, ApplicationId = AppIdentity.Protocol, AppVersion = AppIdentity.Version, Id = ArchiveLine(info.Id), ArchivedAtUtc = archivedAt, SourceUrl = reference, Title = ArchiveLine(info.Title), Creator = info.Creator is null ? null : ArchiveLine(info.Creator), Source = info.Source is null ? null : ArchiveLine(info.Source), info.PublishedAt, DurationSeconds = probe.Duration.TotalSeconds, MediaFile = Path.GetFileName(primary), Components = versions.ToDictionary(item => item.Key, item => item.Value is null ? null : ArchiveLine(item.Value)), PackageFile = packageZip ? "archive.zip" : null, Notes = notices.Count > 0 ? notices : null, Note ="Original downloaded streams preserved; availability and source quality depend on the site. Sensitive query parameters are omitted from the source reference." }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        var readable = new StringBuilder()
            .AppendLine(AppIdentity.DisplayName + " preservation archive")
            .AppendLine("Title: " + ArchiveLine(info.Title))
            .AppendLine("Uploader: " + ArchiveLine(info.Creator))
            .AppendLine("Source: " + ArchiveLine(info.Source))
            .AppendLine("Media ID: " + ArchiveLine(info.Id))
            .AppendLine("Reference: " + reference)
            .AppendLine("Archived (UTC): " + archivedAt.ToString("O", EngineSupport.Invariant))
            .AppendLine("Application: " + AppIdentity.DisplayName + " " + AppIdentity.Version + " (" + AppIdentity.Protocol + ")")
            .AppendLine("Original media: " + Path.GetFileName(primary))
            .AppendLine("The highest available source streams were saved without quality-reducing conversion. Site availability still determines source quality.")
            .AppendLine("Available subtitles and source metadata accompany the media; automatically generated captions in the original language are included only when selected.")
            .AppendLine().AppendLine("Component versions:");
        foreach (var component in versions.OrderBy(item => item.Key, StringComparer.Ordinal)) readable.AppendLine(component.Key + ": " + ArchiveLine(component.Value));
        readable.AppendLine().AppendLine("Integrity: SHA256SUMS.txt records the SHA-256 checksum of each raw archive file. Compare each listed checksum with a newly calculated checksum after copying or sharing. This detects changed bytes; it does not certify source accuracy or rights.")
            .AppendLine("source.json holds source information; technical.json holds media details; archive.json records this app version and the source reference. Sensitive query parameters are omitted from the reference.");
        if (notices.Count > 0)
        {
            readable.AppendLine().AppendLine("Notes:");
            foreach (var notice in notices) readable.AppendLine("- " + notice);
        }
        if (packageZip) readable.AppendLine("archive.zip stores this raw archive without recompressing its media. It includes SHA256SUMS.txt. archive.zip.sha256 separately checks the ZIP itself; the ZIP and its checksum are outside the raw-file manifest to avoid circular checksums. The raw media remains alongside the ZIP.");
        await File.WriteAllTextAsync(Path.Combine(staging, "archive-info.txt"), readable.ToString(), cancellationToken);
        var checksums = new List<string>();
        foreach (var path in ArchiveFiles(staging))
            checksums.Add(await EngineSupport.Sha256Async(path, cancellationToken) + "  " + Path.GetFileName(path));
        // LF endings: the standard "sha256sum -c" format; CRLF makes it look for "name\r".
        await File.WriteAllTextAsync(Path.Combine(staging, "SHA256SUMS.txt"), string.Concat(checksums.Select(line => line + "\n")), cancellationToken);
        if (packageZip)
        {
            try { await PackageArchiveAsync(staging, progress, cancellationToken); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { throw new MediaOperationException("The ZIP package could not be written or verified. The archive was not saved; choose another output folder or retry."); }
        }
        var archiveDirectory = Path.Combine(directory, name + " Archive");
        while (Directory.Exists(archiveDirectory) || File.Exists(archiveDirectory)) archiveDirectory = Path.Combine(directory, name + " Archive " + Guid.NewGuid().ToString("N")[..8]);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.PrepareForPromotion();
        Directory.Move(staging, archiveDirectory);
        var destination = Path.Combine(archiveDirectory, Path.GetFileName(primary));
        progress?.Report(new JobProgress(JobStage.Finished, 100, notices.Count == 0 ? "Archive complete and checked" : "Archive complete and checked. " + string.Join(" ", notices)));
        // source.json on disk holds the cleaned metadata; the result does not repeat it.
        return new DownloadResult(destination, Lean(info), new FileInfo(destination).Length, archiveDirectory, packageZip ? Path.Combine(archiveDirectory, "archive.zip") : null);
    }

    // Evidence text is kept exactly. Free-text redaction would turn "The Secret
    // Life of Pets" into "The Secret [redacted] of Pets"; titles, uploaders and
    // IDs hold no sign-in data. Only control characters and length are limited.
    private static string ArchiveLine(string? value) => new string(WholeCharacters(value ?? "Unknown").Take(2000).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();

    private static async Task PackageArchiveAsync(string staging, IProgress<JobProgress>? progress, CancellationToken token)
    {
        progress?.Report(new JobProgress(JobStage.WritingMetadata, Message: "Packaging the raw archive as ZIP"));
        var files = ArchiveFiles(staging).ToArray();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new MediaOperationException("The archive contains an unexpected file link. Packaging was stopped.");
            expected[Path.GetFileName(file)] = await EngineSupport.Sha256Async(file, token);
        }
        var package = Path.Combine(staging, "archive.zip");
        await using (var file = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var raw in files)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(Path.GetFileName(raw), CompressionLevel.NoCompression);
                await using var input = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = entry.Open();
                await input.CopyToAsync(output, 131072, token);
            }
        }
        progress?.Report(new JobProgress(JobStage.Verifying, Message: "Checking the ZIP against the raw archive checksums"));
        using (var zip = ZipFile.OpenRead(package))
        {
            if (zip.Entries.Count != expected.Count) throw new MediaOperationException("The ZIP archive was incomplete. The archive was not saved.");
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                await using var content = entry.Open();
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(content, token)).ToLowerInvariant();
                if (!expected.TryGetValue(entry.FullName, out var hash) || actual != hash) throw new MediaOperationException("The ZIP archive did not match its raw files. The archive was not saved.");
            }
        }
        var packageHash = await EngineSupport.Sha256Async(package, token);
        await File.WriteAllTextAsync(Path.Combine(staging, "archive.zip.sha256"), packageHash + "  archive.zip\n", token);
    }
    private static IEnumerable<string> ArchiveFiles(string staging) => Directory.EnumerateFiles(staging).Where(path => !OwnedTemporaryWorkspaceService.ControlFile(Path.GetFileName(path))).Order(StringComparer.Ordinal);
    internal static MediaInfo ParseInfo(JsonElement root, Uri requestedUrl)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new MediaOperationException("The site returned incomplete media information.");
        var url = EngineSupport.Url(EngineSupport.Text(root, "webpage_url")) ?? requestedUrl;
        var formats = new List<MediaFormat>();
        if (root.TryGetProperty("formats", out var formatArray) && formatArray.ValueKind == JsonValueKind.Array)
            foreach (var item in formatArray.EnumerateArray().Take(2000))
                formats.Add(new MediaFormat(EngineSupport.Text(item, "format_id") ?? "unknown", EngineSupport.Text(item, "ext"), EngineSupport.Integer(item, "width"), EngineSupport.Integer(item, "height"), EngineSupport.Numeric(item, "fps"), EngineSupport.Text(item, "vcodec"), EngineSupport.Text(item, "acodec"), EngineSupport.Integer64(item, "filesize"), EngineSupport.Numeric(item, "abr"), EngineSupport.Text(item, "language")));
        var entries = new List<PlaylistEntry>();
        if (root.TryGetProperty("entries", out var entryArray) && entryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entryArray.EnumerateArray().Take(PlaylistLimit))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = EngineSupport.Text(item, "id") ?? "";
                var entryUrl = EngineSupport.Url(EngineSupport.Text(item, "webpage_url")) ?? EngineSupport.Url(EngineSupport.Text(item, "url"));
                if (entryUrl is null && EngineSupport.Text(item, "ie_key") == "Youtube" && YoutubeId().IsMatch(id)) entryUrl = new Uri("https://www.youtube.com/watch?v=" + id);
                entries.Add(new PlaylistEntry(id, EngineSupport.Text(item, "title") ?? "Unavailable item", entryUrl, EngineSupport.Duration(EngineSupport.Numeric(item, "duration"))));
            }
        }
        DateTimeOffset? published = null;
        if (EngineSupport.Text(root, "upload_date") is { } date && DateTime.TryParseExact(date, "yyyyMMdd", EngineSupport.Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) published = new DateTimeOffset(parsed);
        return new MediaInfo(EngineSupport.Text(root, "id") ?? "unknown", EngineSupport.Text(root, "title") ?? "Untitled media", url, EngineSupport.Text(root, "uploader") ?? EngineSupport.Text(root, "channel"), EngineSupport.Text(root, "extractor_key") ?? EngineSupport.Text(root, "extractor"), EngineSupport.Duration(EngineSupport.Numeric(root, "duration")), EngineSupport.Url(EngineSupport.Text(root, "thumbnail")), published, EngineSupport.Text(root, "description"), formats, entries, root.GetRawText());
    }

    /// <summary>
    /// What to queue for one entry of an analysed playlist or multi-video post.
    /// An entry with its own page is queued by that address. Items of a post
    /// (X, Instagram) share the post's address, so they are queued as the post
    /// address plus their position, passed as <see cref="DownloadOptions.PlaylistItems"/>.
    /// Returns null for an index outside the listed entries.
    /// </summary>
    public static (Uri Url, string? PlaylistItems)? PlaylistEntryTarget(MediaInfo playlist, int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= playlist.Entries.Count) return null;
        if (playlist.Entries[entryIndex].Url is { } own && Uri.Compare(own, playlist.Url, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0) return (own, null);
        using var json = EngineSupport.Json(playlist.MetadataJson ?? "{}", "URL analysis");
        if (!json.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
        var position = 0;
        var listed = -1;
        // Mirrors ParseInfo, which skips entries that are not objects.
        foreach (var item in entries.EnumerateArray().Take(PlaylistLimit))
        {
            position++;
            if (item.ValueKind == JsonValueKind.Object && ++listed == entryIndex) return (playlist.Url, (EngineSupport.Integer(item, "playlist_index") ?? position).ToString(EngineSupport.Invariant));
        }
        return null;
    }

    /// <summary>
    /// True when an entry is itself a list rather than one media item, such as the
    /// Videos or Shorts tab of a channel or a playlist inside a channel. Queueing it
    /// as a download would fail; it should be offered as "Open as playlist" instead.
    /// </summary>
    public static bool PlaylistEntryIsList(MediaInfo playlist, int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= playlist.Entries.Count) return false;
        using var json = EngineSupport.Json(playlist.MetadataJson ?? "{}", "URL analysis");
        if (!json.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return false;
        var item = entries.EnumerateArray().Take(PlaylistLimit).Where(entry => entry.ValueKind == JsonValueKind.Object).ElementAtOrDefault(entryIndex);
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (IsPlaylistResult(item)) return true;
        // Flat listings name the extractor that would open the entry; list
        // extractors end in Tab (YouTube channel tabs), Playlist, Channel or Album.
        return EngineSupport.Text(item, "_type") is "url" or "url_transparent" && EngineSupport.Text(item, "ie_key") is { } extractor
            && new[] { "Tab", "Playlist", "Channel", "Album" }.Any(suffix => extractor.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The listed entry count, the playlist's own total when the site reports it,
    /// and whether more entries exist than the 500 that analysis lists.
    /// </summary>
    public static (int Listed, int? Total, bool MoreAvailable) PlaylistExtent(MediaInfo playlist)
    {
        using var json = EngineSupport.Json(playlist.MetadataJson ?? "{}", "URL analysis");
        var root = json.RootElement;
        var returned = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.GetArrayLength() : 0;
        var total = EngineSupport.Integer(root, "playlist_count");
        return (playlist.Entries.Count, total, returned > PlaylistLimit || total > Math.Min(returned, PlaylistLimit));
    }
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex Language();
    [GeneratedRegex("^[A-Za-z0-9,*._-]+$")] private static partial Regex Languages();
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")] private static partial Regex YoutubeId();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")] private static partial Regex TrackKey();
    [GeneratedRegex("^[1-9][0-9]{0,5}$")] private static partial Regex PlaylistPosition();
    // Nothing, a partial download, its resume record, or one fragment of it.
    [GeneratedRegex(@"^(?:\.part|\.ytdl|\.part-Frag[0-9]{1,6}(?:\.part)?)?$")] private static partial Regex StreamTail();
    private static readonly HashSet<string> SidecarExtensions = [".vtt", ".srt", ".ass", ".ssa", ".ttml", ".lrc", ".srv1", ".srv2", ".srv3", ".json3", ".jpg", ".jpeg", ".png", ".webp"];
    private static readonly HashSet<string> MediaExtensions = [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".m4a", ".mp3", ".opus", ".ogg", ".oga", ".flac", ".wav", ".aac", ".flv", ".avi", ".ts", ".3gp"];
}

// Turns yt-dlp's per-stream progress into one steady job percentage. A video and
// an audio stream download one after the other and each counts from 0 to 100%, so
// the raw value would fall from 100% back to 0% between them. The plan printed
// before the transfer gives the streams in order and, when known, their sizes.
internal sealed class DownloadProgressTracker
{
    private readonly object sync = new();
    private string[]? planIds;
    private double[]? shares;
    private double[]? plannedBytes;
    private int stream = -1;
    private int slot;
    private string? format;
    private bool streamFinished;
    private double completed;
    private double share = 1;
    private double? last;
    private bool processing;

    /// <summary>The format IDs of separately downloaded streams, once planned.</summary>
    internal IReadOnlyList<string> PlannedFormats { get { lock (sync) return planIds ?? []; } }

    // Both pipes are read on separate threads. The update is reported while the
    // lock is held, so reports reach the queue in the order they were decided: a
    // download line read just before "Merging" can never be shown after it.
    internal void Read(string line, IProgress<JobProgress>? progress)
    {
        var prefix = line.StartsWith("KC_PLAN:", StringComparison.Ordinal) ? "KC_PLAN:" : line.StartsWith("KC_PROGRESS:", StringComparison.Ordinal) ? "KC_PROGRESS:" : line.StartsWith("KC_POST:", StringComparison.Ordinal) ? "KC_POST:" : null;
        if (prefix is null || line.Length > 65536) return;
        try
        {
            using var json = JsonDocument.Parse(line.AsMemory(prefix.Length));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            lock (sync)
            {
                JobProgress? update;
                if (prefix == "KC_PLAN:") { Plan(root); return; }
                if (prefix == "KC_POST:") { processing = true; update = Post(root); }
                // Post-processing starts only after every stream has downloaded. The
                // two pipes are read separately, so a late download line must not
                // send the stage back from Merging to Downloading.
                else update = processing ? null : Download(root);
                if (update is not null) progress?.Report(update);
            }
        }
        catch (JsonException) { }
    }

    private void Plan(JsonElement root)
    {
        var ids = Strings(root, "f");
        if (ids.Length < 2 || stream >= 0) return;
        planIds = ids;
        // Traversal drops missing values, so a list shorter than the plan is unusable.
        var sizes = Numbers(root, "s") is { } exact && exact.Length == ids.Length && exact.All(size => size > 0) ? exact : Numbers(root, "e") is { } approximate && approximate.Length == ids.Length && approximate.All(size => size > 0) ? approximate : null;
        if (sizes is not null)
        {
            plannedBytes = sizes;
            shares = sizes.Select(size => size / sizes.Sum()).ToArray();
            return;
        }
        // Unknown sizes: audio is usually a small part of a video download.
        var videos = Strings(root, "v");
        var audioOnly = ids.Select((_, index) => videos.Length == ids.Length && videos[index] == "none").ToArray();
        var audioCount = audioOnly.Count(value => value);
        var videoCount = ids.Length - audioCount;
        shares = audioCount == 0 || videoCount == 0 ? ids.Select(_ => 1d / ids.Length).ToArray() : audioOnly.Select(audio => audio ? 0.15 / audioCount : 0.85 / videoCount).ToArray();
    }

    private JobProgress Download(JsonElement root)
    {
        var templated = root.TryGetProperty("p", out var nested) && nested.ValueKind == JsonValueKind.Object;
        var data = templated ? nested : root;
        // Subtitle tracks download before the media and carry no format ID. They
        // must not count as the first media stream.
        if (templated && EngineSupport.Text(root, "f") is null) return new JobProgress(JobStage.Downloading, last, "Downloading subtitles");
        var formatId = EngineSupport.Text(root, "f") ?? "";
        var status = EngineSupport.Text(data, "status");
        if (stream < 0 || formatId != format || streamFinished && status != "finished")
        {
            if (stream >= 0) completed = Math.Min(1, completed + share);
            stream++;
            format = formatId;
            var planned = planIds is { } ids ? Array.IndexOf(ids, formatId) : -1;
            slot = planned >= 0 ? planned : stream;
            share = shares is { } weights ? slot < weights.Length ? weights[slot] : 0 : stream == 0 ? 1 : 0;
        }
        streamFinished = status == "finished";
        var total = EngineSupport.Numeric(data, "total_bytes") ?? EngineSupport.Numeric(data, "total_bytes_estimate");
        double? fraction = total is > 0 && EngineSupport.Numeric(data, "downloaded_bytes") is { } done ? Math.Clamp(done / total.Value, 0, 1) : null;
        if (fraction is null && EngineSupport.Numeric(data, "fragment_count") is double count and > 0 && EngineSupport.Numeric(data, "fragment_index") is { } index) fraction = Math.Clamp(index / count, 0, 1);
        if (streamFinished) fraction = 1;
        // Never move backwards; keep the last value while a stream's size is unknown.
        var percent = fraction is { } part ? Math.Max(last ?? 0, Math.Clamp((completed + share * part) * 100, 0, 100)) : last;
        last = percent;
        var speed = EngineSupport.Numeric(data, "speed");
        var seconds = EngineSupport.Numeric(data, "eta");
        if (plannedBytes is { } bytes && speed is > 0 && slot + 1 < bytes.Length) seconds = (seconds ?? 0) + bytes.Skip(slot + 1).Sum() / speed.Value;
        var video = EngineSupport.Text(root, "v");
        var audio = EngineSupport.Text(root, "a");
        var message = video is not null and not "none" && audio == "none" ? "Downloading video" : video == "none" && audio is not null and not "none" ? "Downloading audio" : "Downloading media";
        return new JobProgress(JobStage.Downloading, percent, message, speed, EngineSupport.Duration(seconds));
    }

    private static JobProgress? Post(JsonElement root)
    {
        // Each step reports started and finished; announcing the start is enough.
        if (EngineSupport.Text(root, "status") != "started") return null;
        return EngineSupport.Text(root, "postprocessor") switch
        {
            "Merger" => new JobProgress(JobStage.Merging, Message: "Merging video and audio"),
            "ExtractAudio" => new JobProgress(JobStage.Converting, Message: "Converting the audio"),
            // VideoRemuxer also reports "started" when the file is already in the
            // target container and nothing is done; a real remux is a quick stream
            // copy. Neither is shown as a conversion, so the current stage stays.
            "Metadata" => new JobProgress(JobStage.WritingMetadata, Message: "Writing media details"),
            "EmbedThumbnail" => new JobProgress(JobStage.WritingMetadata, Message: "Embedding the cover image"),
            "EmbedSubtitle" => new JobProgress(JobStage.WritingMetadata, Message: "Embedding subtitles"),
            // MoveFiles, Fixup* and other housekeeping steps keep the current stage.
            _ => null
        };
    }

    private static string[] Strings(JsonElement root, string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "").ToArray() : [];
    private static double[]? Numbers(JsonElement root, string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array && array.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number) ? array.EnumerateArray().Select(item => item.GetDouble()).ToArray() : null;
}
