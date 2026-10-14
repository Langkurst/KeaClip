using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// A download that has to be converted (codec, HDR → SDR, frame rate) or put in the
// chosen container is exported again by MediaService. Without the extras request
// that export dropped the chapters, the cover and Matroska attachments yt-dlp had
// embedded, and re-encoded audio the target container could hold as it was.
public sealed class DownloadExtrasTests
{
    [Theory]
    [InlineData("codec")]
    [InlineData("hdr")]
    [InlineData("fps")]
    public async Task Conversion_to_mp4_keeps_chapters_and_the_cover_and_copies_the_audio(string reason)
    {
        using var files = new EngineTestFiles();
        var engine = ExtrasEngine.ForConversion(reason, ".mkv");
        var progress = new LockedProgress();
        var result = await engine.Service().DownloadAsync(engine.Options(files, MediaContainer.Mp4, reason), progress);
        Assert.Equal(".mp4", Path.GetExtension(result.FilePath));
        var export = Assert.Single(engine.Exports);
        Assert.Contains("-c:v", export.Arguments);
        Assert.Equal("0", ExtrasEngine.Argument(export, "-map_chapters"));
        // MP4 keeps the picture as a stream marked as the cover, copied byte for byte.
        Assert.Equal("0:2", ExtrasEngine.Argument(export, "-map", 3));
        Assert.Equal("copy", ExtrasEngine.Argument(export, "-c:v:1"));
        Assert.Equal("attached_pic", ExtrasEngine.Argument(export, "-disposition:v:1"));
        // Only the video was asked to change; MP4 holds AAC, so it is not re-encoded.
        Assert.Equal("copy", ExtrasEngine.Argument(export, "-c:a"));
        Assert.DoesNotContain("-b:a", export.Arguments);
        // MP4 cannot hold Matroska attachments.
        Assert.DoesNotContain("0:t?", export.Arguments);
        Assert.DoesNotContain("-attach", export.Arguments);
        Assert.Empty(engine.CoverExtractions);
        // The cover arrived, so the completion message does not claim it was lost.
        Assert.DoesNotContain(ExtrasEngine.CoverLostNote, progress.Completion);
        engine.AssertOnlyOutput(files, result.FilePath);
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("hdr")]
    [InlineData("fps")]
    public async Task Conversion_to_mkv_keeps_chapters_attachments_and_the_cover_as_an_attachment(string reason)
    {
        using var files = new EngineTestFiles();
        var engine = ExtrasEngine.ForConversion(reason, ".mkv");
        var progress = new LockedProgress();
        var result = await engine.Service().DownloadAsync(engine.Options(files, MediaContainer.Mkv, reason), progress);
        Assert.Equal(".mkv", Path.GetExtension(result.FilePath));
        var extraction = Assert.Single(engine.CoverExtractions);
        Assert.Equal("0:2", ExtrasEngine.Argument(extraction, "-map"));
        Assert.Equal("copy", ExtrasEngine.Argument(extraction, "-c"));
        var export = Assert.Single(engine.Exports);
        Assert.Contains("-c:v", export.Arguments);
        Assert.Equal("0", ExtrasEngine.Argument(export, "-map_chapters"));
        // The source's own attachments are copied, and the cover is attached again
        // instead of becoming a one-frame video track.
        Assert.Contains("0:t?", export.Arguments);
        Assert.Equal("copy", ExtrasEngine.Argument(export, "-c:t"));
        Assert.Equal(extraction.Arguments[^1], ExtrasEngine.Argument(export, "-attach"));
        Assert.DoesNotContain("0:2", export.Arguments);
        Assert.Equal("copy", ExtrasEngine.Argument(export, "-c:a"));
        Assert.DoesNotContain(ExtrasEngine.CoverLostNote, progress.Completion);
        engine.AssertOnlyOutput(files, result.FilePath);
    }

    [Theory]
    // The audio-misfit case: the AV1 video already matches, but WebM cannot hold
    // AAC, so the conversion runs for the audio and must still convert it.
    [InlineData("av1", "aac", "libopus")]
    [InlineData("vp9", "aac", "libopus")]
    // Opus fits WebM, so a video-only change copies it.
    [InlineData("vp9", "opus", "copy")]
    public async Task WebM_conversion_keeps_chapters_and_converts_only_audio_WebM_cannot_hold(string video, string audio, string expectedAudio)
    {
        using var files = new EngineTestFiles();
        var engine = new ExtrasEngine { SourceExtension = ".mkv", VideoCodec = video, AudioCodec = audio };
        var result = await engine.Service().DownloadAsync(new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = files.Root, Container = MediaContainer.WebM, VideoCodec = VideoCodec.Av1 });
        Assert.Equal(".webm", Path.GetExtension(result.FilePath));
        var export = Assert.Single(engine.Exports);
        Assert.Equal("libsvtav1", ExtrasEngine.Argument(export, "-c:v"));
        Assert.Equal("0", ExtrasEngine.Argument(export, "-map_chapters"));
        Assert.Equal(expectedAudio, ExtrasEngine.Argument(export, "-c:a"));
        Assert.Equal(expectedAudio == "libopus", export.Arguments.Contains("-b:a"));
        // WebM holds neither a cover nor attachments.
        Assert.DoesNotContain("0:2", export.Arguments);
        Assert.DoesNotContain("0:t?", export.Arguments);
        Assert.DoesNotContain("-attach", export.Arguments);
        Assert.Empty(engine.CoverExtractions);
        engine.AssertOnlyOutput(files, result.FilePath);
    }

    [Theory]
    [InlineData(MediaContainer.Mp4, ".mkv")]
    [InlineData(MediaContainer.Mkv, ".mp4")]
    [InlineData(MediaContainer.WebM, ".mkv")]
    public async Task Container_remux_keeps_chapters_cover_and_attachments_without_reencoding(MediaContainer container, string sourceExtension)
    {
        using var files = new EngineTestFiles();
        var webm = container == MediaContainer.WebM;
        var engine = new ExtrasEngine { SourceExtension = sourceExtension, VideoCodec = webm ? "vp9" : "h264", AudioCodec = webm ? "opus" : "aac" };
        var result = await engine.Service().DownloadAsync(new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = files.Root, Container = container });
        Assert.Equal(container switch { MediaContainer.Mp4 => ".mp4", MediaContainer.Mkv => ".mkv", _ => ".webm" }, Path.GetExtension(result.FilePath));
        var export = Assert.Single(engine.Exports);
        Assert.Equal("copy", ExtrasEngine.Argument(export, "-c"));
        Assert.DoesNotContain("-c:v", export.Arguments);
        Assert.DoesNotContain("-c:a", export.Arguments);
        Assert.Equal("0", ExtrasEngine.Argument(export, "-map_chapters"));
        switch (container)
        {
            case MediaContainer.Mp4:
                Assert.Equal("0:2", ExtrasEngine.Argument(export, "-map", 3));
                Assert.Equal("attached_pic", ExtrasEngine.Argument(export, "-disposition:v:1"));
                Assert.DoesNotContain("0:t?", export.Arguments);
                Assert.Empty(engine.CoverExtractions);
                break;
            case MediaContainer.Mkv:
                // An MP4 cover (behind the chapter text track) becomes a Matroska attachment.
                Assert.Equal("0:3", ExtrasEngine.Argument(Assert.Single(engine.CoverExtractions), "-map"));
                Assert.Equal(engine.CoverExtractions[0].Arguments[^1], ExtrasEngine.Argument(export, "-attach"));
                Assert.DoesNotContain("0:3", export.Arguments);
                break;
            default:
                Assert.DoesNotContain("0:2", export.Arguments);
                Assert.DoesNotContain("0:t?", export.Arguments);
                Assert.DoesNotContain("-attach", export.Arguments);
                Assert.Empty(engine.CoverExtractions);
                break;
        }
        engine.AssertOnlyOutput(files, result.FilePath);
    }

    [Fact]
    public async Task A_cover_that_cannot_be_carried_over_is_named_when_the_download_completes()
    {
        using var files = new EngineTestFiles();
        var engine = new ExtrasEngine { SourceExtension = ".mp4", FailCoverExtraction = true };
        var progress = new LockedProgress();
        var result = await engine.Service().DownloadAsync(new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = files.Root, Container = MediaContainer.Mkv }, progress);
        Assert.Single(engine.CoverExtractions);
        var export = Assert.Single(engine.Exports);
        Assert.Equal("0", ExtrasEngine.Argument(export, "-map_chapters"));
        Assert.DoesNotContain("-attach", export.Arguments);
        // The download still succeeds, but does not hide that the cover was lost, and
        // names it once although both the export and the download notice it.
        Assert.Equal(1, ExtrasEngine.Occurrences(progress.Completion, ExtrasEngine.CoverLostNote));
        engine.AssertOnlyOutput(files, result.FilePath);
    }

    [Theory]
    // Every video download that embeds a cover asks yt-dlp for a PNG instead of a WebP picture.
    [InlineData(DownloadMode.VideoAudio, MediaContainer.Auto, VideoCodec.Original, true, true, true)]
    [InlineData(DownloadMode.VideoAudio, MediaContainer.Mp4, VideoCodec.H264, true, true, true)]
    [InlineData(DownloadMode.VideoAudio, MediaContainer.Mkv, VideoCodec.Original, true, true, true)]
    [InlineData(DownloadMode.VideoOnly, MediaContainer.Auto, VideoCodec.Original, true, true, true)]
    // Audio files are not converted afterwards, WebM holds no cover, and a download
    // without a cover, or an archive, embeds nothing.
    [InlineData(DownloadMode.Audio, MediaContainer.Auto, VideoCodec.Original, true, true, false)]
    [InlineData(DownloadMode.VideoAudio, MediaContainer.WebM, VideoCodec.Original, true, false, false)]
    [InlineData(DownloadMode.VideoAudio, MediaContainer.Auto, VideoCodec.Original, false, false, false)]
    [InlineData(DownloadMode.Archive, MediaContainer.Auto, VideoCodec.Original, true, false, false)]
    public async Task A_WebP_thumbnail_is_embedded_as_PNG_in_video_downloads(DownloadMode mode, MediaContainer container, VideoCodec codec, bool cover, bool embedded, bool converted)
    {
        using var files = new EngineTestFiles();
        var engine = new ExtrasEngine { VideoCodec = "vp9", AudioCodec = "opus" };
        // Only the transfer's arguments matter here; the scripted media suits video modes only.
        await Record.ExceptionAsync(() => engine.Service().DownloadAsync(new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = files.Root, Mode = mode, Container = container, VideoCodec = codec, EmbedThumbnail = cover }));
        var transfer = Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal(embedded, transfer.Arguments.Contains("--embed-thumbnail"));
        var arguments = transfer.Arguments.ToList();
        Assert.Equal(converted ? "webp>png" : null, arguments.Contains("--convert-thumbnails") ? ExtrasEngine.Argument(transfer, "--convert-thumbnails") : null);
        // An option, not part of the address after "--".
        if (converted) Assert.True(arguments.IndexOf("--convert-thumbnails") < arguments.IndexOf("--"));
    }

    [Theory]
    // yt-dlp attached a WebP thumbnail to MKV as it is: FFprobe shows an image
    // attachment, not a picture stream marked as the cover, so MP4 cannot carry it.
    [InlineData(MediaContainer.Auto, "fps")]
    [InlineData(MediaContainer.Mp4, "codec")]
    [InlineData(MediaContainer.Mp4, "remux")]
    // MKV copies it with the other attachments, so nothing was lost.
    [InlineData(MediaContainer.Mkv, "codec")]
    public async Task A_WebP_cover_attachment_stays_in_MKV_and_its_loss_in_MP4_is_named(MediaContainer container, string reason)
    {
        using var files = new EngineTestFiles();
        var engine = reason == "remux" ? new ExtrasEngine { WebPCover = true } : ExtrasEngine.ForConversion(reason, ".mkv", webPCover: true);
        var options = reason == "remux" ? new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = files.Root, Container = container } : engine.Options(files, container, reason);
        var progress = new LockedProgress();
        var result = await engine.Service().DownloadAsync(options, progress);
        var mkv = container == MediaContainer.Mkv;
        Assert.Equal(mkv ? ".mkv" : ".mp4", Path.GetExtension(result.FilePath));
        var export = Assert.Single(engine.Exports);
        Assert.Equal(reason != "remux", export.Arguments.Contains("-c:v"));
        Assert.Equal("0", ExtrasEngine.Argument(export, "-map_chapters"));
        // Nothing maps or extracts the attachment as a cover picture.
        Assert.DoesNotContain("0:2", export.Arguments);
        Assert.DoesNotContain("-attach", export.Arguments);
        Assert.Empty(engine.CoverExtractions);
        Assert.Equal(mkv, export.Arguments.Contains("0:t?"));
        Assert.Equal(mkv ? 0 : 1, ExtrasEngine.Occurrences(progress.Completion, ExtrasEngine.CoverLostNote));
        engine.AssertOnlyOutput(files, result.FilePath);
    }
}

/// <summary>Progress values reported from several threads, as the real process runner does.</summary>
internal sealed class LockedProgress : IProgress<JobProgress>
{
    private readonly List<JobProgress> values = [];
    public void Report(JobProgress value) { lock (values) values.Add(value); }
    /// <summary>The message of the final report, which must be the only completion.</summary>
    public string Completion
    {
        get
        {
            lock (values)
            {
                var finished = Assert.Single(values, value => value.Stage == JobStage.Finished);
                Assert.Equal(finished, values[^1]);
                return finished.Message ?? "";
            }
        }
    }
}

/// <summary>
/// A scripted yt-dlp, FFprobe and FFmpeg for downloads whose source carries a cover
/// picture, a Matroska attachment and (as far as the arguments show) chapters.
/// </summary>
internal sealed class ExtrasEngine
{
    public static readonly Uri Address = new("https://example.com/extras");
    public const string CoverLostNote = "The cover picture could not be carried over.";
    private readonly Dictionary<string, JsonObject> probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProcessRequest> calls = [];

    public string SourceExtension { get; init; } = ".mkv";
    public string VideoCodec { get; init; } = "h264";
    public string AudioCodec { get; init; } = "aac";
    public double Fps { get; init; } = 30;
    public string? ColorTransfer { get; init; }
    public bool FailCoverExtraction { get; init; }
    /// <summary>The Matroska source holds its cover as an image/webp attachment, as yt-dlp writes a WebP thumbnail into MKV.</summary>
    public bool WebPCover { get; init; }

    public IReadOnlyList<ProcessRequest> Calls { get { lock (calls) return calls.ToArray(); } }
    public IReadOnlyList<ProcessRequest> Exports => Calls.Where(call => call.Arguments.Contains("-progress")).ToArray();
    public IReadOnlyList<ProcessRequest> CoverExtractions => Calls.Where(call => call.Arguments.Contains("image2")).ToArray();

    /// <summary>A source that needs the given conversion for an H.264 MP4/MKV result.</summary>
    public static ExtrasEngine ForConversion(string reason, string extension, bool webPCover = false) => reason switch
    {
        "codec" => new ExtrasEngine { SourceExtension = extension, VideoCodec = "vp9", WebPCover = webPCover },
        "hdr" => new ExtrasEngine { SourceExtension = extension, VideoCodec = "hevc", ColorTransfer = "smpte2084", WebPCover = webPCover },
        "fps" => new ExtrasEngine { SourceExtension = extension, Fps = 60, WebPCover = webPCover },
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    public DownloadOptions Options(EngineTestFiles files, MediaContainer container, string reason)
    {
        var options = new DownloadOptions { Url = Address, OutputDirectory = files.Root, Container = container };
        return reason switch { "codec" => options with { VideoCodec = Klippi.Core.VideoCodec.H264 }, "hdr" => options with { HdrToSdr = true }, _ => options with { Fps = 30 } };
    }

    public DownloadService Service() => new(new Runner(this), new EngineFakeComponents());

    public static string Argument(ProcessRequest request, string name, int occurrence = 1)
    {
        var index = -1;
        for (var found = 0; found < occurrence; found++)
        {
            index = request.Arguments.ToList().IndexOf(name, index + 1);
            Assert.True(index >= 0 && index + 1 < request.Arguments.Count, $"Missing {name} #{occurrence} in: {string.Join(' ', request.Arguments)}");
        }
        return request.Arguments[index + 1];
    }

    public static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>The checked result is the only file left; the job folder and every intermediate file are gone.</summary>
    public void AssertOnlyOutput(EngineTestFiles files, string output)
    {
        Assert.Empty(Directory.GetDirectories(files.Root));
        Assert.Equal(new[] { output }, Directory.GetFiles(files.Root));
    }

    private ProcessResult Run(ProcessRequest request, Action<string> stdout)
    {
        lock (calls) calls.Add(request);
        var arguments = request.Arguments;
        if (request.Executable == "fake-yt-dlp" && arguments.Contains("--dump-single-json"))
            return new ProcessResult(0, JsonSerializer.Serialize(new { id = "extras", title = "Extras fixture", webpage_url = Address.AbsoluteUri, duration = 60, formats = new[] { new { format_id = "0", ext = SourceExtension.TrimStart('.'), vcodec = VideoCodec, acodec = AudioCodec } } }), "");
        if (request.Executable == "fake-yt-dlp")
        {
            var source = Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace(".%(ext)s", SourceExtension, StringComparison.Ordinal));
            File.WriteAllBytes(source, MediaBytes(SourceExtension));
            probes[source] = SourceProbe();
            stdout("KC_FILE:" + JsonSerializer.Serialize(source));
            return new ProcessResult(0, "", "");
        }
        if (request.Executable == "fake-ffprobe") return new ProcessResult(0, probes[arguments[^1]].ToJsonString(), "");
        if (arguments.Contains("-encoders")) return new ProcessResult(0, " V....D libx264 H.264\n V....D libx265 HEVC\n V....D libsvtav1 AV1\n", "");
        if (arguments.Contains("image2"))
        {
            if (FailCoverExtraction) return new ProcessResult(1, "", "Invalid data found when processing input");
            File.WriteAllBytes(arguments[^1], [0x89, 0x50, 0x4E, 0x47]);
            return new ProcessResult(0, "", "");
        }
        Assert.Contains("-progress", arguments);
        var destination = arguments[^1];
        File.WriteAllBytes(destination, MediaBytes(Path.GetExtension(destination)));
        var copyAll = arguments.Contains("-c");
        var video = copyAll ? VideoCodec : Argument(request, "-c:v") switch { "libx264" => "h264", "libx265" => "hevc", "libsvtav1" => "av1", var other => other };
        var audio = copyAll ? AudioCodec : Argument(request, "-c:a") switch { "copy" => AudioCodec, "libopus" => "opus", var other => other };
        var output = Probe(Path.GetExtension(destination), video, audio, copyAll ? Fps : 30, copyAll ? ColorTransfer : null);
        AddCarriedExtras(arguments, probes[Argument(request, "-i")], output);
        probes[destination] = output;
        stdout("out_time_us=60000000");
        return new ProcessResult(0, "", "");
    }

    // FFprobe of the result shows what the export's arguments carried over: a mapped
    // cover stream, a cover attached again (Matroska) and the copied attachments.
    private static void AddCarriedExtras(IReadOnlyList<string> arguments, JsonObject source, JsonObject output)
    {
        var sourceStreams = source["streams"]!.AsArray();
        var streams = output["streams"]!.AsArray();
        void Add(JsonNode stream) { stream["index"] = streams.Count; streams.Add(stream); }
        for (var position = 0; position + 1 < arguments.Count; position++)
        {
            var value = arguments[position + 1];
            if (arguments[position] == "-map" && value.StartsWith("0:", StringComparison.Ordinal) && int.TryParse(value[2..], CultureInfo.InvariantCulture, out var index)
                && sourceStreams.FirstOrDefault(stream => (int?)stream!["index"] == index) is { } mapped && (int?)mapped["disposition"]?["attached_pic"] == 1)
                Add(mapped.DeepClone());
            if (arguments[position] == "-attach")
                Add(new JsonObject { ["codec_type"] = "video", ["codec_name"] = value.EndsWith(".png", StringComparison.Ordinal) ? "png" : "mjpeg", ["disposition"] = new JsonObject { ["attached_pic"] = 1 }, ["tags"] = new JsonObject { ["filename"] = Path.GetFileName(value) } });
        }
        if (arguments.Contains("0:t?"))
            foreach (var attachment in sourceStreams.Where(stream => (string?)stream!["codec_type"] == "attachment")) Add(attachment!.DeepClone());
    }

    // FFprobe shows a JPEG or PNG Matroska attachment, or an MP4 cover, as a picture
    // stream and any other picture as an attachment; an MP4 written with chapters also
    // holds a chapter text track.
    private JsonObject SourceProbe()
    {
        var probe = Probe(SourceExtension, VideoCodec, AudioCodec, Fps, ColorTransfer);
        var streams = probe["streams"]!.AsArray();
        if (SourceExtension == ".mp4")
        {
            streams.Add(new JsonObject { ["index"] = 2, ["codec_type"] = "data", ["codec_name"] = "bin_data" });
            streams.Add(new JsonObject { ["index"] = 3, ["codec_type"] = "video", ["codec_name"] = "mjpeg", ["disposition"] = new JsonObject { ["attached_pic"] = 1 } });
        }
        else
        {
            streams.Add(WebPCover
                ? new JsonObject { ["index"] = 2, ["codec_type"] = "attachment", ["tags"] = new JsonObject { ["filename"] = "cover.webp", ["mimetype"] = "image/webp" } }
                : new JsonObject { ["index"] = 2, ["codec_type"] = "video", ["codec_name"] = "png", ["disposition"] = new JsonObject { ["attached_pic"] = 1 }, ["tags"] = new JsonObject { ["filename"] = "cover.png", ["mimetype"] = "image/png" } });
            streams.Add(new JsonObject { ["index"] = 3, ["codec_type"] = "attachment", ["codec_name"] = "none", ["tags"] = new JsonObject { ["filename"] = "info.json", ["mimetype"] = "application/json" } });
        }
        return probe;
    }

    private static JsonObject Probe(string extension, string video, string audio, double fps, string? transfer)
    {
        var picture = new JsonObject { ["index"] = 0, ["codec_type"] = "video", ["codec_name"] = video, ["width"] = 1920, ["height"] = 1080, ["avg_frame_rate"] = fps.ToString(CultureInfo.InvariantCulture) + "/1", ["disposition"] = new JsonObject { ["attached_pic"] = 0 } };
        if (transfer is not null) picture["color_transfer"] = transfer;
        var sound = new JsonObject { ["index"] = 1, ["codec_type"] = "audio", ["codec_name"] = audio, ["sample_rate"] = "48000", ["channels"] = 2 };
        var format = extension == ".mp4" ? "mov,mp4,m4a,3gp,3g2,mj2" : "matroska,webm";
        return new JsonObject { ["format"] = new JsonObject { ["duration"] = "60", ["format_name"] = format }, ["streams"] = new JsonArray { picture, sound } };
    }

    private static byte[] MediaBytes(string extension)
    {
        var content = Enumerable.Repeat((byte)23, 512).ToArray();
        byte[] header = extension switch
        {
            ".mp4" => [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0],
            ".webm" => [0x1a, 0x45, 0xdf, 0xa3, 0x87, 0x42, 0x82, 0x84, 119, 101, 98, 109],
            _ => [0x1a, 0x45, 0xdf, 0xa3, 0x8b, 0x42, 0x82, 0x88, 109, 97, 116, 114, 111, 115, 107, 97]
        };
        header.CopyTo(content, 0);
        return content;
    }

    private sealed class Runner(ExtrasEngine engine) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // HDR conversions first check for the tone-mapping filters of the bundled full build.
            if (request.Arguments is ["-hide_banner", "-filters"]) return Task.FromResult(new ProcessResult(0, EngineFakeProcess.FullBuildFilters, ""));
            return Task.FromResult(engine.Run(request, stdout ?? (_ => { })));
        }
    }
}

// Opt-in end-to-end proof with the real FFmpeg and FFprobe (set KLIPPI_FFMPEG_DIR,
// as for MediaRealFfmpegTests; skipped otherwise). Only yt-dlp is scripted: it hands
// DownloadService a small synthetic file with chapters, a cover and an attachment,
// and every conversion, remux and inspection after that runs the real tools. The
// hardware encoders are refused so the check is the same on any PC and leaves the
// GPU alone; the extras arguments do not depend on the video encoder.
[Collection(RealFfmpeg.Collection)]
public sealed class DownloadExtrasRealFfmpegTests
{
    [RealFfmpegTheory]
    [InlineData("mp4-fps")]
    [InlineData("mp4-hdr")]
    [InlineData("mkv-codec")]
    [InlineData("mp4-remux")]
    [InlineData("mkv-remux")]
    [InlineData("webm-misfit")]
    [InlineData("webm-copy")]
    public async Task Converted_or_remuxed_download_keeps_its_extras(string scenario)
    {
        using var media = new RealFfmpeg();
        var source = await MakeSourceAsync(media, scenario);
        var options = new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = media.PathFor("downloads") };
        options = scenario switch
        {
            "mp4-fps" => options with { Container = MediaContainer.Mp4, Fps = 30 },
            "mp4-hdr" => options with { Container = MediaContainer.Mp4, HdrToSdr = true },
            "mkv-codec" => options with { Container = MediaContainer.Mkv, VideoCodec = VideoCodec.Hevc },
            "mp4-remux" => options with { Container = MediaContainer.Mp4 },
            "mkv-remux" => options with { Container = MediaContainer.Mkv },
            _ => options with { Container = MediaContainer.WebM, VideoCodec = VideoCodec.Av1 }
        };
        Directory.CreateDirectory(options.OutputDirectory);
        var tools = DownloadTools.Scripted(media, source);
        var before = await tools.ProbeAsync(source);
        Assert.Equal(2, before["chapters"]!.AsArray().Count);
        Assert.Single(before["streams"]!.AsArray(), DownloadTools.IsCover);
        if (scenario == "mp4-hdr") Assert.Equal("smpte2084", (string?)before["streams"]![0]!["color_transfer"]);
        var progress = new LockedProgress();
        var result = await new DownloadService(tools, tools).DownloadAsync(options, progress);

        var container = options.Container switch { MediaContainer.Mp4 => ".mp4", MediaContainer.Mkv => ".mkv", _ => ".webm" };
        Assert.Equal(container, Path.GetExtension(result.FilePath));
        // Only the checked result is left: no job folder, cover copy or intermediate file.
        Assert.Empty(Directory.GetDirectories(options.OutputDirectory));
        Assert.Equal(new[] { result.FilePath }, Directory.GetFiles(options.OutputDirectory));
        var converted = scenario is not ("mp4-remux" or "mkv-remux");
        Assert.Equal(converted, Assert.Single(tools.Exports).Contains("-c:v"));

        var after = await tools.ProbeAsync(result.FilePath);
        // Chapters, in every container.
        Assert.Equal(new[] { "Opening", "Ending" }, DownloadTools.ChapterTitles(after));
        Assert.InRange(double.Parse((string)after["chapters"]![1]!["start_time"]!, CultureInfo.InvariantCulture), 1.49, 1.51);
        var streams = after["streams"]!.AsArray();
        var covers = streams.Where(DownloadTools.IsCover).ToArray();
        var attachments = DownloadTools.Attachments(after);
        var audio = Assert.Single(streams, stream => (string?)stream!["codec_type"] == "audio")!;
        var video = Assert.Single(streams, stream => (string?)stream!["codec_type"] == "video" && !DownloadTools.IsCover(stream))!;
        var mjpegSource = scenario == "mkv-remux";
        switch (container)
        {
            case ".mp4":
                // The cover stays a picture stream marked as the cover.
                Assert.Equal("png", (string?)Assert.Single(covers)!["codec_name"]);
                Assert.Empty(attachments);
                break;
            case ".mkv":
                // Matroska keeps the cover as an image attachment, which FFprobe shows as the cover.
                var cover = Assert.Single(covers)!;
                Assert.Equal(mjpegSource ? "mjpeg" : "png", (string?)cover["codec_name"]);
                Assert.Equal(mjpegSource ? "cover.jpg" : "cover.png", (string?)cover["tags"]?["filename"]);
                Assert.Equal(mjpegSource ? [] : new[] { "info.json" }, attachments);
                break;
            default:
                Assert.Empty(covers);
                Assert.Empty(attachments);
                break;
        }
        // The cover is the original picture file, byte for byte.
        if (covers.Length == 1) Assert.Equal("MD5=" + Convert.ToHexStringLower(MD5.HashData(await File.ReadAllBytesAsync(media.PathFor(mjpegSource ? "cover.jpg" : "cover.png")))), await tools.PacketHashAsync(result.FilePath, "0:" + (int)covers[0]!["index"]!));
        // A lost cover is named, and a kept one is not. WebM cannot hold the source's cover.
        Assert.Equal(container == ".webm" ? 1 : 0, ExtrasEngine.Occurrences(progress.Completion, ExtrasEngine.CoverLostNote));
        // Audio the container accepts is copied packet for packet; WebM cannot hold AAC, so it is converted.
        if (scenario == "webm-misfit") Assert.Equal("opus", (string?)audio["codec_name"]);
        else Assert.Equal(await tools.PacketHashAsync(source, "0:a"), await tools.PacketHashAsync(result.FilePath, "0:a"));
        if (!converted) Assert.Equal(await tools.PacketHashAsync(source, "0:V:0"), await tools.PacketHashAsync(result.FilePath, "0:V:0"));
        switch (scenario)
        {
            case "mp4-fps": Assert.Equal("30/1", (string?)video["avg_frame_rate"]); break;
            case "mp4-hdr": Assert.Equal("bt709", (string?)video["color_transfer"]); break;
            case "mkv-codec": Assert.Equal("hevc", (string?)video["codec_name"]); break;
            case "webm-misfit" or "webm-copy": Assert.Equal("av1", (string?)video["codec_name"]); break;
        }
    }

    // A WebP thumbnail that reaches MKV unconverted (the download now asks yt-dlp for
    // PNG; see DownloadCoverRealYtDlpTests) is an image/webp attachment. FFmpeg does not
    // read it as the cover: MKV keeps it as an attachment, and MP4, which cannot hold
    // it, now names the loss instead of dropping the cover silently.
    [RealFfmpegTheory]
    [InlineData(MediaContainer.Auto)]
    [InlineData(MediaContainer.Mkv)]
    public async Task A_WebP_cover_attachment_stays_in_MKV_and_its_loss_in_MP4_is_named(MediaContainer container)
    {
        using var media = new RealFfmpeg();
        var chapters = await WriteChaptersAsync(media);
        var webp = await media.MakeAsync("cover.webp", "-f", "lavfi", "-i", "testsrc2=s=96x64:d=1", "-frames:v", "1", "-c:v", "libwebp");
        var streams = await media.MakeAsync("streams.mkv", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=60:d=3", "-f", "lavfi", "-i", "sine=f=440:r=48000:d=3",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-threads", "2", "-c:a", "aac", "-b:a", "96k");
        var source = await media.MakeAsync("source.mkv", "-i", streams, "-i", chapters, "-map", "0", "-map_metadata", "1", "-map_chapters", "1", "-c", "copy",
            "-attach", webp, "-metadata:s:t:0", "mimetype=image/webp", "-metadata:s:t:0", "filename=cover.webp");
        var tools = DownloadTools.Scripted(media, source);
        var before = await tools.ProbeAsync(source);
        Assert.DoesNotContain(before["streams"]!.AsArray(), DownloadTools.IsCover);
        Assert.Equal(new[] { "cover.webp" }, DownloadTools.Attachments(before));
        var options = new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = media.PathFor("downloads"), Container = container, Fps = 30 };
        Directory.CreateDirectory(options.OutputDirectory);
        var progress = new LockedProgress();
        var result = await new DownloadService(tools, tools).DownloadAsync(options, progress);

        var mkv = container == MediaContainer.Mkv;
        Assert.Equal(mkv ? ".mkv" : ".mp4", Path.GetExtension(result.FilePath));
        Assert.Equal(new[] { result.FilePath }, Directory.GetFiles(options.OutputDirectory));
        Assert.Empty(Directory.GetDirectories(options.OutputDirectory));
        Assert.Contains("-c:v", Assert.Single(tools.Exports));
        var after = await tools.ProbeAsync(result.FilePath);
        Assert.Equal(new[] { "Opening", "Ending" }, DownloadTools.ChapterTitles(after));
        Assert.DoesNotContain(after["streams"]!.AsArray(), DownloadTools.IsCover);
        Assert.Equal(mkv ? new[] { "cover.webp" } : [], DownloadTools.Attachments(after));
        Assert.Equal(mkv ? 0 : 1, ExtrasEngine.Occurrences(progress.Completion, ExtrasEngine.CoverLostNote));
        Assert.Equal(await tools.PacketHashAsync(source, "0:a"), await tools.PacketHashAsync(result.FilePath, "0:a"));
    }

    internal static async Task<string> WriteChaptersAsync(RealFfmpeg media)
    {
        var chapters = media.PathFor("chapters.txt");
        await File.WriteAllTextAsync(chapters, ";FFMETADATA1\ntitle=Extras fixture\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1500\ntitle=Opening\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=3000\ntitle=Ending\n");
        return chapters;
    }

    /// <summary>3 s at 160x120 with two chapters, a cover and (in Matroska) an info.json attachment, like a yt-dlp download.</summary>
    private static async Task<string> MakeSourceAsync(RealFfmpeg media, string scenario)
    {
        var chapters = await WriteChaptersAsync(media);
        var info = media.PathFor("info.json");
        await File.WriteAllTextAsync(info, """{"id":"extras","title":"Extras fixture"}""");
        string[] tone = ["-f", "lavfi", "-i", "sine=f=440:r=48000:d=3"];
        string[] aac = ["-c:a", "aac", "-b:a", "96k"];
        if (scenario == "mkv-remux")
        {
            var jpeg = await media.MakeAsync("cover.jpg", "-f", "lavfi", "-i", "color=c=blue:s=64x64:d=1", "-frames:v", "1", "-q:v", "3");
            return await media.MakeAsync("source.mp4", ["-f", "lavfi", "-i", "testsrc2=s=160x120:r=30:d=3", .. tone, "-i", chapters, "-i", jpeg,
                "-map", "0:v", "-map", "1:a", "-map", "3:v", "-map_metadata", "2", "-map_chapters", "2",
                "-c:v:0", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-threads", "2", "-c:v:1", "copy", "-disposition:v:1", "attached_pic", .. aac]);
        }
        var png = await media.MakeAsync("cover.png", "-f", "lavfi", "-i", "color=c=red:s=64x64:d=1,format=rgb24", "-frames:v", "1");
        string[] codecs = scenario switch
        {
            // FFmpeg 9.0.2 drops the PQ transfer when libx265 writes Matroska directly
            // (FFprobe then reads it as SDR), but a stream copy from MP4 keeps it.
            "mp4-hdr" => ["-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error:pools=2:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", .. aac],
            "webm-misfit" => ["-c:v", "libaom-av1", "-usage", "realtime", "-cpu-used", "8", "-threads", "2", .. aac],
            "webm-copy" => ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-threads", "2", "-c:a", "libopus", "-b:a", "64k"],
            _ => ["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-threads", "2", .. aac]
        };
        var rate = scenario == "mp4-fps" ? 60 : 30;
        var streams = await media.MakeAsync(scenario == "mp4-hdr" ? "streams.mp4" : "streams.mkv", ["-f", "lavfi", "-i", $"testsrc2=s=160x120:r={rate}:d=3", .. tone, .. codecs]);
        return await media.MakeAsync("source.mkv", ["-i", streams, "-i", chapters, "-map", "0", "-map_metadata", "1", "-map_chapters", "1", "-c", "copy",
            "-attach", png, "-metadata:s:t:0", "mimetype=image/png", "-metadata:s:t:0", "filename=cover.png",
            "-attach", info, "-metadata:s:t:1", "mimetype=application/json", "-metadata:s:t:1", "filename=info.json"]);
    }
}

// Opt-in end-to-end proof that includes the real yt-dlp: KLIPPI_FFMPEG_DIR must name a
// folder that also holds yt-dlp, as the app's Components version folder does (skipped
// otherwise). DownloadService's own yt-dlp arguments run offline. The harness adds only
// --load-info-json with a local info file, --enable-file-urls for its local streams and
// WebP thumbnail, and an unreachable proxy so nothing can go to the internet.
[Collection(RealFfmpeg.Collection)]
public sealed class DownloadCoverRealYtDlpTests
{
    [RealYtDlpTheory]
    // yt-dlp merges VP9 and Opus into MKV for these, embeds the thumbnail there, and the
    // download is converted afterwards: to MP4 at 30 fps, to H.264 MP4, and to MKV at 30 fps.
    [InlineData("auto-fps")]
    [InlineData("mp4-codec")]
    [InlineData("mkv-fps")]
    public async Task A_WebP_thumbnail_reaches_the_converted_download_as_its_cover(string scenario)
    {
        using var media = new RealFfmpeg();
        var info = await MakeSiteAsync(media);
        var options = new DownloadOptions { Url = ExtrasEngine.Address, OutputDirectory = media.PathFor("downloads"), EmbedThumbnail = true };
        options = scenario switch
        {
            "auto-fps" => options with { Fps = 30 },
            "mp4-codec" => options with { Container = MediaContainer.Mp4, VideoCodec = VideoCodec.H264 },
            _ => options with { Container = MediaContainer.Mkv, Fps = 30 }
        };
        Directory.CreateDirectory(options.OutputDirectory);
        var tools = DownloadTools.WithYtDlp(media, DownloadTools.YtDlp!, info);
        var progress = new LockedProgress();
        var result = await new DownloadService(tools, tools).DownloadAsync(options, progress);

        // What yt-dlp wrote: Matroska with the WebP thumbnail turned into a PNG cover.
        var transfer = Assert.Single(tools.Transfers);
        Assert.Equal("webp>png", transfer[transfer.ToList().IndexOf("--convert-thumbnails") + 1]);
        var downloaded = Assert.IsType<string>(tools.Downloaded);
        Assert.Equal(".mkv", Path.GetExtension(downloaded));
        var fetched = await tools.ProbeAsync(downloaded);
        Assert.Equal(new[] { "Opening", "Ending" }, DownloadTools.ChapterTitles(fetched));
        var fetchedCover = Assert.Single(fetched["streams"]!.AsArray(), DownloadTools.IsCover)!;
        Assert.Equal("png", (string?)fetchedCover["codec_name"]);
        Assert.Equal(96, (int?)fetchedCover["width"]);
        Assert.Equal(64, (int?)fetchedCover["height"]);
        Assert.DoesNotContain("cover.webp", DownloadTools.Attachments(fetched));

        // What was saved: converted, with the same chapters and the same cover picture.
        var mkv = scenario == "mkv-fps";
        Assert.Equal(mkv ? ".mkv" : ".mp4", Path.GetExtension(result.FilePath));
        Assert.Contains("-c:v", Assert.Single(tools.Exports));
        // Only the checked result is left: no thumbnail file, job folder or intermediate file.
        Assert.Equal(new[] { result.FilePath }, Directory.GetFiles(options.OutputDirectory));
        Assert.Empty(Directory.GetDirectories(options.OutputDirectory));
        var after = await tools.ProbeAsync(result.FilePath);
        Assert.Equal(new[] { "Opening", "Ending" }, DownloadTools.ChapterTitles(after));
        var streams = after["streams"]!.AsArray();
        var cover = Assert.Single(streams, DownloadTools.IsCover)!;
        Assert.Equal("png", (string?)cover["codec_name"]);
        if (mkv) Assert.Equal("cover.png", (string?)cover["tags"]?["filename"]);
        Assert.Equal(await tools.PacketHashAsync(downloaded, "0:" + (int)fetchedCover["index"]!), await tools.PacketHashAsync(result.FilePath, "0:" + (int)cover["index"]!));
        // Matroska also keeps whatever else yt-dlp attached; MP4 holds no attachments.
        Assert.Equal(mkv ? DownloadTools.Attachments(fetched) : [], DownloadTools.Attachments(after));
        Assert.DoesNotContain(ExtrasEngine.CoverLostNote, progress.Completion);
        var video = Assert.Single(streams, stream => (string?)stream!["codec_type"] == "video" && !DownloadTools.IsCover(stream))!;
        var audio = Assert.Single(streams, stream => (string?)stream!["codec_type"] == "audio")!;
        if (scenario == "mp4-codec") Assert.Equal("h264", (string?)video["codec_name"]);
        else Assert.Equal("30/1", (string?)video["avg_frame_rate"]);
        // MKV holds the Opus audio as it is; for MP4 it becomes AAC.
        if (mkv) Assert.Equal(await tools.PacketHashAsync(downloaded, "0:a"), await tools.PacketHashAsync(result.FilePath, "0:a"));
        else Assert.Equal("aac", (string?)audio["codec_name"]);
    }

    /// <summary>
    /// A local "site": 3 s of 60 fps VP9 video and Opus audio as separate WebM streams, a
    /// 96x64 WebP thumbnail and two chapters, described by an info file as yt-dlp writes one.
    /// It has no webpage_url, so a failure cannot fall back to downloading from the address.
    /// </summary>
    private static async Task<string> MakeSiteAsync(RealFfmpeg media)
    {
        var video = await media.MakeAsync("video.webm", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=60:d=3", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-threads", "2");
        var audio = await media.MakeAsync("audio.webm", "-f", "lavfi", "-i", "sine=f=440:r=48000:d=3", "-c:a", "libopus", "-b:a", "64k");
        var thumbnail = await media.MakeAsync("thumbnail.webp", "-f", "lavfi", "-i", "testsrc2=s=96x64:d=1", "-frames:v", "1", "-c:v", "libwebp");
        var info = new JsonObject
        {
            ["id"] = "extras", ["title"] = "Extras fixture", ["extractor"] = "generic", ["extractor_key"] = "Generic", ["duration"] = 3,
            ["chapters"] = new JsonArray(new JsonObject { ["start_time"] = 0, ["end_time"] = 1.5, ["title"] = "Opening" }, new JsonObject { ["start_time"] = 1.5, ["end_time"] = 3, ["title"] = "Ending" }),
            ["thumbnails"] = new JsonArray(new JsonObject { ["id"] = "0", ["url"] = new Uri(thumbnail).AbsoluteUri }),
            ["formats"] = new JsonArray(
                new JsonObject { ["format_id"] = "v60", ["url"] = new Uri(video).AbsoluteUri, ["ext"] = "webm", ["vcodec"] = "vp9", ["acodec"] = "none", ["width"] = 160, ["height"] = 120, ["fps"] = 60 },
                new JsonObject { ["format_id"] = "a64", ["url"] = new Uri(audio).AbsoluteUri, ["ext"] = "webm", ["vcodec"] = "none", ["acodec"] = "opus", ["abr"] = 64 })
        };
        var path = media.PathFor("site.info.json");
        await File.WriteAllTextAsync(path, info.ToJsonString());
        return path;
    }
}

internal sealed class RealYtDlpTheoryAttribute : TheoryAttribute
{
    public RealYtDlpTheoryAttribute()
    {
        if (!RealFfmpeg.Configured) Skip = RealFfmpeg.SkipReason;
        else if (DownloadTools.YtDlp is null) Skip = "Set " + RealFfmpeg.Variable + " to a folder that also holds yt-dlp (as the app's Components version folder does) to run the real yt-dlp checks.";
    }
}

/// <summary>
/// The real FFmpeg and FFprobe behind either a scripted yt-dlp that "downloads" a
/// synthetic file, or the real yt-dlp loading a local info file offline.
/// </summary>
internal sealed class DownloadTools : IProcessRunner, IComponentService
{
    private readonly ProcessRunner runner = new();
    private readonly List<IReadOnlyList<string>> exports = [];
    private readonly List<IReadOnlyList<string>> transfers = [];
    private readonly RealFfmpeg media;
    private readonly string ytDlp;
    private readonly string? source;
    private readonly string? info;

    private DownloadTools(RealFfmpeg media, string ytDlp, string? source, string? info) { this.media = media; this.ytDlp = ytDlp; this.source = source; this.info = info; }
    /// <summary>A scripted yt-dlp hands DownloadService a copy of <paramref name="source"/>.</summary>
    public static DownloadTools Scripted(RealFfmpeg media, string source) => new(media, "fake-yt-dlp", source, null);
    /// <summary>The real yt-dlp runs with DownloadService's arguments on the local <paramref name="info"/> file.</summary>
    public static DownloadTools WithYtDlp(RealFfmpeg media, string ytDlp, string info) => new(media, ytDlp, null, info);

    /// <summary>The yt-dlp next to FFmpeg in <c>KLIPPI_FFMPEG_DIR</c>, if there is one.</summary>
    public static string? YtDlp => Environment.GetEnvironmentVariable(RealFfmpeg.Variable) is { Length: > 0 } folder && Path.Combine(folder, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp") is var path && File.Exists(path) ? path : null;
    public IReadOnlyList<IReadOnlyList<string>> Exports { get { lock (exports) return exports.ToArray(); } }
    /// <summary>The arguments DownloadService gave the real yt-dlp, before the offline additions.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Transfers { get { lock (transfers) return transfers.ToArray(); } }
    /// <summary>A copy of the file the real yt-dlp produced, taken before DownloadService converts it.</summary>
    public string? Downloaded { get; private set; }

    public string? Find(string name) => name switch { "yt-dlp" => ytDlp, "ffmpeg" => media.Ffmpeg, "ffprobe" => media.Ffprobe, _ => null };
    public Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentInfo>>([]);
    public Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
    {
        var arguments = request.Arguments;
        if (request.Executable == ytDlp && arguments.Contains("--dump-single-json"))
            return new ProcessResult(0, info is null ? JsonSerializer.Serialize(new { id = "extras", title = "Extras fixture", webpage_url = ExtrasEngine.Address.AbsoluteUri, duration = 3, formats = Array.Empty<object>() }) : await File.ReadAllTextAsync(info, cancellationToken), "");
        if (request.Executable == ytDlp && source is not null)
        {
            var staged = Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace(".%(ext)s", Path.GetExtension(source), StringComparison.Ordinal));
            File.Copy(source, staged);
            stdout?.Invoke("KC_FILE:" + JsonSerializer.Serialize(staged));
            return new ProcessResult(0, "", "");
        }
        if (request.Executable == ytDlp) return await RunYtDlpAsync(request, stdout, stderr, cancellationToken);
        var encoder = arguments.Contains("-c:v") ? EngineFakeProcess.Argument(request, "-c:v") : "";
        if (encoder.EndsWith("_amf", StringComparison.Ordinal) || encoder.EndsWith("_nvenc", StringComparison.Ordinal) || encoder.EndsWith("_qsv", StringComparison.Ordinal))
            return new ProcessResult(1, "", "Hardware encoders are not used in this check.");
        if (arguments.Contains("-progress")) lock (exports) exports.Add(arguments.ToArray());
        return await runner.RunAsync(request, stdout, stderr, cancellationToken);
    }

    private async Task<ProcessResult> RunYtDlpAsync(ProcessRequest request, Action<string>? stdout, Action<string>? stderr, CancellationToken cancellationToken)
    {
        var arguments = request.Arguments;
        lock (transfers) transfers.Add(arguments.ToArray());
        // The address follows "--"; the offline options go before it.
        Assert.Equal("--", arguments[^2]);
        string[] offline = [.. arguments.Take(arguments.Count - 2), "--load-info-json", info!, "--enable-file-urls", "--proxy", "http://127.0.0.1:9", .. arguments.Skip(arguments.Count - 2)];
        string? reported = null;
        var result = await runner.RunAsync(request with { Arguments = offline }, line =>
        {
            if (line.StartsWith("KC_FILE:", StringComparison.Ordinal)) reported = JsonSerializer.Deserialize<string>(line[8..]);
            stdout?.Invoke(line);
        }, stderr, cancellationToken);
        Assert.True(result.ExitCode == 0, "yt-dlp failed: " + result.StandardError);
        if (reported is not null && File.Exists(reported))
        {
            Downloaded = media.PathFor("yt-dlp" + Path.GetExtension(reported));
            File.Copy(reported, Downloaded);
        }
        return result;
    }

    public static bool IsCover(JsonNode? stream) => (string?)stream!["codec_type"] == "video" && (int?)stream["disposition"]?["attached_pic"] == 1;
    public static string?[] Attachments(JsonNode probe) => probe["streams"]!.AsArray().Where(stream => (string?)stream!["codec_type"] == "attachment").Select(stream => (string?)stream!["tags"]?["filename"]).ToArray();
    public static string?[] ChapterTitles(JsonNode probe) => probe["chapters"]!.AsArray().Select(chapter => (string?)chapter!["tags"]?["title"]).ToArray();

    public async Task<JsonNode> ProbeAsync(string path)
    {
        var result = await runner.RunAsync(new ProcessRequest(media.Ffprobe, ["-v", "error", "-show_chapters", "-show_streams", "-of", "json", path], Timeout: TimeSpan.FromMinutes(2)));
        Assert.True(result.ExitCode == 0, "FFprobe failed: " + result.StandardError);
        return JsonNode.Parse(result.StandardOutput)!;
    }

    /// <summary>An MD5 of the selected stream's packets, copied without decoding.</summary>
    public async Task<string> PacketHashAsync(string path, string map)
    {
        var result = await runner.RunAsync(new ProcessRequest(media.Ffmpeg, ["-hide_banner", "-v", "error", "-i", path, "-map", map, "-c", "copy", "-f", "md5", "-"], Timeout: TimeSpan.FromMinutes(2)));
        Assert.True(result.ExitCode == 0, $"FFmpeg could not hash {map}: {result.StandardError}");
        var hash = result.StandardOutput.Trim();
        Assert.StartsWith("MD5=", hash);
        // The MD5 of no data: nothing was hashed, so an equal pair would prove nothing.
        Assert.NotEqual("MD5=d41d8cd98f00b204e9800998ecf8427e", hash);
        return hash;
    }
}
