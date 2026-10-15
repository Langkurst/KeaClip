using System.Text.Json;
using System.Text.Json.Nodes;
using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// Each test pins one confirmed download defect. The fake engine passes both
// standard output and standard error, as the real ProcessRunner does.
public sealed class DownloadReliabilityTests
{
    private const string Watch = "https://www.youtube.com/watch?v=abcdefghijk";

    [Fact]
    public async Task Post_processing_stages_are_read_from_standard_error_and_housekeeping_keeps_the_stage()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario
        {
            // yt-dlp's --print implies quiet mode, which sends the post-processing
            // template to standard error (verified with yt-dlp 2026.08.19).
            Transfer = (_, stdout, stderr, _) =>
            {
                stdout(Progress("137", "avc1", "none", 100, 100, "finished"));
                stderr("KC_POST:{\"status\": \"started\", \"postprocessor\": \"Merger\"}");
                // The pipes are read separately; a late download line must not reopen Downloading.
                stdout(Progress("137", "avc1", "none", 100, 100, "finished"));
                stderr("KC_POST:{\"status\": \"finished\", \"postprocessor\": \"Merger\"}");
                stderr("KC_POST:{\"status\": \"started\", \"postprocessor\": \"FixupM4a\"}");
                // Reported even when the file is already in the target container.
                stderr("KC_POST:{\"status\": \"started\", \"postprocessor\": \"VideoRemuxer\"}");
                stderr("KC_POST:{\"status\": \"started\", \"postprocessor\": \"Metadata\"}");
                stderr("KC_POST:{\"status\": \"started\", \"postprocessor\": \"EmbedThumbnail\"}");
                stderr("KC_POST:{\"status\": \"started\", \"postprocessor\": \"MoveFiles\"}");
            }
        };
        var progress = new EngineProgress();
        await engine.Service().DownloadAsync(Options(files), progress);
        var stages = progress.Values.Select(value => value.Stage).SkipWhile(stage => stage != JobStage.Merging).TakeWhile(stage => stage != JobStage.Verifying).ToArray();
        Assert.Equal([JobStage.Merging, JobStage.WritingMetadata, JobStage.WritingMetadata], stages);
        Assert.Contains(progress.Values, value => value.Message == "Embedding the cover image");
        Assert.DoesNotContain(progress.Values, value => value.Stage == JobStage.Converting);
    }

    [Fact]
    public async Task A_download_update_decided_before_merging_is_never_reported_after_it()
    {
        using var files = new EngineTestFiles();
        // The two pipes are read on separate threads. Here the queue is slow to take
        // the last download update while the merge starts on the other pipe.
        Action<string>? errors = null;
        Task? merge = null;
        var reported = new List<JobStage>();
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == JobStage.Downloading && merge is null)
            {
                merge = Task.Run(() => errors!("KC_POST:{\"status\": \"started\", \"postprocessor\": \"Merger\"}"));
                merge.Wait(TimeSpan.FromMilliseconds(300));
            }
            lock (reported) reported.Add(value.Stage);
        });
        var engine = new Scenario
        {
            Transfer = (_, stdout, stderr, _) =>
            {
                errors = stderr;
                stdout(Progress("137", "avc1", "none", 100, 100, "finished"));
                merge!.Wait();
            }
        };
        await engine.Service().DownloadAsync(Options(files), progress);
        Assert.Equal([JobStage.Downloading, JobStage.Merging], reported.Where(stage => stage is JobStage.Downloading or JobStage.Merging));
    }

    [Fact]
    public async Task Separate_video_and_audio_streams_report_one_rising_percentage_with_stage_names()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario
        {
            Transfer = (_, stdout, _, _) =>
            {
                // Subtitle tracks download first and carry no format ID.
                stdout("KC_PROGRESS:{\"v\":null,\"a\":null,\"f\":null,\"p\":{\"downloaded_bytes\":10,\"total_bytes\":10,\"status\":\"finished\"}}");
                stdout("KC_PLAN:{\"f\":[\"137\", \"251\"],\"v\":[\"avc1.64001f\", \"none\"],\"a\":[\"none\", \"opus\"],\"s\":[900, 100],\"e\":[]}");
                stdout(Progress("137", "avc1.64001f", "none", 0, 900));
                stdout(Progress("137", "avc1.64001f", "none", 450, 900));
                stdout(Progress("137", "avc1.64001f", "none", 900, 900, "finished"));
                stdout(Progress("251", "none", "opus", 0, 100));
                stdout(Progress("251", "none", "opus", 50, 100));
                stdout(Progress("251", "none", "opus", 100, 100, "finished"));
            }
        };
        var progress = new EngineProgress();
        await engine.Service().DownloadAsync(Options(files), progress);
        var downloading = progress.Values.Where(value => value.Stage == JobStage.Downloading && value.Message != "Downloading subtitles").ToArray();
        Assert.Equal([0d, 45, 90, 90, 95, 100], downloading.Select(value => Math.Round(value.Percent!.Value, 3)));
        Assert.Equal(["Downloading video", "Downloading video", "Downloading video", "Downloading audio", "Downloading audio", "Downloading audio"], downloading.Select(value => value.Message));
    }

    [Fact]
    public async Task Unknown_stream_sizes_still_never_move_the_percentage_backwards()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario
        {
            Transfer = (_, stdout, _, _) =>
            {
                stdout("KC_PLAN:{\"f\":[\"137\", \"251\"],\"v\":[\"avc1\", \"none\"],\"a\":[\"none\", \"opus\"],\"s\":[],\"e\":[]}");
                stdout("KC_PROGRESS:{\"v\":\"avc1\",\"a\":\"none\",\"f\":\"137\",\"p\":{\"downloaded_bytes\":5,\"total_bytes_estimate\":10}}");
                stdout("KC_PROGRESS:{\"v\":\"avc1\",\"a\":\"none\",\"f\":\"137\",\"p\":{\"fragment_index\":10,\"fragment_count\":10,\"status\":\"finished\"}}");
                stdout("KC_PROGRESS:{\"v\":\"none\",\"a\":\"opus\",\"f\":\"251\",\"p\":{\"downloaded_bytes\":1,\"total_bytes\":10}}");
            }
        };
        var progress = new EngineProgress();
        await engine.Service().DownloadAsync(Options(files), progress);
        var percents = progress.Values.Where(value => value.Stage == JobStage.Downloading).Select(value => Math.Round(value.Percent!.Value, 3)).ToArray();
        Assert.Equal([42.5, 85, 86.5], percents);
    }

    [Fact]
    public void Failure_text_comes_from_engine_errors_not_from_titles_or_folders_in_progress_output()
    {
        // The output folder and the title both contain words from other messages.
        var stdout = "KC_PROGRESS:{\"p\":{\"filename\":\"D:\\\\Private Videos\\\\How to Log In - Private Video Tips [abcdefghijk].webm\"}}";
        var stderr = "WARNING: [youtube] Sign in to confirm your age\nKC_POST:{\"status\": \"started\", \"postprocessor\": \"Merger\"}\nERROR: Postprocessing: Conversion failed!";
        Assert.StartsWith("FFmpeg could not process this file.", EngineSupport.ClassifyFailure(new ProcessResult(1, stdout, stderr), "Download"));
        // FFmpeg prefixes its error with the input path.
        Assert.StartsWith("FFmpeg could not process this file.", EngineSupport.ClassifyFailure(new ProcessResult(1, "", "D:\\Private Videos\\How to log in.mp4: Invalid data found when processing input"), "Media export"));
    }

    [Theory]
    // Verbatim yt-dlp 2026.08.19 messages, reproduced with scratch browser profiles.
    [InlineData("ERROR: Could not copy Chrome cookie database. See https://github.com/yt-dlp/yt-dlp/issues/7271 for more info", "Browser sign-in could not be read")]
    [InlineData("ERROR: Failed to decrypt with DPAPI. See https://github.com/yt-dlp/yt-dlp/issues/10927 for more info", "Browser sign-in could not be read")]
    [InlineData("ERROR: could not find firefox cookies database in 'C:\\Users\\someone\\AppData\\Roaming\\Mozilla\\Firefox\\Profiles'", "No sign-in data was found")]
    [InlineData("ERROR: [youtube] abcdefghijk: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies for the authentication.", "Sign-in is required")]
    [InlineData("ERROR: [youtube] abcdefghijk: Private video. Sign in if you've been granted access to this video", "This video is private")]
    [InlineData("ERROR: [youtube] abcdefghijk: Video unavailable. This video has been removed by the uploader", "This media is unavailable")]
    [InlineData("ERROR: [youtube] abcdefghijk: The uploader has not made this video available in your country", "not available in your region")]
    [InlineData("ERROR: [youtube] abcdefghijk: This video is DRM protected", "protected by DRM")]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", "The site refused the download")]
    // A retried failure: yt-dlp clears its progress line with a carriage return.
    [InlineData("ERROR: \r[download] Got error: HTTP Error 403: Forbidden. Giving up after 3 retries", "The site refused the download")]
    [InlineData("ERROR: [generic] Unable to download webpage: HTTP Error 429: Too Many Requests", "limiting requests")]
    [InlineData("ERROR: unable to write data: [WinError 112] There is not enough space on the disk", "Not enough disk space")]
    [InlineData("ERROR: [instagram] abc: Unable to extract shared data; please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template", "website may have changed")]
    [InlineData("ERROR: Postprocessing: Supported filetypes for thumbnail embedding are: mp3, mkv/mka, ogg/opus/flac, m4a/mp4/m4v/mov", "cover image could not be embedded")]
    [InlineData("ERROR: Postprocessing: audio conversion failed: Conversion failed!", "FFmpeg could not process this file")]
    // Real transfer failures after yt-dlp's retries (local test server, yt-dlp 2026.08.19).
    [InlineData("ERROR: \r[download] Got error: HTTP Error 503: Service Unavailable. Giving up after 3 retries", "The website is having trouble")]
    [InlineData("ERROR: \r[download] Got error: 709940 bytes read, 709941 more expected. Giving up after 3 retries", "the transfer was cut off")]
    [InlineData("ERROR: \r[download] Got error: HTTPConnection(host='127.0.0.1', port=18799): Failed to establish a new connection: [WinError 10061] No connection could be made because the target machine actively refused it. Giving up after 3 retries", "Could not reach the media service")]
    // The last error decides: here an earlier optional subtitle error names a 404.
    [InlineData("ERROR: Unable to download video subtitles for 'en': HTTP Error 404: Not Found\nERROR: \r[download] Got error: HTTP Error 403: Forbidden. Giving up after 3 retries", "The site refused the download")]
    // After a failed merge yt-dlp may stop on a missing file; the merge error explains it.
    [InlineData("ERROR: Error opening input files: Invalid data found when processing input\nERROR: [Errno 2] No such file or directory: 'C:\\media\\x.mp4'", "FFmpeg could not process this file")]
    // A full disk is the cause even when a later step reports its own error.
    [InlineData("ERROR: unable to write data: [Errno 28] No space left on device\nERROR: Postprocessing: Conversion failed!", "Not enough disk space")]
    public void Real_engine_errors_get_their_specific_message_which_survives_queue_redaction(string error, string expected)
    {
        var message = EngineSupport.ClassifyFailure(new ProcessResult(1, "KC_PROGRESS:{\"p\":{\"filename\":\"C:\\\\Videos\\\\Private video sign in.mp4\"}}", "WARNING: a harmless warning\n" + error), "Download");
        Assert.Contains(expected, message);
        // The queue redacts error text; a message must not be garbled by it.
        Assert.Equal(message, MediaValidation.Redact(message));
    }

    [Fact]
    public async Task A_failed_transfer_is_classified_from_its_error_lines_and_not_its_progress_paths()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario
        {
            ReportFile = false, ExitCode = 1,
            Transfer = (_, stdout, stderr, _) =>
            {
                stdout("KC_PROGRESS:{\"v\":\"avc1\",\"a\":\"none\",\"f\":\"137\",\"p\":{\"downloaded_bytes\":1,\"total_bytes\":9,\"filename\":\"D:\\\\Private Videos\\\\How to log in.mp4\"}}");
                stderr("ERROR: unable to download video data: HTTP Error 403: Forbidden");
            }
        };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(Options(files)));
        Assert.StartsWith("The site refused the download.", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task An_item_of_a_multi_video_post_downloads_by_position_from_the_post_address()
    {
        using var files = new EngineTestFiles();
        var post = new Uri("https://x.com/user/status/1234567890");
        var engine = new Scenario
        {
            Metadata = Playlist(post, Video("111", "Post video one", null), Video("222", "Post video two", null)),
            ItemMetadata = Playlist(post, Video("222", "Post video two", null))
        };
        var analysed = await engine.Service().AnalyseAsync(post);
        // yt-dlp gives every item the post's own webpage_url.
        Assert.All(analysed.Entries, entry => Assert.Equal(post, entry.Url));
        var target = Assert.NotNull(DownloadService.PlaylistEntryTarget(analysed, 1));
        Assert.Equal((post, "2"), target);
        var result = await engine.Service().DownloadAsync(Options(files) with { Url = target.Url, PlaylistItems = target.PlaylistItems });
        Assert.Equal("Post video two [222].mp4", Path.GetFileName(result.FilePath));
        var analysis = engine.Calls.Last(call => call.Arguments.Contains("--dump-single-json"));
        Assert.Equal("2", EngineFakeProcess.Argument(analysis, "--playlist-items"));
        Assert.DoesNotContain("--flat-playlist", analysis.Arguments);
        var transfer = Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal("2", EngineFakeProcess.Argument(transfer, "--playlist-items"));
        Assert.Contains("--yes-playlist", transfer.Arguments);
        Assert.DoesNotContain("--no-playlist", transfer.Arguments);
        // Otherwise an archive would store the post's metadata under the item's name.
        Assert.Contains("--no-write-playlist-metafiles", transfer.Arguments);
    }

    [Fact]
    public async Task Playlist_entries_with_their_own_page_are_queued_by_address_and_bad_positions_are_refused()
    {
        var list = new Uri("https://www.youtube.com/playlist?list=PL123");
        var engine = new Scenario { Metadata = Playlist(list, new { id = "abcdefghijk", title = "First", ie_key = "Youtube", _type = "url", url = Watch }) };
        var analysed = await engine.Service().AnalyseAsync(list);
        Assert.Equal((new Uri(Watch), (string?)null), DownloadService.PlaylistEntryTarget(analysed, 0));
        Assert.Null(DownloadService.PlaylistEntryTarget(analysed, 1));
        using var files = new EngineTestFiles();
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(Options(files) with { PlaylistItems = "1-3" }));
        Assert.Contains("playlist picker", error.Message);
        Assert.Empty(engine.Calls.Skip(1));
    }

    [Fact]
    public async Task Channel_tabs_listed_as_entries_are_recognised_as_lists_rather_than_media()
    {
        // A channel root lists its tabs (flat yt-dlp output); each tab is a playlist.
        var channel = new Uri("https://www.youtube.com/@someone");
        var engine = new Scenario
        {
            Metadata = Playlist(channel,
                new { id = "UCx", title = "someone - Videos", ie_key = "YoutubeTab", _type = "url", url = "https://www.youtube.com/@someone/videos" },
                new { id = "abcdefghijk", title = "A video", ie_key = "Youtube", _type = "url", url = Watch })
        };
        var analysed = await engine.Service().AnalyseAsync(channel);
        Assert.True(DownloadService.PlaylistEntryIsList(analysed, 0));
        Assert.False(DownloadService.PlaylistEntryIsList(analysed, 1));
        Assert.False(DownloadService.PlaylistEntryIsList(analysed, 2));
    }

    [Fact]
    public async Task A_post_holding_a_single_item_downloads_that_item_without_a_picker()
    {
        using var files = new EngineTestFiles();
        var post = new Uri("https://www.instagram.com/p/abc/");
        var engine = new Scenario { Metadata = Playlist(post, Video("only", "Only video", null)), ItemMetadata = Playlist(post, Video("only", "Only video", null)) };
        var result = await engine.Service().DownloadAsync(Options(files) with { Url = post });
        Assert.Equal("Only video [only].mp4", Path.GetFileName(result.FilePath));
        Assert.Equal("1", EngineFakeProcess.Argument(Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate")), "--playlist-items"));
    }

    [Fact]
    public async Task Archive_keeps_titles_descriptions_and_public_ids_exact_while_removing_real_secrets()
    {
        using var files = new EngineTestFiles();
        var metadata = new JsonObject
        {
            ["id"] = "123456", ["title"] = "The Secret Life of Pets - Official Trailer", ["uploader"] = "Password Manager Reviews",
            ["description"] = "Full video: https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42 Token Economics Explained",
            ["webpage_url"] = "https://www.facebook.com/watch/?v=123456&fbclid=tracking-value", ["duration"] = 60, ["extractor_key"] = "Facebook",
            ["http_headers"] = new JsonObject { ["Cookie"] = "SID=sign-in-value" }, ["cookies"] = "SID=sign-in-value",
            ["thumbnail"] = "https://img.example.com/t.jpg?oh=signed-value",
            ["thumbnails"] = new JsonArray(new JsonObject { ["url"] = "https://img.example.com/t.jpg?oh=signed-value" }),
            ["formats"] = new JsonArray(new JsonObject { ["format_id"] = "hd", ["url"] = "https://cdn.example.com/ip/203.0.113.9/sig/abc/video.mp4?expire=1&signature=zzz", ["vcodec"] = "h264", ["acodec"] = "aac" }),
            ["requested_downloads"] = new JsonArray(new JsonObject { ["filepath"] = "C:\\Users\\someone\\video.mp4" }),
            ["requested_subtitles"] = new JsonObject { ["en"] = new JsonObject { ["url"] = "file:///C:/Users/someone/subs.vtt", ["ext"] = "vtt" } },
            ["tags"] = new JsonArray("password", "secret")
        };
        var engine = new Scenario { Metadata = metadata.ToJsonString() };
        var result = await engine.Service().DownloadAsync(Options(files) with { Url = new Uri("https://www.facebook.com/watch/?v=123456"), Mode = DownloadMode.Archive });
        var readable = await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "archive-info.txt"));
        Assert.Contains("Title: The Secret Life of Pets - Official Trailer", readable);
        Assert.Contains("Uploader: Password Manager Reviews", readable);
        Assert.Contains("Reference: https://www.facebook.com/watch/?v=123456" + Environment.NewLine, readable);
        var source = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "source.json")))!;
        Assert.Equal("The Secret Life of Pets - Official Trailer", source["title"]!.GetValue<string>());
        Assert.Equal("Full video: https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42 Token Economics Explained", source["description"]!.GetValue<string>());
        Assert.Equal(["password", "secret"], source["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.Equal("https://www.facebook.com/watch/?v=123456", source["webpage_url"]!.GetValue<string>());
        Assert.Equal("https://cdn.example.com/", source["formats"]![0]!["url"]!.GetValue<string>());
        Assert.Equal("https://img.example.com/t.jpg", source["thumbnails"]![0]!["url"]!.GetValue<string>());
        var text = source.ToJsonString();
        foreach (var secret in new[] { "sign-in-value", "203.0.113.9", "signature", "signed-value", "tracking-value", "someone", "http_headers" }) Assert.DoesNotContain(secret, text);
        Assert.Null(result.Media!.MetadataJson);
        Assert.Empty(result.Media.Formats);
    }

    // Just part of it: only that range is fetched (exact cuts), the file is named for the part so it never takes the
    // whole download's name, and a range that cannot work is refused before anything runs.
    [Fact]
    public async Task A_part_of_the_media_is_fetched_as_a_section_with_its_own_name()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario();
        var result = await engine.Service().DownloadAsync(Options(files) with { SectionStart = TimeSpan.FromSeconds(45), SectionEnd = TimeSpan.FromSeconds(80.5) });
        var transfer = Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal("*45.000-80.500", EngineFakeProcess.Argument(transfer, "--download-sections"));
        Assert.Contains("--force-keyframes-at-cuts", transfer.Arguments);
        Assert.EndsWith(" (0-45 to 1-20).%(ext)s", EngineFakeProcess.Argument(transfer, "--output"));
        Assert.Contains(" (0-45 to 1-20)", Path.GetFileName(result.FilePath));

        var whole = new Scenario();
        await whole.Service().DownloadAsync(Options(files) with { OutputDirectory = Path.Combine(Path.GetDirectoryName(result.FilePath)!, "whole") });
        Assert.DoesNotContain("--download-sections", Assert.Single(whole.Calls, call => call.Arguments.Contains("--no-simulate")).Arguments);
    }

    [Theory]
    [InlineData(45d, null, "both a start and an end")]
    [InlineData(80d, 45d, "start before it ends")]
    [InlineData(10d, 10.2, "half a second")]
    public async Task A_part_that_cannot_work_is_refused_before_anything_runs(double start, double? end, string expected)
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario();
        var options = Options(files) with { SectionStart = TimeSpan.FromSeconds(start), SectionEnd = end is { } value ? TimeSpan.FromSeconds(value) : null };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(options));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
    }

    [Fact]
    public async Task Archive_mode_keeps_the_whole_original_and_refuses_a_part()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario();
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(Options(files) with { Mode = DownloadMode.Archive, SectionStart = TimeSpan.Zero, SectionEnd = TimeSpan.FromSeconds(5) }));
        Assert.Contains("Archive mode keeps the whole original", error.Message);
    }
    [Fact]
    public async Task Unknown_metadata_never_blocks_a_format_and_frame_rate_is_a_preference_then_a_conversion()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario { SourceFps = 60 };
        var progress = new EngineProgress();
        var result = await engine.Service().DownloadAsync(Options(files) with { MaxHeight = 720, Fps = 30, AudioLanguage = "en" }, progress);
        // The inner export's own completion must not mark the job finished early.
        Assert.Equal([progress.Values.Count - 1], progress.Values.Select((value, index) => (value, index)).Where(item => item.value.Stage == JobStage.Finished).Select(item => item.index));
        var transfer = Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal("bv*[height<=?720]+ba[language^=?en]/b[height<=?720][language^=?en]", EngineFakeProcess.Argument(transfer, "--format"));
        Assert.Equal("lang,quality,res,fps:30,vcodec:h264", EngineFakeProcess.Argument(transfer, "--format-sort"));
        // The 1080p60 download keeps its resolution and is converted to 30 fps.
        var encode = Assert.Single(engine.Calls, call => call.Arguments.Contains("-progress"));
        Assert.Contains("fps=30", EngineFakeProcess.Argument(encode, "-vf"));
        Assert.True(File.Exists(result.FilePath));

        using var calm = new EngineTestFiles();
        var native = new Scenario { SourceFps = 30000d / 1001 };
        await native.Service().DownloadAsync(Options(calm) with { Fps = 30 });
        Assert.DoesNotContain(native.Calls, call => call.Arguments.Contains("-progress"));
    }

    [Fact]
    public async Task Video_only_falls_back_to_a_combined_stream_and_removes_its_audio_without_reencoding()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario();
        var result = await engine.Service().DownloadAsync(Options(files) with { Mode = DownloadMode.VideoOnly });
        Assert.Equal("bv/bv*", EngineFakeProcess.Argument(Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate")), "--format"));
        var strip = Assert.Single(engine.Calls, call => call.Executable == "fake-ffmpeg");
        Assert.Equal("copy", EngineFakeProcess.Argument(strip, "-c"));
        Assert.Contains("-0:a", strip.Arguments);
        Assert.DoesNotContain("-c:v", strip.Arguments);
        Assert.True(File.Exists(result.FilePath));
        Assert.Empty(Directory.GetDirectories(files.Root, ".keaclip-work-*"));
    }

    [Theory]
    [InlineData(DownloadMode.VideoAudio, MediaContainer.WebM, AudioFormat.Original, false, null)]
    [InlineData(DownloadMode.Audio, MediaContainer.Auto, AudioFormat.Wav, false, null)]
    [InlineData(DownloadMode.VideoOnly, MediaContainer.Auto, AudioFormat.Original, true, "webm>mkv")]
    [InlineData(DownloadMode.VideoAudio, MediaContainer.Mp4, AudioFormat.Original, true, "mp4")]
    [InlineData(DownloadMode.Audio, MediaContainer.Auto, AudioFormat.Mp3, true, null)]
    public async Task Cover_art_is_embedded_only_where_the_final_container_can_hold_it(DownloadMode mode, MediaContainer container, AudioFormat audio, bool embedded, string? remux)
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario { Metadata = Single("vp9", "opus") };
        var progress = new EngineProgress();
        await Record.ExceptionAsync(() => engine.Service().DownloadAsync(Options(files) with { Mode = mode, Container = container, AudioFormat = audio, EmbedThumbnail = true }, progress));
        var transfer = Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal(embedded, transfer.Arguments.Contains("--embed-thumbnail"));
        Assert.Equal(remux, transfer.Arguments.Contains("--remux-video") ? EngineFakeProcess.Argument(transfer, "--remux-video") : null);
        if (!embedded) Assert.Contains(progress.Values, value => value.Message?.Contains("cannot hold a cover image", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task HDR_to_SDR_prefers_native_SDR_and_converts_only_a_source_that_is_actually_HDR()
    {
        using var files = new EngineTestFiles();
        var sdr = new Scenario { Transfer709 = true };
        await sdr.Service().DownloadAsync(Options(files) with { HdrToSdr = true });
        Assert.Equal("lang,quality,res,fps,hdr:SDR", EngineFakeProcess.Argument(Assert.Single(sdr.Calls, call => call.Arguments.Contains("--no-simulate")), "--format-sort"));
        Assert.DoesNotContain(sdr.Calls, call => call.Arguments.Contains("-progress") || call.Arguments.Contains("-encoders"));

        using var hdrFiles = new EngineTestFiles();
        var hdr = new Scenario { ColorTransfer = "smpte2084" };
        await hdr.Service().DownloadAsync(Options(hdrFiles) with { HdrToSdr = true });
        Assert.Contains("tonemap", EngineFakeProcess.Argument(Assert.Single(hdr.Calls, call => call.Arguments.Contains("-progress")), "-vf"));
    }

    [Fact]
    public async Task A_source_already_in_the_chosen_codec_is_kept_instead_of_reencoded()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario { Metadata = Single("avc1.64001f", "mp4a.40.2") };
        var result = await engine.Service().DownloadAsync(Options(files) with { VideoCodec = VideoCodec.H264 });
        var transfer = Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal("lang,quality,res,fps,vcodec:h264,acodec:aac", EngineFakeProcess.Argument(transfer, "--format-sort"));
        Assert.Equal("mp4/mkv", EngineFakeProcess.Argument(transfer, "--merge-output-format"));
        Assert.DoesNotContain(engine.Calls, call => call.Arguments.Contains("-c:v") || call.Arguments.Contains("-encoders"));
        Assert.Equal(".mp4", Path.GetExtension(result.FilePath));
    }

    [Fact]
    public async Task A_matching_codec_with_audio_that_WebM_cannot_hold_is_still_converted()
    {
        using var files = new EngineTestFiles();
        // AV1 video already matches, but AAC audio cannot be copied into WebM.
        var engine = new Scenario { Metadata = Single("av01.0.08M.08", "mp4a.40.2"), Extension = ".mkv", VideoCodecName = "av1" };
        var result = await engine.Service().DownloadAsync(Options(files) with { Container = MediaContainer.WebM, VideoCodec = VideoCodec.Av1 });
        var encode = Assert.Single(engine.Calls, call => call.Arguments.Contains("-progress"));
        Assert.Equal("libopus", EngineFakeProcess.Argument(encode, "-c:a"));
        Assert.Equal(".webm", Path.GetExtension(result.FilePath));
    }

    [Fact]
    public async Task Archive_requests_manual_subtitles_and_only_original_language_automatic_captions()
    {
        using var files = new EngineTestFiles();
        var metadata = JsonNode.Parse(Single("avc1", "mp4a"))!.AsObject();
        metadata["subtitles"] = new JsonObject { ["en"] = new JsonArray(), ["live_chat"] = new JsonArray() };
        metadata["automatic_captions"] = new JsonObject { ["en-orig"] = new JsonArray(), ["en"] = new JsonArray(), ["de"] = new JsonArray(), ["fr"] = new JsonArray() };
        var engine = new Scenario { Metadata = metadata.ToJsonString() };
        await engine.Service().DownloadAsync(Options(files) with { Mode = DownloadMode.Archive, AutomaticCaptions = true });
        var transfer = Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Single(transfer.Arguments, argument => argument == "--sub-langs");
        Assert.Equal("en,en-orig,-live_chat", EngineFakeProcess.Argument(transfer, "--sub-langs"));

        using var plain = new EngineTestFiles();
        var manual = new Scenario { Metadata = metadata.ToJsonString() };
        await manual.Service().DownloadAsync(Options(plain) with { Mode = DownloadMode.Archive });
        Assert.Equal("all,-live_chat", EngineFakeProcess.Argument(Assert.Single(manual.Calls, call => call.Arguments.Contains("--no-simulate")), "--sub-langs"));
    }

    [Theory]
    // yt-dlp 2026.08.19 writes "ERROR: \r[download] Got error: ..."; the process
    // runner splits lines at the carriage return, so the message arrives on its own line.
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_optional_subtitle_track_keeps_the_finished_archive_with_a_note(bool splitByCarriageReturn)
    {
        using var files = new EngineTestFiles();
        // Real yt-dlp output: the media finished and was reported, then exit code 1.
        const string failure = "[download] Got error: [WinError 2] The system cannot find the file specified: 'C:\\media\\missing.en-orig.vtt'. Giving up after 3 retries";
        var engine = new Scenario
        {
            ExitCode = 1,
            Transfer = (_, _, stderr, _) =>
            {
                if (splitByCarriageReturn) { stderr("ERROR: "); stderr(failure); }
                else stderr("ERROR: " + failure);
            }
        };
        var result = await engine.Service().DownloadAsync(Options(files) with { Mode = DownloadMode.Archive, AutomaticCaptions = true });
        Assert.Contains("--ignore-errors", Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate")).Arguments);
        var readable = await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "archive-info.txt"));
        Assert.Contains("Notes:", readable);
        Assert.Contains("subtitle or caption tracks could not be downloaded", readable);
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task A_subtitle_track_the_site_refuses_is_a_warning_and_the_download_completes_with_a_note()
    {
        using var files = new EngineTestFiles();
        // With --ignore-errors yt-dlp reports a refused track as a warning and exits 0.
        var engine = new Scenario { Transfer = (_, _, stderr, _) => stderr("WARNING: Unable to download video subtitles for 'en-orig': HTTP Error 429: Too Many Requests") };
        var progress = new EngineProgress();
        var result = await engine.Service().DownloadAsync(Options(files) with { SubtitleLanguages = "en", AutomaticCaptions = true }, progress);
        Assert.True(File.Exists(result.FilePath));
        Assert.Contains("subtitle or caption tracks could not be downloaded", progress.Values.Last().Message);
    }

    [Theory]
    [InlineData("ERROR: Postprocessing: Conversion failed!", true)]
    // With --ignore-errors a failed post-processor still reports the file, then exits 1.
    [InlineData("ERROR: Supported filetypes for thumbnail embedding are: mp3, mkv/mka, ogg/opus/flac, m4a/mp4/m4v/mov", true)]
    [InlineData("ERROR: Conversion failed!", true)]
    [InlineData("ERROR: [youtube] abcdefghijk: Something else failed", true)]
    [InlineData("ERROR: [download] Got error: HTTP Error 429: Too Many Requests", false)]
    public async Task Other_failures_after_a_reported_file_or_without_one_still_fail(string error, bool reported)
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario { ExitCode = 1, ReportFile = reported, Transfer = (_, _, stderr, _) => stderr(error) };
        await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(Options(files)));
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Theory]
    [InlineData(AudioFormat.Ogg, 320, "1")]
    [InlineData(AudioFormat.Ogg, 256, "2")]
    [InlineData(AudioFormat.Ogg, 128, "6")]
    [InlineData(AudioFormat.Mp3, 320, "320K")]
    // libopus refuses more than 256 kbps for a mono source.
    [InlineData(AudioFormat.Opus, 320, "256K")]
    [InlineData(AudioFormat.Opus, 160, "160K")]
    public async Task Ogg_and_Opus_audio_settings_work_for_mono_sources(AudioFormat format, int kbps, string expected)
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario { Extension = format switch { AudioFormat.Ogg => ".ogg", AudioFormat.Opus => ".opus", _ => ".mp3" }, Video = false };
        await engine.Service().DownloadAsync(Options(files) with { Mode = DownloadMode.Audio, AudioFormat = format, AudioKbps = kbps });
        Assert.Equal(expected, EngineFakeProcess.Argument(Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate")), "--audio-quality"));
    }

    [Fact]
    public async Task A_source_without_any_sound_is_saved_as_video_in_the_default_mode_with_a_notice()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario { Metadata = Single("avc1", "none"), Audio = false };
        var progress = new EngineProgress();
        var result = await engine.Service().DownloadAsync(Options(files), progress);
        Assert.True(File.Exists(result.FilePath));
        Assert.Contains("no sound", progress.Values.Last().Message);

        using var audioFiles = new EngineTestFiles();
        var audioOnly = new Scenario { Metadata = Single("avc1", "none"), Audio = false };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => audioOnly.Service().DownloadAsync(Options(audioFiles) with { Mode = DownloadMode.Audio }));
        Assert.Contains("no sound track", error.Message);
        Assert.DoesNotContain(audioOnly.Calls, call => call.Arguments.Contains("--no-simulate"));

        using var claimedFiles = new EngineTestFiles();
        var claimed = new Scenario { Audio = false };
        var missing = await Assert.ThrowsAsync<MediaOperationException>(() => claimed.Service().DownloadAsync(Options(claimedFiles)));
        Assert.Contains("choose Video only", missing.Message);
    }

    [Fact]
    public async Task An_existing_output_is_detected_before_the_transfer_starts()
    {
        using var files = new EngineTestFiles();
        var existing = Path.Combine(files.Root, "Fixture title [abcdefghijk].mkv");
        await File.WriteAllTextAsync(existing, "user media");
        var engine = new Scenario();
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(Options(files)));
        Assert.Contains("already exists", error.Message);
        Assert.DoesNotContain(engine.Calls, call => call.Arguments.Contains("--no-simulate"));
        Assert.Equal("user media", await File.ReadAllTextAsync(existing));
        Assert.Equal([existing], Directory.GetFileSystemEntries(files.Root));
        // A different mode's file with the same name is not a conflict.
        var audio = new Scenario { Extension = ".mp3", Video = false };
        await audio.Service().DownloadAsync(Options(files) with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3 });
    }

    [Fact]
    public async Task A_playlist_longer_than_the_listing_limit_is_reported_as_cut_short()
    {
        var list = new Uri("https://www.youtube.com/playlist?list=PL123");
        var entries = Enumerable.Range(1, 501).Select(index => (object)new { id = "video" + index, title = "Video " + index, _type = "url", url = "https://www.youtube.com/watch?v=v" + index.ToString("D10") }).ToArray();
        var root = JsonNode.Parse(Playlist(list, entries))!;
        root["playlist_count"] = 1200;
        var engine = new Scenario { Metadata = root.ToJsonString() };
        var info = await engine.Service().AnalyseAsync(list);
        Assert.Equal("501", EngineFakeProcess.Argument(Assert.Single(engine.Calls), "--playlist-end"));
        Assert.Equal(500, info.Entries.Count);
        Assert.Equal((500, (int?)1200, true), DownloadService.PlaylistExtent(info));
        var post = new Scenario { Metadata = Playlist(new Uri("https://x.com/u/status/1"), Video("1", "One", null), Video("2", "Two", null)) };
        Assert.Equal((2, (int?)null, false), DownloadService.PlaylistExtent(await post.Service().AnalyseAsync(new Uri("https://x.com/u/status/1"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    public async Task A_failed_merge_fails_cleanly_and_ignores_errors_only_when_subtitles_are_requested(string? subtitles)
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario
        {
            ReportFile = false, ExitCode = 1,
            Transfer = (request, _, stderr, output) =>
            {
                // Real yt-dlp with a corrupt audio stream: both streams stay, the merged
                // file is missing and the metadata step has written its chapter file.
                var name = Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace(".%(ext)s", "", StringComparison.Ordinal));
                File.Delete(output);
                File.WriteAllBytes(name + ".f1.mp4", [1, 2, 3]);
                File.WriteAllText(name + ".meta", ";FFMETADATA1");
                stderr("ERROR: Error opening input files: Invalid data found when processing input");
            }
        };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(Options(files) with { SubtitleLanguages = subtitles }));
        Assert.StartsWith("FFmpeg could not process this file.", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
        // Without subtitle tracks yt-dlp must stop at the first failed step.
        Assert.Equal(subtitles is not null, Assert.Single(engine.Calls, call => call.Arguments.Contains("--no-simulate")).Arguments.Contains("--ignore-errors"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streams_with_named_format_ids_are_cleaned_after_a_failed_merge_or_a_cancel(bool cancel)
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario
        {
            ReportFile = false, ExitCode = 1,
            Transfer = (request, stdout, stderr, output) =>
            {
                // X and Vimeo name their streams "http-720p" rather than a number
                // (real yt-dlp leftovers, reproduced with a local test server).
                stdout("KC_PLAN:{\"f\":[\"http-720p\", \"http-audio\"],\"v\":[\"avc1\", \"none\"],\"a\":[\"none\", \"mp4a\"],\"s\":[900, 100],\"e\":[]}");
                var name = Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace(".%(ext)s", "", StringComparison.Ordinal));
                File.Delete(output);
                File.WriteAllBytes(name + ".fhttp-720p.mp4", [1]);
                File.WriteAllBytes(name + ".fhttp-audio.m4a.part", [1]);
                File.WriteAllBytes(name + ".fhttp-audio.m4a.part-Frag3", [1]);
                if (cancel) throw new OperationCanceledException();
                stderr("ERROR: Postprocessing: Error opening input files: Invalid data found when processing input");
            }
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Service().DownloadAsync(Options(files)));
        else await Assert.ThrowsAsync<MediaOperationException>(() => engine.Service().DownloadAsync(Options(files)));
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public void A_name_of_up_to_140_characters_given_as_the_template_comes_back_unchanged()
    {
        // Replace passes the existing file's name as the template. Older versions cut
        // "<title> [id]" at 140 characters, so such names are common.
        var info = new MediaInfo("abcdefghijk", new string('t', 130), new Uri(Watch), null, null, null, null, null, null, [], [], null);
        var older = MediaValidation.FriendlyFileName(info.Title + " [abcdefghijk]", 140);
        Assert.Equal(140, older.Length);
        Assert.Equal(older, DownloadService.OutputBaseName(info, older));
        var current = DownloadService.OutputBaseName(info);
        Assert.Equal(140, current.Length);
        Assert.EndsWith(" [abcdefghijk]", current);
        Assert.Equal(current, DownloadService.OutputBaseName(info, current));
        var emoji = new string('e', 138) + "\U0001F600";
        Assert.Equal(emoji, DownloadService.OutputBaseName(info, emoji));
        // The reserved-name prefix is counted before the cut, which stays between whole characters.
        Assert.Equal("_CON." + new string('r', 135), DownloadService.OutputBaseName(info, "CON." + new string('r', 135) + "\U0001F600"));
    }

    [Fact]
    public void Archive_metadata_drops_key_addresses_segment_secrets_and_share_tokens()
    {
        var input = new JsonObject
        {
            ["id"] = "123", ["title"] = "https://example.com/a?token=TITLEKEPT",
            ["original_url"] = "https://x.com/user/status/123?s=20&t=AbCdSharerToken123",
            ["webpage_url"] = "https://vimeo.com/123?t=1m30s&share=copy",
            ["formats"] = new JsonArray(new JsonObject
            {
                ["format_id"] = "hls-1", ["url"] = "https://cdn.example.com/hls/index.m3u8?hdnts=HMACSECRET",
                ["hls_aes"] = new JsonObject { ["uri"] = "https://keys.example.com/key?token=KEYTOKEN", ["key"] = "00112233" },
                ["extra_param_to_segment_url"] = "hdnts=exp=1~acl=*~hmac=HMACSECRET2",
                ["request_data"] = "session=SESSIONVALUE",
                ["downloader_options"] = new JsonObject { ["http_chunk_size"] = 10485760 },
                ["dash_manifest"] = "https://cdn.example.com/ip/203.0.113.9/manifest.mpd?sig=SIGVALUE"
            }),
            ["key_uri"] = "https://keys.example.com/key?id=KEYID"
        };
        var output = JsonNode.Parse(EngineSupport.SafeMetadata(input.ToJsonString()))!;
        var text = output.ToJsonString();
        foreach (var secret in new[] { "KEYTOKEN", "00112233", "HMACSECRET", "SESSIONVALUE", "SharerToken", "SIGVALUE", "203.0.113.9", "KEYID", "hls_aes", "http_chunk_size" }) Assert.DoesNotContain(secret, text);
        Assert.Equal("https://x.com/user/status/123", output["original_url"]!.GetValue<string>());
        // A start time is public and stays.
        Assert.Equal("https://vimeo.com/123?t=1m30s", output["webpage_url"]!.GetValue<string>());
        Assert.Equal("https://example.com/a?token=TITLEKEPT", output["title"]!.GetValue<string>());
        Assert.Equal("https://cdn.example.com/", output["formats"]![0]!["dash_manifest"]!.GetValue<string>());
    }

    [Theory]
    // Default names: the cut must fall before the " [id]" suffix and between whole characters.
    [InlineData(124, null)]
    // A template name: the old 140-unit cut left the first half of the emoji behind.
    [InlineData(139, "%(title)s")]
    public void A_long_title_is_cut_between_whole_characters_and_keeps_its_id_suffix(int letters, string? template)
    {
        var title = new string('a', letters) + "\U0001F600\U0001F600 tail";
        var info = new MediaInfo("abcdefghijk", title, new Uri(Watch), null, null, null, null, null, null, [], [], null);
        var name = DownloadService.OutputBaseName(info, template);
        if (template is null) Assert.EndsWith(" [abcdefghijk]", name);
        Assert.True(name.Length <= 140);
        Assert.Equal(name, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(name)));
        for (var index = 0; index < name.Length; index++)
            if (char.IsSurrogate(name[index])) { Assert.True(index + 1 < name.Length && char.IsHighSurrogate(name[index]) && char.IsLowSurrogate(name[index + 1])); index++; }
    }

    [Fact]
    public async Task An_unreadable_completion_report_falls_back_to_the_single_named_file_in_the_job_folder()
    {
        using var files = new EngineTestFiles();
        var engine = new Scenario
        {
            ReportFile = false,
            // An unpaired surrogate escape is valid for yt-dlp but rejected by System.Text.Json.
            Transfer = (_, stdout, _, output) => stdout("KC_FILE:\"" + output.Replace("\\", "\\\\", StringComparison.Ordinal)[..^4] + "\\ud83d.mp4\"")
        };
        var result = await engine.Service().DownloadAsync(Options(files));
        Assert.Equal("Fixture title [abcdefghijk].mp4", Path.GetFileName(result.FilePath));
    }

    private static DownloadOptions Options(EngineTestFiles files) => new() { Url = new Uri(Watch), OutputDirectory = files.Root };
    private static string Progress(string format, string video, string audio, long done, long total, string status = "downloading") =>
        "KC_PROGRESS:" + JsonSerializer.Serialize(new { v = video, a = audio, f = format, p = new { status, downloaded_bytes = done, total_bytes = total, speed = 10.0, eta = 1 } });
    private static string Single(string video, string audio) => JsonSerializer.Serialize(new { id = "abcdefghijk", title = "Fixture title", webpage_url = Watch, duration = 60, extractor_key = "Youtube", formats = new[] { new { format_id = "18", ext = "mp4", vcodec = video, acodec = audio } } });
    private static object Video(string id, string title, string? webpageUrl) => new { id, title, duration = 60, webpage_url = webpageUrl, formats = new[] { new { format_id = "hd", ext = "mp4", vcodec = "avc1", acodec = "mp4a" } } };
    private static string Playlist(Uri url, params object[] entries) => JsonSerializer.Serialize(new { _type = "playlist", id = "list", title = "A list", webpage_url = url.AbsoluteUri, entries = entries.Select((entry, index) =>
    {
        // Mirrors yt-dlp, which copies the parent's webpage_url into entries without one.
        var node = JsonSerializer.SerializeToNode(entry)!.AsObject();
        if (node["webpage_url"] is null && node["_type"] is null) node["webpage_url"] = url.AbsoluteUri;
        node["playlist_index"] = index + 1;
        return node;
    }).ToArray() });

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }

    private delegate void TransferScript(ProcessRequest request, Action<string> stdout, Action<string> stderr, string output);

    private sealed class Scenario
    {
        public string Metadata { get; init; } = Single("avc1", "mp4a");
        public string? ItemMetadata { get; init; }
        public string Extension { get; init; } = ".mp4";
        public bool ReportFile { get; init; } = true;
        public int ExitCode { get; init; }
        public TransferScript? Transfer { get; init; }
        public bool Video { get; init; } = true;
        public bool Audio { get; init; } = true;
        public double SourceFps { get; init; } = 30;
        public string? ColorTransfer { get; init; }
        public string VideoCodecName { get; init; } = "h264";
        public bool Transfer709 { get; init; }
        public List<ProcessRequest> Calls { get; } = [];
        private readonly HashSet<string> withoutAudio = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> converted = new(StringComparer.OrdinalIgnoreCase);

        public DownloadService Service() => new(new Runner(this), new EngineFakeComponents());

        private ProcessResult Run(ProcessRequest request, Action<string> stdout, Action<string> stderr)
        {
            Calls.Add(request);
            if (request.Executable == "fake-yt-dlp" && request.Arguments.Contains("--dump-single-json"))
                return new ProcessResult(0, request.Arguments.Contains("--playlist-items") && ItemMetadata is not null ? ItemMetadata : Metadata, "");
            if (request.Executable == "fake-yt-dlp")
            {
                var output = Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace(".%(ext)s", Extension, StringComparison.Ordinal));
                File.WriteAllBytes(output, MediaBytes(Extension));
                Transfer?.Invoke(request, stdout, stderr, output);
                if (ReportFile) stdout("KC_FILE:" + JsonSerializer.Serialize(output));
                return new ProcessResult(ExitCode, "", "");
            }
            if (request.Executable == "fake-ffprobe") return new ProcessResult(0, Probe(request.Arguments[^1]), "");
            if (request.Arguments.Contains("-encoders")) return new ProcessResult(0, " V....D libx264 H264\n V....D libsvtav1 AV1\n", "");
            if (request.Arguments.Contains("-filters")) return new ProcessResult(0, EngineFakeProcess.FullBuildFilters, "");
            var destination = request.Arguments[^1];
            File.WriteAllBytes(destination, MediaBytes(Path.GetExtension(destination)));
            if (request.Arguments.Contains("-0:a")) withoutAudio.Add(destination);
            if (request.Arguments.Contains("-c:v")) converted.Add(destination);
            return new ProcessResult(0, "", "");
        }

        private string Probe(string path)
        {
            var stripped = withoutAudio.Contains(path);
            var reencoded = converted.Contains(path);
            var streams = new JsonArray();
            if (Video)
            {
                var video = new JsonObject { ["codec_type"] = "video", ["codec_name"] = VideoCodecName, ["width"] = 1920, ["height"] = 1080, ["avg_frame_rate"] = reencoded ? "30/1" : SourceFps.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (Transfer709) video["color_transfer"] = "bt709";
                if (ColorTransfer is not null && !reencoded) video["color_transfer"] = ColorTransfer;
                streams.Add(video);
            }
            if (Audio && !stripped) streams.Add(new JsonObject { ["codec_type"] = "audio", ["codec_name"] = "aac", ["sample_rate"] = "48000", ["channels"] = 2 });
            var formatName = Path.GetExtension(path).ToLowerInvariant() switch { ".mp4" or ".m4a" => "mov,mp4,m4a,3gp,3g2,mj2", ".mkv" or ".webm" => "matroska,webm", ".mp3" => "mp3", _ => "ogg" };
            return new JsonObject { ["format"] = new JsonObject { ["duration"] = "60", ["format_name"] = formatName }, ["streams"] = streams }.ToJsonString();
        }

        private static byte[] MediaBytes(string extension)
        {
            var content = Enumerable.Repeat((byte)19, 512).ToArray();
            byte[] header = extension switch
            {
                ".mp4" => [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0],
                ".webm" => [0x1a, 0x45, 0xdf, 0xa3, 0x87, 0x42, 0x82, 0x84, 119, 101, 98, 109],
                ".mkv" => [0x1a, 0x45, 0xdf, 0xa3, 0x8b, 0x42, 0x82, 0x88, 109, 97, 116, 114, 111, 115, 107, 97],
                _ => []
            };
            header.CopyTo(content, 0);
            return content;
        }

        private sealed class Runner(Scenario scenario) : IProcessRunner
        {
            public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(scenario.Run(request, stdout ?? (_ => { }), stderr ?? (_ => { })));
            }
        }
    }
}
