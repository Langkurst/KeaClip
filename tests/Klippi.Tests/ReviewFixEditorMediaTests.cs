using System.Globalization;
using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// Regression tests for the final review's editor/media findings: encoder bitrate limits (#7),
// size targets that inflated small clips (#9), compression presets for audio clips (#10) and
// the extra probe each waveform ran (#21).
public sealed class ReviewFixEditorMediaTests
{
    // ===== #7: -b:a stays inside what the encoder accepts =====

    [Theory]
    [InlineData(AudioFormat.Ogg, 48000, 320, "224k")]
    [InlineData(AudioFormat.Ogg, 22050, 192, "80k")]
    [InlineData(AudioFormat.Opus, 48000, 320, "256k")]
    public async Task Mono_audio_bitrate_is_moved_into_the_encoders_range(AudioFormat format, int sourceRate, int kbps, string expected)
    {
        using var files = new EngineTestFiles();
        var input = files.Input(); File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 10, SampleRate = sourceRate };
        var service = scenario.Service();
        var options = new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "voice." + (format == AudioFormat.Ogg ? "ogg" : "opus")), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), AudioOnly = true, AudioFormat = format, AudioKbps = kbps, Mono = true };

        var result = await service.ExportAsync(options);

        Assert.Equal(expected, MediaScenario.Argument(Assert.Single(scenario.Exports), "-b:a"));
        Assert.Contains("accepts at most", result.Warning);
        Assert.Contains("accepts at most", service.Estimate(await service.ProbeAsync(input), options).Explanation);
    }

    [Theory]
    [InlineData("libvorbis", 1, 48000, 32, 224)]
    [InlineData("libvorbis", 1, 22050, 16, 80)]
    [InlineData("libvorbis", 2, 48000, 48, 320)]
    [InlineData("libopus", 1, 48000, 8, 256)]
    [InlineData("libopus", 2, 48000, 8, 512)]
    [InlineData("libmp3lame", 1, 22050, 1, int.MaxValue)]
    public void Encoder_bitrate_range_matches_the_measured_limits(string codec, int channels, int rate, int min, int max) =>
        Assert.Equal((min, max), MediaService.AudioBitrateRange(codec, channels, rate));

    // Without the clamp each of these failed inside FFmpeg 9.0.2 ("FFmpeg could not process this file").
    [RealFfmpegTheory]
    [InlineData(AudioFormat.Ogg, 48000, 320)]
    [InlineData(AudioFormat.Ogg, 22050, 192)]
    [InlineData(AudioFormat.Opus, 48000, 320)]
    public async Task Real_encoders_accept_the_clamped_mono_bitrate(AudioFormat format, int sourceRate, int kbps)
    {
        using var media = new RealFfmpeg();
        var source = await media.MakeAsync("voice.wav", "-f", "lavfi", "-i", $"sine=f=440:r={sourceRate.ToString(CultureInfo.InvariantCulture)}:d=2", "-ac", "2");
        var extension = format == AudioFormat.Ogg ? "ogg" : "opus";

        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("clip." + extension), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1.5), AudioOnly = true, AudioFormat = format, AudioKbps = kbps, Mono = true });

        Assert.True(result.Probe.HasAudio);
        Assert.Equal(format == AudioFormat.Ogg ? "vorbis" : "opus", result.Probe.AudioCodec);
        Assert.Contains("accepts at most", result.Warning);
    }

    // ===== #9: a size target is a ceiling, not an aim =====

    [Fact]
    public async Task Size_target_never_spends_more_video_bitrate_than_the_source_has()
    {
        using var files = new EngineTestFiles();
        var input = files.Input(); File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 60, VideoBitRate = 1_000_000, AudioBitRate = 128_000 };
        var service = scenario.Service();
        var options = new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(8), Compression = CompressionPreset.TargetSize, TargetBytes = 9_500_000, Encoder = EncoderPreference.Cpu };

        var estimate = service.Estimate(await service.ProbeAsync(input), options);
        await service.ExportAsync(options);

        // The 3 s clip asked for about 23,700 kbps before: 23.7x the source's video bitrate.
        Assert.Equal(1000, estimate.VideoKbps);
        Assert.True(estimate.EstimatedBytes < 1_000_000, $"Estimated {estimate.EstimatedBytes} bytes");
        Assert.Contains("already fits", estimate.Explanation);
        Assert.Equal("1000k", MediaScenario.Argument(Assert.Single(scenario.Exports), "-b:v"));
    }

    [Fact]
    public async Task Size_target_below_the_source_bitrate_still_aims_for_the_target()
    {
        using var files = new EngineTestFiles();
        var input = files.Input(); File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 60, VideoBitRate = 50_000_000, AudioBitRate = 128_000 };
        var service = scenario.Service();
        var probe = await service.ProbeAsync(input);
        var estimate = service.Estimate(probe, new ExportOptions { InputPath = input, OutputPath = "clip.mp4", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(30), Compression = CompressionPreset.TargetSize, TargetBytes = 9_500_000 });
        Assert.Equal(9_500_000, estimate.EstimatedBytes);
        Assert.InRange(estimate.VideoKbps!.Value, 2000, 2500);
    }

    // ===== #10: compression presets lower an audio clip's bitrate =====

    [Theory]
    [InlineData(CompressionPreset.Off, "192k")]
    [InlineData(CompressionPreset.Light, "160k")]
    [InlineData(CompressionPreset.Balanced, "128k")]
    [InlineData(CompressionPreset.Strong, "96k")]
    [InlineData(CompressionPreset.VerySmall, "64k")]
    public async Task Audio_clip_compression_preset_sets_the_audio_bitrate(CompressionPreset preset, string expected)
    {
        using var files = new EngineTestFiles();
        var input = files.Input(); File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 30 };
        var service = scenario.Service();
        var progress = new List<JobStage>();
        var options = new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp3"), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), AudioOnly = true, AudioFormat = AudioFormat.Mp3, AudioKbps = 192, Compression = preset };

        await service.ExportAsync(options, new Progress(progress));

        Assert.Equal(expected, MediaScenario.Argument(Assert.Single(scenario.Exports), "-b:a"));
        Assert.Equal(preset == CompressionPreset.Off ? JobStage.Converting : JobStage.Compressing, progress[0]);
        var estimate = service.Estimate(await service.ProbeAsync(input), options);
        Assert.Equal((long)Math.Ceiling(10 * int.Parse(expected.TrimEnd('k'), CultureInfo.InvariantCulture) * 1000 / 8d * 1.03), estimate.EstimatedBytes);
    }

    [Fact]
    public async Task Lossless_audio_clip_ignores_compression_presets()
    {
        using var files = new EngineTestFiles();
        var input = files.Input(); File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 30 };
        var service = scenario.Service();
        var progress = new List<JobStage>();
        var options = new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.wav"), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), AudioOnly = true, AudioFormat = AudioFormat.Wav, Compression = CompressionPreset.VerySmall };

        await service.ExportAsync(options, new Progress(progress));

        Assert.DoesNotContain("-b:a", Assert.Single(scenario.Exports).Arguments);
        Assert.Equal(JobStage.Converting, progress[0]);
        Assert.Contains("do not change lossless audio", service.Estimate(await service.ProbeAsync(input), options).Explanation);
    }

    // ===== #21: the waveform reuses the open's probe =====

    [Fact]
    public async Task Waveform_reuses_the_probe_of_an_unchanged_file()
    {
        using var files = new EngineTestFiles();
        var input = files.Input(); File.WriteAllBytes(input, new byte[1_000_000]);
        var scenario = new MediaScenario { SourceSeconds = 30 };
        var service = scenario.Service();

        await service.ProbeAsync(input);
        await WaveformAsync(service, input);
        Assert.Equal(1, Probes(scenario));

        // A changed file is probed again.
        File.WriteAllBytes(input, new byte[1_200_000]);
        await WaveformAsync(service, input);
        Assert.Equal(2, Probes(scenario));
        Assert.Equal(2, scenario.Calls.Count(call => call.Arguments.Contains("-fs")));
    }

    private static int Probes(MediaScenario scenario) => scenario.Calls.Count(call => call.Executable == "fake-ffprobe");

    // The scripted runner answers the decode as if it were an export, so the waveform itself
    // fails after the decode was requested; only the calls made matter here.
    private static async Task WaveformAsync(MediaService service, string input)
    {
        try { await service.WaveformAsync(input, 600); }
        catch (Exception) { }
    }

    private sealed class Progress(List<JobStage> stages) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) { lock (stages) stages.Add(value.Stage); }
    }
}
