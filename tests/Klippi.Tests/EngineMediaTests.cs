using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

public sealed class EngineMediaTests
{
    [Fact]
    public async Task Probe_parses_rational_frame_rate_and_reads_actual_file_size()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(0, EngineFakeProcess.ProbeJson(), ""));
        var probe = await new MediaService(process, new EngineFakeComponents()).ProbeAsync(input);
        Assert.InRange(probe.Fps!.Value, 29.97, 29.971);
        Assert.Equal(256, probe.SizeBytes);
        Assert.Equal(48000, probe.SampleRate);
        Assert.Equal(TimeSpan.FromSeconds(60), probe.Duration);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    public async Task Probe_rejects_nonfinite_or_negative_duration(string duration)
    {
        using var files = new EngineTestFiles();
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(0, "{\"format\":{\"duration\":\"" + duration + "\"},\"streams\":[{\"codec_type\":\"audio\"}]}", ""));
        await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ProbeAsync(files.Input()));
    }

    [Theory]
    [InlineData(0, 12, 0, 0, 320, 180)]
    [InlineData(8, 12, 2, 4, 314, 172)]
    [InlineData(0, 0, 0, 0, 320, 192)]
    public async Task Probe_reports_display_crop_geometry_and_preserves_raw_coded_metadata(int top, int bottom, int left, int right, int expectedWidth, int expectedHeight)
    {
        using var files = new EngineTestFiles(); var input = files.Input(); var metadata = CropMetadata(top, bottom, left, right);
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(0, metadata.ToJsonString(), ""));
        var probe = await new MediaService(process, new EngineFakeComponents()).ProbeAsync(input);
        Assert.Equal(expectedWidth, probe.Width); Assert.Equal(expectedHeight, probe.Height);
        var raw = JsonNode.Parse(probe.TechnicalJson!)!; Assert.Equal(320, raw["streams"]![0]!["width"]!.GetValue<int>()); Assert.Equal(192, raw["streams"]![0]!["height"]!.GetValue<int>());
        Assert.Equal(bottom, raw["streams"]![0]!["side_data_list"]![0]!["crop_bottom"]!.GetValue<int>());
        Assert.Single(process.Calls);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("fractional")]
    [InlineData("string")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("height")]
    [InlineData("width")]
    [InlineData("overflow")]
    [InlineData("duplicate")]
    [InlineData("missing-dimensions")]
    public async Task Probe_rejects_invalid_or_oversized_display_crop_without_touching_media(string failure)
    {
        using var files = new EngineTestFiles(); var input = files.Input(); var metadata = CropMetadata(0, 12, 0, 0); var video = metadata["streams"]![0]!; var crop = video["side_data_list"]![0]!;
        switch (failure)
        {
            case "negative": crop["crop_bottom"] = -1; break;
            case "fractional": crop["crop_bottom"] = 1.5; break;
            case "string": crop["crop_bottom"] = "12"; break;
            case "missing": crop.AsObject().Remove("crop_left"); break;
            case "null": crop["crop_left"] = null; break;
            case "height": crop["crop_bottom"] = 192; break;
            case "width": crop["crop_left"] = 320; break;
            case "overflow": crop["crop_left"] = int.MaxValue; crop["crop_right"] = int.MaxValue; break;
            case "duplicate": video["side_data_list"]!.AsArray().Add(crop.DeepClone()); break;
            case "missing-dimensions": video.AsObject().Remove("width"); break;
        }
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(0, metadata.ToJsonString(), ""));
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ProbeAsync(input));
        Assert.Contains("crop", error.Message, StringComparison.OrdinalIgnoreCase); Assert.Single(process.Calls);
        Assert.Equal(Enumerable.Repeat((byte)17, 256), await File.ReadAllBytesAsync(input)); Assert.Equal(new[] { input }, Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Unrelated_side_data_does_not_change_geometry()
    {
        using var files = new EngineTestFiles(); var metadata = CropMetadata(0, 12, 0, 0); metadata["streams"]![0]!["side_data_list"]![0]!["side_data_type"] = "Display Matrix";
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(0, metadata.ToJsonString(), ""));
        var probe = await new MediaService(process, new EngineFakeComponents()).ProbeAsync(files.Input()); Assert.Equal(320, probe.Width); Assert.Equal(192, probe.Height);
    }
    private static JsonNode CropMetadata(int top, int bottom, int left, int right)
    {
        var metadata = JsonNode.Parse(EngineFakeProcess.ProbeJson())!; var video = metadata["streams"]![0]!; video["width"] = 320; video["height"] = 192;
        video["side_data_list"] = new JsonArray(new JsonObject { ["side_data_type"] = "Frame Cropping", ["crop_top"] = top, ["crop_bottom"] = bottom, ["crop_left"] = left, ["crop_right"] = right }); return metadata;
    }

    [Fact]
    public async Task Exact_cut_seeks_and_limits_on_the_input_and_preserves_original_by_default()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var process = ExportProcess();
        var progress = new EngineProgress();
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(15), Encoder = EncoderPreference.Cpu }, progress);
        Assert.True(File.Exists(input));
        Assert.True(File.Exists(result.FilePath));
        Assert.False(result.OriginalDeleted);
        var encode = Assert.Single(process.Calls, call => call.Arguments.Contains("-progress"));
        // Input-side -ss: filters see clip time and only the selection is decoded. Output-side -t:
        // an input -t counted from each stream's own start, so late audio ran past the end.
        Assert.True(Array.IndexOf(encode.Arguments.ToArray(), "-ss") < Array.IndexOf(encode.Arguments.ToArray(), "-i"));
        Assert.True(Array.IndexOf(encode.Arguments.ToArray(), "-t") > Array.IndexOf(encode.Arguments.ToArray(), "-i"));
        Assert.Equal("5", EngineFakeProcess.Argument(encode, "-t"));
        Assert.Equal("libx264", EngineFakeProcess.Argument(encode, "-c:v"));
        Assert.Contains(progress.Values, update => update.Percent == 50);
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Fact]
    public async Task Lossless_cut_uses_copy_and_reports_keyframe_limitation()
    {
        using var files = new EngineTestFiles();
        var process = ExportProcess();
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mkv"), Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(15), ClipMode = ClipMode.Lossless });
        var encode = Assert.Single(process.Calls, call => call.Arguments.Contains("-progress"));
        Assert.True(Array.IndexOf(encode.Arguments.ToArray(), "-ss") < Array.IndexOf(encode.Arguments.ToArray(), "-i"));
        Assert.Equal("copy", EngineFakeProcess.Argument(encode, "-c"));
        Assert.Contains("keyframes", result.Warning);
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Contains("-encoders"));
    }

    [Fact]
    public async Task Lossless_changes_and_nonfinite_volume_are_rejected_without_encoding()
    {
        using var files = new EngineTestFiles();
        var process = ExportProcess();
        var service = new MediaService(process, new EngineFakeComponents());
        var options = new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), ClipMode = ClipMode.Lossless, Normalize = true };
        await Assert.ThrowsAsync<MediaOperationException>(() => service.ExportAsync(options));
        await Assert.ThrowsAsync<MediaOperationException>(() => service.ExportAsync(options with { ClipMode = ClipMode.Exact, Normalize = false, Volume = double.NaN }));
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Contains("-progress"));
    }

    [Theory]
    [InlineData(".webm", ClipMode.Exact, false, VideoCodec.H264, AudioFormat.Original, "h264", "aac")]
    [InlineData(".webm", ClipMode.Exact, false, VideoCodec.Hevc, AudioFormat.Original, "h264", "aac")]
    [InlineData(".webm", ClipMode.Exact, false, VideoCodec.Original, AudioFormat.Original, "h264", "aac")]
    [InlineData(".webm", ClipMode.Lossless, false, VideoCodec.Original, AudioFormat.Original, "h264", "opus")]
    [InlineData(".webm", ClipMode.Lossless, false, VideoCodec.Original, AudioFormat.Original, "vp9", "aac")]
    [InlineData(".webm", ClipMode.Lossless, true, VideoCodec.Original, AudioFormat.Original, "vp9", "aac")]
    [InlineData(".mp3", ClipMode.Exact, false, VideoCodec.H264, AudioFormat.Mp3, "h264", "aac")]
    [InlineData(".m4a", ClipMode.Exact, false, VideoCodec.H264, AudioFormat.Aac, "h264", "aac")]
    [InlineData(".wav", ClipMode.Exact, false, VideoCodec.H264, AudioFormat.Wav, "h264", "aac")]
    [InlineData(".aac", ClipMode.Lossless, false, VideoCodec.Original, AudioFormat.Original, "h264", "aac")]
    [InlineData(".flac", ClipMode.Lossless, false, VideoCodec.Original, AudioFormat.Original, "h264", "flac")]
    [InlineData(".opus", ClipMode.Lossless, false, VideoCodec.Original, AudioFormat.Original, "h264", "opus")]
    [InlineData(".mp3", ClipMode.Lossless, true, VideoCodec.Original, AudioFormat.Original, "h264", "aac")]
    [InlineData(".aac", ClipMode.Lossless, true, VideoCodec.Original, AudioFormat.Original, "h264", "mp3")]
    [InlineData(".opus", ClipMode.Lossless, true, VideoCodec.Original, AudioFormat.Original, "h264", "vorbis")]
    [InlineData(".flac", ClipMode.Lossless, true, VideoCodec.Original, AudioFormat.Original, "h264", "mp3")]
    [InlineData(".ogg", ClipMode.Lossless, true, VideoCodec.Original, AudioFormat.Original, "h264", "aac")]
    [InlineData(".mp4", ClipMode.Lossless, false, VideoCodec.Av1, AudioFormat.Original, "h264", "aac")]
    [InlineData(".mp3", ClipMode.Lossless, true, VideoCodec.Original, AudioFormat.Mp3, "h264", "aac")]
    [InlineData(".ogg", ClipMode.Exact, false, VideoCodec.Av1, AudioFormat.Original, "h264", "opus")]
    [InlineData(".mp3", ClipMode.Exact, false, VideoCodec.Original, AudioFormat.Mp3, null, "aac")]
    [InlineData(".mp3", ClipMode.Exact, true, VideoCodec.Original, AudioFormat.Aac, "h264", "aac")]
    [InlineData(".m4a", ClipMode.Exact, true, VideoCodec.Original, AudioFormat.Mp3, "h264", "aac")]
    [InlineData(".opus", ClipMode.Exact, true, VideoCodec.Original, AudioFormat.Ogg, "h264", "aac")]
    [InlineData(".flac", ClipMode.Exact, true, VideoCodec.Original, AudioFormat.Wav, "h264", "aac")]
    [InlineData(".wav", ClipMode.Exact, true, VideoCodec.Original, AudioFormat.Mp3, "h264", "aac")]
    [InlineData(".webm", ClipMode.Exact, true, VideoCodec.Original, AudioFormat.Aac, "h264", "aac")]
    public async Task Incompatible_tracks_or_codecs_fail_after_probe_before_any_encoder_process(string extension, ClipMode mode, bool audioOnly, VideoCodec videoCodec, AudioFormat audioFormat, string? sourceVideo, string sourceAudio)
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var output = Path.Combine(files.Root, "clip" + extension);
        await File.WriteAllTextAsync(output, "existing user's output");
        var process = FormatExportProcess(sourceVideo, sourceAudio);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = input, OutputPath = output, ClipMode = mode, AudioOnly = audioOnly, VideoCodec = videoCodec, AudioFormat = audioFormat, End = TimeSpan.FromSeconds(1), ReplaceExisting = true, DeleteOriginalAfterSuccess = true }));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal("fake-ffprobe", Assert.Single(process.Calls).Executable);
        Assert.Equal("existing user's output", await File.ReadAllTextAsync(output));
        Assert.Equal(Enumerable.Repeat((byte)17, 256), await File.ReadAllBytesAsync(input));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Theory]
    [InlineData("vp8", "opus", false)]
    [InlineData("vp9", "vorbis", false)]
    [InlineData("av1", "opus", false)]
    [InlineData("vp9", null, false)]
    [InlineData("h264", "opus", true)]
    [InlineData(null, "vorbis", true)]
    public async Task Valid_WebM_source_streams_remain_available_for_lossless_copy(string? sourceVideo, string? sourceAudio, bool audioOnly)
    {
        using var files = new EngineTestFiles();
        var process = FormatExportProcess(sourceVideo, sourceAudio);
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.webm"), ClipMode = ClipMode.Lossless, VideoCodec = VideoCodec.Original, AudioFormat = AudioFormat.Original, AudioOnly = audioOnly, End = TimeSpan.FromSeconds(1) });
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal("copy", EngineFakeProcess.Argument(Assert.Single(process.Calls, call => call.Arguments.Contains("-progress")), "-c"));
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Contains("-encoders"));
    }

    [Theory]
    [InlineData(".webm", "h264", "aac", VideoCodec.Av1, false, AudioFormat.Original)]
    [InlineData(".webm", "vp9", "aac", VideoCodec.Original, false, AudioFormat.Original)]
    [InlineData(".mkv", "h264", "aac", VideoCodec.Hevc, false, AudioFormat.Original)]
    [InlineData(".mkv", "h264", "aac", VideoCodec.Av1, false, AudioFormat.Original)]
    [InlineData(".mkv", "h264", "aac", VideoCodec.Original, true, AudioFormat.Mp3)]
    public async Task Valid_conversions_and_flexible_MKV_container_are_not_rejected(string extension, string sourceVideo, string sourceAudio, VideoCodec videoCodec, bool audioOnly, AudioFormat audioFormat)
    {
        using var files = new EngineTestFiles();
        var process = FormatExportProcess(sourceVideo, sourceAudio);
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip" + extension), End = TimeSpan.FromSeconds(1), VideoCodec = videoCodec, AudioOnly = audioOnly, AudioFormat = audioFormat, Encoder = EncoderPreference.Cpu });
        Assert.True(File.Exists(result.FilePath));
        var encode = Assert.Single(process.Calls, call => call.Arguments.Contains("-progress"));
        if (extension == ".webm") Assert.Equal("libopus", EngineFakeProcess.Argument(encode, "-c:a"));
    }

    [Theory]
    [InlineData(".mkv", "wmv3", "wmav2", false)]
    [InlineData(".mkv", null, "pcm_s24le", true)]
    [InlineData(".ogg", "theora", "vorbis", false)]
    [InlineData(".ogg", "vp8", "opus", false)]
    [InlineData(".wav", null, "pcm_f32le", true)]
    public async Task Legitimate_less_common_lossless_containers_keep_source_codecs(string extension, string? sourceVideo, string sourceAudio, bool audioOnly)
    {
        using var files = new EngineTestFiles();
        var process = FormatExportProcess(sourceVideo, sourceAudio);
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip" + extension), End = TimeSpan.FromSeconds(1), ClipMode = ClipMode.Lossless, VideoCodec = VideoCodec.Original, AudioFormat = AudioFormat.Original, AudioOnly = audioOnly });
        Assert.True(File.Exists(result.FilePath));
        var encode = Assert.Single(process.Calls, call => call.Arguments.Contains("-progress"));
        Assert.Equal("copy", EngineFakeProcess.Argument(encode, "-c"));
    }

    [Fact]
    public async Task Hardware_failure_retries_cpu_and_explains_fallback()
    {
        using var files = new EngineTestFiles();
        var process = ExportProcess(failHardware: true);
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Auto });
        Assert.Contains("CPU", result.Warning);
        var attempts = process.Calls.Where(call => call.Arguments.Contains("-progress")).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal("h264_amf", EngineFakeProcess.Argument(attempts[0], "-c:v"));
        Assert.Equal("libx264", EngineFakeProcess.Argument(attempts[1], "-c:v"));
    }

    [Fact]
    public async Task Failed_export_preserves_original_and_existing_destination()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var destination = Path.Combine(files.Root, "clip.mp4");
        await File.WriteAllTextAsync(destination, "existing");
        var process = ExportProcess(failAll: true);
        await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = input, OutputPath = destination, ReplaceExisting = true, DeleteOriginalAfterSuccess = true, Encoder = EncoderPreference.Cpu }));
        Assert.True(File.Exists(input));
        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Fact]
    public async Task Source_deletion_requires_successful_full_decode_and_source_unchanged()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var process = ExportProcess();
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = input, OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu, DeleteOriginalAfterSuccess = true });
        Assert.True(result.OriginalDeleted);
        Assert.False(File.Exists(input));
        Assert.Contains(process.Calls, call => call.Arguments.Contains("-xerror") && call.Arguments.Contains("null"));
    }

    [Fact]
    public async Task Integrity_decode_failure_prevents_source_deletion_and_output_commit()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var output = Path.Combine(files.Root, "clip.mp4");
        var process = ExportProcess(failDecode: true);
        await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = input, OutputPath = output, End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu, DeleteOriginalAfterSuccess = true }));
        Assert.True(File.Exists(input));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Fact]
    public async Task Changed_source_is_preserved_after_integrity_check()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var output = Path.Combine(files.Root, "clip.mp4");
        var process = ExportProcess(onDecode: () => File.WriteAllText(input, "changed while exporting"));
        await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = input, OutputPath = output, End = TimeSpan.FromSeconds(5), Encoder = EncoderPreference.Cpu, DeleteOriginalAfterSuccess = true }));
        Assert.Equal("changed while exporting", await File.ReadAllTextAsync(input));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Target_size_retries_with_lower_video_bitrate_and_only_commits_a_fitting_result()
    {
        using var files = new EngineTestFiles();
        var process = ExportProcess(outputBytes: attempt => attempt == 1 ? 120_000 : 90_000);
        var result = await new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "clip.mp4"), End = TimeSpan.FromSeconds(2), Encoder = EncoderPreference.Cpu, Compression = CompressionPreset.TargetSize, TargetBytes = 100_000 });
        Assert.Equal(90_000, result.Probe.SizeBytes);
        var encodes = process.Calls.Where(call => call.Arguments.Contains("-progress")).ToArray();
        Assert.Equal(2, encodes.Length);
        Assert.Equal("184k", EngineFakeProcess.Argument(encodes[0], "-b:v"));
        Assert.Equal("141k", EngineFakeProcess.Argument(encodes[1], "-b:v"));
    }

    [Fact]
    public async Task Oversized_audio_is_rejected_before_commit()
    {
        using var files = new EngineTestFiles();
        var input = files.Input();
        var output = Path.Combine(files.Root, "clip.mp3");
        var process = ExportProcess(audioOnly: true, outputBytes: _ => 101_000);
        await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = input, OutputPath = output, AudioOnly = true, End = TimeSpan.FromSeconds(5), Compression = CompressionPreset.TargetSize, TargetBytes = 100_000 }));
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(input));
    }

    [Fact]
    public void Target_estimate_reserves_audio_and_container_budget()
    {
        var service = new MediaService(new EngineFakeProcess((_, _, _) => throw new InvalidOperationException()), new EngineFakeComponents());
        var probe = new ProbeInfo("source", TimeSpan.FromSeconds(60), 1_000_000, true, true, 1920, 1080, 30, "h264", "aac", 48000, 2);
        var estimate = service.Estimate(probe, new ExportOptions { InputPath = "source", OutputPath = "clip.mp4", End = TimeSpan.FromSeconds(2), Compression = CompressionPreset.TargetSize, TargetBytes = 100_000 });
        Assert.Equal(184, estimate.VideoKbps);
        Assert.Equal(100_000, estimate.EstimatedBytes);
        Assert.Contains("actual size", estimate.Explanation);
    }

    [Fact]
    public async Task Soundboard_uses_configurable_limits_and_checks_actual_duration()
    {
        using var files = new EngineTestFiles();
        var process = ExportProcess(outputSeconds: 5.22, audioOnly: true);
        var service = new MediaService(process, new EngineFakeComponents());
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "sound.mp3"), AudioOnly = true, End = TimeSpan.FromSeconds(5.2), DiscordSoundboard = true, SoundboardMaxSeconds = 5.2, SoundboardTargetBytes = 500_000 }));
        Assert.Contains("soundboard", error.Message);
        Assert.False(File.Exists(Path.Combine(files.Root, "sound.mp3")));
    }

    [Theory]
    [InlineData(AudioFormat.Wav, ".wav", ClipMode.Exact)]
    [InlineData(AudioFormat.Flac, ".flac", ClipMode.Exact)]
    [InlineData(AudioFormat.Aac, ".m4a", ClipMode.Exact)]
    [InlineData(AudioFormat.Opus, ".opus", ClipMode.Exact)]
    [InlineData(AudioFormat.Original, ".m4a", ClipMode.Lossless)]
    [InlineData(AudioFormat.Mp3, ".ogg", ClipMode.Lossless)]
    public async Task Soundboard_rejects_unsupported_format_or_extension_before_encoding(AudioFormat format, string extension, ClipMode mode)
    {
        using var files = new EngineTestFiles();
        var process = ExportProcess();
        await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = Path.Combine(files.Root, "sound" + extension), End = TimeSpan.FromSeconds(1), AudioOnly = true, AudioFormat = format, ClipMode = mode, DiscordSoundboard = true }));
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Contains("-progress"));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Fact]
    public async Task Soundboard_rejects_unexpected_actual_output_codec_before_commit()
    {
        using var files = new EngineTestFiles();
        var output = Path.Combine(files.Root, "sound.mp3");
        var process = ExportProcess(audioOnly: true);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new MediaService(process, new EngineFakeComponents()).ExportAsync(new ExportOptions { InputPath = files.Input(), OutputPath = output, End = TimeSpan.FromSeconds(1), AudioOnly = true, AudioFormat = AudioFormat.Mp3, DiscordSoundboard = true }));
        Assert.Contains("does not match", error.Message); // The fake encoder claims AAC despite the requested MP3 format.
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Waveform_handles_full_scale_negative_pcm_and_cleans_temporary_file()
    {
        using var files = new EngineTestFiles();
        string? staging = null;
        var process = new EngineFakeProcess((request, _, _) =>
        {
            if (request.Executable == "fake-ffprobe") return new ProcessResult(0, EngineFakeProcess.ProbeJson(audio: true), "");
            staging = request.WorkingDirectory;
            WriteWave(request.Arguments[^1]);
            return new ProcessResult(0, "", "");
        });
        var waveform = await new MediaService(process, new EngineFakeComponents()).WaveformAsync(files.Input(), 16);
        Assert.Equal(16, waveform.Count);
        Assert.Equal(1f, waveform[0]);
        Assert.All(waveform, peak => Assert.InRange(peak, 0, 1));
        Assert.False(Directory.Exists(staging));
    }

    private static EngineFakeProcess FormatExportProcess(string? sourceVideo, string? sourceAudio)
    {
        var outputs = new Dictionary<string, (double Seconds, string? Video, string? Audio)>(StringComparer.OrdinalIgnoreCase);
        return new EngineFakeProcess((request, _, _) =>
        {
            if (request.Executable == "fake-ffprobe")
            {
                var exported = outputs.TryGetValue(request.Arguments[^1], out var output);
                var video = exported ? output.Video : sourceVideo;
                var audio = exported ? output.Audio : sourceAudio;
                var json = EngineFakeProcess.ProbeJson(exported ? output.Seconds : 60, video: video is not null, audio: audio is not null).Replace("\"h264\"", "\"" + video + "\"", StringComparison.Ordinal).Replace("\"aac\"", "\"" + audio + "\"", StringComparison.Ordinal);
                return new ProcessResult(0, json, "");
            }
            if (request.Arguments.Contains("-encoders")) return new ProcessResult(0, EngineFakeProcess.Encoders + " V....D libx265 HEVC\n V....D libsvtav1 AV1\n V....D libvpx-vp9 VP9\n V....D libvpx VP8\n", "");
            Assert.Contains("-progress", request.Arguments);
            File.WriteAllBytes(request.Arguments[^1], Enumerable.Repeat((byte)31, 512).ToArray());
            var videoCodec = sourceVideo is null || request.Arguments.Contains("-vn") ? null : request.Arguments.Contains("-c") ? sourceVideo : EngineFakeProcess.Argument(request, "-c:v") switch { "libx264" or "h264_amf" => "h264", "libx265" => "hevc", "libsvtav1" => "av1", "libvpx-vp9" => "vp9", "libvpx" => "vp8", _ => throw new InvalidOperationException("Unexpected fixture video encoder") };
            var audioCodec = sourceAudio is null ? null : request.Arguments.Contains("-c") ? sourceAudio : EngineFakeProcess.Argument(request, "-c:a") switch { "libmp3lame" => "mp3", "libopus" => "opus", "libvorbis" => "vorbis", var codec => codec };
            outputs[request.Arguments[^1]] = (double.Parse(EngineFakeProcess.Argument(request, "-t"), CultureInfo.InvariantCulture), videoCodec, audioCodec);
            return new ProcessResult(0, "", "");
        });
    }

    private static EngineFakeProcess ExportProcess(bool failHardware = false, bool failAll = false, bool failDecode = false, double? outputSeconds = null, bool audioOnly = false, Action? onDecode = null, Func<int, int>? outputBytes = null)
    {
        var durations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var attempt = 0;
        return new EngineFakeProcess((request, stdout, _) =>
        {
            if (request.Executable == "fake-ffprobe") return new ProcessResult(0, EngineFakeProcess.ProbeJson(durations.GetValueOrDefault(request.Arguments[^1], 60), video: !audioOnly || !durations.ContainsKey(request.Arguments[^1])), "");
            if (request.Arguments.Contains("-encoders")) return new ProcessResult(0, EngineFakeProcess.Encoders, "");
            if (request.Arguments.Contains("-xerror")) { onDecode?.Invoke(); return new ProcessResult(failDecode ? 1 : 0, "", "private cookie=SECRET"); }
            if (request.Arguments.Contains("lavfi")) return new ProcessResult(0, "", ""); // hardware usability probe: listed encoders open here
            var output = request.Arguments[^1];
            File.WriteAllBytes(output, Enumerable.Repeat((byte)31, outputBytes?.Invoke(++attempt) ?? 512).ToArray());
            durations[output] = outputSeconds ?? double.Parse(EngineFakeProcess.Argument(request, "-t"), CultureInfo.InvariantCulture);
            stdout?.Invoke("out_time_us=2500000");
            var fail = failAll || failHardware && request.Arguments.Contains("h264_amf");
            return new ProcessResult(fail ? 1 : 0, "", fail ? "token=SECRET" : "");
        });
    }

    private static void WriteWave(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 2000); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(2000);
        for (var index = 0; index < 1000; index++) writer.Write(index == 0 ? short.MinValue : (short)1000);
    }
}
