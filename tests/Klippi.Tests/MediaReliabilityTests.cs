using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// Regression tests for the media-engine reliability fixes. Each test pins the
// argument list or flow that the bundled FFmpeg build was checked against.
public sealed class MediaReliabilityTests
{
    [Fact]
    public async Task Exact_clip_seeks_on_the_input_so_fades_use_clip_time()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario();
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(25), FadeIn = TimeSpan.FromSeconds(1), FadeOut = TimeSpan.FromSeconds(1), Encoder = EncoderPreference.Cpu });
        var encode = Assert.Single(scenario.Exports);
        var arguments = encode.Arguments.ToList();
        var input = arguments.IndexOf("-i");
        // -ss on the input, -t on the output: an input -t let late-starting audio run past the end.
        Assert.Equal(new[] { "-ss", "20" }, arguments.GetRange(input - 2, 2));
        Assert.Equal(new[] { "-t", "5" }, arguments.GetRange(input + 2, 2));
        Assert.Equal(1, arguments.Count(argument => argument == "-ss"));
        Assert.Equal(1, arguments.Count(argument => argument == "-t"));
        // With output seeking FFmpeg ran these on source time, which silenced the whole clip.
        Assert.Equal("aresample=async=1:first_pts=0,afade=t=in:st=0:d=1,afade=t=out:st=4:d=1", MediaScenario.Argument(encode, "-af"));
        Assert.DoesNotContain(arguments, argument => argument.Contains("areverse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clip_from_the_start_limits_its_length_on_the_output()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { FormatName = "mov,mp4,m4a,3gp,3g2,mj2" };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(8), Encoder = EncoderPreference.Cpu });
        var arguments = Assert.Single(scenario.Exports).Arguments.ToList();
        // An input -t without -ss counted from each stream's start: audio 0.48 s
        // late ran to 8.48 s and the export failed its duration check.
        Assert.DoesNotContain("-ss", arguments);
        Assert.Equal(new[] { "-t", "8" }, arguments.GetRange(arguments.IndexOf("-i") + 2, 2));
        Assert.Equal(1, arguments.Count(argument => argument == "-t"));
    }

    [Fact]
    public async Task Audio_starts_on_a_continuous_clock_padded_from_the_clip_start()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario();
        var service = scenario.Service();
        await service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp3"), End = TimeSpan.FromSeconds(8), AudioOnly = true });
        await service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(0.2), End = TimeSpan.FromSeconds(7), Normalize = true, Encoder = EncoderPreference.Cpu });
        // Without the padding an MP3 dropped the 0.48 s lead-in of late audio
        // (7.54 s for 8 s, beep 0.48 s early) and loudnorm's renumbering moved
        // normalized audio 0.28 s ahead of the video.
        Assert.Equal("aresample=async=1:first_pts=0", MediaScenario.Argument(scenario.Exports[0], "-af"));
        Assert.StartsWith("aresample=async=1:first_pts=0,loudnorm=", MediaScenario.Argument(scenario.Exports[1], "-af"));
    }

    [Theory]
    [InlineData("mpegts", 20.5, "5.5", "15")]
    [InlineData("mpeg", 20.5, "5.5", "15")]
    [InlineData("mpegts", 5.5, null, "5.5")]
    public async Task Mpeg_sources_decode_a_margin_and_cut_inside_the_filters(string format, double start, string? seek, string cut)
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { FormatName = format };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input("source.ts"), OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(start + 4), FadeIn = TimeSpan.FromSeconds(1), MaxHeight = 720, Encoder = EncoderPreference.Cpu });
        var encode = Assert.Single(scenario.Exports);
        var arguments = encode.Arguments.ToList();
        // An input seek straight to 5.5 s in MPEG-TS began at the next keyframe (6 s).
        if (seek is null) Assert.DoesNotContain("-ss", arguments);
        else Assert.Equal(new[] { "-ss", seek }, arguments.GetRange(arguments.IndexOf("-i") - 2, 2));
        Assert.Equal(new[] { "-t", "4" }, arguments.GetRange(arguments.IndexOf("-i") + 2, 2));
        var end = (double.Parse(cut, CultureInfo.InvariantCulture) + 4).ToString(CultureInfo.InvariantCulture);
        // The cut comes first, so scaling and fades work on clip time; PTS-P/TB keeps late streams aligned.
        Assert.StartsWith($"trim=start={cut}:end={end},setpts=PTS-{cut}/TB,scale=", MediaScenario.Argument(encode, "-vf"));
        Assert.Equal($"aresample=async=1:first_pts=0,atrim=start={cut}:end={end},asetpts=PTS-{cut}/TB,afade=t=in:st=0:d=1", MediaScenario.Argument(encode, "-af"));
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData("matroska,webm")]
    [InlineData("avi")]
    [InlineData("asf")]
    [InlineData("flv")]
    public async Task Containers_that_seek_to_keyframes_keep_the_direct_input_seek(string format)
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { FormatName = format };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(20.5), End = TimeSpan.FromSeconds(24.5), Encoder = EncoderPreference.Cpu });
        var encode = Assert.Single(scenario.Exports);
        var arguments = encode.Arguments.ToList();
        Assert.Equal(new[] { "-ss", "20.5" }, arguments.GetRange(arguments.IndexOf("-i") - 2, 2));
        Assert.DoesNotContain(arguments, argument => argument.Contains("trim=", StringComparison.Ordinal));
        Assert.True(MediaService.SeeksToKeyframes(await scenario.Service().ProbeAsync(Path.Combine(files.Root, "source.mp4"))));
    }

    [Fact]
    public async Task Fast_finished_mp4_gets_time_to_rewrite_its_index_without_progress()
    {
        using var files = new EngineTestFiles();
        // 3 MB written so far, rewritten at 1 MB/s in this test: 3 s more than the 250 ms stall limit.
        Func<Action<string>?, CancellationToken, Task> quietFinish = async (stdout, cancellationToken) =>
        {
            stdout?.Invoke("total_size=3000000");
            stdout?.Invoke("out_time_us=2000000");
            await Task.Delay(800, cancellationToken);
        };
        var scenario = new MediaScenario { DuringExport = quietFinish };
        var service = scenario.Service(stall: TimeSpan.FromMilliseconds(250), faststartBytesPerSecond: 1_000_000);
        // The last report came 3 s before the end, as a fast export can; +faststart then wrote nothing for minutes.
        var result = await service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(result.FilePath));
        // Matroska has no +faststart pass, so the same silence is still a stall.
        var mkv = new MediaScenario { DuringExport = quietFinish };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => mkv.Service(stall: TimeSpan.FromMilliseconds(250), faststartBytesPerSecond: 1_000_000).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mkv"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu }).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("stopped making progress", error.Message);
    }

    [Fact]
    public async Task Stalled_cpu_export_is_stopped_by_the_progress_watchdog()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var output = Path.Combine(files.Root, "clip.mp4");
        var scenario = new MediaScenario { StallEncoders = { "libx264" } };
        var service = scenario.Service(stall: TimeSpan.FromMilliseconds(250));
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ExportAsync(new ExportOptions { InputPath = input, OutputPath = output, End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu }).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("stopped making progress", error.Message);
        Assert.True(File.Exists(input));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Fact]
    public async Task Advancing_progress_keeps_a_slow_export_alive()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario
        {
            DuringExport = async (stdout, cancellationToken) =>
            {
                // 2 s of work against a 1 s stall limit, with progress every 100 ms.
                for (var step = 1; step <= 20; step++)
                {
                    await Task.Delay(100, cancellationToken);
                    stdout?.Invoke("out_time_us=" + (step * 100_000).ToString(CultureInfo.InvariantCulture));
                }
            }
        };
        var result = await scenario.Service(stall: TimeSpan.FromSeconds(1)).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu });
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task Export_over_its_time_limit_explains_the_stop()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario
        {
            DuringExport = async (stdout, cancellationToken) =>
            {
                for (var step = 1; ; step++)
                {
                    await Task.Delay(50, cancellationToken);
                    stdout?.Invoke("out_time_us=" + (step * 1000).ToString(CultureInfo.InvariantCulture));
                }
            }
        };
        var service = scenario.Service(stall: TimeSpan.FromSeconds(30), deadline: TimeSpan.FromMilliseconds(400));
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu }).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("time limit", error.Message);
        Assert.Contains("original was preserved", error.Message);
    }

    [Fact]
    public async Task Stalled_hardware_export_tries_the_next_working_hardware_encoder()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { Encoders = MediaScenario.Listing("h264_amf", "h264_nvenc", "libx264"), UsableHardware = { "h264_amf", "h264_nvenc" }, StallEncoders = { "h264_amf" } };
        var result = await scenario.Service(stall: TimeSpan.FromMilliseconds(250)).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Gpu }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "h264_amf", "h264_nvenc" }, scenario.Exports.Select(call => MediaScenario.Argument(call, "-c:v")));
        Assert.Contains("h264_nvenc was used instead", result.Warning);
    }

    [Fact]
    public void Export_deadline_allows_for_slow_cpu_encoders_and_stays_bounded()
    {
        var options = new ExportOptions { InputPath = "source", OutputPath = "clip.mkv" };
        var tenMinutes = TimeSpan.FromMinutes(10);
        // libaom measured 0.094x realtime on 1080p30 with the old settings: 106 min for 10 min.
        Assert.True(MediaService.ExportDeadline(tenMinutes, new EncoderInfo("libaom-av1", "", false), options) > TimeSpan.FromMinutes(106 * 2));
        Assert.True(MediaService.ExportDeadline(tenMinutes, new EncoderInfo("libx264", "", false), options) >= TimeSpan.FromMinutes(80));
        Assert.Equal(TimeSpan.FromMinutes(720), MediaService.ExportDeadline(TimeSpan.FromHours(3), new EncoderInfo("libaom-av1", "", false), options));
        Assert.True(MediaService.StallLimit(new EncoderInfo("libaom-av1", "", false)) > MediaService.StallLimit(new EncoderInfo("h264_amf", "", true)));
    }

    [Theory]
    [InlineData(CompressionPreset.Off, 18, 40, 26)]
    [InlineData(CompressionPreset.Light, 22, 72, 30)]
    [InlineData(CompressionPreset.Balanced, 26, 100, 34)]
    [InlineData(CompressionPreset.Strong, 30, 124, 38)]
    [InlineData(CompressionPreset.VerySmall, 34, 144, 42)]
    public void Quality_values_follow_each_encoder_scale(CompressionPreset preset, int h264, int av1Amf, int av1Crf)
    {
        Assert.Equal(h264, MediaService.EncoderQuality("h264_amf", preset));
        Assert.Equal(h264, MediaService.EncoderQuality("libx264", preset));
        Assert.Equal(av1Amf, MediaService.EncoderQuality("av1_amf", preset));
        Assert.Equal(av1Crf, MediaService.EncoderQuality("av1_nvenc", preset));
        Assert.Equal(av1Crf, MediaService.EncoderQuality("libaom-av1", preset));
    }

    [Fact]
    public async Task Av1_amf_receives_qps_on_its_0_to_255_scale()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { Encoders = MediaScenario.Listing("av1_amf", "libaom-av1"), UsableHardware = { "av1_amf" } };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Gpu, VideoCodec = VideoCodec.Av1 });
        var encode = Assert.Single(scenario.Exports);
        Assert.Equal("av1_amf", MediaScenario.Argument(encode, "-c:v"));
        // QP 18 on av1_amf was near-lossless: a 'compressed' clip twice the source size.
        // QP 40 matched h264_amf QP 18 by PSNR (47.7 vs 47.6 dB) at a smaller size.
        Assert.Equal("40", MediaScenario.Argument(encode, "-qp_i"));
        Assert.Equal("40", MediaScenario.Argument(encode, "-qp_p"));
    }

    [Theory]
    [InlineData(CompressionPreset.Light, "2100k")]
    [InlineData(CompressionPreset.Balanced, "1500k")]
    [InlineData(CompressionPreset.Strong, "1050k")]
    [InlineData(CompressionPreset.VerySmall, "600k")]
    public async Task Compression_presets_cap_video_below_the_source_bitrate(CompressionPreset preset, string cap)
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[1_000_000]);
        // The 512-byte result is smaller than the clip's share of the source, so no retry follows.
        var scenario = new MediaScenario { VideoBitRate = 3_000_000 };
        var service = scenario.Service();
        var options = new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(10), Compression = preset };
        await service.ExportAsync(options);
        var encode = Assert.Single(scenario.Exports);
        // Auto keeps moderate compressed H.264 jobs on libx264, which honours caps.
        Assert.Equal("libx264", MediaScenario.Argument(encode, "-c:v"));
        Assert.Equal(cap, MediaScenario.Argument(encode, "-maxrate"));
        var estimate = service.Estimate(await service.ProbeAsync(options.InputPath), options);
        Assert.NotNull(estimate.EstimatedBytes);
        Assert.Equal(int.Parse(cap.TrimEnd('k'), CultureInfo.InvariantCulture), estimate.VideoKbps);
    }

    [Theory]
    [InlineData(96_118L, CompressionPreset.Light, "96k")]
    [InlineData(125_000L, CompressionPreset.VerySmall, "128k")]
    [InlineData(72_103L, CompressionPreset.Balanced, "80k")]
    [InlineData(320_000L, CompressionPreset.Light, "192k")]
    [InlineData(96_118L, CompressionPreset.Off, "192k")]
    public async Task Compression_never_spends_more_on_audio_than_the_source_had(long sourceAudio, CompressionPreset preset, string expected)
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 30, VideoBitRate = 285_000, AudioBitRate = sourceAudio };
        var service = scenario.Service();
        var options = new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(25), Compression = preset, Encoder = EncoderPreference.Cpu };
        await service.ExportAsync(options);
        // 192 kbit/s for 96 kbit/s source audio was most of a Light clip's bytes, so
        // the estimate exceeded the source and a second encode cut video to 0.42x.
        Assert.Equal(expected, MediaScenario.Argument(Assert.Single(scenario.Exports), "-b:a"));
        if (preset == CompressionPreset.Off) return;
        var estimate = service.Estimate(await service.ProbeAsync(input), options);
        Assert.Equal((long)Math.Ceiling((estimate.VideoKbps!.Value + int.Parse(expected.TrimEnd('k'), CultureInfo.InvariantCulture)) * 1000d / 8 * 20 * 1.02), estimate.EstimatedBytes);
    }

    [Fact]
    public void Light_estimate_for_low_bitrate_source_stays_below_the_source_share()
    {
        // lowbr.mp4: 285 kbit/s video, 96 kbit/s audio, 30 s, 1,462,665 bytes; 20 s share 975,110 bytes.
        var service = new MediaService(new MediaFakeRunner((_, _, _, _) => throw new InvalidOperationException()), new EngineFakeComponents());
        var probe = new ProbeInfo("lowbr.mp4", TimeSpan.FromSeconds(30), 1_462_665, true, true, 640, 360, 30, "h264", "aac", 48000, 2, MediaScenario.SourceJson(30, video: true, videoBitRate: 284_830, audioBitRate: 96_118));
        var estimate = service.Estimate(probe, new ExportOptions { InputPath = "lowbr.mp4", OutputPath = "clip.mp4", Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(25), Compression = CompressionPreset.Light });
        // It was 997,050 bytes with 192 kbit/s audio: larger than the part of the source it compresses.
        Assert.True(estimate.EstimatedBytes < 975_110, estimate.EstimatedBytes.ToString());
    }

    [Fact]
    public async Task Target_size_moves_from_hardware_to_the_cpu_when_hardware_misses_clearly()
    {
        using var files = new EngineTestFiles();
        var sizes = new[] { 3_300_000L, 2_400_000L };
        var scenario = new MediaScenario { UsableHardware = { "h264_amf" }, OutputBytes = attempt => sizes[attempt - 1] };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(10), Compression = CompressionPreset.TargetSize, TargetBytes = 2_500_000, Encoder = EncoderPreference.Gpu });
        // h264_amf held about 1 Mbit/s whatever lower bitrate it was given, and the
        // export failed after three tries; libx264 met the same limit.
        Assert.Equal(new[] { "h264_amf", "libx264" }, scenario.Exports.Select(call => MediaScenario.Argument(call, "-c:v")));
        Assert.Equal(new[] { "1688k", "1688k" }, scenario.Exports.Select(call => MediaScenario.Argument(call, "-b:v")));
        Assert.Equal(2_400_000, result.Probe.SizeBytes);
        Assert.Contains("could not meet the size limit, so the CPU encoder was used", result.Warning);
    }

    [Fact]
    public async Task Target_size_lowers_hardware_once_for_a_small_miss_then_uses_the_cpu()
    {
        using var files = new EngineTestFiles();
        var sizes = new[] { 2_600_000L, 2_550_000L, 2_400_000L };
        var scenario = new MediaScenario { UsableHardware = { "h264_amf" }, OutputBytes = attempt => sizes[attempt - 1] };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(10), Compression = CompressionPreset.TargetSize, TargetBytes = 2_500_000, Encoder = EncoderPreference.Gpu });
        Assert.Equal(new[] { "h264_amf", "h264_amf", "libx264" }, scenario.Exports.Select(call => MediaScenario.Argument(call, "-c:v")));
        Assert.Equal(new[] { "1688k", "1493k", "1688k" }, scenario.Exports.Select(call => MediaScenario.Argument(call, "-b:v")));
        Assert.Contains("CPU encoder was used", result.Warning);
    }

    [Fact]
    public async Task Hardware_result_a_quarter_above_the_preset_aim_is_reported()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[10_000_000]);
        // Balanced aims at (1500 + 192) kbit/s for 10 s: 2,115,000 bytes. 1.28x got no note at the old 1.5x threshold.
        var scenario = new MediaScenario { SourceSeconds = 10, VideoBitRate = 3_000_000, UsableHardware = { "h264_amf" }, OutputBytes = _ => 2_700_000 };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Compression = CompressionPreset.Balanced, Encoder = EncoderPreference.Gpu });
        Assert.Single(scenario.Exports);
        Assert.Contains("h264_amf could not compress this clip as far as the preset aims", result.Warning);
    }

    [Fact]
    public void Audio_only_size_limit_estimate_uses_the_bitrate_the_encoder_gets()
    {
        var service = new MediaService(new MediaFakeRunner((_, _, _, _) => throw new InvalidOperationException()), new EngineFakeComponents());
        var probe = new ProbeInfo("source.mp4", TimeSpan.FromMinutes(1), 10_000_000, true, true, 1920, 1080, 30, "h264", "aac", 48000, 2, MediaScenario.SourceJson(60, video: true));
        var soundboard = new ExportOptions { InputPath = "source.mp4", OutputPath = "sound.mp3", Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(8), AudioOnly = true, AudioFormat = AudioFormat.Mp3, DiscordSoundboard = true };
        // It showed the 500,000-byte limit; the real 5 s MP3 was 121,683 bytes.
        Assert.Equal((long)Math.Ceiling(5 * 192 * 125 * 1.03), service.Estimate(probe, soundboard).EstimatedBytes);
        // A tight limit lowers the bitrate (floor(100,000 x 0.9 x 8 / 5 / 1000) = 144) and never exceeds the limit.
        var tight = soundboard with { SoundboardTargetBytes = 100_000 };
        Assert.Equal((long)Math.Ceiling(5 * 144 * 125 * 1.03), service.Estimate(probe, tight).EstimatedBytes);
        Assert.True(service.Estimate(probe, tight).EstimatedBytes <= 100_000);
        Assert.Throws<MediaOperationException>(() => service.Estimate(probe, soundboard with { SoundboardTargetBytes = 2_000 }));
    }

    [Fact]
    public async Task Auto_keeps_hardware_for_uncompressed_exports()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { UsableHardware = { "h264_amf" } };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5) });
        Assert.Equal("h264_amf", MediaScenario.Argument(Assert.Single(scenario.Exports), "-c:v"));
    }

    [Fact]
    public async Task Preset_larger_than_the_source_moves_to_the_cpu_then_lowers_the_cap_and_says_so()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[1_000_000]);
        var sizes = new[] { 1_500_000L, 1_200_000L, 1_100_000L };
        var scenario = new MediaScenario { SourceSeconds = 10, VideoBitRate = 3_000_000, UsableHardware = { "h264_amf" }, OutputBytes = attempt => sizes[attempt - 1] };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Compression = CompressionPreset.Balanced, Encoder = EncoderPreference.Gpu });
        var encodes = scenario.Exports;
        Assert.Equal(new[] { "h264_amf", "libx264", "libx264" }, encodes.Select(call => MediaScenario.Argument(call, "-c:v")));
        Assert.Equal("1500k", MediaScenario.Argument(encodes[1], "-maxrate"));
        Assert.Equal("900k", MediaScenario.Argument(encodes[2], "-maxrate"));
        Assert.Contains("CPU encoder was used", result.Warning);
        Assert.Contains("not smaller than the matching part of the original", result.Warning);
    }

    [Theory]
    [InlineData(EncoderPreference.Cpu, new[] { "libx264" })]
    [InlineData(EncoderPreference.Gpu, new[] { "h264_amf", "libx264" })]
    public async Task Preset_at_the_cap_floor_does_not_repeat_the_same_encode(EncoderPreference preference, string[] expected)
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[600_000]);
        // 10 kbit/s video caps Light at the 32 kbit/s floor, so a "lower" cap was
        // the same command again: a whole second encode, still 1% larger.
        var scenario = new MediaScenario { VideoBitRate = 10_000, AudioBitRate = 64_000, UsableHardware = { "h264_amf" }, OutputBytes = _ => 620_000 };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Compression = CompressionPreset.Light, Encoder = preference });
        Assert.Equal(expected, scenario.Exports.Select(call => MediaScenario.Argument(call, "-c:v")));
        Assert.Equal("32k", MediaScenario.Argument(scenario.Exports[^1], "-maxrate"));
        // 64 kbit/s audio for 60 s is 480,000 of the original's 600,000 bytes.
        Assert.Contains("audio and the container already take up most of the original's size", result.Warning);
        Assert.DoesNotContain("efficiently encoded", result.Warning);
        // Both sizes once read "0.6 MB".
        Assert.Contains("compressed clip (0.62 MB) is not smaller than the matching part of the original (0.60 MB)", result.Warning);
    }

    [Theory]
    [InlineData(618_494, 613_251, "0.62 MB", "0.61 MB")]
    [InlineData(2_880_000, 2_100_000, "2.9 MB", "2.1 MB")]
    [InlineData(12_340_000, 12_100_000, "12.3 MB", "12.1 MB")]
    [InlineData(45_000_000, 30_000_000, "45 MB", "30 MB")]
    [InlineData(600_000, 600_000, "0.600 MB", "0.600 MB")]
    public void Size_note_uses_enough_decimals_to_tell_the_sizes_apart(double first, double second, string firstText, string secondText) =>
        Assert.Equal((firstText, secondText), MediaService.DistinctMegabytes(first, second));

    [Fact]
    public async Task Preset_whose_audio_fills_the_original_does_not_lower_the_cap()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[170_000]);
        // 128 kbit/s audio for 10 s is 160,000 bytes, 94% of the original, so no
        // video cap can bring the clip below it.
        var scenario = new MediaScenario { SourceSeconds = 10, VideoBitRate = 200_000, AudioBitRate = 128_000, OutputBytes = _ => 180_000 };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Compression = CompressionPreset.Light, Encoder = EncoderPreference.Cpu });
        Assert.Equal("140k", MediaScenario.Argument(Assert.Single(scenario.Exports), "-maxrate"));
        Assert.Contains("audio and the container already take up most of the original's size", result.Warning);
    }

    [Fact]
    public async Task Cpu_retry_that_compresses_is_kept_without_a_size_warning()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 10, VideoBitRate = 3_000_000, UsableHardware = { "h264_amf" }, OutputBytes = attempt => attempt == 1 ? 1_300_000 : 600_000 };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Compression = CompressionPreset.Balanced, Encoder = EncoderPreference.Gpu });
        Assert.Equal(2, scenario.Exports.Count);
        Assert.Equal(600_000, result.Probe.SizeBytes);
        Assert.Contains("CPU encoder was used", result.Warning);
        Assert.DoesNotContain("not smaller", result.Warning);
    }

    [Fact]
    public async Task Hardware_result_far_above_the_preset_aim_is_reported()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        File.WriteAllBytes(input, new byte[10_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 10, VideoBitRate = 3_000_000, UsableHardware = { "h264_amf" }, OutputBytes = _ => 3_500_000 };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Compression = CompressionPreset.Balanced, Encoder = EncoderPreference.Gpu });
        Assert.Single(scenario.Exports);
        Assert.Contains("h264_amf could not compress this clip as far as the preset aims", result.Warning);
    }

    [Theory]
    [InlineData("smpte2084", "bt2020", true)]
    [InlineData("arib-std-b67", "bt2020", true)]
    [InlineData("bt709", "bt709", false)]
    [InlineData(null, null, false)]
    public async Task Hdr_to_sdr_only_tone_maps_pq_or_hlg_sources(string? transfer, string? primaries, bool toneMapped)
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { Transfer = transfer, Primaries = primaries };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), HdrToSdr = true, Encoder = EncoderPreference.Cpu });
        var encode = Assert.Single(scenario.Exports);
        if (toneMapped)
        {
            // Explicit input tags give zscale a path; untagged input failed with 'no path between colorspaces'.
            Assert.Contains($"zscale=tin={transfer}:pin=bt2020:min=bt2020nc:rin=tv", MediaScenario.Argument(encode, "-vf"));
            Assert.Equal("bt709", MediaScenario.Argument(encode, "-color_trc"));
            Assert.Null(result.Warning);
        }
        else
        {
            // SDR sources were tone mapped darker (YAVG 126 -> 85) or failed.
            Assert.DoesNotContain(encode.Arguments, argument => argument.Contains("zscale", StringComparison.Ordinal) || argument.Contains("tonemap", StringComparison.Ordinal));
            Assert.Contains("already SDR", result.Warning);
        }
        Assert.Equal(toneMapped, MediaService.IsHdr(await scenario.Service().ProbeAsync(Path.Combine(files.Root, "source.mp4"))));
    }

    [Fact]
    public async Task Hdr_source_without_tone_mapping_filters_fails_clearly_before_encoding()
    {
        using var files = new EngineTestFiles();
        var output = Path.Combine(files.Root, "clip.mp4");
        var scenario = new MediaScenario { Transfer = "smpte2084", Primaries = "bt2020", Filters = " .S tonemap           V->V       Conversion to/from different dynamic ranges.\n" };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = output, End = TimeSpan.FromSeconds(5), HdrToSdr = true, Encoder = EncoderPreference.Cpu }));
        Assert.Contains("zscale", error.Message);
        Assert.Empty(scenario.Exports);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Hdr_chain_runs_after_downscaling()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { Transfer = "smpte2084", Primaries = "bt2020" };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), HdrToSdr = true, MaxHeight = 720, Encoder = EncoderPreference.Cpu });
        var filters = MediaScenario.Argument(Assert.Single(scenario.Exports), "-vf");
        Assert.True(filters.IndexOf("scale=-2", StringComparison.Ordinal) < filters.IndexOf("zscale", StringComparison.Ordinal), filters);
    }

    [Fact]
    public async Task Encoder_list_skips_the_legend_and_unusable_hardware()
    {
        var scenario = new MediaScenario
        {
            Encoders = "Encoders:\n V..... = Video\n A..... = Audio\n S..... = Subtitle\n .F.... = Frame-level multithreading\n ------\n V....D a64multi             Multicolor charset for Commodore 64 (codec a64_multi)\n V....D libx264              libx264 H.264\n V....D h264_amf             AMD AMF H.264 Encoder (codec h264)\n V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)\n V..... h264_qsv             H.264 (Intel Quick Sync Video acceleration) (codec h264)\n A....D aac                  AAC (Advanced Audio Coding)\n",
            UsableHardware = { "h264_amf" }
        };
        var encoders = await scenario.Service().EncodersAsync();
        Assert.Equal(new[] { "libx264", "h264_amf", "aac" }, encoders.Select(encoder => encoder.Name));
        Assert.True(encoders.Single(encoder => encoder.Name == "h264_amf").IsHardware);
        Assert.DoesNotContain(MediaService.ParseEncoders(scenario.Encoders), encoder => encoder.Name == "=");
    }

    [Fact]
    public async Task Hardware_probe_that_ran_out_of_time_is_tested_again_once()
    {
        // With a game running, FFmpeg children sat unstarted for up to 38 s; one
        // slow test encode marked AMF unusable for the whole session.
        var scenario = new MediaScenario { UsableHardware = { "h264_amf" }, ProbeHangs = { ["h264_amf"] = 1 } };
        var service = scenario.Service(hardwareProbe: TimeSpan.FromMilliseconds(100));
        Assert.DoesNotContain("h264_amf", (await service.EncodersAsync()).Select(encoder => encoder.Name));
        Assert.Contains("h264_amf", (await service.EncodersAsync()).Select(encoder => encoder.Name));
        Assert.Contains("h264_amf", (await service.EncodersAsync()).Select(encoder => encoder.Name));
        Assert.Equal(2, scenario.HardwareProbes.Count(name => name == "h264_amf"));
    }

    [Fact]
    public async Task Hardware_probe_that_runs_out_of_time_twice_is_not_tried_again()
    {
        // A hanging driver costs two waits per session, not one per export.
        var scenario = new MediaScenario { UsableHardware = { "h264_amf" }, ProbeHangs = { ["h264_amf"] = 10 } };
        var service = scenario.Service(hardwareProbe: TimeSpan.FromMilliseconds(100));
        for (var call = 0; call < 4; call++) Assert.DoesNotContain("h264_amf", (await service.EncodersAsync()).Select(encoder => encoder.Name));
        Assert.Equal(2, scenario.HardwareProbes.Count(name => name == "h264_amf"));
    }

    [Fact]
    public async Task Cancelled_hardware_probe_is_not_counted_against_the_encoder()
    {
        var scenario = new MediaScenario { UsableHardware = { "h264_amf" }, ProbeHangs = { ["h264_amf"] = 2 } };
        var service = scenario.Service();
        for (var call = 0; call < 2; call++)
        {
            // The user cancels while the test encode is running.
            using var cancel = new CancellationTokenSource();
            scenario.OnProbeHang = _ => cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EncodersAsync(cancel.Token));
        }
        scenario.OnProbeHang = null;
        Assert.Contains("h264_amf", (await service.EncodersAsync()).Select(encoder => encoder.Name));
        Assert.Equal(3, scenario.HardwareProbes.Count(name => name == "h264_amf"));
    }

    [Fact]
    public async Task Quick_inspections_allow_for_a_slow_process_start()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { Transfer = "smpte2084", Primaries = "bt2020" };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), HdrToSdr = true, Encoder = EncoderPreference.Cpu });
        // FFprobe had 45 s and the encoder and filter lists 30 s, counted from
        // before a child that can sit unstarted for 38 s on a busy PC.
        var inspections = scenario.Calls.Where(call => call.Executable == "fake-ffprobe" || call.Arguments.Contains("-encoders") || call.Arguments.Contains("-filters")).ToArray();
        Assert.Equal(4, inspections.Length);
        Assert.All(inspections, call => Assert.Equal(TimeSpan.FromMinutes(2), call.Timeout));
    }

    [Fact]
    public async Task Gpu_export_uses_only_probed_hardware_and_probes_once_per_session()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { Encoders = MediaScenario.Listing("h264_amf", "h264_nvenc", "h264_qsv", "libx264"), UsableHardware = { "h264_nvenc" } };
        var service = scenario.Service();
        for (var run = 0; run < 2; run++)
        {
            var result = await service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, $"clip{run}.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Gpu });
            Assert.Null(result.Warning);
        }
        Assert.All(scenario.Exports, call => Assert.Equal("h264_nvenc", MediaScenario.Argument(call, "-c:v")));
        Assert.Equal(new[] { "h264_amf", "h264_nvenc" }, scenario.HardwareProbes);
        // The NVENC default 2 Mbit/s target must not cap constant-quality mode.
        Assert.Equal("0", MediaScenario.Argument(scenario.Exports[0], "-b:v"));
        Assert.Equal("18", MediaScenario.Argument(scenario.Exports[0], "-cq"));
    }

    [Theory]
    [InlineData(new[] { "s0", "e0.5", "s1.5", "e2.1", "s3.1", "e3.6" }, 3.6, 3.6, 0.5, 3.1)]
    [InlineData(new[] { "s0", "e0.5", "s3.1" }, 3.6, 3.6, 0.5, 3.1)]
    [InlineData(new[] { "s1.5", "e2.1" }, 3.6, 3.6, 0, 3.6)]
    [InlineData(new string[0], 3.6, 3.6, 0, 3.6)]
    [InlineData(new[] { "s2.5", "e3" }, 3.0, 3.6, 0, 2.5)]
    public void Silence_bounds_trim_only_the_start_and_end(string[] events, double decoded, double requested, double start, double end)
    {
        var parsed = events.Select(item => (item[0] == 's', double.Parse(item[1..], CultureInfo.InvariantCulture))).ToArray();
        var bounds = MediaService.SilenceBounds(parsed, decoded, requested);
        Assert.NotNull(bounds);
        Assert.Equal(start, bounds.Value.Start, 6);
        Assert.Equal(end, bounds.Value.End, 6);
    }

    [Fact]
    public void Fully_silent_selection_has_no_bounds()
    {
        Assert.Null(MediaService.SilenceBounds([(true, 0), (false, 6)], 6, 6));
    }

    [Fact]
    public async Task Trim_silence_cuts_audio_and_video_to_the_first_and_last_sound()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario
        {
            SilenceLines = ["[silencedetect @ 0000] silence_start: 0", "[silencedetect @ 0000] silence_end: 1.25 | silence_duration: 1.25", "[silencedetect @ 0000] silence_start: 4", "[silencedetect @ 0000] silence_end: 4.6 | silence_duration: 0.6", "[silencedetect @ 0000] silence_start: 8.5", "[silencedetect @ 0000] silence_end: 10 | silence_duration: 1.5"],
            DecodedMicroseconds = 10_000_000
        };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), TrimSilence = true, Encoder = EncoderPreference.Cpu });
        var detect = Assert.Single(scenario.Detections).Arguments.ToList();
        Assert.Equal(new[] { "-ss", "10", "-t", "10", "-i" }, detect.GetRange(detect.IndexOf("-i") - 4, 5));
        // Audio starting after the video left its leading gap undetected without the padding.
        Assert.Equal("aresample=async=1:first_pts=0,silencedetect=n=-45dB:d=0.05", detect[detect.IndexOf("-af") + 1]);
        var encode = Assert.Single(scenario.Exports);
        var arguments = encode.Arguments.ToList();
        // Both streams take the trimmed range, so the 0.6 s pause stays and video keeps sync.
        Assert.Equal(new[] { "-ss", "11.25", "-i" }, arguments.GetRange(arguments.IndexOf("-i") - 2, 3));
        Assert.Equal("7.25", arguments[arguments.IndexOf("-i") + 3]);
        Assert.Contains("0:v:0?", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("silenceremove", StringComparison.Ordinal) || argument.Contains("areverse", StringComparison.Ordinal));
        Assert.Equal(7.25, result.Probe.Duration.TotalSeconds, 3);
    }

    [Fact]
    public async Task Trim_silence_on_a_silent_selection_fails_before_encoding()
    {
        using var files = new EngineTestFiles();
        var output = Path.Combine(files.Root, "clip.mp3");
        var scenario = new MediaScenario { SilenceLines = ["silence_start: 0", "silence_end: 6 | silence_duration: 6"], DecodedMicroseconds = 6_000_000 };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = output, Start = TimeSpan.FromSeconds(12), End = TimeSpan.FromSeconds(18), TrimSilence = true, AudioOnly = true }));
        Assert.Contains("silent", error.Message);
        Assert.Empty(scenario.Exports);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Theory]
    [InlineData(AudioFormat.Wav, ".wav", 48000, "48000")]
    [InlineData(AudioFormat.Flac, ".flac", 96000, "96000")]
    [InlineData(AudioFormat.Aac, ".m4a", 96000, "48000")]
    [InlineData(AudioFormat.Ogg, ".ogg", 48000, "48000")]
    [InlineData(AudioFormat.Mp3, ".mp3", 88200, "44100")]
    public async Task Normalized_audio_keeps_a_sample_rate_the_format_supports(AudioFormat format, string extension, int sourceRate, string expected)
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { SampleRate = sourceRate };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip" + extension), End = TimeSpan.FromSeconds(5), AudioOnly = true, AudioFormat = format, Normalize = true });
        // loudnorm alone upsampled to 192 kHz (WAV/FLAC) or 96 kHz (AAC) and broke Vorbis.
        Assert.Equal(expected, MediaScenario.Argument(Assert.Single(scenario.Exports), "-ar"));
    }

    [Fact]
    public async Task Opus_snaps_an_unsupported_rate_and_says_so()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario();
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.opus"), End = TimeSpan.FromSeconds(5), AudioOnly = true, AudioFormat = AudioFormat.Opus, SampleRate = 44100 });
        Assert.Equal("48000", MediaScenario.Argument(Assert.Single(scenario.Exports), "-ar"));
        Assert.Contains("48 kHz", result.Warning);
    }

    [Fact]
    public async Task Normalize_keeps_fades_on_a_clean_sample_clock_and_ends_on_the_exact_sample()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario();
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.ogg"), Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(25.5), AudioOnly = true, AudioFormat = AudioFormat.Ogg, Normalize = true, FadeIn = TimeSpan.FromSeconds(1), FadeOut = TimeSpan.FromSeconds(1) });
        // loudnorm shifted timestamps: the fade-out ended 0.07 s early and the clip ran up to 0.1 s long.
        // asetpts=N/SR/TB renumbers from zero, so the clock must already be continuous from the clip start.
        Assert.Equal("aresample=async=1:first_pts=0,loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000,asetpts=N/SR/TB,afade=t=in:st=0:d=1,afade=t=out:st=4.5:d=1,atrim=end_sample=264000", MediaScenario.Argument(Assert.Single(scenario.Exports), "-af"));
    }

    [Fact]
    public async Task Whole_file_mp4_conversion_keeps_chapters_cover_and_compatible_audio()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { SourceOverride = ExtrasSource() };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input("source.mkv"), OutputPath = Path.Combine(files.Root, "converted.mp4"), MaxHeight = 720, Encoder = EncoderPreference.Cpu }, PreserveExtras.All);
        var arguments = Assert.Single(scenario.Exports).Arguments.ToList();
        Assert.Equal("0", MediaScenario.Argument(scenario.Exports[0], "-map_chapters"));
        // Main video by index, then the cover as the second video stream, copied and marked.
        Assert.Equal(new[] { "-map", "0:0", "-map", "0:a:0?", "-map", "0:2" }, arguments.GetRange(arguments.IndexOf("-map"), 6));
        Assert.True(arguments.IndexOf("-c:v:1") > arguments.IndexOf("-c:v"));
        Assert.Equal("copy", MediaScenario.Argument(scenario.Exports[0], "-c:v:1"));
        Assert.Equal("attached_pic", MediaScenario.Argument(scenario.Exports[0], "-disposition:v:1"));
        // FFmpeg refused an unscoped -vf next to a stream-copied cover.
        Assert.StartsWith("scale=", MediaScenario.Argument(scenario.Exports[0], "-filter:v:0"));
        Assert.DoesNotContain("-vf", arguments);
        Assert.Equal("copy", MediaScenario.Argument(scenario.Exports[0], "-c:a"));
        Assert.DoesNotContain("-b:a", arguments);
        Assert.DoesNotContain("0:t?", arguments);
        Assert.DoesNotContain("-attach", arguments);
        Assert.Empty(scenario.CoverExtractions);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task Mkv_conversion_attaches_the_extracted_cover_after_the_source_attachments()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { SourceOverride = ExtrasSource(audioCodec: "opus"), Encoders = MediaScenario.Listing("libx265", "libx264") };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input("source.mkv"), OutputPath = Path.Combine(files.Root, "converted.mkv"), VideoCodec = VideoCodec.Hevc, Encoder = EncoderPreference.Cpu }, PreserveExtras.All);
        var extraction = Assert.Single(scenario.CoverExtractions);
        Assert.Equal("0:2", MediaScenario.Argument(extraction, "-map"));
        Assert.Equal("copy", MediaScenario.Argument(extraction, "-c"));
        Assert.EndsWith("cover.png", extraction.Arguments[^1]);
        var encode = Assert.Single(scenario.Exports);
        var arguments = encode.Arguments.ToList();
        // A mapped cover became a one-frame video track in Matroska; an attachment stays a cover.
        Assert.DoesNotContain("0:2", arguments);
        Assert.DoesNotContain("-c:v:1", arguments);
        Assert.Equal("0:t?", arguments[arguments.LastIndexOf("-map") + 1]);
        Assert.Equal("copy", MediaScenario.Argument(encode, "-c:t"));
        Assert.Equal(extraction.Arguments[^1], MediaScenario.Argument(encode, "-attach"));
        Assert.Equal(new[] { "-metadata:s:t:1", "mimetype=image/png", "-metadata:s:t:1", "filename=cover.png" }, arguments.GetRange(arguments.IndexOf("-attach") + 2, 4));
        Assert.Equal("copy", MediaScenario.Argument(encode, "-c:a"));
    }

    [Fact]
    public async Task Lossless_remux_to_mp4_keeps_the_cover_marked_as_a_picture()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { SourceOverride = ExtrasSource() };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input("source.mkv"), OutputPath = Path.Combine(files.Root, "remuxed.mp4"), ClipMode = ClipMode.Lossless, VideoCodec = VideoCodec.Original, AudioFormat = AudioFormat.Original }, PreserveExtras.All);
        var encode = Assert.Single(scenario.Exports);
        Assert.Equal("copy", MediaScenario.Argument(encode, "-c"));
        Assert.Equal("0", MediaScenario.Argument(encode, "-map_chapters"));
        Assert.Contains("0:2", encode.Arguments);
        Assert.Equal("attached_pic", MediaScenario.Argument(encode, "-disposition:v:1"));
    }

    [Fact]
    public async Task Webm_keeps_chapters_only_and_reencodes_audio_it_cannot_hold()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { SourceOverride = ExtrasSource(), Encoders = MediaScenario.Listing("libaom-av1", "libx264") };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input("source.mkv"), OutputPath = Path.Combine(files.Root, "converted.webm"), VideoCodec = VideoCodec.Av1, Encoder = EncoderPreference.Cpu }, PreserveExtras.All);
        var encode = Assert.Single(scenario.Exports);
        Assert.Equal("0", MediaScenario.Argument(encode, "-map_chapters"));
        Assert.DoesNotContain("0:2", encode.Arguments);
        Assert.DoesNotContain("-attach", encode.Arguments);
        Assert.Equal("libopus", MediaScenario.Argument(encode, "-c:a"));
        Assert.Empty(scenario.CoverExtractions);
    }

    [Fact]
    public async Task Clips_audio_changes_and_default_exports_do_not_carry_extras()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { SourceOverride = ExtrasSource() };
        var service = scenario.Service();
        var input = files.Input("source.mkv");
        await service.ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(4), Encoder = EncoderPreference.Cpu }, PreserveExtras.All);
        await service.ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "louder.mp4"), Volume = 2, Encoder = EncoderPreference.Cpu }, PreserveExtras.All);
        await service.ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "plain.mp4"), Encoder = EncoderPreference.Cpu });
        var (clip, louder, plain) = (scenario.Exports[0], scenario.Exports[1], scenario.Exports[2]);
        // Chapters and covers describe the whole source; a clip drops them.
        Assert.Equal("-1", MediaScenario.Argument(clip, "-map_chapters"));
        Assert.DoesNotContain("0:2", clip.Arguments);
        Assert.Equal("aac", MediaScenario.Argument(clip, "-c:a"));
        // A requested audio change is never skipped by copying.
        Assert.Equal("0", MediaScenario.Argument(louder, "-map_chapters"));
        Assert.Equal("aac", MediaScenario.Argument(louder, "-c:a"));
        Assert.Equal("aresample=async=1:first_pts=0,volume=2", MediaScenario.Argument(louder, "-af"));
        // Without extras the export behaves as before.
        Assert.Equal("-1", MediaScenario.Argument(plain, "-map_chapters"));
        Assert.DoesNotContain("0:2", plain.Arguments);
        Assert.Equal("aac", MediaScenario.Argument(plain, "-c:a"));
    }

    [Fact]
    public async Task Failed_cover_extraction_still_exports_and_says_so()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { SourceOverride = ExtrasSource(), FailCoverExtraction = true };
        var result = await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input("source.mkv"), OutputPath = Path.Combine(files.Root, "converted.mkv"), Encoder = EncoderPreference.Cpu }, PreserveExtras.All);
        Assert.DoesNotContain("-attach", Assert.Single(scenario.Exports).Arguments);
        Assert.Contains("cover picture could not be carried over", result.Warning);
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    private static string ExtrasSource(string audioCodec = "aac") => new JsonObject
    {
        ["format"] = new JsonObject { ["duration"] = "60" },
        ["streams"] = new JsonArray
        {
            new JsonObject { ["index"] = 0, ["codec_type"] = "video", ["codec_name"] = "h264", ["width"] = 1920, ["height"] = 1080, ["avg_frame_rate"] = "30/1" },
            new JsonObject { ["index"] = 1, ["codec_type"] = "audio", ["codec_name"] = audioCodec, ["sample_rate"] = "48000", ["channels"] = 2 },
            // FFmpeg shows a Matroska image attachment, or an MP4 cover, as a picture stream.
            new JsonObject { ["index"] = 2, ["codec_type"] = "video", ["codec_name"] = "png", ["disposition"] = new JsonObject { ["attached_pic"] = 1 } },
            new JsonObject { ["index"] = 3, ["codec_type"] = "attachment", ["codec_name"] = "none" }
        }
    }.ToJsonString();

    [Theory]
    [InlineData("libopus", 44100, 48000)]
    [InlineData("libopus", 24000, 24000)]
    [InlineData("libmp3lame", 96000, 48000)]
    [InlineData("aac", 88200, 44100)]
    [InlineData("libvorbis", 22050, 22050)]
    [InlineData("aac", 7350, 8000)]
    [InlineData("pcm_s16le", 96000, 96000)]
    public void Sample_rates_snap_to_encoder_supported_values(string codec, int requested, int expected) => Assert.Equal(expected, MediaService.SupportedSampleRate(codec, requested));

    [Fact]
    public async Task Cpu_av1_uses_row_threading_and_a_fast_speed()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario { Encoders = MediaScenario.Listing("libaom-av1", "libx264") };
        await scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mkv"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu, VideoCodec = VideoCodec.Av1 });
        var encode = Assert.Single(scenario.Exports);
        Assert.Equal("libaom-av1", MediaScenario.Argument(encode, "-c:v"));
        Assert.Equal("1", MediaScenario.Argument(encode, "-row-mt"));
        Assert.Equal("realtime", MediaScenario.Argument(encode, "-usage"));
    }

    [Fact]
    public async Task Waveform_never_continues_on_the_callers_context()
    {
        using var files = new EngineTestFiles();
        var runner = new MediaFakeRunner(async (request, _, _, cancellationToken) =>
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false); // completes later, like a real process
            if (request.Executable == "fake-ffprobe") return new ProcessResult(0, EngineFakeProcess.ProbeJson(), "");
            WriteWave(request.Arguments[^1], Enumerable.Range(0, 4000).Select(index => (short)(index % 2000)).ToArray());
            return new ProcessResult(0, "", "");
        });
        var service = new MediaService(runner, new EngineFakeComponents());
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        Task<IReadOnlyList<float>> waveform;
        SynchronizationContext.SetSynchronizationContext(context);
        try { waveform = service.WaveformAsync(files.Input(), 64); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        // The UI context here never runs posted work; continuing on it would hang.
        var points = await waveform.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(64, points.Count);
        Assert.Equal(0, context.Posts);
    }

    [Fact]
    public void Waveform_peaks_match_the_reference_bucket_algorithm()
    {
        using var files = new EngineTestFiles();
        var random = new Random(1234);
        var samples = Enumerable.Range(0, 100_003).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1)).ToArray();
        samples[77_777] = short.MinValue;
        var path = Path.Combine(files.Root, "wave.wav");
        WriteWave(path, samples);
        foreach (var points in new[] { 16, 600, 4096 })
            Assert.Equal(ReferencePeaks(samples, points), MediaService.ReadWaveform(path, points, CancellationToken.None));
    }

    [Fact]
    public async Task Lossless_audio_cut_starts_at_the_selection_not_the_video_keyframe()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario();
        var service = scenario.Service();
        var audio = await service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.m4a"), Start = TimeSpan.FromSeconds(11), End = TimeSpan.FromSeconds(14), AudioOnly = true, ClipMode = ClipMode.Lossless, AudioFormat = AudioFormat.Original });
        // Without it, a copy from 11 s with keyframes every 4 s began at 8 s: 6.1 s instead of 3 s.
        Assert.Equal("0", MediaScenario.Argument(scenario.Exports[0], "-copypriorss"));
        Assert.Null(audio.Warning);
        var video = await service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mkv"), Start = TimeSpan.FromSeconds(11), End = TimeSpan.FromSeconds(14), ClipMode = ClipMode.Lossless, VideoCodec = VideoCodec.Original, AudioFormat = AudioFormat.Original });
        // Video copies must keep the packets from the keyframe so the clip decodes.
        Assert.DoesNotContain("-copypriorss", scenario.Exports[1].Arguments);
        Assert.Contains("keyframes", video.Warning);
        // A whole-file container change has no cut, so no keyframe note.
        var remux = await service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "whole.mkv"), ClipMode = ClipMode.Lossless, VideoCodec = VideoCodec.Original, AudioFormat = AudioFormat.Original });
        Assert.Null(remux.Warning);
    }

    [Fact]
    public void Lossless_audio_estimate_uses_the_audio_stream_bitrate()
    {
        var service = new MediaService(new MediaFakeRunner((_, _, _, _) => throw new InvalidOperationException()), new EngineFakeComponents());
        var json = MediaScenario.SourceJson(600, video: true, audioBitRate: 160_000);
        var probe = new ProbeInfo("source.mp4", TimeSpan.FromMinutes(10), 842_000_000, true, true, 1920, 1080, 30, "h264", "aac", 48000, 2, json);
        var options = new ExportOptions { InputPath = "source.mp4", OutputPath = "clip.m4a", End = TimeSpan.FromSeconds(5), AudioOnly = true, ClipMode = ClipMode.Lossless, AudioFormat = AudioFormat.Original };
        // The whole-file share would claim about 7 MB for 5 s of 160 kbit/s audio.
        Assert.InRange(service.Estimate(probe, options).EstimatedBytes!.Value, 95_000, 110_000);
        Assert.Null(service.Estimate(probe with { TechnicalJson = MediaScenario.SourceJson(600, video: true) }, options).EstimatedBytes);
    }

    [Fact]
    public void Wav_and_flac_estimates_are_honest()
    {
        var service = new MediaService(new MediaFakeRunner((_, _, _, _) => throw new InvalidOperationException()), new EngineFakeComponents());
        var probe = new ProbeInfo("source.mp4", TimeSpan.FromMinutes(2), 10_000_000, true, true, 1920, 1080, 30, "h264", "aac", 48000, 2, MediaScenario.SourceJson(120, video: true));
        var wav = new ExportOptions { InputPath = "source.mp4", OutputPath = "clip.wav", End = TimeSpan.FromSeconds(60), AudioOnly = true, AudioFormat = AudioFormat.Wav };
        Assert.Equal(44 + 48_000L * 2 * 2 * 60, service.Estimate(probe, wav).EstimatedBytes);
        Assert.Equal(44 + 48_000L * 1 * 2 * 60, service.Estimate(probe, wav with { Mono = true }).EstimatedBytes);
        var flac = service.Estimate(probe, wav with { OutputPath = "clip.flac", AudioFormat = AudioFormat.Flac });
        Assert.Null(flac.EstimatedBytes);
        Assert.Contains("FLAC is lossless", flac.Explanation);
        // A plain tone measured 77% of the WAV size, so the range ends near it and states the ceiling.
        Assert.Contains("4.6 MB–9.2 MB, at most about 12 MB", flac.Explanation);
        Assert.Throws<MediaOperationException>(() => service.Estimate(probe, wav with { Compression = CompressionPreset.TargetSize, TargetBytes = 1_000_000 }));
    }

    [Theory]
    [InlineData(AudioFormat.Wav, ".wav")]
    [InlineData(AudioFormat.Flac, ".flac")]
    public async Task Lossless_audio_formats_reject_a_size_limit_before_encoding(AudioFormat format, string extension)
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario();
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip" + extension), End = TimeSpan.FromSeconds(60), AudioOnly = true, AudioFormat = format, Compression = CompressionPreset.TargetSize, TargetBytes = 1_000_000 }));
        Assert.Contains("cannot be limited", error.Message);
        Assert.Empty(scenario.Exports);
    }

    [Fact]
    public async Task Soundboard_with_a_lossless_format_names_the_supported_formats()
    {
        using var files = new EngineTestFiles();
        var scenario = new MediaScenario();
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => scenario.Service().ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "sound.wav"), End = TimeSpan.FromSeconds(2), AudioOnly = true, AudioFormat = AudioFormat.Wav, DiscordSoundboard = true }));
        Assert.Contains("MP3 (.mp3) or Ogg Vorbis (.ogg)", error.Message);
    }

    private static float[] ReferencePeaks(short[] samples, int points)
    {
        // The original per-sample loop, kept as the reference for the bulk reader.
        var count = Math.Min(points, samples.Length);
        var peaks = new float[count];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var peak = Math.Abs((int)samples[sample]) / 32768f;
            var index = Math.Min(count - 1, (int)((long)sample * count / samples.Length));
            peaks[index] = Math.Max(peaks[index], peak);
        }
        return peaks;
    }

    private static void WriteWave(string path, short[] samples)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        var bytes = samples.Length * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
        foreach (var sample in samples) writer.Write(sample);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int posts;
        public int Posts => Volatile.Read(ref posts);
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref posts);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref posts);
    }
}

internal sealed class MediaFakeRunner(Func<ProcessRequest, Action<string>?, Action<string>?, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
{
    private readonly List<ProcessRequest> calls = [];
    public IReadOnlyList<ProcessRequest> Calls { get { lock (calls) return calls.ToArray(); } }
    public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (calls) calls.Add(request);
        return handler(request, stdout, stderr, cancellationToken);
    }
}

/// <summary>A scripted FFmpeg/FFprobe: source metadata, encoder list, hardware probes, silence detection and exports.</summary>
internal sealed class MediaScenario
{
    private readonly Dictionary<string, (double Seconds, bool Video, string? Audio)> outputs = new(StringComparer.OrdinalIgnoreCase);
    private int attempts;
    private MediaFakeRunner? runner;

    public double SourceSeconds { get; init; } = 60;
    public long? VideoBitRate { get; init; }
    public long? AudioBitRate { get; init; }
    /// <summary>FFprobe's container name, such as "mpegts"; null leaves it out.</summary>
    public string? FormatName { get; init; }
    public int SampleRate { get; init; } = 48000;
    public string? Transfer { get; init; }
    public string? Primaries { get; init; }
    public string Encoders { get; init; } = Listing("libx264", "h264_amf");
    // Hardware encoders whose one-frame test encode succeeds; empty means none
    // works. Tests add to it with a collection initializer.
    public HashSet<string> UsableHardware { get; init; } = [];
    /// <summary>How many times each hardware encoder's test encode hangs until it is cancelled before it answers.</summary>
    public Dictionary<string, int> ProbeHangs { get; init; } = [];
    /// <summary>Called with the encoder name when one of those test encodes starts to hang.</summary>
    public Action<string>? OnProbeHang { get; set; }
    public HashSet<string> StallEncoders { get; init; } = [];
    public Func<int, long>? OutputBytes { get; init; }
    public string[] SilenceLines { get; init; } = [];
    public long? DecodedMicroseconds { get; init; }
    public Func<Action<string>?, CancellationToken, Task>? DuringExport { get; init; }
    // The bundled build's lines for the HDR → SDR filters.
    public string Filters { get; init; } = "Filters:\n  T.. = Timeline support\n ------\n .S tonemap           V->V       Conversion to/from different dynamic ranges.\n .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.\n";
    /// <summary>Raw ffprobe JSON for the source instead of the generated one.</summary>
    public string? SourceOverride { get; init; }
    public bool FailCoverExtraction { get; init; }

    public IReadOnlyList<ProcessRequest> Calls => runner?.Calls ?? [];
    public IReadOnlyList<ProcessRequest> CoverExtractions => Calls.Where(call => call.Arguments.Contains("image2")).ToArray();
    public IReadOnlyList<ProcessRequest> Exports => Calls.Where(call => call.Arguments.Contains("-progress") && !IsDetection(call)).ToArray();
    public IReadOnlyList<ProcessRequest> Detections => Calls.Where(IsDetection).ToArray();
    public IReadOnlyList<string> HardwareProbes => Calls.Where(call => call.Arguments.Contains("lavfi")).Select(call => Argument(call, "-c:v")).ToArray();

    public MediaService Service(TimeSpan? stall = null, TimeSpan? deadline = null, double faststartBytesPerSecond = 20_000_000, TimeSpan? hardwareProbe = null)
    {
        runner ??= new MediaFakeRunner(HandleAsync);
        return new MediaService(runner, new EngineFakeComponents()) { ProgressStallLimit = stall, ExportTimeLimit = deadline, FaststartBytesPerSecond = faststartBytesPerSecond, HardwareProbeLimit = hardwareProbe ?? TimeSpan.FromSeconds(60) };
    }

    public static string Listing(params string[] names) => string.Concat(names.Select(name => " V....D " + name + "  test encoder\n"));
    public static string Argument(ProcessRequest request, string name)
    {
        var index = request.Arguments.ToList().IndexOf(name);
        Assert.True(index >= 0 && index + 1 < request.Arguments.Count, $"Missing {name} in: {string.Join(' ', request.Arguments)}");
        return request.Arguments[index + 1];
    }
    public static string SourceJson(double seconds, bool video, long? videoBitRate = null, long? audioBitRate = null, int sampleRate = 48000, string? transfer = null, string? primaries = null, string? formatName = null)
    {
        var streams = new JsonArray();
        if (video)
        {
            var stream = new JsonObject { ["codec_type"] = "video", ["codec_name"] = "h264", ["width"] = 1920, ["height"] = 1080, ["avg_frame_rate"] = "30/1" };
            if (videoBitRate is { } bits) stream["bit_rate"] = bits.ToString(CultureInfo.InvariantCulture);
            if (transfer is not null) stream["color_transfer"] = transfer;
            if (primaries is not null) stream["color_primaries"] = primaries;
            streams.Add(stream);
        }
        var audio = new JsonObject { ["codec_type"] = "audio", ["codec_name"] = "aac", ["sample_rate"] = sampleRate.ToString(CultureInfo.InvariantCulture), ["channels"] = 2 };
        if (audioBitRate is { } audioBits) audio["bit_rate"] = audioBits.ToString(CultureInfo.InvariantCulture);
        streams.Add(audio);
        var format = new JsonObject { ["duration"] = seconds.ToString(CultureInfo.InvariantCulture) };
        if (formatName is not null) format["format_name"] = formatName;
        return new JsonObject { ["format"] = format, ["streams"] = streams }.ToJsonString();
    }

    private static bool IsDetection(ProcessRequest call) => call.Arguments.Any(argument => argument.Contains("silencedetect", StringComparison.Ordinal));

    private async Task<ProcessResult> HandleAsync(ProcessRequest request, Action<string>? stdout, Action<string>? stderr, CancellationToken cancellationToken)
    {
        var arguments = request.Arguments;
        if (request.Executable == "fake-ffprobe")
        {
            if (!outputs.TryGetValue(arguments[^1], out var output)) return new ProcessResult(0, SourceOverride ?? SourceJson(SourceSeconds, video: true, VideoBitRate, AudioBitRate, SampleRate, Transfer, Primaries, FormatName), "");
            var streams = new JsonArray();
            if (output.Video) streams.Add(new JsonObject { ["codec_type"] = "video", ["codec_name"] = "h264", ["width"] = 1920, ["height"] = 1080 });
            if (output.Audio is not null) streams.Add(new JsonObject { ["codec_type"] = "audio", ["codec_name"] = output.Audio, ["sample_rate"] = "48000", ["channels"] = 2 });
            return new ProcessResult(0, new JsonObject { ["format"] = new JsonObject { ["duration"] = output.Seconds.ToString(CultureInfo.InvariantCulture) }, ["streams"] = streams }.ToJsonString(), "");
        }
        if (arguments.Contains("-encoders")) return new ProcessResult(0, Encoders, "");
        if (arguments.Contains("-filters")) return new ProcessResult(0, Filters, "");
        if (arguments.Contains("lavfi"))
        {
            var probed = Argument(request, "-c:v");
            bool hangs;
            lock (ProbeHangs) { hangs = ProbeHangs.TryGetValue(probed, out var left) && left > 0; if (hangs) ProbeHangs[probed] = left - 1; }
            if (hangs)
            {
                OnProbeHang?.Invoke(probed);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new ProcessResult(UsableHardware.Contains(probed) ? 0 : 1, "", "");
        }
        if (IsDetection(request))
        {
            foreach (var line in SilenceLines) stderr?.Invoke(line);
            if (DecodedMicroseconds is { } decoded) stdout?.Invoke("out_time_us=" + decoded.ToString(CultureInfo.InvariantCulture));
            return new ProcessResult(0, "", "");
        }
        if (arguments.Contains("-xerror")) return new ProcessResult(0, "", "");
        if (arguments.Contains("image2"))
        {
            if (FailCoverExtraction) return new ProcessResult(1, "", "Invalid data found when processing input");
            File.WriteAllBytes(arguments[^1], [0x89, 0x50, 0x4E, 0x47]);
            return new ProcessResult(0, "", "");
        }
        var encoder = arguments.Contains("-c:v") ? Argument(request, "-c:v") : null;
        if (encoder is not null && StallEncoders.Contains(encoder)) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (DuringExport is not null) await DuringExport(stdout, cancellationToken);
        var path = arguments[^1];
        var attempt = Interlocked.Increment(ref attempts);
        File.WriteAllBytes(path, new byte[OutputBytes?.Invoke(attempt) ?? 512]);
        var audio = arguments.Contains("-c") ? "aac" : Argument(request, "-c:a") switch { "libmp3lame" => "mp3", "libopus" => "opus", "libvorbis" => "vorbis", "copy" => "aac", var codec => codec };
        outputs[path] = (double.Parse(Argument(request, "-t"), CultureInfo.InvariantCulture), !arguments.Contains("-vn"), audio);
        stdout?.Invoke("out_time_us=1000000");
        return new ProcessResult(0, "", "");
    }
}

// Opt-in end-to-end checks: MediaService with the real ProcessRunner, FFmpeg and
// FFprobe on synthetic lavfi media. The scripted runner above takes a clip's length
// from -t and starts every stream at zero, so stream offsets, seek accuracy and
// filter timelines can only fail here: 375 scripted tests passed while the real
// FFmpeg rejected Exact clips of late-audio files, started MPEG-TS cuts 15 frames
// late and moved normalized audio 0.5 s ahead of the video. Set KLIPPI_FFMPEG_DIR
// to a folder holding ffmpeg and ffprobe (checked with the bundled 9.0.2 essentials
// build) to run them; without it they are skipped. Each test makes its media in its
// own temp folder and deletes only that folder.
[Collection(RealFfmpeg.Collection)]
public sealed class MediaRealFfmpegTests
{
    [RealFfmpegTheory]
    [InlineData("audio", false)]
    [InlineData("audio", true)]
    [InlineData("video", false)]
    public async Task Exact_clip_of_a_late_stream_keeps_its_length_and_sync(string lateStream, bool normalize)
    {
        using var media = new RealFfmpeg();
        double videoDelay = lateStream == "video" ? 0.27 : 0;
        var source = await MakeLateSourceAsync(media, lateStream == "audio" ? 0.5 : 0, videoDelay);
        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("clip.mp4"), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(8), Normalize = normalize, Encoder = EncoderPreference.Cpu });
        // An input -t counted from each stream's own start, so the late stream
        // ran to 8.5 s and the export was rejected.
        Assert.InRange(result.Probe.Duration.TotalSeconds, 7.95, 8.05);
        var streams = await media.StreamsAsync(result.FilePath);
        Assert.InRange(streams.Video.End, 7.95, 8.05);
        Assert.InRange(streams.Audio.End, 7.95, 8.05);
        // Late audio is padded from zero; late video keeps its place against the
        // audio (first frame at 0.27 s on the 30 fps grid) instead of moving.
        Assert.InRange(streams.Audio.Start, 0, 0.01);
        Assert.InRange(streams.Video.Start, videoDelay - 0.04, videoDelay + 0.01);
        // asetpts=N/SR/TB dropped the 0.5 s offset and put the beep at 2.5 s.
        Assert.InRange(await media.BeepOnsetAsync(result.FilePath), 2.98, 3.02);
        media.AssertOnlyFiles(source, result.FilePath);
    }

    [RealFfmpegFact]
    public async Task Audio_only_mp3_of_late_audio_keeps_its_lead_in_and_its_fade()
    {
        using var media = new RealFfmpeg();
        var source = await MakeLateSourceAsync(media, audioDelay: 0.5, videoDelay: 0);
        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("clip.mp3"), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(8), AudioOnly = true, AudioFormat = AudioFormat.Mp3, FadeOut = TimeSpan.FromSeconds(1) });
        Assert.InRange(result.Probe.Duration.TotalSeconds, 7.95, 8.05);
        // MP3 has no start offset: without padding the 0.5 s lead-in was lost and the beep moved to 2.5 s.
        Assert.InRange(await media.BeepOnsetAsync(result.FilePath), 2.98, 3.02);
        // Half-way through the fade-out the 200 Hz bed is still audible; a fade on
        // the wrong clock ended 0.55 s early and left silence there.
        var seconds = result.Probe.Duration.TotalSeconds;
        var middle = await media.MeanVolumeAsync(result.FilePath, 5, 6);
        var fading = await media.MeanVolumeAsync(result.FilePath, seconds - 0.6, seconds - 0.4);
        Assert.InRange(fading, middle - 12, middle - 2);
        media.AssertOnlyFiles(source, result.FilePath);
    }

    /// <summary>A 1 kHz beep at 3.0 s of the file over a 200 Hz bed, with the audio or the video starting late.</summary>
    private static Task<string> MakeLateSourceAsync(RealFfmpeg media, double audioDelay, double videoDelay)
    {
        var beep = RealFfmpeg.Text(3 - audioDelay);
        return media.MakeAsync("late.mkv",
            "-itsoffset", RealFfmpeg.Text(videoDelay), "-f", "lavfi", "-i", "testsrc2=s=160x120:r=30:d=10",
            "-itsoffset", RealFfmpeg.Text(audioDelay), "-f", "lavfi", "-i", $"aevalsrc='0.1*sin(2*PI*200*t)+0.5*sin(2*PI*1000*t)*between(t,{beep},{beep}+0.5)':s=48000:d=9.5",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k");
    }

    [RealFfmpegTheory]
    [InlineData(5.5, 10.5, 165)]
    [InlineData(20.5, 24.5, 103)]
    public async Task Exact_cut_from_mpeg_ts_starts_on_the_selected_frame(double start, double end, int firstFrame)
    {
        using var media = new RealFfmpeg();
        // Keyframes every 2 s with B-frames, so both starts are mid-GOP; 20.5 s
        // also takes the path that decodes from 15 s before the start.
        var source = await media.MakeAsync("gop.ts", [.. RealFfmpeg.NumberedFrames(30), "-f", "lavfi", "-i", "sine=f=440:r=48000:d=30",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-bf", "2", "-c:a", "aac", "-f", "mpegts"]);
        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("cut.mp4"), Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Encoder = EncoderPreference.Cpu });
        // A direct input seek began at the next keyframe: frame 180 for 165.
        Assert.InRange(await media.FirstFrameLumaAsync(result.FilePath), firstFrame - 0.5, firstFrame + 0.5);
        var streams = await media.StreamsAsync(result.FilePath);
        var seconds = end - start;
        Assert.InRange(streams.Video.End, seconds - 0.05, seconds + 0.05);
        Assert.InRange(streams.Audio.End, seconds - 0.05, seconds + 0.05);
        // The source's video starts 0.02 s after its audio; the cut keeps that gap,
        // rounded to the frame grid, so its first frame may sit at one frame in.
        Assert.InRange(streams.Video.Start, 0, 0.04);
        var frames = (int)Math.Round(seconds * 30);
        Assert.InRange(streams.VideoFrames, frames - 1, frames);
        media.AssertOnlyFiles(source, result.FilePath);
    }

    [RealFfmpegFact]
    public async Task Exact_mp4_clip_fades_on_clip_time_from_the_selected_frame()
    {
        using var media = new RealFfmpeg();
        var source = await media.MakeAsync("tone.mp4", [.. RealFfmpeg.NumberedFrames(30), "-f", "lavfi", "-i", "sine=f=440:r=48000:d=30,volume=0.5",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-bf", "2", "-c:a", "aac", "-b:a", "128k"]);
        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("clip.mp4"), Start = TimeSpan.FromSeconds(20.5), End = TimeSpan.FromSeconds(25.5), FadeIn = TimeSpan.FromSeconds(1), FadeOut = TimeSpan.FromSeconds(1), Encoder = EncoderPreference.Cpu });
        // Frame-accurate: source frame 615 (20.5 s) first, and all 150 frames.
        Assert.InRange(await media.FirstFrameLumaAsync(result.FilePath), 102.5, 103.5);
        var streams = await media.StreamsAsync(result.FilePath);
        Assert.Equal(150, streams.VideoFrames);
        Assert.InRange(streams.Video.End, 4.95, 5.05);
        var seconds = streams.Audio.End;
        Assert.InRange(seconds, 4.95, 5.05);
        var middle = await media.MeanVolumeAsync(result.FilePath, 2.25, 2.75);
        // Fades on source time silenced the whole clip (-91 dB).
        Assert.True(middle > -30, $"The middle of the clip measured {middle} dB.");
        var first = await media.MeanVolumeAsync(result.FilePath, 0, 0.25);
        var last = await media.MeanVolumeAsync(result.FilePath, seconds - 0.25, seconds);
        Assert.True(first <= middle - 10, $"Fade-in: {first} dB against {middle} dB in the middle.");
        Assert.True(last <= middle - 10, $"Fade-out: {last} dB against {middle} dB in the middle.");
        media.AssertOnlyFiles(source, result.FilePath);
    }

    [RealFfmpegTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hdr_to_sdr_converts_only_hdr_sources(bool hdr)
    {
        using var media = new RealFfmpeg();
        string[] tags = hdr
            ? ["-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]
            : ["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"];
        var source = await media.MakeAsync("source.mp4", ["-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=6", "-f", "lavfi", "-i", "sine=f=440:r=48000:d=6", .. tags, "-c:a", "aac"]);
        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("clip.mp4"), Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(4), HdrToSdr = true, Encoder = EncoderPreference.Cpu });
        Assert.InRange(result.Probe.Duration.TotalSeconds, 2.95, 3.05);
        var video = await media.VideoColorAsync(result.FilePath);
        Assert.Equal("yuv420p", video.PixelFormat);
        if (hdr)
        {
            // PQ is tone-mapped through zscale to BT.709.
            Assert.Equal(("bt709", "bt709"), (video.Transfer, video.Primaries));
            Assert.Null(result.Warning);
        }
        else
        {
            // An SDR source keeps its picture; tone mapping it had darkened it.
            Assert.Contains("already SDR", result.Warning);
            var expected = await media.FirstFrameLumaAsync(source, TimeSpan.FromSeconds(1));
            Assert.InRange(await media.FirstFrameLumaAsync(result.FilePath), expected - 1, expected + 1);
        }
        media.AssertOnlyFiles(source, result.FilePath);
    }

    [RealFfmpegFact]
    public async Task Cancelled_export_stops_ffmpeg_and_leaves_no_files()
    {
        using var media = new RealFfmpeg();
        var source = await media.MakeAsync("long.mp4", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=120", "-f", "lavfi", "-i", "sine=f=440:r=48000:d=120", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac");
        using var cancel = new CancellationTokenSource();
        var progress = new CancelOnProgress(cancel);
        var output = media.PathFor("clip.mp4");
        // Cancel once FFmpeg reports encoding progress, well before the end.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = output, Encoder = EncoderPreference.Cpu, Compression = CompressionPreset.Balanced }, progress, cancel.Token));
        Assert.True(progress.Cancelled, "No encoding progress was reported before the export ended.");
        Assert.False(File.Exists(output));
        // The staging folder and its partial file are gone, which Windows allows
        // only once the stopped FFmpeg has released them.
        media.AssertOnlyFiles(source);
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancel) : IProgress<JobProgress>
    {
        public bool Cancelled { get; private set; }
        public void Report(JobProgress value)
        {
            if (Cancelled || value.Percent is not (> 0 and < 100)) return;
            Cancelled = true;
            cancel.Cancel();
        }
    }
}

[CollectionDefinition(RealFfmpeg.Collection, DisableParallelization = true)]
public sealed class RealFfmpegCollection;

internal sealed class RealFfmpegFactAttribute : FactAttribute
{
    public RealFfmpegFactAttribute() { if (!RealFfmpeg.Configured) Skip = RealFfmpeg.SkipReason; }
}

internal sealed class RealFfmpegTheoryAttribute : TheoryAttribute
{
    public RealFfmpegTheoryAttribute() { if (!RealFfmpeg.Configured) Skip = RealFfmpeg.SkipReason; }
}

/// <summary>The real FFmpeg and FFprobe from <c>KLIPPI_FFMPEG_DIR</c>, a temp folder and measurements of exported media.</summary>
internal sealed partial class RealFfmpeg : IDisposable
{
    public const string Collection = "Real FFmpeg";
    public const string Variable = "KLIPPI_FFMPEG_DIR";
    public const string SkipReason = "Set " + Variable + " to a folder with ffmpeg and ffprobe to run the real-FFmpeg media checks.";
    private static readonly string Suffix = OperatingSystem.IsWindows() ? ".exe" : "";
    private readonly EngineTestFiles files = new();
    private readonly ProcessRunner runner = new();

    public RealFfmpeg()
    {
        var folder = Environment.GetEnvironmentVariable(Variable)!;
        Ffmpeg = Path.Combine(folder, "ffmpeg" + Suffix);
        Ffprobe = Path.Combine(folder, "ffprobe" + Suffix);
        // A folder that was named but is wrong fails instead of skipping.
        Assert.True(File.Exists(Ffmpeg) && File.Exists(Ffprobe), Variable + " must name a folder that holds ffmpeg and ffprobe.");
        Service = new MediaService(runner, new Components(Ffmpeg, Ffprobe));
    }

    public static bool Configured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));
    public string Ffmpeg { get; }
    public string Ffprobe { get; }
    public MediaService Service { get; }

    public string PathFor(string name) => Path.Combine(files.Root, name);
    public static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);
    /// <summary>A 160x120, 30 fps video input whose every frame has its frame number (mod 256) as its brightness.</summary>
    public static string[] NumberedFrames(int seconds) => ["-f", "lavfi", "-i", $"color=c=black:s=160x120:r=30:d={seconds},geq=lum='mod(N,256)':cb=128:cr=128,format=yuv420p"];

    public async Task<string> MakeAsync(string name, params string[] arguments)
    {
        var path = PathFor(name);
        await FfmpegAsync(["-v", "error", "-y", .. arguments, path]);
        return path;
    }

    /// <summary>The average brightness of the first frame, or of the frame at <paramref name="at"/>.</summary>
    public async Task<double> FirstFrameLumaAsync(string path, TimeSpan? at = null) =>
        Number(await FfmpegAsync([.. at is { } time ? ["-ss", Text(time.TotalSeconds)] : Array.Empty<string>(), "-i", path, "-an", "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-frames:v", "1", "-f", "null", "-"]), YavgPattern());

    public async Task<(string? PixelFormat, string? Transfer, string? Primaries)> VideoColorAsync(string path)
    {
        var result = await runner.RunAsync(new ProcessRequest(Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,color_transfer,color_primaries", "-of", "json", path], files.Root, TimeSpan.FromMinutes(2)));
        Assert.True(result.ExitCode == 0, "FFprobe failed: " + result.StandardError);
        var video = Assert.Single(JsonNode.Parse(result.StandardOutput)!["streams"]!.AsArray())!;
        return ((string?)video["pix_fmt"], (string?)video["color_transfer"], (string?)video["color_primaries"]);
    }

    /// <summary>When the 1 kHz beep starts, with the audio placed on the file's clock (a late start is padded, not moved).</summary>
    public async Task<double> BeepOnsetAsync(string path) =>
        Number(await FfmpegAsync(["-i", path, "-vn", "-af", "aresample=async=1:first_pts=0,highpass=f=800,highpass=f=800,highpass=f=800,silencedetect=n=-30dB:d=0.1", "-f", "null", "-"]), SilenceEndPattern());

    public async Task<double> MeanVolumeAsync(string path, double from, double to) =>
        Number(await FfmpegAsync(["-i", path, "-vn", "-af", $"atrim=start={Text(from)}:end={Text(to)},volumedetect", "-f", "null", "-"]), MeanVolumePattern());

    /// <summary>Where each stream starts and ends on the file's clock, and how many video frames it holds.</summary>
    public async Task<(int VideoFrames, StreamSpan Video, StreamSpan Audio)> StreamsAsync(string path)
    {
        var result = await runner.RunAsync(new ProcessRequest(Ffprobe, ["-v", "error", "-count_packets", "-show_entries", "stream=codec_type,start_time,duration,nb_read_packets", "-of", "json", path], files.Root, TimeSpan.FromMinutes(2)));
        Assert.True(result.ExitCode == 0, "FFprobe failed: " + result.StandardError);
        var streams = JsonNode.Parse(result.StandardOutput)!["streams"]!.AsArray();
        JsonNode Stream(string type) => Assert.Single(streams, stream => (string?)stream!["codec_type"] == type)!;
        static double Value(JsonNode stream, string name) => double.Parse((string)stream[name]!, CultureInfo.InvariantCulture);
        static StreamSpan Span(JsonNode stream) => new(Value(stream, "start_time"), Value(stream, "duration"));
        var video = Stream("video");
        return ((int)Value(video, "nb_read_packets"), Span(video), Span(Stream("audio")));
    }

    public readonly record struct StreamSpan(double Start, double Seconds)
    {
        public double End => Start + Seconds;
    }

    /// <summary>The export leaves its result and the source only: no staging folder or partial file.</summary>
    public void AssertOnlyFiles(params string[] expected)
    {
        Assert.Empty(Directory.GetDirectories(files.Root));
        Assert.Equal(expected.Select(Path.GetFileName).Order(StringComparer.Ordinal), Directory.GetFiles(files.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    public void Dispose() => files.Dispose();

    private async Task<string> FfmpegAsync(IReadOnlyList<string> arguments)
    {
        var result = await runner.RunAsync(new ProcessRequest(Ffmpeg, ["-hide_banner", "-nostats", .. arguments], files.Root, TimeSpan.FromMinutes(2)));
        Assert.True(result.ExitCode == 0, $"FFmpeg exited with {result.ExitCode}: {result.StandardError[Math.Max(0, result.StandardError.Length - 2000)..]}");
        return result.StandardError;
    }

    private static double Number(string log, Regex pattern)
    {
        var match = pattern.Match(log);
        Assert.True(match.Success, $"No {pattern} in the FFmpeg output: {log[Math.Max(0, log.Length - 2000)..]}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"lavfi\.signalstats\.YAVG=([0-9.]+)")] private static partial Regex YavgPattern();
    [GeneratedRegex(@"silence_end: ([0-9.]+)")] private static partial Regex SilenceEndPattern();
    [GeneratedRegex(@"mean_volume: (-?[0-9.]+) dB")] private static partial Regex MeanVolumePattern();

    private sealed class Components(string ffmpeg, string ffprobe) : IComponentService
    {
        public string? Find(string name) => name switch { "ffmpeg" => ffmpeg, "ffprobe" => ffprobe, _ => null };
        public Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentInfo>>([new("ffmpeg", ffmpeg, null, true), new("ffprobe", ffprobe, null, true)]);
        public Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
