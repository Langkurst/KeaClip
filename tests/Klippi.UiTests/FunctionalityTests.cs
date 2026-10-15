using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using Avalonia.Headless.XUnit;
using Klippi.Core;
using Klippi.Desktop;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.UiTests;

// Download, library, editor and share behaviour behind the pages. Each test fails without the
// fix it names, except those marked as regression guards, which pin behaviour a fix must keep.
public sealed class FunctionalityTests
{
    private const string Post = "https://x.com/kea/status/1";

    // An X/Instagram post with three videos: every item carries the post's own address.
    private static MediaInfo PostInfo => UiFakes.Info with
    {
        Url = new(Post), Title = "Three kea clips",
        Entries = [new("a", "First clip", new(Post), null), new("b", "Second clip", new(Post), null), new("c", "Third clip", new(Post), null)],
        MetadataJson = """{"id":"1","title":"Three kea clips","webpage_url":"https://x.com/kea/status/1","_type":"playlist","entries":[{"id":"a","title":"First clip","webpage_url":"https://x.com/kea/status/1"},{"id":"b","title":"Second clip","webpage_url":"https://x.com/kea/status/1"},{"id":"c","title":"Third clip","webpage_url":"https://x.com/kea/status/1"}]}""",
    };

    // A channel page: its Videos tab is a list, the other entry is one video.
    private static MediaInfo ChannelInfo => UiFakes.Info with
    {
        Url = new("https://www.youtube.com/@kea"), Title = "Kea channel",
        Entries = [new("tab", "Kea - Videos", new("https://www.youtube.com/@kea/videos"), null), new("abcdefghijk", "One video", new("https://www.youtube.com/watch?v=abcdefghijk"), null)],
        MetadataJson = """{"id":"kea","title":"Kea channel","_type":"playlist","entries":[{"_type":"url","ie_key":"YoutubeTab","id":"tab","title":"Kea - Videos","url":"https://www.youtube.com/@kea/videos"},{"_type":"url","ie_key":"Youtube","id":"abcdefghijk","title":"One video","url":"https://www.youtube.com/watch?v=abcdefghijk"}]}""",
    };

    // ----- #8/#61 existing-file probe -----

    [AvaloniaFact]
    public async Task An_existing_output_with_a_200_character_title_raises_the_duplicate_prompt()
    {
        var root = TempFolder();
        try
        {
            var info = UiFakes.Info with { Title = new string('K', 200) };
            // The engine shortens long titles before " [id]"; the old 140-character formula cut the id off instead.
            var name = DownloadService.OutputBaseName(info);
            Assert.EndsWith(" [" + info.Id + "]", name);
            Assert.NotEqual(MediaValidation.FriendlyFileName((info.Title + " [" + info.Id + "]").Replace('%', '_'), 140), name);
            var existing = Path.Combine(root, name + ".mp4"); await File.WriteAllTextAsync(existing, "kept");
            var fake = new UiFakes(); fake.Downloads.Info = info;
            using var model = new MainViewModel(fake.Services) { Url = info.Url.AbsoluteUri, DownloadDirectory = root };
            await model.QueueDownloadAsync(false);
            Assert.Equal(existing, Assert.Single(model.DuplicateChoices).Existing.FilePath); Assert.Empty(fake.Downloads.Requests);
            Assert.Equal("kept", await File.ReadAllTextAsync(existing));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task An_existing_output_named_by_the_filename_template_is_found_too()
    {
        var root = TempFolder();
        try
        {
            const string template = "%(uploader)s - %(title)s";
            var existing = Path.Combine(root, DownloadService.OutputBaseName(UiFakes.Info, template) + ".mkv"); await File.WriteAllTextAsync(existing, "kept");
            var fake = new UiFakes();
            using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri, DownloadDirectory = root, NamingTemplate = template };
            await model.QueueDownloadAsync(false);
            Assert.Equal(existing, Assert.Single(model.DuplicateChoices).Existing.FilePath); Assert.Empty(fake.Downloads.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(DownloadMode.Audio, AudioFormat.Mp3, MediaContainer.Auto, VideoCodec.Original, ".webm", false)]
    [InlineData(DownloadMode.Audio, AudioFormat.Mp3, MediaContainer.Auto, VideoCodec.Original, ".mp3", true)]
    [InlineData(DownloadMode.Audio, AudioFormat.Original, MediaContainer.Auto, VideoCodec.Original, ".mp4", false)]
    [InlineData(DownloadMode.VideoAudio, AudioFormat.Original, MediaContainer.Auto, VideoCodec.Original, ".mp3", false)]
    [InlineData(DownloadMode.VideoAudio, AudioFormat.Original, MediaContainer.Mkv, VideoCodec.Original, ".mp4", false)]
    [InlineData(DownloadMode.VideoAudio, AudioFormat.Original, MediaContainer.Auto, VideoCodec.H264, ".webm", false)]
    [InlineData(DownloadMode.VideoAudio, AudioFormat.Original, MediaContainer.Auto, VideoCodec.H264, ".mp4", true)]
    // An archive goes into a new folder, so a plain saved file never stops it (seen natively:
    // Archive was blocked by the saved WebM video). An existing archive does ask (PolishTests).
    [InlineData(DownloadMode.Archive, AudioFormat.Original, MediaContainer.Auto, VideoCodec.Original, ".mp3", false)]
    [InlineData(DownloadMode.Archive, AudioFormat.Original, MediaContainer.Auto, VideoCodec.Original, ".webm", false)]
    public async Task Only_a_file_this_download_could_write_raises_the_duplicate_prompt(DownloadMode mode, AudioFormat audio, MediaContainer container, VideoCodec codec, string savedExtension, bool prompts)
    {
        // An MP3 request is never stopped by the saved WebM video of the same title.
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, DownloadService.OutputBaseName(UiFakes.Info) + savedExtension); await File.WriteAllTextAsync(saved, "kept");
            var fake = new UiFakes();
            using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri, DownloadDirectory = root, DownloadMode = mode, AudioFormat = audio, Container = container, VideoCodec = codec };
            await model.QueueDownloadAsync(false);
            if (prompts) { Assert.Equal(saved, Assert.Single(model.DuplicateChoices).Existing.FilePath); Assert.Empty(fake.Downloads.Requests); }
            else { Assert.Empty(model.DuplicateChoices); await WaitUntil(() => fake.Downloads.Requests.Count == 1); }
            Assert.Equal("kept", await File.ReadAllTextAsync(saved));
        }
        finally { Directory.Delete(root, true); }
    }

    // ----- #5/#62 playlist entries -----

    [AvaloniaFact]
    public async Task Items_of_a_multi_video_post_are_queued_by_position()
    {
        var fake = new UiFakes(); fake.Downloads.Info = PostInfo;
        using var model = new MainViewModel(fake.Services) { Url = Post };
        await model.AnalyseAsync();
        Assert.All(model.Playlist, item => Assert.False(item.IsList));
        model.Playlist[0].Selected = true; model.Playlist[1].Selected = true;
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 2);
        Assert.All(fake.Downloads.Requests, request => Assert.Equal(Post, request.Url.AbsoluteUri));
        Assert.Equal(["1", "2"], fake.Downloads.Requests.Select(request => request.PlaylistItems).Order());
        Assert.StartsWith("Added to your queue", model.Status);
    }

    [AvaloniaFact]
    public async Task A_list_entry_is_never_queued_and_opens_as_its_own_playlist()
    {
        var fake = new UiFakes(); fake.Downloads.Info = ChannelInfo;
        using var model = new MainViewModel(fake.Services) { Url = "https://www.youtube.com/@kea" };
        await model.AnalyseAsync();
        Assert.Contains("1 of them is a playlist", model.Status);
        var tab = model.Playlist[0]; var video = model.Playlist[1];
        Assert.True(tab.IsList); Assert.True(tab.CanOpenAsPlaylist); Assert.True(tab.OpenAsPlaylistCommand.CanExecute(null));
        Assert.False(video.IsList); Assert.False(video.OpenAsPlaylistCommand.CanExecute(null));
        Assert.Same(tab.OpenAsPlaylistCommand, tab.OpenAsPlaylistCommand);

        // The list and the video: the video is queued, the list is skipped with a note that names
        // controls the page already has.
        tab.Selected = true; video.Selected = true;
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", fake.Downloads.Requests.Single().Url.AbsoluteUri);
        Assert.Null(fake.Downloads.Requests.Single().PlaylistItems);
        Assert.Contains("1 selected entry is a playlist itself and was not queued. Choose Open as playlist next to it to see its videos.", model.Status);

        // Open as playlist analyses the tab's own address; nothing more is downloaded.
        var analyses = fake.Downloads.Analyses;
        await tab.OpenAsPlaylistCommand.ExecuteAsync(null);
        Assert.Equal("https://www.youtube.com/@kea/videos", model.Url); Assert.Equal(analyses + 1, fake.Downloads.Analyses); Assert.True(model.HasMedia);
        Assert.Single(fake.Downloads.Requests);
    }

    [AvaloniaFact]
    public async Task Download_with_only_a_list_entry_chosen_opens_that_list_instead_of_queueing_it()
    {
        // This path needs no extra button: the row's checkbox and Download already exist.
        var fake = new UiFakes(); fake.Downloads.Info = ChannelInfo;
        using var model = new MainViewModel(fake.Services) { Url = "https://www.youtube.com/@kea" };
        await model.AnalyseAsync();
        Assert.Contains("1 of them is a playlist: choose Open as playlist next to it to see its videos.", model.Status);
        model.SelectedTab = 1; model.Playlist[0].Selected = true;
        var analyses = fake.Downloads.Analyses;
        await model.QueueDownloadAsync(false);
        Assert.Empty(fake.Downloads.Requests); Assert.Empty(model.Queue); Assert.Equal(analyses + 1, fake.Downloads.Analyses);
        Assert.Equal("https://www.youtube.com/@kea/videos", model.Url); Assert.True(model.HasMedia); Assert.Equal(0, model.SelectedTab);
        Assert.StartsWith("That entry is a playlist itself, so it was opened instead of queued. Playlist found:", model.Status);
        Assert.All(model.Playlist, item => Assert.False(item.Selected));
    }

    [AvaloniaFact]
    public async Task Two_list_entries_or_one_without_a_link_are_explained_not_queued()
    {
        var fake = new UiFakes();
        fake.Downloads.Info = ChannelInfo with
        {
            Entries = [new("tab", "Kea - Videos", new("https://www.youtube.com/@kea/videos"), null), new("shorts", "Kea - Shorts", new("https://www.youtube.com/@kea/shorts"), null), new("lost", "Kea - Lost", null, null)],
            MetadataJson = """{"_type":"playlist","entries":[{"_type":"url","ie_key":"YoutubeTab","id":"tab","url":"https://www.youtube.com/@kea/videos"},{"_type":"url","ie_key":"YoutubeTab","id":"shorts","url":"https://www.youtube.com/@kea/shorts"},{"_type":"playlist","id":"lost","entries":[]}]}""",
        };
        using var model = new MainViewModel(fake.Services) { Url = "https://www.youtube.com/@kea" };
        await model.AnalyseAsync();
        Assert.All(model.Playlist, item => Assert.True(item.IsList));
        Assert.Contains("Each entry is a playlist itself: choose Open as playlist next to one to see its videos.", model.Status);
        var analyses = fake.Downloads.Analyses;
        // Two lists: neither is opened, since only one can be shown at a time.
        model.Playlist[0].Selected = true; model.Playlist[1].Selected = true;
        await model.QueueDownloadAsync(false);
        Assert.Equal("Nothing was queued. 2 selected entries are playlists themselves and were not queued. Choose Open as playlist next to one to see its videos.", model.Status);
        Assert.Equal(analyses, fake.Downloads.Analyses); Assert.Equal("https://www.youtube.com/@kea", model.Url);
        // A list without a link cannot be opened: it is counted with the entries that have no link.
        model.Playlist[0].Selected = false; model.Playlist[1].Selected = false; model.Playlist[2].Selected = true;
        Assert.False(model.Playlist[2].CanOpenAsPlaylist);
        await model.QueueDownloadAsync(false);
        Assert.Equal("Nothing was queued. 1 entry has no downloadable link and was skipped.", model.Status);
        Assert.Equal(analyses, fake.Downloads.Analyses); Assert.Empty(fake.Downloads.Requests);
    }

    [Fact]
    public void List_entries_are_found_in_one_read_and_agree_with_the_engine_rule()
    {
        // Objects only, in order (analysis skips anything else): a channel tab, a video, a nested playlist, a SoundCloud set.
        var json = """{"entries":[{"_type":"url","ie_key":"YoutubeTab","url":"https://www.youtube.com/@kea/videos","title":"Videos"},null,{"_type":"url","ie_key":"Youtube","url":"https://www.youtube.com/watch?v=abcdefghijk","title":"Video"},{"_type":"playlist","title":"Nested","entries":[]},"text",{"_type":"url_transparent","ie_key":"SoundcloudPlaylist","url":"https://soundcloud.com/kea/sets/a","title":"Set"}]}""";
        var info = UiFakes.Info with { Entries = [new("1", "Videos", null, null), new("2", "Video", null, null), new("3", "Nested", null, null), new("4", "Set", null, null)], MetadataJson = json };
        var lists = MainViewModel.PlaylistListEntries(info);
        Assert.Equal([true, false, true, true], lists);
        Assert.Equal(Enumerable.Range(0, 4).Select(index => DownloadService.PlaylistEntryIsList(info, index)), lists);
        Assert.Equal([false, false], MainViewModel.PlaylistListEntries(info with { Entries = info.Entries.Take(2).ToArray(), MetadataJson = "not json" }));
    }

    // ----- #6/#63 duplicate keys include the entry position -----

    [AvaloniaFact]
    public async Task Item_two_of_a_post_is_not_a_duplicate_of_item_one()
    {
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, "first.mp4"); await File.WriteAllTextAsync(saved, "item one");
            var fake = new UiFakes(); fake.Downloads.Info = PostInfo;
            fake.Store.History.Add(new("item-1", "First clip", Post, "Twitter", DateTimeOffset.UtcNow, saved, "VideoAudio", "Best available", 8, "Finished", "1"));
            using var model = new MainViewModel(fake.Services) { Url = Post }; await model.InitializeAsync();
            await model.AnalyseAsync();
            model.Playlist[0].Selected = true; model.Playlist[1].Selected = true;
            await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
            Assert.Equal("2", fake.Downloads.Requests.Single().PlaylistItems);
            var pending = Assert.Single(model.DuplicateChoices); Assert.Equal("1", pending.Options.PlaylistItems); Assert.Equal(saved, pending.Existing.FilePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task A_running_item_of_a_post_does_not_block_its_other_items()
    {
        var fake = new UiFakes(); fake.Downloads.Info = PostInfo; fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = Post };
        await model.AnalyseAsync(); model.Playlist[0].Selected = true;
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        model.Playlist[0].Selected = false; model.Playlist[2].Selected = true;
        await model.QueueDownloadAsync(false);
        Assert.Equal(2, model.Queue.Count); Assert.DoesNotContain("already in your queue", model.Status);
        model.Playlist[2].Selected = true; await model.QueueDownloadAsync(false);
        Assert.Equal(2, model.Queue.Count); Assert.Contains("already in your queue", model.Status);
    }

    [AvaloniaFact]
    public async Task Queueing_a_link_that_is_still_downloading_never_claims_it_was_added()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" };
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        Assert.StartsWith("Added to your queue", model.Status);
        await model.QueueDownloadAsync(false);
        Assert.Single(model.Queue);
        Assert.Equal("Nothing new was queued. 1 item is already in your queue.", model.Status);
    }

    [AvaloniaFact]
    public async Task Download_again_from_the_library_reselects_the_saved_item_of_a_post()
    {
        var fake = new UiFakes(); fake.Downloads.Info = PostInfo;
        using var model = new MainViewModel(fake.Services);
        await model.RepeatDownloadAsync(new("item-2", "Second clip", Post, "Twitter", DateTimeOffset.UtcNow, "missing.mp4", "VideoAudio", "Best available", 8, "Finished", "2"));
        Assert.Equal([false, true, false], model.Playlist.Select(item => item.Selected));
        Assert.Contains("saved item selected", model.Status); Assert.Empty(fake.Downloads.Requests);
    }

    [AvaloniaFact]
    public async Task Download_again_while_another_link_is_checked_analyses_the_saved_link()
    {
        var fake = new UiFakes(); var gate = new TaskCompletionSource(); fake.Downloads.AnalysisGate = gate;
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/slow", SelectedTab = 2 };
        var slow = model.AnalyseAsync();
        try
        {
            // The fake has taken the slow check (IsAnalysing turns true before the work reaches it).
            await WaitUntil(() => fake.Downloads.Analyses == 1); Assert.True(model.IsAnalysing);
            // The slow check does not answer by itself; only cancellation ends it.
            fake.Downloads.AnalysisGate = null;
            await model.RepeatDownloadAsync(new("saved", "Saved video", "https://example.com/saved", "Example", DateTimeOffset.UtcNow, "missing.mp4", "VideoAudio", "Best available", 8, "Finished")).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("https://example.com/saved", model.Url); Assert.Equal(0, model.SelectedTab);
            Assert.True(model.HasMedia); Assert.Equal(2, fake.Downloads.Analyses);
            Assert.Equal("Formats checked. Review your choices, then download.", model.Status);
        }
        finally { gate.TrySetResult(); await slow.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    // ----- #7/#64 playlist extent -----

    [AvaloniaFact]
    public async Task A_longer_playlist_says_how_much_of_it_is_shown()
    {
        var fake = new UiFakes();
        fake.Downloads.Info = UiFakes.Info with
        {
            Entries = [new("1", "One", new("https://example.com/1"), null), new("2", "Two", new("https://example.com/2"), null), new("3", "Three", new("https://example.com/3"), null)],
            MetadataJson = """{"playlist_count":1200,"entries":[{"id":"1"},{"id":"2"},{"id":"3"}]}""",
        };
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/list" };
        await model.AnalyseAsync();
        var total = 1200.ToString("N0", CultureInfo.CurrentCulture);
        Assert.StartsWith($"Playlist found: showing the first 3 of {total} entries.", model.Status);
        Assert.StartsWith($"First 3 of {total} entries shown · 0 selected.", model.PlaylistSummary);
        Assert.Equal($"Playlist · 3 of {total} entries", model.PlaylistCountText);
    }

    [AvaloniaFact]
    public async Task A_playlist_cut_at_the_listing_limit_says_it_is_longer()
    {
        var fake = new UiFakes();
        var listed = Enumerable.Range(1, 500).Select(i => new PlaylistEntry(i.ToString(CultureInfo.InvariantCulture), "Entry " + i, new("https://example.com/" + i), null)).ToArray();
        fake.Downloads.Info = UiFakes.Info with { Entries = listed, MetadataJson = "{\"entries\":[" + string.Join(',', Enumerable.Range(1, 501).Select(i => "{\"id\":\"" + i + "\"}")) + "]}" };
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/list" };
        await model.AnalyseAsync();
        Assert.StartsWith("Playlist found: showing the first 500 entries (the playlist is longer).", model.Status);
        Assert.StartsWith("First 500 entries shown (the playlist is longer) · 0 selected.", model.PlaylistSummary);
        // A complete listing reads as before.
        fake.Downloads.Info = fake.Downloads.Info with { Entries = listed[..2], MetadataJson = null }; model.Url = "https://example.com/short";
        await model.AnalyseAsync();
        Assert.StartsWith("Playlist found: 2 entries.", model.Status); Assert.StartsWith("2 available entries · 0 selected.", model.PlaylistSummary);
    }

    // ----- #37 qualities filtered at the source -----

    [AvaloniaFact]
    public async Task A_height_cap_this_media_cannot_offer_falls_back_to_best_available()
    {
        var fake = new UiFakes();
        fake.Downloads.Info = UiFakes.Info with { Formats = [new("sb", "mhtml", 160, 90, null, "none", "none", null, null, null), new("img", "jpg", 320, 180, null, "none", "none", null, null, null), new("22", "mp4", 1280, 720, 30, "avc1", "mp4a", null, 128, null)] };
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video", Quality = "1080p maximum" };
        // The list is built once from real video heights: storyboard heights and a cap from the
        // previous link never appear, not even briefly while the picker is open.
        var offered = new List<string>(); model.Qualities.CollectionChanged += (_, e) => offered.AddRange(e.NewItems?.Cast<string>() ?? []);
        await model.AnalyseAsync();
        Assert.Equal(["Best available", "Smallest available", "720p maximum"], model.Qualities);
        Assert.Equal(["Best available", "Smallest available", "720p maximum"], offered);
        Assert.Equal("Best available", model.Quality);
    }

    [AvaloniaFact]
    public async Task A_chosen_height_cap_carries_over_to_a_playlist_that_lists_no_heights()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" };
        await model.AnalyseAsync(); model.Quality = "1080p maximum";
        // A playlist's entries are analysed only when they download, so it reports no heights
        // that could rule the cap out; the cap must keep applying to every entry.
        fake.Downloads.Info = UiFakes.Info with { Formats = [], Entries = [new("1", "One", new("https://example.com/1"), null), new("2", "Two", new("https://example.com/2"), null)] };
        model.Url = "https://example.com/list"; await model.AnalyseAsync();
        Assert.True(model.IsPlaylist);
        Assert.Equal(["Best available", "Smallest available", "1080p maximum"], model.Qualities);
        Assert.Equal("1080p maximum", model.Quality);
        model.Playlist[1].Selected = true; await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        Assert.Equal(1080, fake.Downloads.Requests.Single().MaxHeight);
    }

    // ----- #43 WebM cannot hold H.264/HEVC -----

    [AvaloniaTheory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.Hevc)]
    public async Task WebM_with_H264_or_HEVC_is_never_queued(VideoCodec codec)
    {
        var fake = new UiFakes(); fake.Platform.Clipboard = "https://example.com/one\nhttps://example.com/two";
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video", Container = MediaContainer.WebM, VideoCodec = codec };
        await model.QueueDownloadAsync(false);
        Assert.Equal("WebM can't hold H.264 or HEVC video. Choose MKV, MP4 or Automatic.", model.Status);
        Assert.Equal(0, fake.Downloads.Analyses); Assert.Empty(fake.Downloads.Requests); Assert.Empty(model.Queue);
        await model.PasteAsync(); model.SelectBatchCommand.Execute(null);
        await model.QueueBatchAsync();
        Assert.Equal("WebM can't hold H.264 or HEVC video. Choose MKV, MP4 or Automatic.", model.Status); Assert.Empty(model.Queue); Assert.Equal(2, model.BatchLinks.Count);
        // Archive mode keeps the source streams, so the picker's WebM choice does not apply there.
        model.DownloadMode = DownloadMode.Archive; await model.QueueBatchAsync(); await WaitUntil(() => fake.Downloads.Requests.Count == 2);
    }

    // ----- #35 first-run status -----

    [AvaloniaFact]
    public async Task First_run_status_points_at_prepare_components()
    {
        var fake = new UiFakes(); fake.Components.Ready = false;
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.Equal("One more step: KeaClip needs its helper tools. Press Download the tools.", model.Status);
    }

    // ----- #19/#20 library rows -----

    [AvaloniaFact]
    public async Task Clearing_a_search_brings_back_the_same_row_instances()
    {
        var fake = new UiFakes();
        fake.Store.History.Add(new("a", "Kea at Arthur's Pass", null, "Local", DateTimeOffset.UtcNow, "a.mp4", "VideoAudio", "Best", 1, "Finished"));
        fake.Store.History.Add(new("b", "Tui song", null, "Local", DateTimeOffset.UtcNow.AddMinutes(-1), "b.mp3", "Audio", "Best", 1, "Finished"));
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var kea = model.History[0]; var tui = model.History[1];
        model.HistorySearch = "tui"; await WaitUntil(() => model.History.Count == 1);
        model.HistorySearch = ""; await WaitUntil(() => model.History.Count == 2);
        Assert.Same(kea, model.History[0]); Assert.Same(tui, model.History[1]);
    }

    // Regression guard for #19: the old FilterHistory also inserted one row here. Clearing a
    // search (above) and searching by file name (below) are the tests that pin #19 itself.
    [AvaloniaFact]
    public async Task A_finished_download_adds_one_row_and_keeps_the_others()
    {
        var fake = new UiFakes();
        fake.Store.History.Add(new("old", "Saved earlier", null, "Local", DateTimeOffset.UtcNow.AddDays(-1), "old.mp4", "VideoAudio", "Best", 1, "Finished"));
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" }; await model.InitializeAsync();
        var earlier = Assert.Single(model.History);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>(); model.History.CollectionChanged += (_, e) => changes.Add(e.Action);
        await model.QueueDownloadAsync(false); await WaitUntil(() => model.History.Count == 2);
        Assert.Same(earlier, model.History[1]); Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add], changes);
    }

    [AvaloniaFact]
    public async Task A_record_without_a_title_is_found_by_its_file_name()
    {
        var fake = new UiFakes();
        fake.Store.History.Add(new("blank", "", null, "Local export", DateTimeOffset.UtcNow, Path.Combine(Path.GetTempPath(), "kea-dawn-chorus.mp3"), "Audio Clip", "Verified export", 1, "Finished"));
        fake.Store.History.Add(new("other", "Tui song", null, "Local", DateTimeOffset.UtcNow.AddMinutes(-1), "b.mp3", "Audio", "Best", 1, "Finished"));
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.HistorySearch = "dawn-chorus"; await WaitUntil(() => model.History.Count == 1);
        Assert.Equal("blank", model.History.Single().Entry.Id);
    }

    [AvaloniaFact]
    public void History_row_commands_are_created_on_first_use_and_then_kept()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var row = new HistoryRow(new("id", "Title", "https://example.com/v", "Example", DateTimeOffset.UtcNow, "file.mp4", "VideoAudio", "Best available", 1, "Finished"), model);
        var commandFields = typeof(HistoryRow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(field => typeof(ICommand).IsAssignableFrom(field.FieldType)).ToArray();
        Assert.True(commandFields.Length >= 8);
        Assert.All(commandFields, field => Assert.Null(field.GetValue(row)));
        ICommand[] first = [row.OpenCommand, row.RevealCommand, row.EditCommand, row.ShareCommand, row.RemoveCommand, row.CopyPathCommand, row.CopySourceCommand, row.RepeatCommand];
        ICommand[] again = [row.OpenCommand, row.RevealCommand, row.EditCommand, row.ShareCommand, row.RemoveCommand, row.CopyPathCommand, row.CopySourceCommand, row.RepeatCommand];
        Assert.Equal(8, first.Distinct().Count());
        for (var i = 0; i < first.Length; i++) Assert.Same(first[i], again[i]);
    }

    // ----- #21 missing files -----

    [AvaloniaFact]
    public void Desktop_platform_reports_a_missing_file_instead_of_ignoring_it()
    {
        var missing = Path.Combine(Path.GetTempPath(), "keaclip-missing-" + Guid.NewGuid().ToString("N") + ".mp4");
        var platform = new DesktopPlatform();
        Assert.Throws<FileNotFoundException>(() => platform.OpenFile(missing));
        Assert.Throws<FileNotFoundException>(() => platform.RevealFile(missing));
        Assert.Throws<FileNotFoundException>(() => platform.OpenFile(""));
    }

    [AvaloniaFact]
    public async Task Open_and_show_file_say_when_the_file_has_gone()
    {
        var fake = new UiFakes(); fake.Platform.FilesMissing = true;
        fake.Store.History.Add(new("gone", "Moved away", null, "Local", DateTimeOffset.UtcNow, "gone.mp4", "VideoAudio", "Best", 1, "Finished"));
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.History[0].OpenCommand.Execute(null);
        Assert.Equal("This file is no longer available. It may have been moved, renamed or deleted.", model.Status);
        model.Status = ""; model.History[0].RevealCommand.Execute(null);
        Assert.StartsWith("This file is no longer available", model.Status);
        // A queue row's Open goes through the same path.
        fake.Platform.FilesMissing = false; model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false); await WaitUntil(() => model.Queue.Count == 1 && model.Queue[0].HasResult);
        fake.Platform.FilesMissing = true; model.Status = ""; model.Queue[0].OpenCommand.Execute(null);
        Assert.StartsWith("This file is no longer available", model.Status);
    }

    [AvaloniaFact]
    public async Task Opening_a_missing_duplicate_keeps_the_choice_open()
    {
        var root = TempFolder();
        try
        {
            var existing = Path.Combine(root, "saved.mp4"); await File.WriteAllTextAsync(existing, "saved");
            var fake = new UiFakes(); fake.Store.History.Add(new("saved", "Saved media", UiFakes.Info.Url.AbsoluteUri, "Example", DateTimeOffset.UtcNow, existing, "VideoAudio", "Best available", 5, "Finished"));
            using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri }; await model.InitializeAsync(); await model.QueueDownloadAsync(false);
            var pending = Assert.Single(model.DuplicateChoices);
            fake.Platform.FilesMissing = true; pending.OpenCommand.Execute(null);
            // The duplicate card names the choices still open, not "choose another local file".
            Assert.Equal(MainViewModel.MissingFileStatus + " Choose Download again or Skip.", model.Status); Assert.Same(pending, Assert.Single(model.DuplicateChoices));
        }
        finally { Directory.Delete(root, true); }
    }

    // ----- #50 editor opening hook -----

    [AvaloniaFact]
    public async Task The_editor_stays_loading_while_other_work_writes_the_status()
    {
        var fake = new UiFakes(); fake.Media.ProbeGate = new TaskCompletionSource();
        using var model = new MainViewModel(fake.Services);
        var open = model.OpenEditorAsync("fixture.mp4");
        // Another lane's progress replaces "Reading local media…" before any view looked.
        model.Status = "Uploading · 37%";
        Assert.True(model.IsEditorLoading); Assert.True(model.IsOpeningEditor);
        fake.Media.ProbeGate.SetResult(); await open;
        Assert.False(model.IsEditorLoading); Assert.True(model.HasEditor);
    }

    // ----- #49 late progress, #2 share progress -----

    [AvaloniaFact]
    public async Task A_late_export_report_never_replaces_the_final_status()
    {
        var fake = new UiFakes(); fake.Media.LateProgressDelay = TimeSpan.FromMilliseconds(40); fake.Platform.SavePath = "export.mp4";
        using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        await model.ExportAsync(); Assert.StartsWith("Export verified", model.Status);
        await Task.Delay(250);
        Assert.StartsWith("Export verified", model.Status); Assert.False(model.HasExportPercent); Assert.False(model.HasOperationPercent);
    }

    [AvaloniaFact]
    public async Task Share_progress_shows_the_amount_sent_and_keeps_it_through_a_retry()
    {
        var file = Path.Combine(Path.GetTempPath(), "keaclip-share-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllBytesAsync(file, new byte[1_000_000]);
        try
        {
            var fake = new UiFakes(); fake.Sharing.Gate = new TaskCompletionSource();
            fake.Sharing.Reports.Add(new(JobStage.Uploading, 37, "Uploading encrypted data…"));
            fake.Sharing.Reports.Add(new(JobStage.Uploading, null, "Connection lost. Retrying the encrypted upload in 5 s…"));
            using var model = new MainViewModel(fake.Services);
            var share = model.ShareFileAsync(file);
            await WaitUntil(() => model.Status.Contains("Retrying", StringComparison.Ordinal));
            Assert.Equal("Uploading · 37% · 370 KB of 1 MB · Connection lost. Retrying the encrypted upload in 5 s…", model.Status);
            Assert.True(model.HasSharePercent); Assert.Equal(37, model.SharePercent);
            Assert.Equal("370 KB of 1 MB · Connection lost. Retrying the encrypted upload in 5 s…", model.ShareProgressText);
            fake.Sharing.Gate.SetResult(); await share;
            Assert.StartsWith("Upload finished", model.Status); Assert.False(model.HasSharePercent); Assert.Equal("", model.ShareProgressText);
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task Share_progress_reads_size_sent_while_uploading()
    {
        var file = Path.Combine(Path.GetTempPath(), "keaclip-share-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllBytesAsync(file, new byte[35_000_000]);
        try
        {
            var fake = new UiFakes(); fake.Sharing.Gate = new TaskCompletionSource();
            fake.Sharing.Reports.Add(new(JobStage.Uploading, 100d * 12_400_000 / 35_000_000, "Uploading encrypted data…"));
            using var model = new MainViewModel(fake.Services);
            var share = model.ShareFileAsync(file);
            await WaitUntil(() => model.Status.StartsWith("Uploading", StringComparison.Ordinal));
            var culture = CultureInfo.CurrentCulture;
            Assert.Equal($"Uploading · {35.4.ToString("0.#", culture)}% · {12.4.ToString("0.##", culture)} MB of 35 MB", model.Status);
            fake.Sharing.Gate.SetResult(); await share;
        }
        finally { File.Delete(file); }
    }

    // ----- #3 very large shares -----

    [AvaloniaFact]
    public async Task A_very_large_share_waits_for_confirmation()
    {
        var file = Path.Combine(Path.GetTempPath(), "keaclip-large-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllBytesAsync(file, new byte[4096]);
        try
        {
            var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { LargeShareThresholdBytes = 4000, SelectedTab = 2 };
            await model.ShareFileAsync(file);
            // The prompt stays on the page where Share was chosen, so the same button can confirm it.
            Assert.Equal(0, fake.Sharing.Calls); Assert.True(model.HasPendingLargeShare); Assert.True(model.ShowShareCard); Assert.Equal(2, model.SelectedTab);
            Assert.StartsWith("Large upload (", model.Status); Assert.Contains(" KB): choose Share again to upload it anyway. file.kiwi gives an upload 36 hours", model.Status);
            Assert.Contains("36 hours", model.LargeShareWarning); Assert.Contains("can't be resumed", model.LargeShareWarning); Assert.Contains(Path.GetFileName(file), model.LargeShareWarning);
            model.CancelLargeShareCommand.Execute(null);
            Assert.False(model.HasPendingLargeShare); Assert.Equal(0, fake.Sharing.Calls); Assert.Equal("Large upload cancelled. Nothing was uploaded.", model.Status);
            await model.ShareFileAsync(file); Assert.True(model.HasPendingLargeShare);
            await model.ConfirmLargeShareCommand.ExecuteAsync(null);
            Assert.Equal(1, fake.Sharing.Calls); Assert.False(model.HasPendingLargeShare); Assert.False(string.IsNullOrEmpty(model.ShareUrl));
            // Below the threshold a share starts at once.
            model.LargeShareThresholdBytes = 1_000_000; await model.ShareFileAsync(file); Assert.Equal(2, fake.Sharing.Calls);
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task Share_again_confirms_a_large_upload_but_a_double_click_does_not()
    {
        // The upload must be possible from the pages that exist today: Library, Queue and the
        // editor all share through ShareFileAsync, so choosing Share again is the confirmation.
        var file = Path.Combine(Path.GetTempPath(), "keaclip-large-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllBytesAsync(file, new byte[4096]);
        try
        {
            var fake = new UiFakes();
            fake.Store.History.Add(new("big", "Big file", null, "Local", DateTimeOffset.UtcNow, file, "VideoAudio", "Best", 4096, "Finished"));
            using var model = new MainViewModel(fake.Services) { LargeShareThresholdBytes = 4000 }; await model.InitializeAsync();
            var row = model.History.Single();
            // A double-click is two Shares within LargeShareRepeatDelay (1 s by default); a long delay
            // keeps this check independent of machine load.
            Assert.Equal(TimeSpan.FromSeconds(1), model.LargeShareRepeatDelay); model.LargeShareRepeatDelay = TimeSpan.FromHours(1);
            await row.ShareCommand.ExecuteAsync(null);
            var prompt = model.Status;
            // The second click of a double-click arrives at once: it changes nothing.
            await row.ShareCommand.ExecuteAsync(null);
            Assert.Equal(0, fake.Sharing.Calls); Assert.True(model.HasPendingLargeShare); Assert.Equal(prompt, model.Status);
            // A deliberate second choice confirms.
            model.LargeShareRepeatDelay = TimeSpan.Zero;
            await row.ShareCommand.ExecuteAsync(null);
            Assert.Equal(1, fake.Sharing.Calls); Assert.False(model.HasPendingLargeShare); Assert.StartsWith("Upload finished", model.Status);
            // A prompt left too long is shown again instead of being taken as agreed.
            model.LargeShareConfirmWindow = TimeSpan.Zero;
            await row.ShareCommand.ExecuteAsync(null); await Task.Delay(5); await row.ShareCommand.ExecuteAsync(null);
            Assert.Equal(1, fake.Sharing.Calls); Assert.True(model.HasPendingLargeShare); Assert.StartsWith("Large upload", model.Status);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Confirmation_applies_from_about_100_GB_or_over_30_hours_at_1_MB_per_second()
    {
        Assert.False(MainViewModel.NeedsLargeShareConfirmation(99_999_999_999, MainViewModel.LargeShareBytes100Gb));
        Assert.True(MainViewModel.NeedsLargeShareConfirmation(100_000_000_000, MainViewModel.LargeShareBytes100Gb));
        // The time rule on its own: 108 GB takes 30 hours at 1 MB/s.
        Assert.False(MainViewModel.NeedsLargeShareConfirmation(108_000_000_000, long.MaxValue));
        Assert.True(MainViewModel.NeedsLargeShareConfirmation(108_000_000_001, long.MaxValue));
    }

    private static string TempFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeaClip-Functionality-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
