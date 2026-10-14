using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Coordinator checks for wiring between the view-model and the pages after the polish merges.
public sealed class IntegrationTests
{
    private static Button Named(Window window, string automationName)
        => window.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == automationName);

    [AvaloniaFact]
    public async Task Cancelling_an_export_never_stops_a_running_upload()
    {
        var fake = new UiFakes(); fake.Sharing.Gate = new TaskCompletionSource();
        using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        var share = model.ShareFileAsync("fixture.mp4");
        Assert.False(model.CanShare);

        // An upload cannot resume, so the editor's export Cancel must leave it running.
        Named(window, "Cancel export").Command!.Execute(null);
        await Task.Delay(50);
        Assert.False(model.CanShare); Assert.False(share.IsCompleted);

        Named(window, "Cancel upload").Command!.Execute(null);
        await share.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.CanShare);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_large_share_notice_offers_upload_and_cancel_on_the_editor()
    {
        var file = Path.Combine(Path.GetTempPath(), "keaclip-large-ui-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllBytesAsync(file, new byte[4096]);
        try
        {
            var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { LargeShareThresholdBytes = 4000 };
            await model.OpenEditorAsync(file);
            var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
            var notice = window.FindNamed<Border>("LargeShareNotice")!;
            Assert.False(notice.IsVisible);

            await model.ShareFileAsync(file);
            Assert.True(notice.IsVisible); Assert.Equal(0, fake.Sharing.Calls);
            window.FindNamed<Button>("CancelLargeShareButton")!.Command!.Execute(null);
            Assert.False(notice.IsVisible); Assert.Equal(0, fake.Sharing.Calls);

            await model.ShareFileAsync(file);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)window.FindNamed<Button>("ConfirmLargeShareButton")!.Command!).ExecuteAsync(null);
            Assert.Equal(1, fake.Sharing.Calls); Assert.False(notice.IsVisible);
            window.Close();
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task The_header_cancel_stops_other_work_before_an_upload()
    {
        var fake = new UiFakes(); fake.Sharing.Gate = new TaskCompletionSource(); fake.Downloads.AnalysisGate = new TaskCompletionSource();
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/slow" };
        var share = model.ShareFileAsync("fixture.mp4");
        var analysis = model.AnalyseAsync();
        Assert.False(model.CanShare); Assert.False(model.CanDownload);

        // The visible operation (the link check) stops; the upload, which cannot resume, keeps going.
        model.CancelOperationCommand.Execute(null);
        await analysis.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.CanDownload); Assert.False(model.CanShare); Assert.False(share.IsCompleted);

        // With nothing else running, the upload is the current operation and Cancel stops it.
        model.CancelOperationCommand.Execute(null);
        await share.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.CanShare); Assert.False(model.IsBusy);
    }

    [AvaloniaFact]
    public async Task A_list_entry_shows_Open_as_playlist_and_opens_that_list()
    {
        var fake = new UiFakes();
        fake.Downloads.Info = UiFakes.Info with
        {
            Url = new("https://www.youtube.com/@kea"), Title = "Kea channel",
            Entries = [new("tab", "Kea - Videos", new("https://www.youtube.com/@kea/videos"), null), new("abcdefghijk", "One video", new("https://www.youtube.com/watch?v=abcdefghijk"), null)],
            MetadataJson = """{"id":"kea","title":"Kea channel","_type":"playlist","entries":[{"_type":"url","ie_key":"YoutubeTab","id":"tab","title":"Kea - Videos","url":"https://www.youtube.com/@kea/videos"},{"_type":"url","ie_key":"Youtube","id":"abcdefghijk","title":"One video","url":"https://www.youtube.com/watch?v=abcdefghijk"}]}""",
        };
        using var model = new MainViewModel(fake.Services) { Url = "https://www.youtube.com/@kea" };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        await model.AnalyseAsync(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // Only the channel tab is a list, so it alone offers the button.
        var open = Assert.Single(window.GetLogicalDescendants().OfType<Button>(), button => Equals(button.Content, "Open as playlist") && button.IsVisible);
        Assert.Equal("Open Kea - Videos as a playlist", AutomationProperties.GetName(open));
        Assert.True(model.Playlist[0].CanOpenAsPlaylist); Assert.False(model.Playlist[1].CanOpenAsPlaylist);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)open.Command!).ExecuteAsync(null);
        Assert.Equal("https://www.youtube.com/@kea/videos", model.Url); Assert.Empty(fake.Downloads.Requests);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("saved", new string[0], "saved (2)")]
    [InlineData("saved", new[] { "saved (2).mkv" }, "saved (3)")]
    [InlineData("saved (2)", new string[0], "saved (3)")]
    public async Task Download_again_names_the_copy_with_the_first_free_number(string savedStem, string[] alreadyThere, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "KeaClip-Ui-Again-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, savedStem + ".mp4"); await File.WriteAllTextAsync(existing, "original bytes");
            foreach (var name in alreadyThere) await File.WriteAllTextAsync(Path.Combine(root, name), "other copy");
            var fake = new UiFakes(); fake.Store.History.Add(new("existing", "Saved media", "https://example.com/video", "Example", DateTimeOffset.Now, existing, "VideoAudio", "Best available", 14, "Finished"));
            using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video", DownloadDirectory = root }; await model.InitializeAsync(); model.DownloadDirectory = root;
            await model.QueueDownloadAsync(false);
            model.ResolveDuplicate(Assert.Single(model.DuplicateChoices), "again");
            for (var wait = 0; fake.Downloads.Requests.Count == 0 && wait < 500; wait++) await Task.Delay(10);
            Assert.Equal(expected, Assert.Single(fake.Downloads.Requests).NamingTemplate);
            Assert.Equal("original bytes", await File.ReadAllTextAsync(existing));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Opus_offers_only_bitrates_the_encoder_can_make()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video", DownloadMode = DownloadMode.Audio, AudioKbps = 320 };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        model.AudioFormat = AudioFormat.Opus; model.RefreshFormatChoice(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // libopus tops out at 256 kbps; a 320 choice would silently become 256.
        Assert.Equal(256, model.Bitrates.Max()); Assert.Equal(256, model.AudioKbps);
        Assert.Equal(256, window.FindNamed<ComboBox>("BitratePicker")!.SelectedItem);
        await model.QueueDownloadAsync(false);
        for (var wait = 0; fake.Downloads.Requests.Count == 0 && wait < 500; wait++) await Task.Delay(10);
        Assert.Equal(256, Assert.Single(fake.Downloads.Requests).AudioKbps);
        model.AudioFormat = AudioFormat.Mp3; model.RefreshFormatChoice();
        Assert.Contains(320, model.Bitrates); Assert.Equal(256, model.AudioKbps);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HDR_to_SDR_is_offered_only_for_an_HDR_file()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        fake.Media.TechnicalJson = """{"streams":[{"codec_type":"video","codec_name":"hevc","color_transfer":"smpte2084","color_primaries":"bt2020"}]}""";
        await model.OpenEditorAsync("hdr.mp4"); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(model.EditorSourceIsHdr); Assert.True(window.FindNamed<Grid>("ExportHdrRow")!.IsEnabled);
        model.ExportHdr = true;

        // An SDR file has nothing to convert: the option is off and unavailable.
        fake.Media.TechnicalJson = """{"streams":[{"codec_type":"video","codec_name":"h264","color_transfer":"bt709"}]}""";
        await model.OpenEditorAsync("sdr.mp4"); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(model.EditorSourceIsHdr); Assert.False(model.ExportHdr); Assert.False(window.FindNamed<Grid>("ExportHdrRow")!.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_export_dialog_suggests_a_clip_name_that_is_still_free()
    {
        var folder = Path.Combine(Path.GetTempPath(), "KeaClip-Ui-Clips-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal("fixture - clip", MainViewModel.FreeClipName("fixture", "mp4", folder));
            await File.WriteAllTextAsync(Path.Combine(folder, "fixture - clip.mp4"), "first");
            Assert.Equal("fixture - clip (2)", MainViewModel.FreeClipName("fixture", "mp4", folder));
            Assert.Equal("fixture - clip", MainViewModel.FreeClipName("fixture", "mp3", folder)); // Another format is free.
            await File.WriteAllTextAsync(Path.Combine(folder, "fixture - clip (2).mp4"), "second");
            Assert.Equal("fixture - clip (3)", MainViewModel.FreeClipName("fixture", "mp4", folder));

            var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { ClipsDirectory = folder };
            await model.OpenEditorAsync("fixture.mp4"); await model.ExportAsync();
            Assert.Equal("fixture - clip (3).mp4", fake.Platform.SuggestedName);
        }
        finally { Directory.Delete(folder, true); }
    }
}
