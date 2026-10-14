using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure.Sharing;

namespace Klippi.Infrastructure.Engines;

/// <summary>Makes the browser-playable copy a watch link uploads.</summary>
public interface IWatchMediaPreparer
{
    Task<WatchCopy> PrepareWatchCopyAsync(string path, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A fragmented MP4 (H.264/AAC) copy of a file in an app-owned temporary folder, with its probe and fragment index.
/// Disposing it deletes the copy. The original file is only read.
/// </summary>
public sealed class WatchCopy(string filePath, ProbeInfo probe, Fmp4Layout layout, bool reencoded, Action cleanup) : IDisposable
{
    private Action? _cleanup = cleanup;
    public string FilePath { get; } = filePath;
    public ProbeInfo Probe { get; } = probe;
    public Fmp4Layout Layout { get; } = layout;
    /// <summary>True when the video or audio was re-encoded; false when the streams were only repackaged.</summary>
    public bool Reencoded { get; } = reencoded;
    public void Dispose() => Interlocked.Exchange(ref _cleanup, null)?.Invoke();
}

public sealed partial class MediaService : IWatchMediaPreparer
{
    // Longest gap between seek points (and largest fragment) a repackaged copy may have before it is re-encoded instead, so
    // the browser never has to fetch a huge fragment to start playing after a seek.
    internal const double WatchMaximumFragmentSeconds = 12;
    internal const long WatchMaximumFragmentBytes = 48L * 1024 * 1024;

    /// <summary>
    /// Writes a fragmented MP4 that browsers can stream with Media Source Extensions. H.264 (8-bit 4:2:0) video and AAC audio
    /// are repackaged without re-encoding; anything else is converted to H.264/AAC. Audio-only files become AAC in MP4.
    /// Metadata, chapters, subtitles and attachments are left out of the copy.
    /// </summary>
    public async Task<WatchCopy> PrepareWatchCopyAsync(string path, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var input = EngineSupport.Input(path);
        var probe = RecentProbe(input) ?? await ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        var ffmpeg = EngineSupport.Required(components, "ffmpeg");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KeaClip");
        var workspace = EngineSupport.CreateWorkspace(temporaryRoot, temporaryWorkspaces);
        void Cleanup() { try { EngineSupport.Cleanup(workspace, temporaryRoot); } finally { workspace.Dispose(); } }
        try
        {
            var output = Path.Combine(workspace.Path, "watch.mp4");
            workspace.RegisterPayload(Path.GetFileName(output));
            var (copyVideo, copyAudio) = WatchStreamsPlayable(probe);
            string? videoEncoder = null;
            if (probe.HasVideo && !copyVideo) videoEncoder = await WatchVideoEncoderAsync(cancellationToken).ConfigureAwait(false);
            await RunWatchCopyAsync(ffmpeg, input, output, workspace.Path, probe, videoEncoder, copyAudio, progress, cancellationToken).ConfigureAwait(false);
            var layout = ReadWatchLayout(output);
            bool reencoded = !copyVideo || !copyAudio;
            // A repackaged source with very long gaps between keyframes would make seeking fetch huge fragments: convert it instead.
            if (probe.HasVideo && copyVideo && (layout.LongestFragmentSeconds(probe.Duration.TotalSeconds) > WatchMaximumFragmentSeconds || layout.LargestFragmentBytes > WatchMaximumFragmentBytes))
            {
                File.Delete(output);
                videoEncoder = await WatchVideoEncoderAsync(cancellationToken).ConfigureAwait(false);
                await RunWatchCopyAsync(ffmpeg, input, output, workspace.Path, probe, videoEncoder, copyAudio, progress, cancellationToken).ConfigureAwait(false);
                layout = ReadWatchLayout(output);
                reencoded = true;
            }
            var copy = await ProbeAsync(output, cancellationToken).ConfigureAwait(false);
            if (copy.HasVideo != probe.HasVideo || copy.HasAudio != probe.HasAudio || layout.HasVideo != probe.HasVideo || layout.HasAudio != probe.HasAudio
                || Math.Abs((copy.Duration - probe.Duration).TotalSeconds) > Math.Max(2, probe.Duration.TotalSeconds * 0.02))
                throw new MediaOperationException("The browser copy did not match the original.", "No watch link was created. The original file was not changed.");
            progress?.Report(new JobProgress(JobStage.Converting, 100, "Browser copy ready"));
            return new WatchCopy(output, copy, layout, reencoded, Cleanup);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>Whether the video and audio streams can be repackaged as they are (browsers play 8-bit 4:2:0 H.264 and AAC).</summary>
    internal static (bool CopyVideo, bool CopyAudio) WatchStreamsPlayable(ProbeInfo probe)
    {
        bool video = !probe.HasVideo, audio = !probe.HasAudio || probe.AudioCodec == "aac";
        if (probe.HasVideo && probe.VideoCodec == "h264" && probe.TechnicalJson is { } json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
                {
                    if (EngineSupport.Text(stream, "codec_type") != "video" || IsAttachedPicture(stream)) continue;
                    var pixelFormat = EngineSupport.Text(stream, "pix_fmt");
                    var profile = EngineSupport.Text(stream, "profile") ?? "";
                    video = pixelFormat is "yuv420p" or "yuvj420p" && !profile.Contains("10", StringComparison.Ordinal) && !profile.Contains("4:2:2", StringComparison.Ordinal) && !profile.Contains("4:4:4", StringComparison.Ordinal);
                    break;
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { video = false; }
        }
        return (video, audio);
    }

    /// <summary>FFmpeg arguments for the watch copy. A null <paramref name="videoEncoder"/> repackages the video.</summary>
    internal static List<string> WatchCopyArguments(string input, string output, ProbeInfo probe, string? videoEncoder, bool copyAudio)
    {
        var arguments = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-progress", "pipe:1", "-nostats", "-i", input };
        if (probe.HasVideo) arguments.AddRange(["-map", "0:V:0"]);
        if (probe.HasAudio) arguments.AddRange(["-map", "0:a:0"]);
        arguments.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);
        if (probe.HasVideo)
        {
            if (videoEncoder is null) arguments.AddRange(["-c:v", "copy"]);
            else
            {
                arguments.AddRange(["-c:v", videoEncoder, "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"]);
                // A keyframe every 2 seconds gives the player a seek point (and fragment) every 2 seconds.
                arguments.AddRange(["-force_key_frames", "expr:gte(t,n_forced*2)"]);
                if (videoEncoder == "libx264") arguments.AddRange(["-preset", "veryfast", "-crf", "21", "-profile:v", "high"]);
                else arguments.AddRange(["-b:v", WatchVideoKbps(probe).ToString(EngineSupport.Invariant) + "k"]);
            }
        }
        if (probe.HasAudio)
        {
            if (copyAudio) arguments.AddRange(["-c:a", "copy"]);
            else arguments.AddRange(["-c:a", "aac", "-b:a", "160k", "-ac", "2"]);
        }
        // Video fragments start at keyframes and last at least 2 s; audio-only files (every frame is a sync sample) use 2 s fragments.
        arguments.AddRange(probe.HasVideo
            ? ["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-min_frag_duration", "2000000"]
            : ["-movflags", "+empty_moov+default_base_moof", "-frag_duration", "2000000"]);
        arguments.AddRange(["-f", "mp4", "-n", output]);
        return arguments;
    }

    // Bitrate for encoders without a quality mode (OpenH264): about 0.1 bit per pixel per frame, between 1 and 12 Mbit/s.
    private static int WatchVideoKbps(ProbeInfo probe)
        => (int)Math.Clamp((probe.Width ?? 1280) * (double)(probe.Height ?? 720) * (probe.Fps ?? 30) * 0.1 / 1000, 1000, 12000);

    private async Task<string> WatchVideoEncoderAsync(CancellationToken cancellationToken)
    {
        var encoders = await EncodersAsync(cancellationToken).ConfigureAwait(false);
        return encoders.FirstOrDefault(e => e.Name == "libx264")?.Name ?? encoders.FirstOrDefault(e => e.Name == "libopenh264")?.Name
            ?? throw new MediaOperationException("This FFmpeg build has no H.264 encoder for the browser copy.", "Repair the components in Settings, then try again.");
    }

    private async Task RunWatchCopyAsync(string ffmpeg, string input, string output, string staging, ProbeInfo probe, string? videoEncoder, bool copyAudio, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(output)) File.Delete(output);
        var arguments = WatchCopyArguments(input, output, probe, videoEncoder, copyAudio);
        bool encodes = videoEncoder is not null || (probe.HasAudio && !copyAudio);
        // Repackaging reads and writes the file once; encoding is usually faster than real time at "veryfast".
        var deadline = TimeSpan.FromMinutes(Math.Min(24 * 60, 10 + probe.Duration.TotalMinutes * (encodes ? 4 : 0.5)));
        progress?.Report(new JobProgress(JobStage.Converting, 0, encodes ? "Converting for browsers" : "Preparing the browser copy"));
        void Line(string line)
        {
            if (ParseProgress(line, probe.Duration, JobStage.Converting) is { } update) progress?.Report(update with { Message = encodes ? "Converting for browsers" : "Preparing the browser copy" });
        }
        var result = await runner.RunAsync(new ProcessRequest(ffmpeg, arguments, staging, deadline, 1_048_576), Line, cancellationToken: cancellationToken).ConfigureAwait(false);
        EngineSupport.Success(result, "Browser copy");
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new MediaOperationException("The engine did not create the browser copy.");
    }

    private static Fmp4Layout ReadWatchLayout(string output)
    {
        using var file = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.RandomAccess);
        try { return Fmp4Index.Read(file); }
        catch (InvalidDataException) { throw new MediaOperationException("The browser copy could not be prepared for streaming.", "No watch link was created. The original file was not changed."); }
    }
}
