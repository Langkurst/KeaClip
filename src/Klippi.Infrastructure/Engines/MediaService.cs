using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klippi.Core;

namespace Klippi.Infrastructure.Engines;

/// <summary>
/// Source extras a whole-file conversion or container change can keep. Each
/// one applies only where the output container supports it; clips never carry them.
/// </summary>
[Flags]
public enum PreserveExtras
{
    None = 0,
    /// <summary>Chapter markers, for MP4, MOV, MKV and WebM outputs.</summary>
    Chapters = 1,
    /// <summary>The embedded JPEG or PNG cover picture, for MP4 and MKV outputs.</summary>
    CoverArt = 2,
    /// <summary>Other Matroska attachments, such as fonts or an embedded info file, for MKV outputs.</summary>
    Attachments = 4,
    /// <summary>Copies the source audio when no audio change was asked for and the container accepts the codec.</summary>
    CompatibleAudio = 8,
    All = Chapters | CoverArt | Attachments | CompatibleAudio
}

/// <summary>The audio bitrates (kbps) an export's encoder accepts, and what they apply to, such as "Ogg Vorbis for mono audio at 48 kHz".</summary>
public sealed record AudioBitrateLimits(int MinKbps, int MaxKbps, string Description)
{
    /// <summary>Any bitrate works, or none applies.</summary>
    public static readonly AudioBitrateLimits None = new(1, int.MaxValue, "");
}

public sealed partial class MediaService(IProcessRunner runner, IComponentService components, ITemporaryWorkspaceService? temporaryWorkspaces = null) : IMediaService
{
    private const string SdrSourceNote = "The source is already SDR, so HDR → SDR was skipped.";
    private const string LosslessSizeLimit = "WAV and FLAC are lossless, so their size cannot be limited. Choose MP3, AAC, Opus or Ogg for a size limit.";
    private const string StallMessage = "FFmpeg stopped making progress, so the export was stopped. The original was preserved. Try again, or choose another encoder or codec.";
    private const string SoundboardFormats = "Soundboard export supports MP3 (.mp3) or Ogg Vorbis (.ogg). Choose one of these formats before exporting.";
    // Windows FFmpeg builds list AMF, NVENC and QSV encoders together, whatever
    // GPU is installed. One tiny real encode per encoder decides usability; the
    // result is kept for the session and keyed by the FFmpeg path.
    private readonly ConcurrentDictionary<string, bool> hardwareUsable = new(StringComparer.Ordinal);
    // Test encodes that ran out of time. One is not trusted as a verdict: on a busy
    // PC a child process can wait tens of seconds before it gets any CPU time.
    private readonly ConcurrentDictionary<string, int> hardwareTimeouts = new(StringComparer.Ordinal);
    // Whether an FFmpeg build has the zscale and tonemap filters HDR → SDR needs.
    private readonly ConcurrentDictionary<string, bool> toneMapping = new(StringComparer.Ordinal);
    // The editor probes a file and then asks for its waveform. The waveform reuses that probe
    // while the file is unchanged (same full path, size and write time) instead of running
    // FFprobe again. A few entries are enough; the cache starts over when it fills.
    private readonly ConcurrentDictionary<string, (FileStamp Stamp, ProbeInfo Probe)> recentProbes = new(StringComparer.OrdinalIgnoreCase);
    private const int RecentProbeLimit = 8;
    // Video and audio encoders KeaClip can choose. Only these are reported as
    // capabilities; the full FFmpeg list has hundreds of unrelated entries.
    private static readonly HashSet<string> UsableSoftwareEncoders = new(StringComparer.Ordinal) { "libx264", "libopenh264", "libx265", "libsvtav1", "libaom-av1", "libvpx", "libvpx-vp9", "aac", "libmp3lame", "libopus", "libvorbis", "flac", "pcm_s16le" };

    /// <summary>
    /// How long FFmpeg may go without reporting a later output time before an export
    /// is stopped. Null uses encoder-specific limits (2-6 minutes plus a start-up allowance).
    /// </summary>
    internal TimeSpan? ProgressStallLimit { get; init; }
    /// <summary>Overall export time limit. Null scales it with the clip length and encoder speed.</summary>
    internal TimeSpan? ExportTimeLimit { get; init; }
    /// <summary>The slowest expected speed of the silent +faststart rewrite at the end of an MP4 or M4A export (a hard disk reads and writes every byte).</summary>
    internal double FaststartBytesPerSecond { get; init; } = 20_000_000;
    /// <summary>
    /// Time limit for quick inspections (FFprobe, the encoder and filter lists). It counts
    /// from before the child starts, and with a game running, FFmpeg children waited 18
    /// and 38 s without any CPU time, so the old 30-45 s limits could fail an export.
    /// </summary>
    internal static readonly TimeSpan InspectionLimit = TimeSpan.FromMinutes(2);
    /// <summary>Time limit for one hardware encoder's test encode; a timeout is retried once on a later call before it counts as unusable.</summary>
    internal TimeSpan HardwareProbeLimit { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<ProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var input = EngineSupport.Input(path);
        var before = Stamp(input);
        var probe = await ReadProbeAsync(input, cancellationToken).ConfigureAwait(false);
        // Only a file that did not change while FFprobe read it is remembered.
        if (before is { } stamp && Stamp(input) == stamp)
        {
            if (recentProbes.Count >= RecentProbeLimit) recentProbes.Clear();
            recentProbes[input] = (stamp, probe);
        }
        return probe;
    }
    private readonly record struct FileStamp(long Length, DateTime WrittenUtc);
    private static FileStamp? Stamp(string path)
    {
        try { var file = new FileInfo(path); return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    /// <summary>The probe <see cref="ProbeAsync"/> made of this file, while the file is unchanged since; otherwise null.</summary>
    private ProbeInfo? RecentProbe(string input) => recentProbes.TryGetValue(input, out var entry) && Stamp(input) == entry.Stamp ? entry.Probe : null;

    private async Task<ProbeInfo> ReadProbeAsync(string input, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest(EngineSupport.Required(components, "ffprobe"), ["-v", "error", "-show_format", "-show_streams", "-of", "json", input], Timeout: InspectionLimit, OutputLimit: 4_194_304, Interactive: true), cancellationToken: cancellationToken).ConfigureAwait(false);
        EngineSupport.Success(result, "Media inspection");
        using var json = EngineSupport.Json(result.StandardOutput, "Media inspection");
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) throw new MediaOperationException("The file contains no readable media streams.");
        JsonElement? video = null, audio = null;
        double? streamDuration = null;
        foreach (var stream in streams.EnumerateArray())
        {
            var type = EngineSupport.Text(stream, "codec_type");
            if (type == "video" && video is null && !IsAttachedPicture(stream)) video = stream;
            if (type == "audio" && audio is null) audio = stream;
            if (EngineSupport.Numeric(stream, "duration") is { } duration) streamDuration = Math.Max(streamDuration ?? 0, duration);
        }
        var seconds = root.TryGetProperty("format", out var format) ? EngineSupport.Numeric(format, "duration") : null;
        var time = EngineSupport.Duration(seconds ?? streamDuration);
        var length = new FileInfo(input).Length;
        if (video is null && audio is null || time is null || time <= TimeSpan.Zero || length == 0) throw new MediaOperationException("The file has no readable duration or audio/video content.");
        var geometry = video is { } visual ? DisplayGeometry(visual) : (Width: (int?)null, Height: (int?)null);
        return new ProbeInfo(input, time.Value, length, video is not null, audio is not null,
            geometry.Width, geometry.Height,
            video is { } f ? Fraction(EngineSupport.Text(f, "avg_frame_rate") ?? EngineSupport.Text(f, "r_frame_rate")) : null,
            video is { } vc ? EngineSupport.Text(vc, "codec_name") : null, audio is { } ac ? EngineSupport.Text(ac, "codec_name") : null,
            audio is { } sr ? EngineSupport.Integer(sr, "sample_rate") : null, audio is { } ch ? EngineSupport.Integer(ch, "channels") : null, root.GetRawText());
    }
    private static bool IsAttachedPicture(JsonElement stream) => stream.TryGetProperty("disposition", out var disposition) && EngineSupport.Integer(disposition, "attached_pic") == 1;

    private static (int? Width, int? Height) DisplayGeometry(JsonElement video)
    {
        var width = EngineSupport.Integer(video, "width"); var height = EngineSupport.Integer(video, "height");
        if (!video.TryGetProperty("side_data_list", out var sideData)) return (width, height);
        if (sideData.ValueKind != JsonValueKind.Array) throw new MediaOperationException("The media display geometry could not be verified.");
        var found = false;
        foreach (var item in sideData.EnumerateArray())
        {
            if (EngineSupport.Text(item, "side_data_type") != "Frame Cropping") continue;
            if (found || width is null or <= 0 || height is null or <= 0) throw new MediaOperationException("The media crop metadata could not be verified.");
            found = true;
            var left = CropOffset(item, "crop_left"); var right = CropOffset(item, "crop_right");
            var top = CropOffset(item, "crop_top"); var bottom = CropOffset(item, "crop_bottom");
            var horizontal = (long)left + right; var vertical = (long)top + bottom;
            if (horizontal >= width || vertical >= height) throw new MediaOperationException("The media crop metadata exceeds the video dimensions.");
            // AMF AV1 can pad its coded dimensions and carry an MP4 display
            // crop. The UI uses displayed geometry; TechnicalJson retains the
            // engine's original coded dimensions and complete crop metadata.
            width -= (int)horizontal; height -= (int)vertical;
        }
        return (width, height);
    }
    private static int CropOffset(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var offset) || offset < 0) throw new MediaOperationException("The media crop metadata contains an invalid offset.");
        return offset;
    }

    public Task<ExportResult> ExportAsync(ExportOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => ExportAsync(options, PreserveExtras.None, progress, cancellationToken);

    /// <summary>
    /// Exports like <see cref="ExportAsync(ExportOptions, IProgress{JobProgress}?, CancellationToken)"/> and
    /// carries the chosen source extras over when the export covers the whole source
    /// (no start, end or silence trimming). Clips never carry them.
    /// </summary>
    /// <summary>Longest GIF export: GIFs grow fast, and a minute is already tens of megabytes.</summary>
    public static readonly TimeSpan MaxGifLength = TimeSpan.FromSeconds(60);
    internal const string GifFilter = "fps=15,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle";

    // A GIF is its own small job: silent, looping, palette built from the clip itself, checked before it is committed.
    private async Task<ExportResult> ExportGifAsync(ProbeInfo probe, ExportOptions options, string input, string output, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (!probe.HasVideo) throw new MediaOperationException("A GIF needs a picture, and this file is audio only.");
        if (!string.Equals(Path.GetExtension(output), ".gif", StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("Save the GIF with a .gif name.");
        if (options.DeleteOriginalAfterSuccess) throw new MediaOperationException("A GIF is a preview copy, so KeaClip never deletes the original after making one.");
        var start = options.Start ?? TimeSpan.Zero;
        var end = options.End ?? probe.Duration;
        if (start < TimeSpan.Zero || end > probe.Duration + TimeSpan.FromSeconds(0.05) || end - start < TimeSpan.FromSeconds(0.2)) throw new MediaOperationException("Choose a part of the video to turn into a GIF.");
        if (end - start > MaxGifLength) throw new MediaOperationException("GIFs are limited to 60 seconds. Trim the selection shorter, or export a video clip instead.");
        var ffmpeg = EngineSupport.Required(components, "ffmpeg");
        var parent = Path.GetDirectoryName(output)!;
        using var workspace = EngineSupport.CreateWorkspace(parent, temporaryWorkspaces);
        var staged = Path.Combine(workspace.Path, Path.GetFileName(output));
        workspace.RegisterPayload(Path.GetFileName(staged));
        try
        {
            progress?.Report(new JobProgress(JobStage.Converting, 0, "Making the GIF"));
            string[] arguments = ["-hide_banner", "-nostdin", "-v", "error", "-ss", Seconds(start), "-t", Seconds(end - start), "-i", input, "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", GifFilter, "-loop", "0", "-y", staged];
            var result = await runner.RunAsync(new ProcessRequest(ffmpeg, arguments, workspace.Path, TimeSpan.FromMinutes(10), 262_144), cancellationToken: cancellationToken).ConfigureAwait(false);
            EngineSupport.Success(result, "GIF export");
            progress?.Report(new JobProgress(JobStage.Verifying, Message: "Checking the GIF"));
            var exported = await ProbeAsync(staged, cancellationToken).ConfigureAwait(false);
            if (!exported.HasVideo || exported.HasAudio || !string.Equals(exported.VideoCodec, "gif", StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("The GIF did not come out right. The original was preserved.");
            cancellationToken.ThrowIfCancellationRequested();
            EngineSupport.CommitFile(staged, output, options.ReplaceExisting);
            progress?.Report(new JobProgress(JobStage.Finished, 100, "GIF ready"));
            return new ExportResult(output, exported with { FilePath = output }, false);
        }
        finally { EngineSupport.Cleanup(workspace, parent); }
    }
    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", EngineSupport.Invariant);

    public async Task<ExportResult> ExportAsync(ExportOptions options, PreserveExtras extras, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var input = EngineSupport.Input(options.InputPath);
        var output = EngineSupport.Absolute(options.OutputPath);
        if (string.Equals(EngineSupport.Canonical(input), EngineSupport.Canonical(output), StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("Choose a separate output file so the original stays safe.");
        if (File.Exists(output) && !options.ReplaceExisting) throw new MediaOperationException("That output already exists. Choose another name or explicitly allow replacement.");
        if ((extras & ~PreserveExtras.All) != 0) throw new MediaOperationException("Choose supported export options.");
        var probe = await ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (options.Gif) return await ExportGifAsync(probe, options, input, output, progress, cancellationToken).ConfigureAwait(false);
        var range = ValidateExport(probe, options, output);
        var carry = PlanExtras(probe, options, range, output, extras);
        var inputHash = options.DeleteOriginalAfterSuccess ? await EngineSupport.Sha256Async(input, cancellationToken).ConfigureAwait(false) : null;
        var ffmpeg = EngineSupport.Required(components, "ffmpeg");
        var encodesVideo = options.ClipMode == ClipMode.Exact && !options.AudioOnly && probe.HasVideo;
        var notes = new List<string>();
        // Audio-only copies start at the selected audio frame (-copypriorss 0), and a
        // copy from the beginning has no earlier keyframe to fall back to.
        if (options.ClipMode == ClipMode.Lossless && !options.AudioOnly && range.Start > TimeSpan.Zero) notes.Add("Lossless cuts follow codec keyframes; the start may differ from the selected time.");
        var hdr = encodesVideo && options.HdrToSdr ? HdrSource(probe) : null;
        if (encodesVideo && options.HdrToSdr && hdr is null) notes.Add(SdrSourceNote);
        if (hdr is not null && !await ToneMappingAvailableAsync(ffmpeg, cancellationToken).ConfigureAwait(false)) throw new MediaOperationException("The installed FFmpeg cannot convert HDR to SDR because it lacks the zscale and tonemap filters. Check Components, or turn off HDR → SDR. The original was preserved.");
        var audio = options.ClipMode == ClipMode.Exact && probe.HasAudio ? AudioSettingsFor(probe, options, output) : null;
        if (audio?.Note is { } audioNote) notes.Add(audioNote);
        // An audio-only size limit sets its own bitrate, so the range note would name the wrong one.
        if (audio?.BitrateNote is { } bitrateNote && !(options.AudioOnly && EffectiveTarget(options) is not null)) notes.Add(bitrateNote);
        var encoders = encodesVideo ? await ListEncodersAsync(ffmpeg, cancellationToken).ConfigureAwait(false) : Array.Empty<EncoderInfo>();
        var hardware = new Queue<EncoderInfo>(encodesVideo ? HardwareCandidates(probe, options, encoders) : []);
        var encoder = encodesVideo ? await NextEncoderAsync(ffmpeg, hardware, probe, options, encoders, cancellationToken).ConfigureAwait(false) : null;
        var hardwareNote = options.Encoder == EncoderPreference.Gpu && encoder is { IsHardware: false } ? "A suitable hardware encoder was unavailable; the CPU encoder was used." : null;
        var parent = Path.GetDirectoryName(output)!;
        using var workspace = EngineSupport.CreateWorkspace(parent, temporaryWorkspaces);
        var staging = workspace.Path;
        var stagedOutput = Path.Combine(staging, Path.GetFileName(output));
        workspace.RegisterPayload(Path.GetFileName(stagedOutput));
        try
        {
            if (options.TrimSilence) range = await TrimSilenceAsync(ffmpeg, input, range, staging, progress, cancellationToken).ConfigureAwait(false);
            if (carry.Cover is { Attach: true } attached)
            {
                // Matroska stores a cover as an attachment; mapping the picture
                // stream would add a one-frame video track instead.
                carry = carry with { CoverFile = await ExtractCoverAsync(ffmpeg, input, attached, staging, workspace, cancellationToken).ConfigureAwait(false) };
                if (carry.CoverFile is null) { carry = carry with { Cover = null }; notes.Add("The cover picture could not be carried over."); }
            }
            var target = EffectiveTarget(options);
            var plan = new EncodePlan(target is null ? null : Estimate(probe, options).VideoKbps, target is null && encodesVideo ? PresetCapKbps(probe, options) : null, target, audio, hdr, carry);
            var firstTargetKbps = plan.TargetKbps;
            // What the preset aims for, used to tell the user when a hardware encoder fell far short.
            double? presetAimBytes = plan.CapKbps is { } aimKbps ? (aimKbps + (probe.HasAudio ? AudioKbpsFor(probe, options) : 0)) * 125d * range.Duration.TotalSeconds : null;
            // An audio clip is compressed only when its preset really lowers the bitrate (never for WAV or FLAC).
            var compresses = target is not null || (encodesVideo ? options.Compression != CompressionPreset.Off : audio is { Codec: not ("flac" or "pcm_s16le") } && options.AudioOnly && AudioKbpsFor(probe, options) < options.AudioKbps);
            var stage = compresses ? JobStage.Compressing : JobStage.Converting;
            var lowered = false;
            ProbeInfo? exported = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new JobProgress(stage, 0, options.ClipMode == ClipMode.Lossless ? "Copying media without re-encoding" : "Exporting the selected clip"));
                var run = await RunExportAsync(ffmpeg, input, stagedOutput, staging, probe, options, range, encoder, plan, stage, progress, cancellationToken).ConfigureAwait(false);
                while ((run.Result.ExitCode != 0 || run.Outcome != RunOutcome.Completed) && encoder is { IsHardware: true })
                {
                    // Listed and probed is still not a guarantee for every input;
                    // try the next working hardware encoder, then the CPU.
                    encoder = await NextEncoderAsync(ffmpeg, hardware, probe, options, encoders, cancellationToken).ConfigureAwait(false);
                    hardwareNote = encoder.IsHardware ? "One hardware encoder could not complete this export; " + encoder.Name + " was used instead." : "Hardware encoding could not complete this export; the CPU encoder was used.";
                    run = await RunExportAsync(ffmpeg, input, stagedOutput, staging, probe, options, range, encoder, plan, stage, progress, cancellationToken).ConfigureAwait(false);
                }
                if (run.Outcome == RunOutcome.Stalled) throw new MediaOperationException(StallMessage);
                if (run.Outcome == RunOutcome.OverDeadline) throw new MediaOperationException($"The export reached its time limit of {Math.Ceiling(DeadlineFor(range.Duration, encoder, options).TotalMinutes).ToString(EngineSupport.Invariant)} minutes for this clip and encoder, so it was stopped. The original was preserved. Choose a faster encoder, a lower resolution or a shorter clip.");
                EngineSupport.Success(run.Result, "Media export");
                progress?.Report(new JobProgress(JobStage.Verifying, Message: "Checking the exported media"));
                exported = await ProbeAsync(stagedOutput, cancellationToken).ConfigureAwait(false);
                if (options.AudioOnly && (!exported.HasAudio || exported.HasVideo) || !options.AudioOnly && probe.HasVideo && !exported.HasVideo || probe.HasAudio && !exported.HasAudio) throw new MediaOperationException("The output did not contain the required media tracks. The original was preserved.");
                if (options.ClipMode == ClipMode.Exact && Math.Abs(exported.Duration.TotalSeconds - range.Duration.TotalSeconds) > Math.Max(0.15, range.Duration.TotalSeconds * 0.002)) throw new MediaOperationException("The exported duration did not match the selected clip. The original was preserved.");
                if (options.DiscordSoundboard && exported.Duration.TotalSeconds > options.SoundboardMaxSeconds) throw new MediaOperationException("The exported audio exceeds the current soundboard duration preset. Choose a slightly shorter clip.");
                if (options.DiscordSoundboard && (exported.HasVideo || exported.AudioCodec != (options.AudioFormat == AudioFormat.Mp3 ? "mp3" : "vorbis"))) throw new MediaOperationException("The exported audio does not match the selected MP3 or Ogg soundboard format. The original was preserved.");
                if (target is not null)
                {
                    if (exported.SizeBytes <= target) break;
                    if (options.ClipMode == ClipMode.Lossless || options.AudioOnly || attempt == 2) throw new MediaOperationException("The export could not meet the chosen size limit. Choose a smaller clip, lower resolution or lower bitrate. The original was preserved.");
                    // Hardware rate control can hold a floor far above a low bitrate:
                    // h264_amf asked for 748k, 608k and 497k gave about 1 Mbit/s each
                    // time, where libx264 met the same limit. A clear miss, or a second
                    // one, moves to the CPU encoder at the first bitrate.
                    if (encoder is { IsHardware: true } && (exported.SizeBytes > target.Value * 1.1 || lowered) && CpuEncoderOrNull(probe, options, encoders) is { } cpuEncoder)
                    {
                        encoder = cpuEncoder;
                        hardwareNote = "The hardware encoder could not meet the size limit, so the CPU encoder was used.";
                        plan = plan with { TargetKbps = firstTargetKbps };
                        progress?.Report(new JobProgress(JobStage.Compressing, Message: "Using the CPU encoder to meet the size limit"));
                        continue;
                    }
                    lowered = true;
                    plan = plan with { TargetKbps = Math.Max(32, (int)Math.Floor((plan.TargetKbps ?? 128) * (double)target.Value / exported.SizeBytes * 0.92)) };
                    progress?.Report(new JobProgress(JobStage.Compressing, Message: "Adjusting bitrate to meet the size limit"));
                    continue;
                }
                if (plan.CapKbps is { } cap && exported.SizeBytes >= SourceShare(probe, range))
                {
                    // A compression preset must not hand back a larger file than
                    // the matching part of the original without saying so. Hardware
                    // encoders stop at their highest QP (h264_amf measured well above
                    // low caps), so the CPU encoder gets the next try; then a lower cap.
                    if (attempt < 2 && encoder is { IsHardware: true } && CpuEncoderOrNull(probe, options, encoders) is { } cpu)
                    {
                        encoder = cpu;
                        hardwareNote = "The hardware encoder could not make this clip smaller than the original, so the CPU encoder was used.";
                        progress?.Report(new JobProgress(JobStage.Compressing, Message: "Using the CPU encoder so the clip is smaller than the original"));
                        continue;
                    }
                    var share = SourceShare(probe, range);
                    // The audio is encoded at about the source bitrate. When it alone is
                    // most of the original's share, or the cap is already at its floor,
                    // a lower video cap repeats the same encode: a 60 s source with
                    // 10 kbit/s video and 64 kbit/s audio ran the identical libx264
                    // command twice and still came out 1% larger.
                    var audioBytes = probe.HasAudio ? AudioKbpsFor(probe, options) * 125d * range.Duration.TotalSeconds : 0;
                    var lowerCap = Math.Max(32, (int)(cap * 0.6));
                    if (attempt < 2 && !lowered && lowerCap < cap && audioBytes < share * 0.9)
                    {
                        lowered = true;
                        plan = plan with { CapKbps = lowerCap };
                        progress?.Report(new JobProgress(JobStage.Compressing, Message: "Lowering the bitrate so the clip is smaller than the original"));
                        continue;
                    }
                    var (clipSize, shareSize) = DistinctMegabytes(exported.SizeBytes, share);
                    notes.Add($"The compressed clip ({clipSize}) is not smaller than the matching part of the original ({shareSize}), " + (audioBytes >= share * 0.5
                        ? "because its audio and the container already take up most of the original's size; a lower video bitrate cannot make it smaller."
                        : "because the source is already efficiently encoded."));
                }
                // h264_amf Very Small came out 1.44x its aim, as large as Strong, so
                // a quarter above the aim is already worth saying.
                else if (encoder is { IsHardware: true } && presetAimBytes is { } aim && exported.SizeBytes > aim * 1.25)
                    notes.Add($"{encoder.Name} could not compress this clip as far as the preset aims (about {Megabytes(aim)}); choose the CPU encoder for a smaller file.");
                break;
            }
            if (exported is null) throw new MediaOperationException("The engine did not create a verified export.");
            if (options.DeleteOriginalAfterSuccess)
            {
                // A readable header alone is insufficient when the user chooses source deletion.
                var decode = await runner.RunAsync(new ProcessRequest(ffmpeg, ["-hide_banner", "-v", "error", "-xerror", "-threads", "1", "-i", stagedOutput, "-map", "0:v?", "-map", "0:a?", "-f", "null", "-"], Timeout: TimeSpan.FromMinutes(Math.Min(720, 5 + exported.Duration.TotalMinutes * 2)), OutputLimit: 262144), cancellationToken: cancellationToken).ConfigureAwait(false);
                EngineSupport.Success(decode, "Export integrity check");
                if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0 || inputHash != await EngineSupport.Sha256Async(input, cancellationToken).ConfigureAwait(false)) throw new MediaOperationException("The original changed or is a file link. It was preserved; export again without deletion.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            EngineSupport.CommitFile(stagedOutput, output, options.ReplaceExisting);
            var deleted = false;
            string? deletionNote = null;
            if (options.DeleteOriginalAfterSuccess)
            {
                try
                {
                    if (inputHash == await EngineSupport.Sha256Async(input, CancellationToken.None).ConfigureAwait(false)) { File.Delete(input); deleted = true; }
                    else deletionNote = "The export succeeded, but the original changed and was preserved.";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { deletionNote = "The export succeeded, but the original could not be deleted and was preserved."; }
            }
            progress?.Report(new JobProgress(JobStage.Finished, 100, "Export complete and checked"));
            var warning = string.Join(" ", new[] { deletionNote, hardwareNote }.Concat(notes).OfType<string>());
            return new ExportResult(output, exported with { FilePath = output }, deleted, warning.Length == 0 ? null : warning);
        }
        finally { EngineSupport.Cleanup(workspace, parent); }
    }

    private async Task<(ProcessResult Result, RunOutcome Outcome)> RunExportAsync(string ffmpeg, string input, string stagedOutput, string staging, ProbeInfo probe, ExportOptions options, (TimeSpan Start, TimeSpan Duration) range, EncoderInfo? encoder, EncodePlan plan, JobStage stage, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(stagedOutput)) File.Delete(stagedOutput);
        var arguments = ExportArguments(input, stagedOutput, probe, options, range, encoder, plan);
        // The deadline is only a generous backstop scaled to the encoder's
        // speed. A stuck engine is stopped by the progress watchdog instead,
        // which restarts every time FFmpeg reports a later output time.
        var stall = ProgressStallLimit ?? StallLimit(encoder);
        var deadline = DeadlineFor(range.Duration, encoder, options);
        using var backstop = new CancellationTokenSource(deadline);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, backstop.Token);
        // Seeking and the first frames of a slow encoder get a start-up allowance.
        watchdog.CancelAfter(ProgressStallLimit ?? stall + TimeSpan.FromMinutes(3));
        // FFmpeg reports nothing while it writes the container trailer, which for
        // +faststart rewrites the whole file, so the end of the clip gets longer.
        var finishing = Math.Max(0, range.Duration.TotalSeconds - 0.5) * 1_000_000;
        var finishAllowance = ProgressStallLimit ?? TimeSpan.FromTicks(Math.Max(stall.Ticks, TimeSpan.FromMinutes(10).Ticks));
        // The rewrite reads and writes the whole output, and a fast export can
        // report last well before its end, so every allowance also covers
        // rewriting what has been written so far (tens of GB on a hard disk
        // took longer than the fixed 10 minutes).
        var faststart = UsesFaststart(stagedOutput);
        long furthest = -1, written = 0;
        void Line(string line)
        {
            if (faststart && line.StartsWith("total_size=", StringComparison.Ordinal) && line.Length < 40 && long.TryParse(line.AsSpan(11), NumberStyles.Integer, EngineSupport.Invariant, out var size) && size > 0) Interlocked.Exchange(ref written, size);
            if (ProgressMicroseconds(line) is not { } microseconds) return;
            if (microseconds > Interlocked.Read(ref furthest))
            {
                Interlocked.Exchange(ref furthest, microseconds);
                var allowance = (microseconds >= finishing ? finishAllowance : stall) + TimeSpan.FromSeconds(Interlocked.Read(ref written) / FaststartBytesPerSecond);
                try { watchdog.CancelAfter(allowance); }
                catch (ObjectDisposedException) { }
            }
            if (ParseProgress(line, range.Duration, stage) is { } update) progress?.Report(update);
        }
        try
        {
            // The runner's own limit stays a minute later so this method can explain the stop.
            var result = await runner.RunAsync(new ProcessRequest(ffmpeg, arguments, staging, deadline + TimeSpan.FromMinutes(1), 1_048_576), Line, cancellationToken: watchdog.Token).ConfigureAwait(false);
            return (result, RunOutcome.Completed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && watchdog.IsCancellationRequested)
        {
            return (new ProcessResult(-1, "", ""), backstop.IsCancellationRequested ? RunOutcome.OverDeadline : RunOutcome.Stalled);
        }
    }
    private TimeSpan DeadlineFor(TimeSpan clip, EncoderInfo? encoder, ExportOptions options) => ExportTimeLimit ?? ExportDeadline(clip, encoder, options);
    private static EncoderInfo? CpuEncoderOrNull(ProbeInfo probe, ExportOptions options, IReadOnlyList<EncoderInfo> encoders)
    {
        try { return ChooseCpu(probe, options, encoders); }
        catch (MediaOperationException) { return null; }
    }
    internal static TimeSpan ExportDeadline(TimeSpan clip, EncoderInfo? encoder, ExportOptions options)
    {
        var minutesPerClipMinute = encoder?.Name switch { "libaom-av1" => 60, "libx265" or "libsvtav1" or "libvpx" or "libvpx-vp9" => 20, _ => 8 } + (options.HdrToSdr ? 4 : 0);
        return TimeSpan.FromMinutes(Math.Min(720, 10 + clip.TotalMinutes * minutesPerClipMinute));
    }
    internal static TimeSpan StallLimit(EncoderInfo? encoder) => encoder switch { { IsHardware: true } => TimeSpan.FromMinutes(2), { Name: "libaom-av1" } => TimeSpan.FromMinutes(6), _ => TimeSpan.FromMinutes(3) };

    private async Task<(TimeSpan Start, TimeSpan Duration)> TrimSilenceAsync(string ffmpeg, string input, (TimeSpan Start, TimeSpan Duration) range, string staging, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        // Detect silence on the selection only, then cut every stream to the
        // first and last sound. Pauses inside the clip stay, and video keeps
        // its sync because audio and video share the same new range.
        progress?.Report(new JobProgress(JobStage.Converting, null, "Finding silence at the start and end"));
        var arguments = new List<string> { "-hide_banner", "-nostats", "-v", "info", "-nostdin", "-threads", "2" };
        if (range.Start > TimeSpan.Zero) arguments.AddRange(["-ss", EngineSupport.Number(range.Start.TotalSeconds)]);
        // Audio that starts after the video (0.3 s measured on an offset file)
        // left its leading gap undetected; padding to time zero makes the gap
        // part of the leading silence while times stay on the clip timeline.
        arguments.AddRange(["-t", EngineSupport.Number(range.Duration.TotalSeconds), "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn", "-af", "aresample=async=1:first_pts=0,silencedetect=n=-45dB:d=0.05", "-progress", "pipe:1", "-f", "null", "-"]);
        var events = new List<(bool IsStart, double Seconds)>();
        long decoded = -1;
        var result = await runner.RunAsync(new ProcessRequest(ffmpeg, arguments, staging, TimeSpan.FromMinutes(Math.Min(120, 5 + range.Duration.TotalMinutes)), 262_144),
            line => { if (ProgressMicroseconds(line) is { } microseconds && microseconds > Interlocked.Read(ref decoded)) Interlocked.Exchange(ref decoded, microseconds); },
            line =>
            {
                var match = SilenceEvent().Match(line);
                if (match.Success && double.TryParse(match.Groups[2].Value, NumberStyles.Float, EngineSupport.Invariant, out var seconds) && double.IsFinite(seconds))
                    lock (events) events.Add((match.Groups[1].Value == "start", seconds));
            }, cancellationToken).ConfigureAwait(false);
        EngineSupport.Success(result, "Silence detection");
        (double Start, double End)? bounds;
        lock (events) bounds = SilenceBounds(events, decoded > 0 ? decoded / 1_000_000d : range.Duration.TotalSeconds, range.Duration.TotalSeconds);
        if (bounds is not { } sound) throw new MediaOperationException("The selected range is silent, so Trim silence would leave nothing to export. Choose a range with sound or turn off Trim silence.");
        return (range.Start + TimeSpan.FromSeconds(sound.Start), TimeSpan.FromSeconds(sound.End - sound.Start));
    }
    internal static (double Start, double End)? SilenceBounds(IReadOnlyList<(bool IsStart, double Seconds)> events, double decodedSeconds, double requestedSeconds)
    {
        const double Edge = 0.005;
        var segments = new List<(double Start, double? End)>();
        foreach (var (isStart, seconds) in events)
            if (isStart) segments.Add((seconds, null));
            else if (segments.Count > 0 && segments[^1].End is null) segments[^1] = (segments[^1].Start, seconds);
        var audioEnd = decodedSeconds > 0 ? Math.Min(decodedSeconds, requestedSeconds) : requestedSeconds;
        double start = 0, end = requestedSeconds;
        if (segments.Count > 0 && segments[0].Start <= Edge) start = Math.Max(0, segments[0].End ?? audioEnd);
        // FFmpeg closes a silence that reaches the end of the input at that end.
        if (segments.Count > 0 && (segments[^1].End is null || segments[^1].End >= audioEnd - Edge)) end = Math.Min(end, Math.Max(0, segments[^1].Start));
        return end - start >= 0.02 ? (start, end) : null;
    }

    public async Task<IReadOnlyList<EncoderInfo>> EncodersAsync(CancellationToken cancellationToken = default)
    {
        var ffmpeg = EngineSupport.Required(components, "ffmpeg");
        var listed = await ListEncodersAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        var checks = listed.Where(encoder => IsCandidateHardware(encoder.Name)).Select(async encoder => (encoder.Name, Usable: await HardwareUsableAsync(ffmpeg, encoder.Name, cancellationToken).ConfigureAwait(false)));
        var usable = (await Task.WhenAll(checks).ConfigureAwait(false)).Where(check => check.Usable).Select(check => check.Name).ToHashSet(StringComparer.Ordinal);
        // Only encoders KeaClip can choose are reported, and hardware ones only
        // after a real test encode succeeded on this PC.
        return listed.Where(encoder => usable.Contains(encoder.Name) || !encoder.IsHardware && UsableSoftwareEncoders.Contains(encoder.Name)).ToArray();
    }
    private async Task<IReadOnlyList<EncoderInfo>> ListEncodersAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest(ffmpeg, ["-hide_banner", "-encoders"], Timeout: InspectionLimit, OutputLimit: 1_048_576, Interactive: true), cancellationToken: cancellationToken).ConfigureAwait(false);
        EngineSupport.Success(result, "Encoder inspection");
        return ParseEncoders(result.StandardOutput);
    }
    internal static IReadOnlyList<EncoderInfo> ParseEncoders(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        // FFmpeg prints a capability legend (" V..... = Video") before a dashed separator.
        var separator = Array.FindIndex(lines, line => line.Trim() is { Length: >= 3 } trimmed && trimmed.All(character => character == '-'));
        var encoders = new List<EncoderInfo>();
        for (var index = separator + 1; index < lines.Length; index++)
        {
            var match = EncoderLine().Match(lines[index]);
            if (!match.Success || match.Groups[1].Value == "=") continue;
            var name = match.Groups[1].Value;
            encoders.Add(new EncoderInfo(name, match.Groups[2].Value.Trim(), name.EndsWith("_amf", StringComparison.Ordinal) || name.EndsWith("_nvenc", StringComparison.Ordinal) || name.EndsWith("_qsv", StringComparison.Ordinal) || name.EndsWith("_vaapi", StringComparison.Ordinal) || name.EndsWith("_videotoolbox", StringComparison.Ordinal)));
        }
        return encoders;
    }
    private static bool IsCandidateHardware(string name) => (name.StartsWith("h264_", StringComparison.Ordinal) || name.StartsWith("hevc_", StringComparison.Ordinal) || name.StartsWith("av1_", StringComparison.Ordinal)) && (name.EndsWith("_amf", StringComparison.Ordinal) || name.EndsWith("_nvenc", StringComparison.Ordinal) || name.EndsWith("_qsv", StringComparison.Ordinal));
    private async Task<bool> HardwareUsableAsync(string ffmpeg, string encoder, CancellationToken cancellationToken)
    {
        var key = ffmpeg + "\n" + encoder;
        if (hardwareUsable.TryGetValue(key, out var known)) return known;
        bool usable;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(HardwareProbeLimit);
        try
        {
            // The runner's own limit is a backstop; the probe limit cancels first so a timeout is recognisable.
            var result = await runner.RunAsync(new ProcessRequest(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30:d=0.1", "-frames:v", "1", "-an", "-c:v", encoder, "-pix_fmt", "yuv420p", "-f", "null", "-"], Timeout: HardwareProbeLimit + TimeSpan.FromMinutes(1), OutputLimit: 65536, Interactive: true), cancellationToken: limit.Token).ConfigureAwait(false);
            usable = result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not a verdict on the encoder the first time: this call goes on without
            // it, and a later call tests it again. A second timeout marks it unusable,
            // so a hanging driver costs at most two waits per session.
            if (hardwareTimeouts.AddOrUpdate(key, 1, (_, count) => count + 1) < 2) return false;
            usable = false;
        }
        catch (MediaOperationException) { usable = false; }
        hardwareUsable[key] = usable;
        return usable;
    }
    private async Task<bool> ToneMappingAvailableAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        if (toneMapping.TryGetValue(ffmpeg, out var known)) return known;
        var result = await runner.RunAsync(new ProcessRequest(ffmpeg, ["-hide_banner", "-filters"], Timeout: InspectionLimit, OutputLimit: 1_048_576, Interactive: true), cancellationToken: cancellationToken).ConfigureAwait(false);
        EngineSupport.Success(result, "Filter inspection");
        var names = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(columns => columns.Length > 1).Select(columns => columns[1]).ToHashSet(StringComparer.Ordinal);
        return toneMapping[ffmpeg] = names.Contains("zscale") && names.Contains("tonemap");
    }
    private async Task<EncoderInfo> NextEncoderAsync(string ffmpeg, Queue<EncoderInfo> hardware, ProbeInfo probe, ExportOptions options, IReadOnlyList<EncoderInfo> encoders, CancellationToken cancellationToken)
    {
        while (hardware.TryDequeue(out var candidate))
            if (await HardwareUsableAsync(ffmpeg, candidate.Name, cancellationToken).ConfigureAwait(false)) return candidate;
        return ChooseCpu(probe, options, encoders);
    }

    public async Task<IReadOnlyList<float>> WaveformAsync(string path, int points = 600, CancellationToken cancellationToken = default)
    {
        if (points is < 16 or > 4096) throw new MediaOperationException("Choose between 16 and 4096 waveform points.");
        // The editor has just probed this file to open it; that probe is reused while the file is unchanged.
        var input = EngineSupport.Input(path);
        var probe = RecentProbe(input) ?? await ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (!probe.HasAudio) return Array.Empty<float>();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KeaClip");
        using var workspace = EngineSupport.CreateWorkspace(temporaryRoot, temporaryWorkspaces);
        var staging = workspace.Path;
        try
        {
            var output = Path.Combine(staging, "waveform.wav");
            workspace.RegisterPayload(Path.GetFileName(output));
            // Adaptive preview sample rate bounds temporary storage to roughly 16 MB.
            // This is a visual overview, not a sample-accurate editor/audio measurement.
            var rate = (int)Math.Clamp(Math.Floor(8_000_000 / probe.Duration.TotalSeconds), 50, 8000);
            if (probe.Duration.TotalSeconds * rate > 8_000_000) throw new MediaOperationException("This file is too long for the bounded waveform preview.");
            var result = await runner.RunAsync(new ProcessRequest(EngineSupport.Required(components, "ffmpeg"), ["-hide_banner", "-v", "error", "-nostdin", "-threads", "1", "-i", probe.FilePath, "-vn", "-ac", "1", "-ar", rate.ToString(EngineSupport.Invariant), "-c:a", "pcm_s16le", "-threads", "1", "-fs", "17000000", "-n", output], staging, TimeSpan.FromMinutes(Math.Min(120, 5 + probe.Duration.TotalMinutes * 2)), 262144), cancellationToken: cancellationToken).ConfigureAwait(false);
            EngineSupport.Success(result, "Waveform preview");
            // Parsing up to 8 million samples must never run on the caller's (UI) thread.
            return await Task.Run(() => ReadWaveform(output, points, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { EngineSupport.Cleanup(workspace, temporaryRoot); }
    }

    public ExportEstimate Estimate(ProbeInfo probe, ExportOptions options)
    {
        var start = options.Start ?? TimeSpan.Zero;
        var end = options.End ?? probe.Duration;
        if (start < TimeSpan.Zero || end <= start || end > probe.Duration) throw new MediaOperationException("Choose a start/end inside the media duration.");
        var duration = end - start;
        var seconds = duration.TotalSeconds;
        // The export would refuse these after the save dialog; the estimate says so first.
        if (options.DiscordSoundboard) ValidateSoundboard(options, duration);
        var target = EffectiveTarget(options);
        var exactAudio = options.AudioOnly && options.ClipMode == ClipMode.Exact;
        if (exactAudio && target is not null && options.AudioFormat is AudioFormat.Wav or AudioFormat.Flac) throw new MediaOperationException(options.DiscordSoundboard ? SoundboardFormats : LosslessSizeLimit);
        var encodesVideo = options.ClipMode == ClipMode.Exact && !options.AudioOnly && probe.HasVideo;
        var sdrNote = encodesVideo && options.HdrToSdr && HdrSource(probe) is null ? " " + SdrSourceNote : "";
        if (target is { } bytes && !(options.AudioOnly && options.ClipMode == ClipMode.Lossless))
        {
            if (bytes < 1024) throw new MediaOperationException("Choose a target size of at least 1 KiB.");
            var totalKbps = Math.Floor(bytes * 0.94 * 8 / seconds / 1000);
            if (options.AudioOnly)
            {
                // The bitrate the encoder receives; a 5 s soundboard MP3 showed its
                // 500,000-byte limit as the size although it came out near 122,000.
                var audio = AudioSettingsFor(probe, options, options.OutputPath);
                var audioKbps = EncodedAudioKbps(probe, options, audio, bytes, seconds);
                return new ExportEstimate(duration, Math.Min(bytes, (long)Math.Ceiling(seconds * audioKbps * 125d * 1.03)), null, "Estimated from the audio bitrate within the size limit; the actual exported size is checked before completion.");
            }
            var audioPart = probe.HasAudio ? AudioKbpsFor(probe, options) : 0;
            var videoKbps = totalKbps - audioPart;
            if (videoKbps < 32 || videoKbps > int.MaxValue) throw new MediaOperationException("This size is too small for the selected duration and audio quality.");
            // A size target is a ceiling, not an aim: a clip whose part of the original already
            // fits keeps about the source's video bitrate instead of being inflated to fill the
            // limit (a 3 s clip came out 6.4x its part of the original at the 10 MB preset).
            if (TargetCapKbps(probe) is { } sourceCap && videoKbps > sourceCap)
                return new ExportEstimate(duration, Math.Min(bytes, (long)Math.Ceiling((sourceCap + audioPart) * 125d * seconds * 1.02)), sourceCap, $"This part of the original already fits the target, so the video keeps about its source bitrate ({sourceCap.ToString(EngineSupport.Invariant)} kbps) instead of growing to fill the limit. The actual size is checked after export." + sdrNote);
            return new ExportEstimate(duration, bytes, (int)videoKbps, "Bitrate estimate allows for audio and container overhead; actual size is checked and may require another encode." + sdrNote);
        }
        if (options.AudioOnly && options.ClipMode == ClipMode.Lossless)
            return AudioStreamKbps(probe) is { } sourceKbps
                ? new ExportEstimate(duration, (long)Math.Ceiling(sourceKbps * 1000 / 8 * seconds * 1.01), null, "Estimated from the source audio bitrate; the copied audio and its container can differ slightly.")
                : new ExportEstimate(duration, null, null, "The source does not report its audio bitrate, so the size of the copied audio is known only after export.");
        if (exactAudio)
        {
            var audio = AudioSettingsFor(probe, options, options.OutputPath);
            var pcmBytes = 44 + audio.SampleRate * (long)AudioChannels(probe, options) * 2 * seconds;
            // Compression presets set an audio bitrate; WAV and FLAC have none to lower.
            var presetNote = AudioPresetKbps(options.Compression) is not null ? " Compression presets do not change lossless audio." : "";
            if (options.AudioFormat == AudioFormat.Wav) return new ExportEstimate(duration, (long)Math.Ceiling(pcmBytes), null, "WAV size follows the duration, sample rate and channel count." + presetNote);
            if (options.AudioFormat == AudioFormat.Flac) return new ExportEstimate(duration, null, null, $"FLAC is lossless, so its size depends on the sound: usually {Megabytes(pcmBytes * 0.4)}–{Megabytes(pcmBytes * 0.8)}, at most about {Megabytes(pcmBytes)} for this selection." + presetNote);
            var kbps = EncodedAudioKbps(probe, options, audio, null, seconds);
            var lowered = AudioPresetKbps(options.Compression) is not null && kbps < options.AudioKbps && audio.BitrateNote is null
                ? $" {CompressionName(options.Compression)} compression encodes the audio at {kbps.ToString(EngineSupport.Invariant)} kbps instead of {options.AudioKbps.ToString(EngineSupport.Invariant)} kbps."
                : "";
            return new ExportEstimate(duration, (long)Math.Ceiling(seconds * kbps * 1000 / 8 * 1.03), null, "Estimated audio size; tags, bitrate behavior and encoding padding can change the result." + lowered + Note(audio.BitrateNote));
        }
        if (options.ClipMode == ClipMode.Lossless) return new ExportEstimate(duration, (long)Math.Ceiling(probe.SizeBytes * seconds / probe.Duration.TotalSeconds), null, "Estimated from the source average bitrate; keyframes and variable bitrate affect the actual size.");
        if (encodesVideo && PresetCapKbps(probe, options) is { } cap)
            return new ExportEstimate(duration, (long)Math.Ceiling((cap + (probe.HasAudio ? AudioKbpsFor(probe, options) : 0)) * 1000d / 8 * seconds * 1.02), cap, $"Estimated from the source bitrate: this preset aims to keep video at or below about {cap.ToString(EngineSupport.Invariant)} kbps, so the result is usually about this size or smaller; hardware H.264 can land higher. The actual size is checked after export." + sdrNote);
        // The card already says the size is known after export; only an HDR note needs saying.
        return new ExportEstimate(duration, null, null, sdrNote.Trim());
    }
    private static string Note(string? note) => note is null ? "" : " " + note;
    private static string CompressionName(CompressionPreset preset) => preset == CompressionPreset.VerySmall ? "Very small" : preset.ToString();
    private static string Megabytes(double bytes) => (bytes / 1_000_000).ToString(bytes < 10_000_000 ? "0.0" : "0", EngineSupport.Invariant) + " MB";
    /// <summary>
    /// Two sizes with enough decimals to tell them apart: a clip 1% larger than a
    /// 0.61 MB original read "(0.6 MB) is not smaller than ... (0.6 MB)".
    /// </summary>
    internal static (string First, string Second) DistinctMegabytes(double first, double second)
    {
        var digits = first < 10_000_000 && second < 10_000_000 ? 1 : 0;
        string Format(double bytes) => (bytes / 1_000_000).ToString("F" + digits.ToString(EngineSupport.Invariant), EngineSupport.Invariant) + " MB";
        while (digits < 3 && Format(first) == Format(second)) digits++;
        return (Format(first), Format(second));
    }

    /// <summary>True when the first video stream signals PQ (HDR10) or HLG, the only inputs HDR → SDR converts.</summary>
    public static bool IsHdr(ProbeInfo probe) => HdrSource(probe) is not null;
    private static HdrSignal? HdrSource(ProbeInfo probe)
    {
        if (!probe.HasVideo || VideoStream(probe) is not { } stream) return null;
        var transfer = EngineSupport.Text(stream, "color_transfer");
        if (transfer is not ("smpte2084" or "arib-std-b67")) return null;
        // Only whitelisted names reach the filter graph; missing tags fall back
        // to the BT.2020 values HDR10 and HLG use, so zscale always has a path.
        var primaries = EngineSupport.Text(stream, "color_primaries") switch { "bt709" => "bt709", "smpte432" => "smpte432", _ => "bt2020" };
        var matrix = EngineSupport.Text(stream, "color_space") switch { "bt2020c" => "bt2020c", "bt709" => "bt709", _ => "bt2020nc" };
        var fullRange = EngineSupport.Text(stream, "color_range") == "pc";
        return new HdrSignal(transfer, primaries, matrix, fullRange ? "pc" : "tv");
    }
    private static JsonElement? VideoStream(ProbeInfo probe) => FirstStream(probe, "video");
    private static JsonElement? FirstStream(ProbeInfo probe, string type)
    {
        foreach (var stream in Streams(probe))
            if (EngineSupport.Text(stream, "codec_type") == type && !(type == "video" && IsAttachedPicture(stream))) return stream;
        return null;
    }
    private static List<JsonElement> Streams(ProbeInfo probe)
    {
        var list = new List<JsonElement>();
        if (string.IsNullOrEmpty(probe.TechnicalJson)) return list;
        try
        {
            using var json = JsonDocument.Parse(probe.TechnicalJson, new JsonDocumentOptions { MaxDepth = 64 });
            if (json.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var stream in streams.EnumerateArray()) list.Add(stream.Clone());
        }
        catch (JsonException) { }
        return list;
    }

    private static CarryPlan PlanExtras(ProbeInfo probe, ExportOptions options, (TimeSpan Start, TimeSpan Duration) range, string output, PreserveExtras extras)
    {
        // Chapters, covers and attachments describe the whole source, so only a
        // whole-file conversion or container change carries them.
        if (extras == PreserveExtras.None || options.AudioOnly || options.TrimSilence || !probe.HasVideo || range.Start > TimeSpan.Zero || Math.Abs((range.Duration - probe.Duration).TotalMilliseconds) > 1) return CarryPlan.None;
        var extension = Path.GetExtension(output).ToLowerInvariant();
        var streams = Streams(probe);
        var chapters = extras.HasFlag(PreserveExtras.Chapters) && extension is ".mp4" or ".mov" or ".mkv" or ".webm";
        CoverPicture? cover = null;
        if (extras.HasFlag(PreserveExtras.CoverArt) && extension is ".mp4" or ".mkv")
            foreach (var stream in streams)
                if (EngineSupport.Text(stream, "codec_type") == "video" && IsAttachedPicture(stream) && EngineSupport.Text(stream, "codec_name") is ("mjpeg" or "png") and var codec && EngineSupport.Integer(stream, "index") is >= 0 and var index)
                {
                    cover = new CoverPicture(index, codec, Attach: extension == ".mkv");
                    break;
                }
        var attachments = extras.HasFlag(PreserveExtras.Attachments) && extension == ".mkv" ? streams.Count(stream => EngineSupport.Text(stream, "codec_type") == "attachment") : 0;
        // Copying is only right when nothing about the audio was asked to change.
        var copyAudio = extras.HasFlag(PreserveExtras.CompatibleAudio) && options.ClipMode == ClipMode.Exact && probe.HasAudio && options.Volume == 1 && !options.Normalize && !options.Mono && options.SampleRate is null && options.FadeIn == TimeSpan.Zero && options.FadeOut == TimeSpan.Zero && options.Compression == CompressionPreset.Off && EffectiveTarget(options) is null && AudioFitsContainer(probe.AudioCodec, extension);
        return new CarryPlan(chapters, cover, attachments, copyAudio);
    }
    private static bool AudioFitsContainer(string? codec, string extension) => extension switch
    {
        // MP4 keeps to codecs that common players accept there; Opus or FLAC sources get AAC.
        ".mp4" or ".mov" => codec is "aac" or "mp3" or "ac3" or "eac3" or "alac",
        ".mkv" => codec is "aac" or "mp3" or "opus" or "vorbis" or "flac" or "ac3" or "eac3" or "dts" or "alac" or "truehd" or "pcm_s16le" or "pcm_s24le",
        ".webm" => codec is "opus" or "vorbis",
        _ => false
    };
    private async Task<string?> ExtractCoverAsync(string ffmpeg, string input, CoverPicture cover, string staging, ITemporaryWorkspace workspace, CancellationToken cancellationToken)
    {
        var name = cover.Codec == "png" ? "cover.png" : "cover.jpg";
        workspace.RegisterPayload(name);
        var path = Path.Combine(staging, name);
        try
        {
            // A stream copy writes the embedded picture byte for byte.
            var result = await runner.RunAsync(new ProcessRequest(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-i", input, "-map", "0:" + cover.Index.ToString(EngineSupport.Invariant), "-c", "copy", "-frames:v", "1", "-f", "image2", "-update", "1", "-n", path], staging, TimeSpan.FromMinutes(2), 65_536), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }
        catch (MediaOperationException) { return null; }
    }
    private static double? StreamKbps(JsonElement stream)
    {
        if (EngineSupport.Numeric(stream, "bit_rate") is > 0 and var bits) return bits / 1000;
        // Matroska and WebM carry the stream bitrate as a statistics tag.
        if (stream.TryGetProperty("tags", out var tags) && (EngineSupport.Numeric(tags, "BPS") ?? EngineSupport.Numeric(tags, "BPS-eng")) is > 0 and var tagged) return tagged / 1000;
        return null;
    }
    private static double? AudioStreamKbps(ProbeInfo probe) => FirstStream(probe, "audio") is { } stream ? StreamKbps(stream) : null;
    internal static double SourceVideoKbps(ProbeInfo probe) => ReportedSourceVideoKbps(probe) ?? Math.Max(1, probe.SizeBytes * 8d / probe.Duration.TotalSeconds / 1000 - (probe.HasAudio ? AudioStreamKbps(probe) ?? 128 : 0));
    /// <summary>The source video bitrate FFprobe reported (for the stream, or the container minus the audio); null when it reported none.</summary>
    private static double? ReportedSourceVideoKbps(ProbeInfo probe)
    {
        if (VideoStream(probe) is { } video && StreamKbps(video) is { } direct) return direct;
        if (FormatKbps(probe) is not { } total) return null;
        return Math.Max(1, total - (probe.HasAudio ? AudioStreamKbps(probe) ?? 128 : 0));
    }
    private static double? FormatKbps(ProbeInfo probe)
    {
        if (string.IsNullOrEmpty(probe.TechnicalJson)) return null;
        try
        {
            using var json = JsonDocument.Parse(probe.TechnicalJson, new JsonDocumentOptions { MaxDepth = 64 });
            if (json.RootElement.TryGetProperty("format", out var format) && EngineSupport.Numeric(format, "bit_rate") is > 0 and var bits) return bits / 1000;
        }
        catch (JsonException) { }
        return null;
    }
    /// <summary>
    /// The most video bitrate a size target spends: about what the source has, so a clip that
    /// already fits is never re-encoded larger than its part of the original. Null when FFprobe
    /// reported no bitrate to compare with.
    /// </summary>
    private static int? TargetCapKbps(ProbeInfo probe) => probe.HasVideo && ReportedSourceVideoKbps(probe) is { } source ? (int)Math.Clamp(Math.Ceiling(source), 32, 2_000_000) : null;
    internal static int? PresetCapKbps(ProbeInfo probe, ExportOptions options)
    {
        // Presets keep a quality target but never ask for more video bitrate
        // than this share of the source, so they compress instead of growing.
        var share = options.Compression switch { CompressionPreset.Light => 0.7, CompressionPreset.Balanced => 0.5, CompressionPreset.Strong => 0.35, CompressionPreset.VerySmall => 0.2, _ => 0d };
        if (share == 0 || !probe.HasVideo) return null;
        return (int)Math.Clamp(Math.Floor(SourceVideoKbps(probe) * share), 32, 2_000_000);
    }
    private static long SourceShare(ProbeInfo probe, (TimeSpan Start, TimeSpan Duration) range) => (long)(probe.SizeBytes * Math.Min(1, range.Duration.TotalSeconds / probe.Duration.TotalSeconds));

    /// <summary>
    /// The audio bitrate an export encodes. Compression and size limits never
    /// spend more on audio than the source had: a 96 kbit/s source took 192 kbit/s,
    /// most of a Light clip, and the preset then cut the video far below its aim.
    /// </summary>
    internal static int AudioKbpsFor(ProbeInfo probe, ExportOptions options)
    {
        // An audio clip has no video to compress, so a preset sets its audio bitrate instead
        // (never above the chosen one); "Very small" left a 192 kbps clip at 192 kbps.
        if (options.AudioOnly) return AudioPresetKbps(options.Compression) is { } preset ? Math.Min(options.AudioKbps, preset) : options.AudioKbps;
        if (options.Compression == CompressionPreset.Off && EffectiveTarget(options) is null || AudioStreamKbps(probe) is not { } source) return options.AudioKbps;
        // The next standard bitrate at or above the source; 3% slack keeps a
        // measured 96.1 kbit/s at 96 instead of 112.
        int[] steps = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        var step = steps.FirstOrDefault(value => value >= source * 0.97);
        return step == 0 ? options.AudioKbps : Math.Min(options.AudioKbps, step);
    }
    /// <summary>The audio bitrate (kbps) a compression preset gives an audio clip, at most the chosen bitrate; null for Off and a size target.</summary>
    public static int? AudioPresetKbps(CompressionPreset preset) => preset switch
    {
        CompressionPreset.Light => 160,
        CompressionPreset.Balanced => 128,
        CompressionPreset.Strong => 96,
        CompressionPreset.VerySmall => 64,
        _ => null
    };
    /// <summary>The audio bitrate that keeps an audio-only export within its size limit.</summary>
    private static int TargetAudioKbps(int kbps, long targetBytes, double seconds) => (int)Math.Min(kbps, Math.Floor(targetBytes * 0.90 * 8 / seconds / 1000));
    /// <summary>
    /// The -b:a an export passes: the chosen or preset bitrate kept inside the range the encoder
    /// accepts (see <see cref="AudioBitrateRange(string, int, int)"/>), then lowered for an
    /// audio-only size limit. A limit that leaves less than the encoder's minimum is refused
    /// before encoding instead of failing inside FFmpeg.
    /// </summary>
    private static int EncodedAudioKbps(ProbeInfo probe, ExportOptions options, AudioSettings audio, long? targetBytes, double seconds)
    {
        var kbps = Math.Clamp(AudioKbpsFor(probe, options), audio.MinKbps, audio.MaxKbps);
        if (!options.AudioOnly || targetBytes is not { } bytes) return kbps;
        kbps = TargetAudioKbps(kbps, bytes, seconds);
        if (kbps < 8) throw new MediaOperationException("This audio size limit is too small for the chosen duration.");
        if (kbps < audio.MinKbps) throw new MediaOperationException($"This size limit leaves {kbps.ToString(EngineSupport.Invariant)} kbps for the audio, but {audio.Label} needs at least {audio.MinKbps.ToString(EngineSupport.Invariant)} kbps for {ChannelText(AudioChannels(probe, options))} audio at {Kilohertz(audio.SampleRate)}. Choose a larger size, a shorter clip, or MP3 or AAC.");
        return kbps;
    }
    private static int AudioChannels(ProbeInfo probe, ExportOptions options) => options.Mono ? 1 : Math.Max(1, probe.Channels ?? 2);
    private static string ChannelText(int channels) => channels switch { 1 => "mono", 2 => "stereo", _ => channels.ToString(EngineSupport.Invariant) + "-channel" };

    /// <summary>
    /// The -b:a range (kbps) an audio encoder sets up with, measured with the bundled FFmpeg 9.0.2
    /// (0.4 s encodes at every standard rate, 8-320 kbps). LAME and the native AAC encoder took
    /// every value (they adjust it themselves). libopus refused more than 256 kbps per channel.
    /// libvorbis has a window that depends on channels and sample rate: mono stopped at 224 kbps
    /// at 44.1/48 kHz and at 80 kbps at 22.05/24 kHz, stereo at 160 kbps at 22.05/24 kHz, and
    /// 6-channel audio needed at least 96 kbps. (1, int.MaxValue) means no known limit.
    /// </summary>
    internal static (int Min, int Max) AudioBitrateRange(string codec, int channels, int sampleRate)
    {
        channels = Math.Max(1, channels);
        if (codec == "libopus") return (8, 256 * channels);
        if (codec != "libvorbis") return (1, int.MaxValue);
        return channels switch
        {
            1 => sampleRate switch { <= 8000 => (8, 40), <= 12000 => (16, 48), <= 16000 => (16, 96), <= 24000 => (16, 80), <= 32000 => (32, 160), _ => (32, 224) },
            2 => sampleRate switch { <= 8000 => (16, 80), <= 12000 => (16, 96), <= 16000 => (24, 192), <= 24000 => (32, 160), <= 32000 => (40, 320), _ => (48, 320) },
            // Three and six channels both accepted 96-256 kbps at every measured rate.
            _ => (96, 256)
        };
    }
    /// <summary>
    /// The audio bitrate range (kbps) the encoder for this export accepts, from its format, Mono
    /// and the sample rate it will use; (1, int.MaxValue) when any value works or no bitrate applies
    /// (WAV, FLAC or a copied stream). The editor offers only bitrates inside it.
    /// </summary>
    public static AudioBitrateLimits AudioBitrateLimitsFor(ProbeInfo probe, ExportOptions options)
    {
        if (options.ClipMode == ClipMode.Lossless || !probe.HasAudio) return AudioBitrateLimits.None;
        var audio = AudioSettingsFor(probe, options, options.OutputPath);
        return new AudioBitrateLimits(audio.MinKbps, audio.MaxKbps, $"{audio.Label} for {ChannelText(AudioChannels(probe, options))} audio at {Kilohertz(audio.SampleRate)}");
    }

    // How far before the selection a source with imprecise seeking is decoded.
    private static readonly TimeSpan DecodeMargin = TimeSpan.FromSeconds(15);
    /// <summary>
    /// Whether an input seek in this container lands on a keyframe at or before
    /// the selection. Checked with the bundled FFmpeg: MP4/MOV, Matroska/WebM,
    /// AVI, ASF/WMV and FLV cut on the selected frame; MPEG-TS, M2TS and MPEG-PS
    /// lost the frames before the next keyframe. Other containers FFprobe names
    /// decode a margin too.
    /// </summary>
    internal static bool SeeksToKeyframes(ProbeInfo probe)
    {
        // Every audio frame is a keyframe, so only video can lose frames.
        if (!probe.HasVideo || string.IsNullOrEmpty(probe.TechnicalJson)) return true;
        string? formatName = null;
        try
        {
            using var json = JsonDocument.Parse(probe.TechnicalJson, new JsonDocumentOptions { MaxDepth = 64 });
            if (json.RootElement.TryGetProperty("format", out var format)) formatName = EngineSupport.Text(format, "format_name");
        }
        catch (JsonException) { }
        return formatName is null || formatName.Split(',').Any(name => name is "mov" or "mp4" or "matroska" or "webm" or "avi" or "asf" or "flv");
    }

    private static (TimeSpan Start, TimeSpan Duration) ValidateExport(ProbeInfo probe, ExportOptions options, string output)
    {
        if (!Enum.IsDefined(options.ClipMode) || !Enum.IsDefined(options.AudioFormat) || !Enum.IsDefined(options.VideoCodec) || !Enum.IsDefined(options.Encoder) || !Enum.IsDefined(options.Compression)) throw new MediaOperationException("Choose supported export options.");
        var start = options.Start ?? TimeSpan.Zero;
        var end = options.End ?? probe.Duration;
        if (start < TimeSpan.Zero || end <= start || end > probe.Duration || options.AudioOnly && !probe.HasAudio) throw new MediaOperationException("Choose a valid clip inside a file with the required media tracks.");
        if (!double.IsFinite(options.Volume) || options.Volume is < 0 or > 10 || options.AudioKbps is < 8 or > 1024 || options.MaxHeight is < 64 or > 16384 || options.Fps is < 1 or > 240 || options.SampleRate is < 8000 or > 192000 || options.FadeIn < TimeSpan.Zero || options.FadeOut < TimeSpan.Zero || options.FadeIn > end - start || options.FadeOut > end - start) throw new MediaOperationException("Choose valid volume, bitrate, frame rate, sample rate and fade values.");
        if (options.Compression == CompressionPreset.TargetSize && options.TargetBytes is null || options.TargetBytes is < 1024) throw new MediaOperationException("Enter a valid target size.");
        var extension = Path.GetExtension(output).ToLowerInvariant();
        if (extension.Length == 0 || !new[] { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".mp3", ".m4a", ".aac", ".opus", ".ogg", ".flac", ".wav" }.Contains(extension)) throw new MediaOperationException("Choose a supported media filename extension.");
        if (options.ClipMode == ClipMode.Lossless && (options.Compression != CompressionPreset.Off || options.MaxHeight is not null || options.Fps is not null || options.HdrToSdr || options.Volume != 1 || options.Normalize || options.TrimSilence || options.Mono || options.SampleRate is not null || options.FadeIn > TimeSpan.Zero || options.FadeOut > TimeSpan.Zero)) throw new MediaOperationException("Lossless mode copies the source streams. Choose Exact mode for compression or audio/video changes.");
        if (options.AudioOnly && options.ClipMode == ClipMode.Exact && options.AudioFormat == AudioFormat.Original) throw new MediaOperationException("Choose an audio format for an exact audio cut, or use Lossless to preserve the original codec.");
        if (options.TrimSilence && !probe.HasAudio) throw new MediaOperationException("Trim silence needs an audio track. Turn it off for this file.");
        ValidateContainer(probe, options, extension);
        if (options.DiscordSoundboard)
        {
            ValidateSoundboard(options, end - start);
            if (extension != (options.AudioFormat == AudioFormat.Mp3 ? ".mp3" : ".ogg")) throw new MediaOperationException(SoundboardFormats);
            if (options.ClipMode == ClipMode.Lossless && probe.AudioCodec != (options.AudioFormat == AudioFormat.Mp3 ? "mp3" : "vorbis")) throw new MediaOperationException("Choose Exact mode to convert the source audio to the selected soundboard format.");
        }
        // FFmpeg ignores a bitrate for PCM and FLAC, so a size limit could only fail after a full encode.
        if (options.AudioOnly && options.ClipMode == ClipMode.Exact && options.AudioFormat is AudioFormat.Wav or AudioFormat.Flac && EffectiveTarget(options) is not null) throw new MediaOperationException(LosslessSizeLimit);
        return (start, end - start);
    }
    /// <summary>
    /// Checks the soundboard rules that need no probe or output file (limits, an audio clip within
    /// the duration, MP3 or Ogg), so the editor can refuse before its save dialog. Throws
    /// <see cref="MediaOperationException"/> with the same message the export would give.
    /// </summary>
    public static void ValidateSoundboardChoice(ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.DiscordSoundboard) return;
        if (options.Start is not { } start || options.End is not { } end || end <= start) throw new MediaOperationException("Choose a start/end inside the media duration.");
        ValidateSoundboard(options, end - start);
    }
    /// <summary>The soundboard rules that need no output file: limits, an audio clip within the duration, MP3 or Ogg.</summary>
    private static void ValidateSoundboard(ExportOptions options, TimeSpan clip)
    {
        if (!double.IsFinite(options.SoundboardMaxSeconds) || options.SoundboardMaxSeconds is <= 0 or > 300 || options.SoundboardTargetBytes is < 1024 or > 100_000_000) throw new MediaOperationException("Check the configurable soundboard duration and size limits in Settings.");
        if (!options.AudioOnly || clip.TotalSeconds > options.SoundboardMaxSeconds) throw new MediaOperationException("Soundboard export needs an audio clip within the current duration preset.");
        if (options.AudioFormat is not (AudioFormat.Mp3 or AudioFormat.Ogg)) throw new MediaOperationException(SoundboardFormats);
    }
    private static void ValidateContainer(ProbeInfo probe, ExportOptions options, string extension)
    {
        var hasVideo = probe.HasVideo && !options.AudioOnly;
        var copying = options.ClipMode == ClipMode.Lossless;
        var requestedVideo = options.VideoCodec switch { VideoCodec.H264 => "h264", VideoCodec.Hevc => "hevc", VideoCodec.Av1 => "av1", _ => probe.VideoCodec };
        var requestedAudio = options.AudioFormat switch { AudioFormat.Mp3 => "mp3", AudioFormat.Aac => "aac", AudioFormat.Opus => "opus", AudioFormat.Flac => "flac", AudioFormat.Wav => "pcm_s16le", AudioFormat.Ogg => "vorbis", _ => probe.AudioCodec };
        if (copying && (hasVideo && options.VideoCodec != VideoCodec.Original && requestedVideo != probe.VideoCodec || options.AudioOnly && options.AudioFormat != AudioFormat.Original && requestedAudio != probe.AudioCodec)) throw new MediaOperationException("Lossless mode keeps the source codecs. Choose Original, or use Exact mode to change the audio/video format.");
        var videoCodec = copying ? probe.VideoCodec : requestedVideo;
        var audioCodec = copying ? probe.AudioCodec : options.AudioOnly ? requestedAudio : extension == ".webm" ? "opus" : "aac";
        if (hasVideo && extension is ".mp3" or ".m4a" or ".aac" or ".opus" or ".flac" or ".wav") throw new MediaOperationException("That filename is an audio-only format. Select Audio Only, or choose a video container such as MP4, MKV or WebM.");
        // WebM and Ogg have restricted codec sets. MKV remains flexible, and
        // WAV/MOV/MP4 are left to their muxers for uncommon supported codecs.
        // Validate the copied codecs rather than silently changing source media.
        if (extension == ".webm" && (hasVideo && videoCodec is not ("vp8" or "vp9" or "av1") || probe.HasAudio && audioCodec is not ("opus" or "vorbis"))) throw new MediaOperationException("WebM needs VP8, VP9 or AV1 video and Opus or Vorbis audio. Choose compatible codecs with Exact mode, or use MKV to preserve these source streams.");
        if (extension == ".ogg" && (hasVideo && videoCodec is not ("theora" or "vp8") || probe.HasAudio && audioCodec is not ("vorbis" or "opus" or "flac" or "speex"))) throw new MediaOperationException("The source or selected codecs do not fit Ogg. Choose a compatible audio format with Exact mode, or use MKV to preserve these source streams.");
        var requiredAudio = extension switch { ".mp3" => "mp3", ".aac" => "aac", ".opus" => "opus", ".flac" => "flac", _ => null };
        if (requiredAudio is not null && audioCodec != requiredAudio) throw new MediaOperationException("The output filename extension does not match the audio codec. Choose the matching extension, or use Exact with Audio Only to convert it.");
        if (options.AudioOnly && !copying && extension == ".m4a" && options.AudioFormat != AudioFormat.Aac || options.AudioOnly && !copying && extension == ".wav" && options.AudioFormat != AudioFormat.Wav) throw new MediaOperationException("The output filename extension does not match the selected audio format.");
    }
    private static long? EffectiveTarget(ExportOptions options)
    {
        var target = options.Compression == CompressionPreset.TargetSize ? options.TargetBytes : null;
        return options.DiscordSoundboard ? Math.Min(target ?? long.MaxValue, options.SoundboardTargetBytes) : target;
    }
    private static IReadOnlyList<EncoderInfo> HardwareCandidates(ProbeInfo probe, ExportOptions options, IReadOnlyList<EncoderInfo> encoders)
    {
        if (options.Encoder == EncoderPreference.Cpu || options.VideoCodec == VideoCodec.Original && probe.VideoCodec is not ("h264" or "hevc" or "av1")) return [];
        var codec = EffectiveCodec(probe, options);
        // Measured on AMF: libx264 gives a clearly better picture per bit and
        // keeps to bitrate caps that h264_amf overshoots. Auto therefore uses
        // the CPU for compressed H.264 jobs of moderate size; long or large
        // jobs keep the much faster hardware encoder.
        if (options.Encoder == EncoderPreference.Auto && codec == VideoCodec.H264 && options.Compression != CompressionPreset.Off && encoders.Any(encoder => encoder.Name == "libx264") && CompressionWork(probe, options) <= 1920d * 1080 * 30 * 300) return [];
        var prefix = codec switch { VideoCodec.Hevc => "hevc", VideoCodec.Av1 => "av1", _ => "h264" };
        return new[] { "_amf", "_nvenc", "_qsv" }.Select(suffix => encoders.FirstOrDefault(encoder => encoder.Name == prefix + suffix)).OfType<EncoderInfo>().ToArray();
    }
    private static double CompressionWork(ProbeInfo probe, ExportOptions options)
    {
        double width = probe.Width ?? 1920, height = probe.Height ?? 1080;
        if (options.MaxHeight is { } maximum && maximum < height) { width *= maximum / height; height = maximum; }
        var fps = Math.Min(options.Fps ?? double.MaxValue, probe.Fps ?? 30);
        var seconds = ((options.End ?? probe.Duration) - (options.Start ?? TimeSpan.Zero)).TotalSeconds;
        return width * height * fps * seconds;
    }
    private static EncoderInfo ChooseCpu(ProbeInfo probe, ExportOptions options, IReadOnlyList<EncoderInfo> encoders)
    {
        if (options.VideoCodec == VideoCodec.Original && probe.VideoCodec is not ("h264" or "hevc" or "av1"))
        {
            var sameCodec = probe.VideoCodec switch { "vp9" => "libvpx-vp9", "vp8" => "libvpx", _ => null };
            if (sameCodec is not null && encoders.FirstOrDefault(encoder => encoder.Name == sameCodec) is { } originalEncoder) return originalEncoder;
            throw new MediaOperationException("This source codec cannot be re-encoded with the installed components. Choose H.264, HEVC or AV1, or use Lossless to keep the original streams.");
        }
        var names = EffectiveCodec(probe, options) switch { VideoCodec.Hevc => new[] { "libx265" }, VideoCodec.Av1 => new[] { "libsvtav1", "libaom-av1" }, _ => new[] { "libx264", "libopenh264" } };
        foreach (var name in names) if (encoders.FirstOrDefault(encoder => encoder.Name == name) is { } encoder) return encoder;
        throw new MediaOperationException("The installed FFmpeg build does not include a CPU encoder for this codec. Check Components or choose another codec.");
    }
    private static VideoCodec EffectiveCodec(ProbeInfo probe, ExportOptions options) => options.VideoCodec != VideoCodec.Original ? options.VideoCodec : probe.VideoCodec switch { "hevc" => VideoCodec.Hevc, "av1" => VideoCodec.Av1, _ => VideoCodec.H264 };

    /// <summary>The preset's quality value on the scale of the chosen encoder.</summary>
    internal static int EncoderQuality(string encoder, CompressionPreset compression)
    {
        // H.264 CRF/QP scale (0-51); x265 and the HEVC encoders land at similar sizes.
        var h264 = compression switch { CompressionPreset.Light => 22, CompressionPreset.Balanced => 26, CompressionPreset.Strong => 30, CompressionPreset.VerySmall => 34, _ => 18 };
        return encoder switch
        {
            // AMF AV1 QPs run 0-255. Calibrated on an RX 7900 XTX by PSNR: each
            // value matches h264_amf at the same preset (Off: 47.6 dB at QP 18)
            // while the AV1 file stays smaller.
            "av1_amf" => compression switch { CompressionPreset.Light => 72, CompressionPreset.Balanced => 100, CompressionPreset.Strong => 124, CompressionPreset.VerySmall => 144, _ => 40 },
            // AV1/VP9 CRF and NVENC AV1 CQ run 0-63.
            "libaom-av1" or "libsvtav1" or "libvpx" or "libvpx-vp9" or "av1_nvenc" => h264 + 8,
            _ => h264
        };
    }
    private static string Kilobits(int kbps) => kbps.ToString(EngineSupport.Invariant) + "k";

    private static List<string> ExportArguments(string input, string output, ProbeInfo probe, ExportOptions options, (TimeSpan Start, TimeSpan Duration) range, EncoderInfo? encoder, EncodePlan plan)
    {
        var args = new List<string> { "-hide_banner", "-v", "warning", "-nostdin", "-threads", "2" };
        var clip = range.Duration.TotalSeconds;
        // Both modes seek on the input, so FFmpeg decodes only the selection
        // (accurate_seek keeps an Exact cut frame accurate) and fades and other
        // filters see clip time starting at zero. -t stays an output option: as
        // an input option it counts from each stream's own first timestamp, so
        // audio starting 0.48 s after the video ran to 8.48 s in an 8 s clip.
        var seek = range.Start;
        TimeSpan? cut = null;
        if (options.ClipMode == ClipMode.Exact && range.Start > TimeSpan.Zero && !SeeksToKeyframes(probe))
        {
            // MPEG-TS and MPEG-PS seeks land between keyframes, and the frames up
            // to the next keyframe were lost (a cut at 5.5 s began at 6 s). Decode
            // from earlier and make the cut in the filters instead.
            seek = range.Start > DecodeMargin ? range.Start - DecodeMargin : TimeSpan.Zero;
            cut = range.Start - seek;
        }
        if (seek > TimeSpan.Zero) args.AddRange(["-ss", EngineSupport.Number(seek.TotalSeconds)]);
        args.AddRange(["-i", input, "-t", EngineSupport.Number(clip)]);
        // The cut in the filters: PTS-P/TB, not STARTPTS, keeps streams that
        // start at different times aligned.
        string Cut(string trim, string setpts, TimeSpan offset)
        {
            var from = EngineSupport.Number(offset.TotalSeconds);
            return $"{trim}=start={from}:end={EngineSupport.Number(offset.TotalSeconds + clip)},{setpts}=PTS-{from}/TB";
        }
        var carry = plan.Carry;
        args.AddRange(["-map_metadata", "0", "-map_chapters", carry.Chapters ? "0" : "-1"]);
        if (options.AudioOnly) args.AddRange(["-map", "0:a:0", "-vn"]);
        // The probed main video by index: 0:v:0 is a cover picture when that comes first.
        else args.AddRange(["-map", probe.HasVideo && VideoStream(probe) is { } main && EngineSupport.Integer(main, "index") is >= 0 and var index ? "0:" + index.ToString(EngineSupport.Invariant) : "0:v:0?", "-map", "0:a:0?"]);
        // A mapped cover is the second output video stream (v:1).
        var mappedCover = carry.Cover is { Attach: false } picture ? picture : null;
        if (mappedCover is not null) args.AddRange(["-map", "0:" + mappedCover.Index.ToString(EngineSupport.Invariant)]);
        if (carry.Attachments > 0) args.AddRange(["-map", "0:t?"]);
        if (options.ClipMode == ClipMode.Lossless)
        {
            args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
            if (mappedCover is not null) args.AddRange(["-disposition:v:1", "attached_pic"]);
            // The input seek lands on the previous video keyframe, which can be
            // seconds early. Every audio frame is a keyframe, so an audio-only
            // copy drops the packets before the selected start.
            if (options.AudioOnly) args.AddRange(["-copypriorss", "0"]);
        }
        else
        {
            if (!options.AudioOnly && probe.HasVideo)
            {
                var selected = encoder ?? throw new MediaOperationException("Choose a video encoder.");
                var name = selected.Name;
                args.AddRange(["-c:v", name]);
                var quality = EncoderQuality(name, options.Compression).ToString(EngineSupport.Invariant);
                var cap = plan.CapKbps;
                if (plan.TargetKbps is { } kbps) args.AddRange(["-b:v", Kilobits(kbps)]);
                else if (name.EndsWith("_amf", StringComparison.Ordinal))
                {
                    // AMF QVBR hung on the tested driver; peak-constrained VBR is reliable.
                    if (cap is { } peak) args.AddRange(["-quality", "quality", "-rc", "vbr_peak", "-b:v", Kilobits(Math.Max(32, peak * 85 / 100)), "-maxrate", Kilobits(peak), "-bufsize", Kilobits(peak * 2)]);
                    else
                    {
                        args.AddRange(["-quality", "speed", "-rc", "cqp", "-qp_i", quality, "-qp_p", quality]);
                        if (name != "hevc_amf") args.AddRange(["-qp_b", quality]);
                    }
                }
                else if (name.EndsWith("_nvenc", StringComparison.Ordinal))
                {
                    // -b:v 0 keeps the default 2 Mbit/s target from capping constant quality.
                    args.AddRange(["-preset", "p4", "-rc", "vbr", "-cq", quality, "-b:v", "0"]);
                    if (cap is { } peak) args.AddRange(["-maxrate", Kilobits(peak), "-bufsize", Kilobits(peak * 2)]);
                }
                else if (name.EndsWith("_qsv", StringComparison.Ordinal))
                {
                    if (cap is { } peak) args.AddRange(["-b:v", Kilobits(Math.Max(32, peak * 85 / 100)), "-maxrate", Kilobits(peak)]);
                    else args.AddRange(["-global_quality", quality]);
                }
                else if (name == "libopenh264") args.AddRange(["-b:v", Kilobits(cap ?? 4000)]);
                else
                {
                    args.AddRange(["-crf", quality]);
                    if (name is "libvpx" or "libvpx-vp9") args.AddRange(["-b:v", cap is { } peak ? Kilobits(peak) : "0", "-deadline", "good", "-cpu-used", "4"]);
                    else if (name == "libaom-av1") { if (cap is { } peak) args.AddRange(["-b:v", Kilobits(peak)]); }
                    else if (cap is { } peak) args.AddRange(["-maxrate", Kilobits(peak), "-bufsize", Kilobits(peak)]);
                }
                if (name == "libx264") args.AddRange(["-preset", "fast"]);
                if (name == "libx265") args.AddRange(["-preset", "fast", "-x265-params", "pools=2:frame-threads=2"]);
                if (name == "libsvtav1") args.AddRange(["-preset", "10", "-svtav1-params", "lp=2"]);
                // Good-quality libaom ran at 0.05x realtime here; realtime usage
                // with row threading ran faster than realtime at a modestly higher bitrate.
                if (name == "libaom-av1") args.AddRange(["-usage", "realtime", "-cpu-used", "7", "-row-mt", "1", "-tiles", "2x2"]);
                args.AddRange(["-pix_fmt", "yuv420p"]);
                var filters = new List<string>();
                if (cut is { } videoCut) filters.Add(Cut("trim", "setpts", videoCut));
                if (options.Fps is { } fps) filters.Add("fps=" + fps.ToString(EngineSupport.Invariant));
                // Scale before tone mapping so the float conversion works on fewer pixels.
                if (options.MaxHeight is { } height) filters.Add($"scale=-2:trunc(min(ih\\,{height})/2)*2");
                if (plan.Hdr is { } hdr) filters.Add($"zscale=tin={hdr.Transfer}:pin={hdr.Primaries}:min={hdr.Matrix}:rin={hdr.Range}:t=linear:npl=100:p={hdr.Primaries},format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
                // FFmpeg refuses a filter on a stream-copied cover, so it then targets the main video only.
                if (filters.Count > 0) args.AddRange([mappedCover is null ? "-vf" : "-filter:v:0", string.Join(',', filters)]);
                if (plan.Hdr is not null) args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
                // The later, more specific codec option wins for the cover.
                if (mappedCover is not null) args.AddRange(["-c:v:1", "copy", "-disposition:v:1", "attached_pic"]);
            }
            if (carry.Attachments > 0) args.AddRange(["-c:t", "copy"]);
            if (probe.HasAudio && carry.CopyAudio) args.AddRange(["-c:a", "copy"]);
            else if (probe.HasAudio)
            {
                var audio = plan.Audio ?? AudioSettingsFor(probe, options, output);
                args.AddRange(["-c:a", audio.Codec]);
                if (audio.Codec is not ("flac" or "pcm_s16le")) args.AddRange(["-b:a", Kilobits(EncodedAudioKbps(probe, options, audio, plan.TargetBytes, clip))]);
                if (options.Mono) args.AddRange(["-ac", "1"]);
                // Always explicit: loudnorm otherwise upsamples to 192 kHz, which
                // bloats WAV/FLAC and breaks Vorbis, and Opus accepts only 48 kHz-family rates.
                var rate = audio.SampleRate.ToString(EngineSupport.Invariant);
                args.AddRange(["-ar", rate]);
                // One continuous sample clock from the start of the clip: audio that
                // starts after the video, or skips dropped packets, is padded with
                // silence instead of moving earlier. Without it an MP3 cut and a
                // normalized clip lost a 0.48 s lead-in, so the sound came 0.48 s early.
                var audioFilters = new List<string> { "aresample=async=1:first_pts=0" };
                if (cut is { } audioCut) audioFilters.Add(Cut("atrim", "asetpts", audioCut));
                if (options.Volume != 1) audioFilters.Add("volume=" + EngineSupport.Number(options.Volume));
                // loudnorm runs at 192 kHz in 100 ms frames and shifts timestamps:
                // a fade-out then ended 0.07 s early, and the output ran up to 0.1 s
                // long. A clean sample clock and a sample-exact end fix both.
                if (options.Normalize) audioFilters.AddRange(["loudnorm=I=-16:TP=-1.5:LRA=11", "aresample=" + rate, "asetpts=N/SR/TB"]);
                var fadeIn = Math.Min(options.FadeIn.TotalSeconds, clip);
                var fadeOut = Math.Min(options.FadeOut.TotalSeconds, clip);
                if (fadeIn > 0) audioFilters.Add("afade=t=in:st=0:d=" + EngineSupport.Number(fadeIn));
                if (fadeOut > 0) audioFilters.Add("afade=t=out:st=" + EngineSupport.Number(clip - fadeOut) + ":d=" + EngineSupport.Number(fadeOut));
                if (options.Normalize) audioFilters.Add("atrim=end_sample=" + Math.Round(clip * audio.SampleRate).ToString(EngineSupport.Invariant));
                args.AddRange(["-af", string.Join(',', audioFilters)]);
            }
        }
        if (carry.CoverFile is { } coverFile && carry.Cover is { } cover)
        {
            // -attach streams follow the mapped attachments in output order.
            var stream = "-metadata:s:t:" + carry.Attachments.ToString(EngineSupport.Invariant);
            args.AddRange(["-attach", coverFile, stream, cover.Codec == "png" ? "mimetype=image/png" : "mimetype=image/jpeg", stream, "filename=" + Path.GetFileName(coverFile)]);
        }
        if (UsesFaststart(output)) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-filter_threads", "1", "-threads", "2", "-progress", "pipe:1", "-nostats", "-n", output]);
        return args;
    }
    private static bool UsesFaststart(string output) => Path.GetExtension(output).ToLowerInvariant() is ".mp4" or ".m4a";
    private static string AudioCodecFor(ExportOptions options, string output) => options.AudioOnly
        ? options.AudioFormat switch { AudioFormat.Mp3 => "libmp3lame", AudioFormat.Aac => "aac", AudioFormat.Opus => "libopus", AudioFormat.Flac => "flac", AudioFormat.Wav => "pcm_s16le", AudioFormat.Ogg => "libvorbis", _ => "aac" }
        : Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "libopus" : "aac";
    private static AudioSettings AudioSettingsFor(ProbeInfo probe, ExportOptions options, string output)
    {
        var codec = AudioCodecFor(options, output);
        var rate = SupportedSampleRate(codec, options.SampleRate ?? (probe.SampleRate is > 0 and var source ? source : 48000));
        var label = codec switch { "libopus" => "Opus", "libmp3lame" => "MP3", "libvorbis" => "Ogg Vorbis", "flac" => "FLAC", "pcm_s16le" => "WAV", _ => "AAC" };
        var note = options.SampleRate is { } requested && requested != rate ? $"{label} does not support {Kilohertz(requested)}, so the audio was exported at {Kilohertz(rate)}." : null;
        if (codec is "flac" or "pcm_s16le") return new AudioSettings(codec, rate, note, label, 1, int.MaxValue, null);
        // A bitrate the encoder cannot set up with failed the whole export with "FFmpeg could not
        // process this file"; it is moved into the encoder's range, and the note says so.
        var channels = AudioChannels(probe, options);
        var (min, max) = AudioBitrateRange(codec, channels, rate);
        var wanted = AudioKbpsFor(probe, options);
        var bitrateNote = wanted > max ? $"{label} accepts at most {max.ToString(EngineSupport.Invariant)} kbps for {ChannelText(channels)} audio at {Kilohertz(rate)}, so the audio was exported at {max.ToString(EngineSupport.Invariant)} kbps."
            : wanted < min ? $"{label} needs at least {min.ToString(EngineSupport.Invariant)} kbps for {ChannelText(channels)} audio at {Kilohertz(rate)}, so the audio was exported at {min.ToString(EngineSupport.Invariant)} kbps."
            : null;
        return new AudioSettings(codec, rate, note, label, min, max, bitrateNote);
    }
    private static string Kilohertz(int rate) => (rate / 1000d).ToString("0.###", EngineSupport.Invariant) + " kHz";
    internal static int SupportedSampleRate(string codec, int rate)
    {
        if (codec is "flac" or "pcm_s16le") return rate;
        if (codec == "libopus") return rate is 8000 or 12000 or 16000 or 24000 or 48000 ? rate : 48000;
        // MP3, AAC and Vorbis: the standard rates up to 48 kHz. Higher rates
        // map to the matching 44.1 kHz or 48 kHz family.
        if (rate > 48000) return rate % 11025 == 0 ? 44100 : 48000;
        int[] standard = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000];
        return standard.First(value => value >= rate);
    }

    internal static long? ProgressMicroseconds(string line)
    {
        if (line.Length > 200 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return null;
        return long.TryParse(line.AsSpan(12), NumberStyles.Integer, EngineSupport.Invariant, out var microseconds) && microseconds >= 0 ? microseconds : null;
    }
    internal static JobProgress? ParseProgress(string line, TimeSpan duration, JobStage stage)
    {
        if (ProgressMicroseconds(line) is not { } microseconds || duration <= TimeSpan.Zero) return null;
        var percent = Math.Clamp(microseconds / 1_000_000d / duration.TotalSeconds * 100, 0, 99.9);
        return new JobProgress(stage, percent, "Processing media");
    }
    private static double? Fraction(string? value)
    {
        if (value is null) return null;
        var parts = value.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, EngineSupport.Invariant, out var numerator)) return null;
        var divisor = parts.Length == 1 ? 1 : parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, EngineSupport.Invariant, out var denominator) ? denominator : 0;
        var rate = numerator / divisor;
        return double.IsFinite(rate) && rate > 0 ? rate : null;
    }
    internal static IReadOnlyList<float> ReadWaveform(string path, int points, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 17_000_000 || stream.Length < 44) throw new MediaOperationException("The waveform preview was incomplete or too large.");
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new MediaOperationException("The waveform engine returned an unsupported audio file.");
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new MediaOperationException("The waveform engine returned an unsupported audio file.");
        var formatVerified = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end > stream.Length) throw new MediaOperationException("The waveform audio was truncated.");
            if (chunk == "fmt ")
            {
                if (size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1) throw new MediaOperationException("The waveform audio was not mono PCM.");
                reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt16();
                if (reader.ReadUInt16() != 16) throw new MediaOperationException("The waveform audio was not 16-bit PCM.");
                formatVerified = true;
            }
            else if (chunk == "data")
            {
                if (!formatVerified || size == 0 || (size & 1) != 0) throw new MediaOperationException("The waveform audio had no valid samples.");
                // One bulk read (bounded to 17 MB above) instead of a call per sample.
                var bytes = new byte[size];
                stream.ReadExactly(bytes);
                var pcm = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
                var samples = pcm.Length;
                var count = Math.Min(points, samples);
                var peaks = new float[count];
                for (var bucket = 0; bucket < count; bucket++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Sample s belongs to bucket floor(s * count / samples).
                    var first = (int)(((long)bucket * samples + count - 1) / count);
                    var last = (int)(((long)(bucket + 1) * samples + count - 1) / count);
                    var peak = 0;
                    for (var sample = first; sample < last; sample++)
                    {
                        var value = BitConverter.IsLittleEndian ? pcm[sample] : BinaryPrimitives.ReverseEndianness(pcm[sample]);
                        peak = Math.Max(peak, Math.Abs((int)value));
                    }
                    peaks[bucket] = peak / 32768f;
                }
                return peaks;
            }
            stream.Position = end + (size & 1);
        }
        throw new MediaOperationException("The waveform audio contained no sample data.");
    }
    private enum RunOutcome { Completed, Stalled, OverDeadline }
    private sealed record HdrSignal(string Transfer, string Primaries, string Matrix, string Range);
    private sealed record AudioSettings(string Codec, int SampleRate, string? Note, string Label, int MinKbps, int MaxKbps, string? BitrateNote);
    private sealed record EncodePlan(int? TargetKbps, int? CapKbps, long? TargetBytes, AudioSettings? Audio, HdrSignal? Hdr, CarryPlan Carry);
    private sealed record CoverPicture(int Index, string Codec, bool Attach);
    private sealed record CarryPlan(bool Chapters, CoverPicture? Cover, int Attachments, bool CopyAudio, string? CoverFile = null)
    {
        public static readonly CarryPlan None = new(false, null, 0, false);
    }
    [GeneratedRegex(@"^\s*[VAS][A-Z.]{5}\s+(\S+)\s+(.+)$")] private static partial Regex EncoderLine();
    [GeneratedRegex(@"\bsilence_(start|end):\s*(-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)")] private static partial Regex SilenceEvent();
}
