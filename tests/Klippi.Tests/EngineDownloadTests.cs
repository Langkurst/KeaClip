using System.Security.Cryptography;
using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

public sealed class EngineDownloadTests
{
    private const string Metadata = """{"id":"abcdefghijk","title":"100% Artist / Movie","webpage_url":"https://www.youtube.com/watch?v=abcdefghijk","duration":60,"uploader":"Artist","extractor_key":"Youtube","formats":[{"format_id":"137","ext":"mp4","height":1080,"vcodec":"avc1","acodec":"none","filesize_approx":12345}],"http_headers":{"Cookie":"private"},"cookie":"private","token":"private"}""";

    [Fact]
    public async Task Analysis_keeps_unknown_measurements_unknown_and_uses_browser_only_when_selected()
    {
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(0, Metadata, ""));
        var service = new DownloadService(process, new EngineFakeComponents());
        var info = await service.AnalyseAsync(new Uri("https://www.youtube.com/watch?v=abcdefghijk"));
        Assert.Null(info.Formats[0].SizeBytes);
        Assert.Null(info.Formats[0].Fps);
        Assert.Equal(TimeSpan.FromSeconds(60), info.Duration);
        Assert.DoesNotContain("--cookies-from-browser", process.Calls[0].Arguments);
        await service.AnalyseAsync(info.Url, "firefox");
        Assert.Equal("firefox", EngineFakeProcess.Argument(process.Calls[1], "--cookies-from-browser"));
        Assert.Contains("--ignore-config", process.Calls[1].Arguments);
    }

    [Fact]
    public async Task Analysis_rejects_browser_profile_command_fragments_before_running()
    {
        var process = new EngineFakeProcess((_, _, _) => throw new InvalidOperationException());
        var service = new DownloadService(process, new EngineFakeComponents());
        await Assert.ThrowsAsync<MediaOperationException>(() => service.AnalyseAsync(new Uri("https://example.com/video"), "firefox:C:\\private"));
        Assert.Empty(process.Calls);
    }

    [Theory]
    [InlineData(DownloadMode.VideoAudio, VideoCodec.H264)]
    [InlineData(DownloadMode.VideoAudio, VideoCodec.Hevc)]
    [InlineData(DownloadMode.VideoOnly, VideoCodec.H264)]
    [InlineData(DownloadMode.VideoOnly, VideoCodec.Hevc)]
    public async Task Incompatible_WebM_codec_is_rejected_before_analysis_or_transfer(DownloadMode mode, VideoCodec codec)
    {
        using var files = new EngineTestFiles();
        var process = SuccessfulDownload();
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = mode, Container = MediaContainer.WebM, VideoCodec = codec }));
        Assert.Contains("WebM", error.Message);
        Assert.Contains("MKV", error.Message);
        Assert.Empty(process.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Theory]
    [InlineData(MediaContainer.Auto, VideoCodec.H264)]
    [InlineData(MediaContainer.Auto, VideoCodec.Hevc)]
    [InlineData(MediaContainer.Mkv, VideoCodec.H264)]
    [InlineData(MediaContainer.Mkv, VideoCodec.Hevc)]
    [InlineData(MediaContainer.WebM, VideoCodec.Original)]
    [InlineData(MediaContainer.WebM, VideoCodec.Av1)]
    public async Task Compatible_container_choices_reach_analysis(MediaContainer container, VideoCodec codec)
    {
        using var files = new EngineTestFiles();
        // Stop at a known extractor failure, proving validation allows this
        // combination without simulating a falsely compatible output file.
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(1, "", "Unavailable test fixture"));
        await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Container = container, VideoCodec = codec }));
        Assert.Contains("--dump-single-json", Assert.Single(process.Calls).Arguments);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Theory]
    [InlineData(MediaContainer.Mp4, ".webm", "vp9", "opus")]
    [InlineData(MediaContainer.Mp4, ".mkv", "h264", "aac")]
    [InlineData(MediaContainer.Mkv, ".mp4", "h264", "aac")]
    [InlineData(MediaContainer.WebM, ".mkv", "vp9", "opus")]
    public async Task Explicit_container_copy_remuxes_combined_source_before_promotion(MediaContainer container, string sourceExtension, string video, string audio)
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(sourceExtension, video, audio);
        var result = await new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = container });
        Assert.Equal(container switch { MediaContainer.Mp4 => ".mp4", MediaContainer.Mkv => ".mkv", _ => ".webm" }, Path.GetExtension(result.FilePath));
        var remux = Assert.Single(process.Calls, call => call.Arguments.Contains("-progress"));
        Assert.Equal("copy", EngineFakeProcess.Argument(remux, "-c"));
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Contains("-encoders"));
        Assert.True(File.Exists(result.FilePath));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
        if (container == MediaContainer.WebM)
        {
            var transfer = Assert.Single(process.Calls, call => call.Arguments.Contains("--no-simulate"));
            Assert.Contains("[vcodec~=?'^(vp8|vp9|vp09|av1|av01)']", EngineFakeProcess.Argument(transfer, "--format"));
            Assert.Contains("[acodec~=?'^(opus|vorbis)$']", EngineFakeProcess.Argument(transfer, "--format"));
        }
    }

    [Theory]
    [InlineData(MediaContainer.Mp4, ".mp4", "h264", "aac")]
    [InlineData(MediaContainer.Mkv, ".mkv", "h264", "aac")]
    [InlineData(MediaContainer.WebM, ".webm", "vp9", "opus")]
    public async Task Already_matching_container_is_verified_without_reencoding(MediaContainer container, string sourceExtension, string video, string audio)
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(sourceExtension, video, audio);
        var result = await new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = container });
        Assert.True(File.Exists(result.FilePath));
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Contains("-progress") || call.Arguments.Contains("-encoders"));
    }

    [Theory]
    [InlineData(MediaContainer.Mp4, ".webm")]
    [InlineData(MediaContainer.Mkv, ".webm")]
    [InlineData(MediaContainer.WebM, ".mkv")]
    public async Task Shared_demuxer_name_does_not_hide_wrong_actual_container_header(MediaContainer container, string sourceExtension)
    {
        using var files = new EngineTestFiles();
        var unrelated = files.Input("existing-user-media.mp4");
        var process = ContainerDownloadProcess(sourceExtension, "vp9", "opus", wrongExportHeader: true);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = container }));
        Assert.Contains("did not match the selected container", error.Message);
        Assert.Equal(new[] { unrelated }, Directory.GetFileSystemEntries(files.Root));
        Assert.Equal(Enumerable.Repeat((byte)17, 256), await File.ReadAllBytesAsync(unrelated));
    }

    [Fact]
    public async Task Container_choice_is_not_satisfied_by_renaming_a_different_container()
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(".mp4", "vp9", "opus", sourceHeaderExtension: ".webm");
        var result = await new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = MediaContainer.Mp4 });
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal("copy", EngineFakeProcess.Argument(Assert.Single(process.Calls, call => call.Arguments.Contains("-progress")), "-c"));
    }

    [Theory]
    [InlineData("qt  ")]
    [InlineData("3gp6")]
    public async Task Shared_MP4_demuxer_cannot_mislabel_QuickTime_or_3GP_as_selected_MP4(string brand)
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(".mp4", "h264", "aac", sourceBrand: brand);
        var result = await new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = MediaContainer.Mp4 });
        Assert.Equal("copy", EngineFakeProcess.Argument(Assert.Single(process.Calls, call => call.Arguments.Contains("-progress")), "-c"));
        Assert.Equal("isom", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(result.FilePath), 8, 4));
    }

    // A codec change merges into the target container when the streams allow it
    // ("mp4/mkv" lets yt-dlp fall back to MKV). HDR conversion applies only to a
    // source that is actually HDR, so the HDR row uses a PQ-tagged source; an
    // SDR source is covered in DownloadReliabilityTests.
    [Theory]
    [InlineData(MediaContainer.WebM, VideoCodec.Av1, ".mp4", "h264", "aac", false, "webm/mkv")]
    [InlineData(MediaContainer.Mp4, VideoCodec.H264, ".webm", "vp9", "opus", false, "mp4/mkv")]
    [InlineData(MediaContainer.Mp4, VideoCodec.Original, ".mp4", "h264", "aac", true, "mp4")]
    public async Task Codec_or_HDR_conversion_uses_safe_intermediate_merge_then_verified_target(MediaContainer container, VideoCodec codec, string sourceExtension, string video, string audio, bool hdr, string merge)
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(sourceExtension, video, audio, colorTransfer: hdr ? "smpte2084" : null);
        var result = await new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = container, VideoCodec = codec, HdrToSdr = hdr });
        var transfer = Assert.Single(process.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal(merge, EngineFakeProcess.Argument(transfer, "--merge-output-format"));
        Assert.DoesNotContain("[vcodec~", EngineFakeProcess.Argument(transfer, "--format"));
        var encode = Assert.Single(process.Calls, call => call.Arguments.Contains("-progress"));
        Assert.Equal(container == MediaContainer.WebM ? "libsvtav1" : "libx264", EngineFakeProcess.Argument(encode, "-c:v"));
        Assert.True(File.Exists(result.FilePath));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Fact]
    public async Task Known_incompatible_WebM_original_source_fails_before_transfer()
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(".mp4", "h264", "aac");
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = MediaContainer.WebM }));
        Assert.Contains("no available WebM-compatible video", error.Message);
        Assert.Contains("--dump-single-json", Assert.Single(process.Calls).Arguments);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Known_incompatible_WebM_original_audio_fails_before_transfer()
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(".mkv", "vp9", "aac");
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = MediaContainer.WebM }));
        Assert.Contains("no available WebM-compatible audio", error.Message);
        Assert.Contains("--dump-single-json", Assert.Single(process.Calls).Arguments);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Unknown_WebM_metadata_is_checked_against_actual_streams_without_encoding_bad_codecs()
    {
        using var files = new EngineTestFiles();
        var process = ContainerDownloadProcess(".mp4", "h264", "aac", unknownMetadata: true);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = MediaContainer.WebM }));
        Assert.Contains("cannot be preserved in WebM", error.Message);
        Assert.Contains(process.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Contains("-progress") || call.Arguments.Contains("-encoders"));
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_container_remux_cleans_staging_and_preserves_user_media(bool cancel)
    {
        using var files = new EngineTestFiles();
        var unrelated = files.Input("existing-user-media.mp4");
        using var cancellation = new CancellationTokenSource();
        var process = ContainerDownloadProcess(".mkv", "h264", "aac", failExport: !cancel, onExport: cancel ? cancellation.Cancel : null);
        var operation = new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/container"), OutputDirectory = files.Root, Container = MediaContainer.Mp4 }, cancellationToken: cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else await Assert.ThrowsAsync<MediaOperationException>(() => operation);
        Assert.Equal(new[] { unrelated }, Directory.GetFileSystemEntries(files.Root));
        Assert.Equal(Enumerable.Repeat((byte)17, 256), await File.ReadAllBytesAsync(unrelated));
    }

    [Fact]
    public async Task Playlist_analysis_returns_individual_public_urls_and_download_requires_selection_even_when_empty()
    {
        const string json = """{"_type":"playlist","id":"playlist","title":"A playlist","entries":[{"id":"abcdefghijk","title":"First","ie_key":"Youtube","duration":3},null,{"id":"removed","title":"Unavailable"}]}""";
        var process = new EngineFakeProcess((_, _, _) => new ProcessResult(0, json, ""));
        var service = new DownloadService(process, new EngineFakeComponents());
        var info = await service.AnalyseAsync(new Uri("https://www.youtube.com/playlist?list=123"));
        Assert.True(info.IsPlaylist);
        Assert.Equal(2, info.Entries.Count);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", info.Entries[0].Url!.AbsoluteUri);
        Assert.Null(info.Entries[1].Url);
        using var files = new EngineTestFiles();
        await Assert.ThrowsAsync<MediaOperationException>(() => service.DownloadAsync(new DownloadOptions { Url = info.Url, OutputDirectory = files.Root }));
        var emptyService = new DownloadService(new EngineFakeProcess((_, _, _) => new ProcessResult(0, "{\"_type\":\"playlist\",\"entries\":[]}", "")), new EngineFakeComponents());
        await Assert.ThrowsAsync<MediaOperationException>(() => emptyService.DownloadAsync(new DownloadOptions { Url = info.Url, OutputDirectory = files.Root }));
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Download_checks_tracks_keeps_subtitles_and_reports_observed_progress()
    {
        using var files = new EngineTestFiles();
        var progress = new EngineProgress();
        var process = SuccessfulDownload();
        var result = await new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://www.youtube.com/watch?v=abcdefghijk"), OutputDirectory = files.Root, MaxHeight = 720, SubtitleLanguages = "en" }, progress);
        Assert.True(File.Exists(result.FilePath));
        Assert.Single(Directory.GetFiles(files.Root, "*.vtt"));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
        Assert.Contains(progress.Values, value => value.Stage == JobStage.Downloading && value.Percent == 50);
        Assert.Contains(progress.Values, value => value.Stage == JobStage.Merging);
        var download = Assert.Single(process.Calls, call => call.Arguments.Contains("--no-simulate"));
        // '?' keeps formats whose height the site did not report.
        Assert.Contains("[height<=?720]", EngineFakeProcess.Argument(download, "--format"));
        Assert.Equal("1", EngineFakeProcess.Argument(download, "--concurrent-fragments"));
        Assert.Contains("--progress", download.Arguments); // --print otherwise suppresses the real templates.
        Assert.Equal("--", download.Arguments[^2]);
        // Finished queue items keep no engine metadata (archive mode writes it to source.json).
        Assert.Null(result.Media!.MetadataJson);
    }

    [Fact]
    public async Task Existing_output_is_preserved_and_failed_staging_is_cleaned()
    {
        using var files = new EngineTestFiles();
        var existing = Path.Combine(files.Root, "100_ Artist _ Movie [abcdefghijk].mp4");
        await File.WriteAllTextAsync(existing, "existing user content");
        await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(SuccessfulDownload(), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root }));
        Assert.Equal("existing user content", await File.ReadAllTextAsync(existing));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Directory_collision_for_main_or_subtitle_rejects_before_any_output_is_promoted(bool main, bool replace)
    {
        using var files = new EngineTestFiles();
        const string name = "100_ Artist _ Movie [abcdefghijk]";
        var blocked = Path.Combine(files.Root, name + (main ? ".mp4" : ".en.vtt"));
        Directory.CreateDirectory(blocked);
        await File.WriteAllTextAsync(Path.Combine(blocked, "user-content.txt"), "keep this folder");
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(SuccessfulDownload(), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, ReplaceExisting = replace }));
        Assert.Contains("already exists", error.Message);
        Assert.Equal("keep this folder", await File.ReadAllTextAsync(Path.Combine(blocked, "user-content.txt")));
        Assert.Empty(Directory.GetFiles(files.Root));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [EngineWindowsFact]
    public async Task Subtitle_move_failure_rolls_back_promoted_media_and_restores_explicit_replacements()
    {
        using var files = new EngineTestFiles();
        var existing = Path.Combine(files.Root, "100_ Artist _ Movie [abcdefghijk].mp4");
        await File.WriteAllTextAsync(existing, "original user output");
        FileStream? lockedSubtitle = null;
        try
        {
            var successful = SuccessfulDownload();
            var process = new EngineFakeProcess((request, output, token) =>
            {
                var result = successful.RunAsync(request, output, cancellationToken: token).GetAwaiter().GetResult();
                if (request.Arguments.Contains("--no-simulate")) lockedSubtitle = new FileStream(Directory.GetFiles(request.WorkingDirectory!, "*.vtt").Single(), FileMode.Open, FileAccess.Read, FileShare.None);
                return result;
            });
            var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, ReplaceExisting = true }));
            Assert.Contains("Existing files were restored", error.Message);
            Assert.Equal("original user output", await File.ReadAllTextAsync(existing));
            Assert.Empty(Directory.GetFiles(files.Root, "*.vtt"));
            Assert.Empty(Directory.GetFiles(files.Root, ".restore-*", SearchOption.AllDirectories));
        }
        finally { lockedSubtitle?.Dispose(); }
    }

    [Fact]
    public async Task Output_created_after_preflight_is_preserved_when_replacement_was_not_selected()
    {
        using var files = new EngineTestFiles();
        var staging = Path.Combine(files.Root, ".keaclip-work-race");
        Directory.CreateDirectory(staging);
        var stagedMedia = Path.Combine(staging, "new.mp4");
        var stagedSubtitle = Path.Combine(staging, "new.vtt");
        await File.WriteAllTextAsync(stagedMedia, "new media");
        await File.WriteAllTextAsync(stagedSubtitle, "new subtitle");
        var mediaOutput = Path.Combine(files.Root, "output.mp4");
        var subtitleOutput = Path.Combine(files.Root, "output.vtt");
        // Inject another writer exactly between whole-set preflight and backups.
        // This exercises the filesystem race without timing or production hooks.
        var outputs = new EngineRaceOutputList([(stagedMedia, mediaOutput), (stagedSubtitle, subtitleOutput)], () => File.WriteAllText(subtitleOutput, "another writer's file"));
        var retainRecovery = false;
        var promote = typeof(DownloadService).GetMethod("PromoteOutputs", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<EnginePromotion>();
        Assert.Throws<MediaOperationException>(() => promote(outputs, false, out retainRecovery));
        Assert.Equal("another writer's file", await File.ReadAllTextAsync(subtitleOutput));
        Assert.False(File.Exists(mediaOutput));
        Assert.Equal("new media", await File.ReadAllTextAsync(stagedMedia));
        Assert.False(retainRecovery);
        Assert.Empty(Directory.GetFiles(staging, ".restore-*"));
    }

    [Fact]
    public async Task Rollback_collision_preserves_existing_writer_and_original_backup_for_recovery()
    {
        using var files = new EngineTestFiles();
        var staging = Path.Combine(files.Root, ".keaclip-work-recovery");
        Directory.CreateDirectory(staging);
        var staged = Path.Combine(staging, "new.mp4");
        var output = Path.Combine(files.Root, "output.mp4");
        await File.WriteAllTextAsync(staged, "new download");
        await File.WriteAllTextAsync(output, "original user file");
        // A second writer claims the destination after its original was backed
        // up. Both promotion and restoration must fail without destroying either.
        var outputs = new EngineRaceOutputList([(staged, output)], () => File.WriteAllText(output, "new writer's file"), 3);
        var retainRecovery = false;
        var promote = typeof(DownloadService).GetMethod("PromoteOutputs", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<EnginePromotion>();
        var error = Assert.Throws<MediaOperationException>(() => promote(outputs, true, out retainRecovery));
        Assert.Contains("Recovery files were preserved", error.Message);
        Assert.True(retainRecovery); // The caller must retain this owned staging folder.
        Assert.Equal("new writer's file", await File.ReadAllTextAsync(output));
        Assert.Equal("new download", await File.ReadAllTextAsync(staged));
        var backup = Assert.Single(Directory.GetFiles(staging, ".restore-*"));
        Assert.Equal("original user file", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task A_foreign_reported_output_is_rejected_without_deleting_it()
    {
        using var files = new EngineTestFiles();
        var foreign = files.Input("unrelated.mp4");
        var process = new EngineFakeProcess((request, output, _) =>
        {
            if (request.Arguments.Contains("--dump-single-json")) return new ProcessResult(0, Metadata, "");
            output!("KC_FILE:" + JsonSerializer.Serialize(foreign));
            return new ProcessResult(0, "", "");
        });
        await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root }));
        Assert.True(File.Exists(foreign));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Fact]
    public async Task Cancellation_removes_owned_partial_output()
    {
        using var files = new EngineTestFiles();
        using var cancellation = new CancellationTokenSource();
        var process = new EngineFakeProcess((request, _, _) =>
        {
            if (request.Arguments.Contains("--dump-single-json")) return new ProcessResult(0, Metadata, "");
            File.WriteAllText(Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace(".%(ext)s", ".mp4.part", StringComparison.Ordinal)), "partial");
            cancellation.Cancel();
            return new ProcessResult(0, "", "");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root }, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Successful_replacement_registers_exact_backups_before_discarding_them()
    {
        using var files = new EngineTestFiles(); var process = SuccessfulDownload(); var service = new DownloadService(process, new EngineFakeComponents(), new OwnedTemporaryWorkspaceService(Path.Combine(files.Root, "service-data")));
        var options = new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root };
        var original = await service.DownloadAsync(options); await File.WriteAllTextAsync(original.FilePath, "old user media");
        var registered = false;
        var result = await service.DownloadAsync(options with { ReplaceExisting = true }, new ArchiveProgress(progress =>
        {
            if (progress.Stage != JobStage.Finished) return;
            var staging = process.Calls.Last(call => call.Arguments.Contains("--no-simulate")).WorkingDirectory!;
            var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(staging, OwnedTemporaryWorkspaceService.ReceiptName)))!;
            var expected = JsonSerializer.Deserialize<string[]>(receipt["ExpectedFiles"]!.GetValue<string>())!;
            var backups = Directory.GetFiles(staging, ".restore-*"); Assert.Equal(2, backups.Length);
            Assert.All(backups, backup => Assert.Contains(Path.GetFileName(backup), expected)); registered = true;
        }));
        Assert.True(registered); Assert.Equal(original.FilePath, result.FilePath); Assert.Equal(Enumerable.Repeat((byte)19, 512), await File.ReadAllBytesAsync(result.FilePath));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*")); Assert.Empty(Directory.GetFiles(Path.Combine(files.Root, "service-data", "TemporaryWorkspaces"), "*.lock"));
    }

    [Fact]
    public void Backup_receipt_failure_occurs_before_any_original_is_moved()
    {
        using var files = new EngineTestFiles(); var staging = Path.Combine(files.Root, "staged"); Directory.CreateDirectory(staging);
        var media = files.Input("old.mp4"); var subtitle = files.Input("old.vtt"); var stagedMedia = Path.Combine(staging, "new.mp4"); var stagedSubtitle = Path.Combine(staging, "new.vtt");
        File.WriteAllText(stagedMedia, "new media"); File.WriteAllText(stagedSubtitle, "new subtitles"); var workspace = new FailingBackupReceipt(staging);
        var method = typeof(DownloadService).GetMethod("PromoteOwnedOutputs", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failure = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [new List<(string Staged, string Output)> { (stagedMedia, media), (stagedSubtitle, subtitle) }, true, workspace, false]));
        Assert.IsType<IOException>(failure.InnerException); Assert.Equal(2, workspace.Registrations); Assert.Empty(Directory.GetFiles(staging, ".restore-*"));
        Assert.Equal(Enumerable.Repeat((byte)17, 256), File.ReadAllBytes(media)); Assert.Equal(Enumerable.Repeat((byte)17, 256), File.ReadAllBytes(subtitle));
        Assert.Equal("new media", File.ReadAllText(stagedMedia)); Assert.Equal("new subtitles", File.ReadAllText(stagedSubtitle));
    }

    private sealed class FailingBackupReceipt(string path) : ITemporaryWorkspace
    {
        public string Path => path; public int Registrations { get; private set; }
        public void RegisterPayload(string name) { Assert.StartsWith(".restore-", name); if (++Registrations == 2) throw new IOException("Simulated receipt write failure"); }
        public void PreserveForRecovery() { } public void PrepareForPromotion() { } public void Dispose() { }
    }

    [Fact]
    public async Task Archive_sanitizes_metadata_and_checksums_every_preserved_file()
    {
        using var files = new EngineTestFiles();
        var result = await new DownloadService(SuccessfulDownload(), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive });
        Assert.NotNull(result.ArchiveDirectory);
        var json = await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "source.json"));
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("http_headers", json);
        using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "archive.json")));
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", archive.RootElement.GetProperty("SourceUrl").GetString());
        Assert.Equal("abcdefghijk", archive.RootElement.GetProperty("Id").GetString());
        Assert.Equal(AppIdentity.Version, archive.RootElement.GetProperty("AppVersion").GetString());
        Assert.Equal(AppIdentity.Protocol, archive.RootElement.GetProperty("ApplicationId").GetString());
        var readable = await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "archive-info.txt"));
        Assert.Contains("Title: 100% Artist / Movie", readable);
        Assert.Contains("Uploader: Artist", readable);
        Assert.Contains("Source: Youtube", readable);
        Assert.Contains("Reference: https://www.youtube.com/watch?v=abcdefghijk", readable);
        Assert.Contains("SHA256SUMS.txt", readable);
        Assert.Contains(AppIdentity.Version, readable);
        Assert.DoesNotContain("private", readable);
        var sums = await File.ReadAllLinesAsync(Path.Combine(result.ArchiveDirectory!, "SHA256SUMS.txt"));
        // Standard "sha256sum -c" format: LF only (CRLF made every line fail with "name\r" in a native run).
        var manifestText = await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "SHA256SUMS.txt"));
        Assert.DoesNotContain('\r', manifestText); Assert.EndsWith("\n", manifestText);
        Assert.DoesNotContain(sums, line => line.Contains(".keaclip-workspace", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(result.ArchiveDirectory!), path => Path.GetFileName(path).StartsWith(".keaclip-workspace", StringComparison.Ordinal));
        foreach (var sum in sums)
        {
            var path = Path.Combine(result.ArchiveDirectory!, sum[66..]);
            Assert.Equal(sum[..64], Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant());
        }
        Assert.Contains(sums, sum => sum.EndsWith("archive.json", StringComparison.Ordinal));
        Assert.Contains(sums, sum => sum.EndsWith("technical.json", StringComparison.Ordinal));
        Assert.Contains(sums, sum => sum.EndsWith("archive-info.txt", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(result.ArchiveDirectory!, "archive.zip")));
        Assert.Null(result.ArchiveZipPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_includes_automatic_captions_only_when_selected(bool automaticCaptions)
    {
        using var files = new EngineTestFiles();
        var process = SuccessfulDownload();
        await new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive, AutomaticCaptions = automaticCaptions });
        var request = Assert.Single(process.Calls, item => item.Arguments.Contains("--no-simulate"));
        Assert.Equal(automaticCaptions, request.Arguments.Contains("--write-auto-subs"));
        Assert.Contains("--write-subs", request.Arguments);
        Assert.Equal("bv*+ba/b", EngineFakeProcess.Argument(request, "--format"));
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("frame-rate")]
    [InlineData("smallest")]
    [InlineData("codec")]
    [InlineData("hdr")]
    [InlineData("container")]
    public async Task Direct_archive_call_rejects_quality_reducing_options_before_any_process(string choice)
    {
        using var files = new EngineTestFiles();
        var process = SuccessfulDownload();
        var options = new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive };
        options = choice switch { "resolution" => options with { MaxHeight = 720 }, "frame-rate" => options with { Fps = 30 }, "smallest" => options with { SmallestFile = true }, "codec" => options with { VideoCodec = VideoCodec.H264 }, "hdr" => options with { HdrToSdr = true }, _ => options with { Container = MediaContainer.Mp4 } };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(options));
        Assert.Contains("highest available source", error.Message);
        Assert.Empty(process.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Archive_zip_keeps_raw_media_and_independently_verified_manifest_entries()
    {
        using var files = new EngineTestFiles();
        var result = await new DownloadService(SuccessfulDownload(), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive, ArchiveZip = true });
        var package = Path.Combine(result.ArchiveDirectory!, "archive.zip");
        Assert.Equal(package, result.ArchiveZipPath);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(Enumerable.Repeat((byte)19, 512), await File.ReadAllBytesAsync(result.FilePath));
        using var zip = ZipFile.OpenRead(package);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith(".keaclip-workspace", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(result.ArchiveDirectory!), path => Path.GetFileName(path).StartsWith(".keaclip-workspace", StringComparison.Ordinal));
        var manifest = Assert.Single(zip.Entries, entry => entry.FullName == "SHA256SUMS.txt");
        Assert.DoesNotContain('\r', await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "archive.zip.sha256")));
        await using (var content = manifest.Open())
        using (var reader = new StreamReader(content))
        {
            var lines = (await reader.ReadToEndAsync()).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var entry = Assert.Single(zip.Entries, item => item.FullName == line[66..]);
                await using var entryContent = entry.Open();
                Assert.Equal(line[..64], Convert.ToHexString(await SHA256.HashDataAsync(entryContent)).ToLowerInvariant());
            }
        }
        foreach (var entry in zip.Entries)
        {
            Assert.Equal(entry.Name, entry.FullName);
            Assert.DoesNotContain("archive.zip", entry.FullName);
            await using var content = entry.Open();
            Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(result.ArchiveDirectory!, entry.FullName))), await SHA256.HashDataAsync(content));
        }
        var packageChecksum = await File.ReadAllTextAsync(package + ".sha256");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package))).ToLowerInvariant() + "  archive.zip\n", packageChecksum);
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_archive_or_file_is_preserved_even_when_replacement_was_selected(bool directory)
    {
        using var files = new EngineTestFiles();
        var original = Path.Combine(files.Root, "100_ Artist _ Movie [abcdefghijk] Archive");
        var existing = directory ? Path.Combine(original, "archive.zip") : original;
        if (directory) Directory.CreateDirectory(original);
        await File.WriteAllTextAsync(existing, "existing user's package");
        var result = await new DownloadService(SuccessfulDownload(), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive, ArchiveZip = true, ReplaceExisting = true });
        Assert.NotEqual(original, result.ArchiveDirectory);
        Assert.Equal("existing user's package", await File.ReadAllTextAsync(existing));
        Assert.True(File.Exists(Path.Combine(result.ArchiveDirectory!, "archive.zip")));
    }

    [Fact]
    public async Task Cancellation_during_zip_packaging_removes_only_owned_staging()
    {
        using var files = new EngineTestFiles();
        var unrelated = files.Input("unrelated.mp4");
        using var cancellation = new CancellationTokenSource();
        var observedPackaging = false;
        var progress = new ArchiveProgress(update =>
        {
            if (update.Message != "Packaging the raw archive as ZIP") return;
            observedPackaging = true; cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DownloadService(SuccessfulDownload(), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive, ArchiveZip = true }, progress, cancellation.Token));
        Assert.True(observedPackaging);
        Assert.Equal(new[] { unrelated }, Directory.GetFileSystemEntries(files.Root));
        Assert.Equal(Enumerable.Repeat((byte)17, 256), await File.ReadAllBytesAsync(unrelated));
    }

    [Fact]
    public async Task An_unexpected_reserved_package_filename_is_not_overwritten()
    {
        using var files = new EngineTestFiles();
        var normal = SuccessfulDownload();
        var process = new EngineFakeProcess((request, output, token) =>
        {
            var result = normal.RunAsync(request, output, cancellationToken: token).GetAwaiter().GetResult();
            if (request.Arguments.Contains("--no-simulate")) File.WriteAllText(Path.Combine(request.WorkingDirectory!, "archive.zip"), "unexpected engine file");
            return result;
        });
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive, ArchiveZip = true }));
        Assert.Contains("conflicts", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Failed_zip_integrity_check_does_not_promote_an_archive_or_touch_existing_media()
    {
        using var files = new EngineTestFiles();
        var unrelated = files.Input("unrelated.mp4");
        var progress = new ArchiveProgress(update =>
        {
            if (update.Message != "Checking the ZIP against the raw archive checksums") return;
            var staging = Directory.GetDirectories(files.Root, ".keaclip-work-*").Single();
            File.WriteAllBytes(Path.Combine(staging, "archive.zip"), [1, 2, 3]);
        });
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(SuccessfulDownload(), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, Mode = DownloadMode.Archive, ArchiveZip = true }, progress));
        Assert.Contains("could not be written or verified", error.Message);
        Assert.Equal(new[] { unrelated }, Directory.GetFileSystemEntries(files.Root));
        Assert.Equal(Enumerable.Repeat((byte)17, 256), await File.ReadAllBytesAsync(unrelated));
    }

    private sealed class ArchiveProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }

    private static EngineFakeProcess ContainerDownloadProcess(string sourceExtension, string video, string audio, bool wrongExportHeader = false, string? sourceHeaderExtension = null, bool unknownMetadata = false, bool failExport = false, Action? onExport = null, string? sourceBrand = null, string? colorTransfer = null)
    {
        var outputs = new Dictionary<string, (string Extension, string Video, string Audio)>(StringComparer.OrdinalIgnoreCase);
        return new EngineFakeProcess((request, output, _) =>
        {
            if (request.Arguments.Contains("--dump-single-json")) return new ProcessResult(0, JsonSerializer.Serialize(new { id = "fixture", title = "Container fixture", duration = 60, formats = new[] { new { format_id = "0", ext = sourceExtension.TrimStart('.'), vcodec = unknownMetadata ? null : video, acodec = unknownMetadata ? null : audio } } }), "");
            if (request.Arguments.Contains("--no-simulate"))
            {
                var path = Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace(".%(ext)s", sourceExtension, StringComparison.Ordinal));
                WriteContainerFixture(path, sourceHeaderExtension ?? sourceExtension);
                if (sourceBrand is not null)
                {
                    using var file = File.OpenWrite(path);
                    file.Position = 8;
                    file.Write(System.Text.Encoding.ASCII.GetBytes(sourceBrand));
                }
                outputs[path] = (sourceHeaderExtension ?? sourceExtension, video, audio);
                output!("KC_FILE:" + JsonSerializer.Serialize(path));
                return new ProcessResult(0, "", "");
            }
            if (request.Executable == "fake-ffprobe")
            {
                var actual = outputs[request.Arguments[^1]];
                var probe = JsonNode.Parse(EngineFakeProcess.ProbeJson())!;
                probe["format"]!["format_name"] = actual.Extension == ".mp4" ? "mov,mp4,m4a,3gp,3g2,mj2" : "matroska,webm";
                probe["streams"]![0]!["codec_name"] = actual.Video;
                if (colorTransfer is not null) probe["streams"]![0]!["color_transfer"] = colorTransfer;
                probe["streams"]![1]!["codec_name"] = actual.Audio;
                return new ProcessResult(0, probe.ToJsonString(), "");
            }
            if (request.Arguments.Contains("-encoders")) return new ProcessResult(0, " V....D libx264 H264\n V....D libsvtav1 AV1\n", "");
            Assert.Contains("-progress", request.Arguments);
            var destination = request.Arguments[^1];
            var extension = Path.GetExtension(destination);
            var copying = request.Arguments.Contains("-c");
            var outputVideo = copying ? video : EngineFakeProcess.Argument(request, "-c:v") == "libsvtav1" ? "av1" : "h264";
            var outputAudio = copying ? audio : extension == ".webm" ? "opus" : "aac";
            WriteContainerFixture(destination, wrongExportHeader ? extension == ".webm" ? ".mkv" : ".webm" : extension);
            outputs[destination] = (extension, outputVideo, outputAudio);
            onExport?.Invoke();
            return new ProcessResult(failExport ? 1 : 0, "", failExport ? "private cookie=secret" : "");
        });
    }

    private static void WriteContainerFixture(string path, string extension)
    {
        var content = Enumerable.Repeat((byte)31, 512).ToArray();
        var header = extension switch { ".mp4" => new byte[] { 0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0 }, ".webm" => new byte[] { 0x1a, 0x45, 0xdf, 0xa3, 0x87, 0x42, 0x82, 0x84, 119, 101, 98, 109 }, _ => new byte[] { 0x1a, 0x45, 0xdf, 0xa3, 0x8b, 0x42, 0x82, 0x88, 109, 97, 116, 114, 111, 115, 107, 97 } };
        header.CopyTo(content, 0);
        File.WriteAllBytes(path, content);
    }

    private static EngineFakeProcess SuccessfulDownload() => new((request, output, _) =>
    {
        if (request.Arguments.Contains("--dump-single-json")) return new ProcessResult(0, Metadata, "");
        if (request.Executable == "fake-ffprobe") return new ProcessResult(0, EngineFakeProcess.ProbeJson(), "");
        var pattern = EngineFakeProcess.Argument(request, "--output");
        var name = pattern.Replace("%(ext)s", "mp4", StringComparison.Ordinal);
        var path = Path.Combine(request.WorkingDirectory!, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)19, 512).ToArray());
        File.WriteAllText(Path.ChangeExtension(path, ".en.vtt"), "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n");
        output!("KC_PROGRESS:{\"downloaded_bytes\":50,\"total_bytes\":100,\"speed\":2,\"eta\":25}");
        output!("KC_POST:{\"postprocessor\":\"Merger\",\"status\":\"started\"}");
        output!("KC_FILE:" + JsonSerializer.Serialize(path));
        return new ProcessResult(0, "", "");
    });
}
