using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Klippi.Core;
using Klippi.Desktop;
using Klippi.Desktop.Controls;
using Klippi.Desktop.Converters;
using Xunit;

namespace Klippi.UiTests;

public sealed class DownloadViewTests
{
    private static readonly MediaInfo RichInfo = UiFakes.Info with
    {
        Title = "Kea at Arthur's Pass: a cheeky alpine parrot up close",
        Creator = "Southern Alps Wildlife",
        Source = "Youtube",
        Duration = TimeSpan.FromSeconds(754),
        PublishedAt = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero),
        Description = "A short documentary about kea, the world's only alpine parrot, filmed near Arthur's Pass.",
        Formats =
        [
            new("sb0", "mhtml", 160, 90, 0.5, "none", "none", null, null, null),
            new("137", "mp4", 1920, 1080, 60, "avc1.640028", "none", 412_000_000, null, null),
            new("136", "mp4", 1280, 720, 30, "avc1.4d401f", "none", 188_000_000, null, null),
            new("251", "webm", null, null, null, "none", "opus", 12_400_000, 160, "en"),
        ],
    };

    private static MediaInfo PlaylistInfo(int count) => UiFakes.Info with
    {
        Title = "Dawn chorus field recordings", Duration = null,
        Entries = Enumerable.Range(1, count).Select(i => new PlaylistEntry(i.ToString(CultureInfo.InvariantCulture), $"Recording {i:00}: tūī, korimako and kererū at first light", new Uri("https://example.com/r" + i), TimeSpan.FromSeconds(95 * i))).ToArray(),
    };

    [AvaloniaFact]
    public async Task Mode_tiles_hidden_picker_and_choice_panels_stay_in_sync()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 900, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; Settle(window);
            var picker = window.FindNamed<ComboBox>("DownloadModePicker")!;
            var audio = window.FindNamed<RadioButton>("AudioTile")!;
            Assert.True(window.FindNamed<RadioButton>("VideoAudioTile")!.IsChecked);
            Assert.True(window.FindNamed<Control>("VideoChoices")!.IsEffectivelyVisible);

            Click(window, audio);
            Assert.Equal(DownloadMode.Audio, model.DownloadMode);
            Assert.Equal(DownloadMode.Audio, picker.SelectedItem);
            Assert.False(window.FindNamed<RadioButton>("VideoAudioTile")!.IsChecked);
            Assert.True(window.FindNamed<Control>("AudioChoices")!.IsEffectivelyVisible);
            Assert.False(window.FindNamed<Control>("VideoChoices")!.IsEffectivelyVisible);

            model.DownloadMode = DownloadMode.Archive; window.UpdateLayout();
            Assert.True(window.FindNamed<RadioButton>("ArchiveTile")!.IsChecked); Assert.False(audio.IsChecked);
            Assert.True(window.FindNamed<Control>("ArchiveChoices")!.IsEffectivelyVisible);

            picker.SelectedItem = DownloadMode.VideoOnly; window.UpdateLayout();
            Assert.Equal(DownloadMode.VideoOnly, model.DownloadMode);
            Assert.True(window.FindNamed<RadioButton>("VideoOnlyTile")!.IsChecked);
            Assert.Equal(DownloadMode.VideoOnly, model.BuildDownloadOptions(UiFakes.Info.Url).Mode);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Mode_tiles_are_one_tab_stop_and_arrow_keys_move_the_choice()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri };
        var window = Show(model, 900, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; model.DownloadMode = DownloadMode.Audio; Settle(window);
            var group = window.FindNamed<Control>("ModeTiles")!;
            Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(group));
            Assert.Same(window.FindNamed<RadioButton>("AudioTile"), KeyboardNavigation.GetTabOnceActiveElement((InputElement)group));

            // Tab from Analyse enters the group on the chosen mode, not on the first tile.
            window.FindNamed<Button>("AnalyseButton")!.Focus(NavigationMethod.Tab);
            Press(window, Key.Tab, PhysicalKey.Tab);
            Assert.Same(window.FindNamed<RadioButton>("AudioTile"), window.FocusManager!.GetFocusedElement());

            Press(window, Key.Right, PhysicalKey.ArrowRight);
            Assert.Equal(DownloadMode.VideoOnly, model.DownloadMode);
            Assert.Same(window.FindNamed<RadioButton>("VideoOnlyTile"), window.FocusManager!.GetFocusedElement());
            Press(window, Key.Down, PhysicalKey.ArrowDown); Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.Equal(DownloadMode.VideoAudio, model.DownloadMode); // wraps around
            Press(window, Key.Left, PhysicalKey.ArrowLeft);
            Assert.Equal(DownloadMode.Archive, model.DownloadMode);
            Assert.Same(window.FindNamed<RadioButton>("ArchiveTile"), KeyboardNavigation.GetTabOnceActiveElement((InputElement)group));
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Enter_in_the_link_field_analyses_but_never_downloads()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true;
            model.Url = UiFakes.Info.Url.AbsoluteUri; Settle(window);
            window.FindNamed<TextBox>("UrlInput")!.Focus();
            await PressEnterAndAnalyse(window, model);
            Assert.Equal(1, fake.Downloads.Analyses); Assert.Empty(fake.Downloads.Requests);
            Assert.True(model.HasMedia); Assert.Equal(UiFakes.Info.Url.AbsoluteUri, window.FindNamed<TextBox>("UrlInput")!.Text);
            Settle(window);
            Assert.True(window.FindNamed<Control>("MediaCard")!.IsEffectivelyVisible);
            Assert.False(window.FindNamed<Control>("MediaHint")!.IsEffectivelyVisible);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Links_typed_without_https_are_completed_and_only_real_mistakes_are_explained()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 900, 1200);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true;
            var hint = window.FindNamed<TextBlock>("UrlHintText")!;
            // Typing a link without a scheme is not an error, and Enter completes it.
            model.Url = "youtube.com/watch?v=kea"; Settle(window);
            Assert.False(hint.IsEffectivelyVisible);
            window.FindNamed<TextBox>("UrlInput")!.Focus();
            await PressEnterAndAnalyse(window, model);
            Assert.Equal("https://youtube.com/watch?v=kea", model.Url); Assert.Equal(1, fake.Downloads.Analyses); Assert.True(model.HasMedia);

            // Leaving the field completes it too.
            model.Url = "www.vimeo.com/123456"; window.FindNamed<TextBox>("UrlInput")!.Focus(); window.FindNamed<Button>("PasteButton")!.Focus(); Settle(window);
            Assert.Equal("https://www.vimeo.com/123456", model.Url);

            // Plain words stay quiet while typing, then Analyse explains instead of sending them anywhere.
            model.Url = "kea parrot"; Settle(window);
            Assert.False(hint.IsEffectivelyVisible);
            Click(window, window.FindNamed<Button>("AnalyseButton")!); Settle(window);
            Assert.True(hint.IsEffectivelyVisible); Assert.Equal(1, fake.Downloads.Analyses); Assert.False(model.IsBusy);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(hint));
            // Editing again clears the explanation until the next attempt.
            model.Url = "kea parrot video"; Settle(window); Assert.False(hint.IsEffectivelyVisible);

            // A file path is explained at once, and Download refuses it without queueing.
            model.Url = @"C:\Users\Sam\Videos\kea.mp4"; Settle(window);
            Assert.True(hint.IsEffectivelyVisible); Assert.Contains("Open local media", model.UrlHint);
            Click(window, window.FindNamed<Button>("DownloadButton")!);
            Assert.Empty(fake.Downloads.Requests); Assert.Equal(1, fake.Downloads.Analyses);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Analysis_failures_are_explained_inline_with_a_matching_next_step()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 900, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true;
            fake.Downloads.AnalysisFailure = new MediaOperationException("Sign-in is required. Choose a browser you are signed into, and only save content you are allowed to access.");
            model.Url = "https://example.com/members-only"; Settle(window);
            Click(window, window.FindNamed<Button>("AnalyseButton")!);
            if (model.AnalyseCommand.ExecutionTask is { } analysis) await analysis;
            Settle(window);
            var problem = window.FindNamed<Control>("AnalysisProblem")!;
            Assert.True(problem.IsEffectivelyVisible);
            Assert.Equal("Sign-in required", window.FindNamed<TextBlock>("AnalysisProblemTitleText")!.Text);
            // The body does not repeat the title.
            Assert.StartsWith("Choose a browser you are signed into", window.FindNamed<TextBlock>("AnalysisProblemBodyText")!.Text);
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(problem));
            Assert.False(window.FindNamed<Control>("MediaHint")!.IsEffectivelyVisible);
            var choose = window.FindNamed<Button>("ChooseBrowserButton")!; Assert.True(choose.IsEffectivelyVisible);
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindNamed<Expander>("MoreOptions")!.IsExpanded);

            model.Url = "https://example.com/another"; Settle(window);
            Assert.False(model.HasAnalysisProblem); Assert.False(problem.IsEffectivelyVisible);

            // A locked browser profile is not the same as needing to sign in.
            fake.Downloads.AnalysisFailure = new MediaOperationException("Browser sign-in could not be read. Try Firefox or close the selected browser and retry. No cookie export was saved by KeaClip.");
            await model.AnalyseCommand.ExecuteAsync(null);
            Assert.Equal("Couldn't read your browser sign-in", model.AnalysisProblemTitle); Assert.True(model.AnalysisSuggestsSignIn); Assert.StartsWith("Try Firefox", model.AnalysisProblemText);

            // Missing components offer Prepare right where the attempt failed.
            fake.Downloads.AnalysisFailure = new MediaOperationException("yt-dlp is not ready. Open Components and set it up first.");
            model.Url = "https://example.com/setup"; await model.AnalyseCommand.ExecuteAsync(null); Settle(window);
            Assert.Equal("Setup needed", model.AnalysisProblemTitle); Assert.True(window.FindNamed<Button>("PrepareFromProblemButton")!.IsEffectivelyVisible);
            Assert.Contains("Prepare components", model.AnalysisProblemText);

            // Something that cannot be a link never reaches the service.
            fake.Downloads.AnalysisFailure = null; model.Url = "file:///C:/private/notes.txt"; Settle(window);
            Assert.True(window.FindNamed<TextBlock>("UrlHintText")!.IsEffectivelyVisible);
            await model.DownloadCommand.ExecuteAsync(null);
            Assert.Contains("complete http", model.Status); Assert.Empty(fake.Downloads.Requests); Assert.Equal(3, fake.Downloads.Analyses);
            Assert.False(model.HasAnalysisProblem);
            // An empty field offers nothing to press, and says why.
            model.Url = ""; Settle(window);
            var download = window.FindNamed<Button>("DownloadButton")!;
            Assert.False(download.IsEnabled); Assert.False(window.FindNamed<Button>("AnalyseButton")!.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(download)); Assert.Equal("Paste or type a link first", ToolTip.GetTip(download));
            Assert.False(window.FindNamed<TextBlock>("UrlHintText")!.IsEffectivelyVisible);
            fake.Downloads.AnalysisFailure = new MediaOperationException("This link is not supported by the installed downloader. Check the link or update the downloader.");
            model.Url = "https://example.com/unsupported"; await model.AnalyseCommand.ExecuteAsync(null);
            Assert.Equal("This link isn't supported", model.AnalysisProblemTitle); Assert.True(model.AnalysisSuggestsUpdate); Assert.False(model.AnalysisSuggestsSignIn);
            model.DismissAnalysisProblemCommand.Execute(null); Assert.False(model.HasAnalysisProblem);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Cancelled_analysis_does_not_show_a_problem()
    {
        var fake = new UiFakes(); var downloads = new GatedDownloads();
        using var model = new MainViewModel(fake.Services with { Downloads = downloads }) { Url = UiFakes.Info.Url.AbsoluteUri };
        downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.AttachDownloadPresentation();
        var analysis = model.AnalyseCommand.ExecuteAsync(null);
        await Until(() => model.IsAnalysing);
        model.CancelOperationCommand.Execute(null); await analysis;
        Assert.False(model.HasMedia); Assert.False(model.HasAnalysisProblem); Assert.Contains("cancelled", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Clearing_or_editing_the_link_stops_checking_the_old_one_quietly()
    {
        var fake = new UiFakes(); var downloads = new GatedDownloads();
        using var model = new MainViewModel(fake.Services with { Downloads = downloads }) { Url = UiFakes.Info.Url.AbsoluteUri };
        model.AttachDownloadPresentation();

        downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysis = model.AnalyseCommand.ExecuteAsync(null);
        await Until(() => model.IsAnalysing);
        model.ClearUrlCommand.Execute(null); await analysis;
        Assert.False(model.IsBusy); Assert.False(model.IsCheckingLink); Assert.False(model.HasMedia);
        Assert.False(model.HasAnalysisProblem); Assert.StartsWith("Link cleared", model.Status);

        model.Url = "https://example.com/first";
        downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analysis = model.AnalyseCommand.ExecuteAsync(null);
        await Until(() => model.IsAnalysing);
        model.Url = "https://example.com/second"; await analysis;
        Assert.False(model.HasAnalysisProblem); Assert.False(model.HasMedia); Assert.StartsWith("Stopped checking the previous link", model.Status);
        Assert.Equal("https://example.com/second", model.Url);
    }

    [AvaloniaFact]
    public async Task Link_checks_downloads_and_setup_wait_while_work_runs()
    {
        var fake = new UiFakes(); var downloads = new GatedDownloads();
        using var model = new MainViewModel(fake.Services with { Downloads = downloads }) { Url = UiFakes.Info.Url.AbsoluteUri };
        model.AttachDownloadPresentation();
        Assert.True(model.CanCheckLink); Assert.True(model.CanStartDownload); Assert.True(model.CanRunSetup);
        var raised = new List<string?>(); model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysis = model.AnalyseCommand.ExecuteAsync(null);
        await Until(() => model.IsAnalysing);
        // Link work has its own lane: component setup (maintenance lane) may still start.
        Assert.False(model.CanCheckLink); Assert.False(model.CanStartDownload); Assert.True(model.CanRunSetup);
        Assert.Contains(nameof(MainViewModel.CanCheckLink), raised);
        Assert.Equal("Wait for the current task to finish, or cancel it", model.DownloadTip);
        downloads.Gate.SetResult(); await analysis;
        Assert.True(model.CanCheckLink); Assert.True(model.CanStartDownload); Assert.True(model.CanRunSetup);
        // Both wordings of a cancelled operation are recognised, so a cancel never becomes a "problem".
        var cancelled = typeof(MainViewModel).GetMethod("IsCancelledStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True((bool)cancelled.Invoke(null, ["Operation cancelled. Check the queue or library for work that already finished."])!);
        Assert.True((bool)cancelled.Invoke(null, ["Cancelled. Anything that already finished is in your queue or library."])!);
        Assert.False((bool)cancelled.Invoke(null, ["Sign-in is required."])!);
    }

    [AvaloniaFact]
    public async Task First_run_notice_prepares_components_with_inline_progress_and_then_leaves()
    {
        var fake = new UiFakes(); var components = new SetupComponents { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var model = new MainViewModel(fake.Services with { Components = components });
        var window = Show(model);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; Settle(window);
            var notice = window.FindNamed<Control>("SetupNotice")!;
            Assert.True(notice.IsEffectivelyVisible); Assert.Contains("yt-dlp", model.MissingComponentsText);
            // Tools by their publishers' names, as a sentence: never "ffmpeg, ffprobe".
            Assert.DoesNotContain("ffmpeg", model.MissingComponentsText); Assert.Matches("^Still needed: [^,]+(, [^,]+)* and [^,]+$|^Still needed: [^,]+$", model.MissingComponentsText);
            Assert.Equal("v" + Klippi.Core.AppIdentity.Version, model.AppVersion);
            Click(window, window.FindNamed<Button>("PrepareComponentsButton")!);
            await Until(() => model.HasOperationPercent);
            Assert.True(model.ProvisionCommand.IsRunning); Assert.Equal(40, model.OperationPercent);
            Settle(window);
            Assert.Contains(notice.GetLogicalDescendants().OfType<ProgressBar>(), bar => bar.IsEffectivelyVisible && bar.Value > 0);
            // The percentage is shown once, by the animated counter; the detail line names the step.
            Assert.Equal("Downloading · yt-dlp", window.FindNamed<TextBlock>("SetupProgressText")!.Text);
            components.Gate.SetResult();
            if (model.ProvisionCommand.ExecutionTask is { } provision) await provision;
            Settle(window);
            Assert.Equal(1, components.Provisions); Assert.True(model.ComponentsReady); Assert.False(notice.IsEffectivelyVisible);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task WebM_with_H264_blocks_every_way_to_queue_until_a_compatible_choice_is_made()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 900, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; model.Url = UiFakes.Info.Url.AbsoluteUri;
            fake.Platform.Clipboard = "https://example.com/one\nhttps://example.com/two"; await model.PasteAsync(); model.BatchLinks[0].Selected = true; Settle(window);
            var download = window.FindNamed<Button>("DownloadButton")!; var queueBatch = window.FindNamed<Button>("QueueBatchButton")!; var notice = window.FindNamed<Control>("FormatConflictNotice")!;
            Assert.True(download.IsEnabled); Assert.True(queueBatch.IsEnabled); Assert.False(notice.IsEffectivelyVisible);
            window.FindNamed<ComboBox>("ContainerPicker")!.SelectedItem = MediaContainer.WebM;
            window.FindNamed<ComboBox>("VideoCodecPicker")!.SelectedItem = VideoCodec.H264; Settle(window);
            Assert.Equal(VideoCodec.H264, model.VideoCodec); Assert.Equal(MediaContainer.WebM, model.Container); Assert.True(model.HasFormatConflict);
            Assert.False(download.IsEnabled); Assert.False(window.FindNamed<Button>("DownloadEditButton")!.IsEnabled); Assert.False(queueBatch.IsEnabled);
            Assert.True(notice.IsEffectivelyVisible); Assert.Contains("compatible format", model.DownloadTip); Assert.Contains("compatible format", model.QueueBatchTip);

            model.DownloadMode = DownloadMode.Audio; Settle(window);
            Assert.True(download.IsEnabled); Assert.True(queueBatch.IsEnabled); Assert.False(notice.IsEffectivelyVisible);
            model.DownloadMode = DownloadMode.VideoAudio; Settle(window);
            Assert.False(download.IsEnabled);

            Click(window, window.FindNamed<Button>("UseMkvButton")!); Settle(window);
            Assert.Equal(MediaContainer.Mkv, model.Container); Assert.Equal(MediaContainer.Mkv, window.FindNamed<ComboBox>("ContainerPicker")!.SelectedItem);
            Assert.True(download.IsEnabled); Assert.True(queueBatch.IsEnabled); Assert.False(notice.IsEffectivelyVisible);
            Assert.Empty(fake.Downloads.Requests);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Audio_mode_offers_format_bitrate_and_the_smallest_stream_with_original_recommended()
    {
        var fake = new UiFakes(); fake.Downloads.Info = RichInfo;
        using var model = new MainViewModel(fake.Services) { DownloadMode = DownloadMode.Audio, Url = UiFakes.Info.Url.AbsoluteUri };
        var window = Show(model, 900, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; Settle(window);
            var format = window.FindNamed<ComboBox>("AudioFormatPicker")!; var bitrate = window.FindNamed<ComboBox>("BitratePicker")!; var kept = window.FindNamed<TextBlock>("KeptBitrateText")!;
            Assert.Equal(AudioFormat.Original, format.SelectedItem);
            Assert.False(bitrate.IsEnabled); Assert.False(bitrate.IsEffectivelyVisible); Assert.True(kept.IsEffectivelyVisible); Assert.Equal("As the source provides it", kept.Text);
            Assert.True(window.FindNamed<Control>("OriginalAudioNote")!.IsEffectivelyVisible); Assert.False(window.FindNamed<Control>("ConversionNote")!.IsEffectivelyVisible);
            await model.AnalyseAsync(); Settle(window);
            Assert.Equal("As the source · up to 160 kbps", kept.Text);

            format.SelectedItem = AudioFormat.Mp3; bitrate.SelectedItem = 192; Settle(window);
            Assert.True(bitrate.IsEnabled); Assert.True(bitrate.IsEffectivelyVisible); Assert.False(kept.IsEffectivelyVisible);
            Assert.True(window.FindNamed<Control>("ConversionNote")!.IsEffectivelyVisible); Assert.False(window.FindNamed<Control>("OriginalAudioNote")!.IsEffectivelyVisible);
            var options = model.BuildDownloadOptions(UiFakes.Info.Url); Assert.Equal(AudioFormat.Mp3, options.AudioFormat); Assert.Equal(192, options.AudioKbps); Assert.False(options.SmallestFile);
            format.SelectedItem = AudioFormat.Flac; Settle(window);
            Assert.False(bitrate.IsEnabled); Assert.Equal("Lossless · no bitrate to choose", kept.Text);

            // The smallest audio stream is a first-class audio choice again.
            Click(window, window.FindNamed<ToggleSwitch>("SmallestAudioSwitch")!);
            Assert.Equal("Smallest available", model.Quality); Assert.True(model.BuildDownloadOptions(UiFakes.Info.Url).SmallestFile);
            Click(window, window.FindNamed<ToggleSwitch>("SmallestAudioSwitch")!);
            Assert.Equal("Best available", model.Quality);
            model.Quality = "Smallest available"; Settle(window);
            Assert.True(window.FindNamed<ToggleSwitch>("SmallestAudioSwitch")!.IsChecked);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Playlist_picker_needs_a_selection_and_keeps_edit_for_single_links()
    {
        var fake = new UiFakes(); fake.Downloads.Info = PlaylistInfo(3);
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/list" };
        var window = Show(model, 900, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.AnalyseAsync(); Settle(window);
            Assert.True(window.FindNamed<Control>("PlaylistCard")!.IsEffectivelyVisible);
            Assert.Equal("Download selected", model.DownloadActionText); Assert.Equal("0 of 3 selected", window.FindNamed<TextBlock>("PlaylistSelectionChip")!.Text);
            var download = window.FindNamed<Button>("DownloadButton")!; var edit = window.FindNamed<Button>("DownloadEditButton")!;
            Assert.False(download.IsEnabled); Assert.Equal("Select at least one playlist entry first", ToolTip.GetTip(download));
            Assert.False(edit.IsEnabled); Assert.StartsWith("Download & edit works on single links", (string)ToolTip.GetTip(edit)!);
            model.Playlist[0].Selected = true; model.Playlist[2].Selected = true; Settle(window);
            Assert.True(download.IsEnabled); Assert.False(edit.IsEnabled);
            Assert.Equal("Download 2 selected", model.DownloadActionText); Assert.Equal("2 of 3 selected", window.FindNamed<TextBlock>("PlaylistSelectionChip")!.Text);
            // At the minimum window width the range tools still share one line with Select all/none.
            window.Width = 880; Settle(window);
            var range = window.FindNamed<Control>("PlaylistRange")!;
            Assert.Equal(0, range.Bounds.Y, 1); Assert.True(range.Bounds.Right <= ((Control)range.GetVisualParent()!).Bounds.Width + 0.5, $"range ends at {range.Bounds.Right}");
            Assert.Empty(fake.Downloads.Requests);
            await model.DownloadCommand.ExecuteAsync(null); await Until(() => fake.Downloads.Requests.Count == 2);
            Assert.Equal(new[] { "https://example.com/r1", "https://example.com/r3" }, fake.Downloads.Requests.Select(r => r.Url.AbsoluteUri).Order());
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Skeleton_shows_only_while_analysing_and_its_animation_targets_go_idle()
    {
        var fake = new UiFakes(); var downloads = new GatedDownloads { Info = RichInfo };
        using var model = new MainViewModel(fake.Services with { Downloads = downloads });
        var window = Show(model);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = false; model.Url = UiFakes.Info.Url.AbsoluteUri; Settle(window);
            Assert.True(window.FindNamed<Control>("MediaHint")!.IsEffectivelyVisible);
            downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var analysis = model.AnalyseCommand.ExecuteAsync(null);
            await Until(() => model.IsAnalysing); Settle(window);
            var skeleton = window.FindNamed<Control>("MediaSkeleton")!;
            Assert.True(skeleton.IsEffectivelyVisible); Assert.False(window.FindNamed<Control>("MediaCard")!.IsEffectivelyVisible); Assert.False(window.FindNamed<Control>("MediaHint")!.IsEffectivelyVisible);
            Assert.All(Skeletons(window), part => Assert.True(part.IsVisible));
            downloads.Gate.SetResult(); await analysis; Settle(window);
            Assert.False(skeleton.IsEffectivelyVisible); Assert.True(window.FindNamed<Control>("MediaCard")!.IsEffectivelyVisible);
            // Infinite "breathing" styles match [IsVisible=True]; every skeleton part must switch itself off.
            Assert.All(Skeletons(window), part => Assert.False(part.IsVisible));
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<ProgressRing>(), ring => ring.IsIndeterminate);
            Assert.Equal("12:34", model.MediaDurationText); Assert.Equal("Up to 1080p", model.MediaResolutionText); Assert.Equal("60 fps", model.MediaFrameRateText);
            Assert.Equal("Audio up to 160 kbps", model.MediaAudioText); Assert.Equal("12.4–412 MB", model.MediaSizeRangeText);
            Assert.Equal("Southern Alps Wildlife · YouTube", model.MediaByline);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task The_loading_state_lasts_until_thumbnail_entries_and_qualities_are_in()
    {
        // The service call returns before the thumbnail, playlist entries and quality list are
        // filled in. Nothing final-looking (an empty "0 of 0" picker) may show in that gap.
        var fake = new UiFakes(); fake.Downloads.Info = PlaylistInfo(4);
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/list" };
        model.AttachDownloadPresentation();
        (bool Preview, bool Checking, bool Picker)? atMedia = null;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.HasMedia) && model.HasMedia) atMedia = (model.ShowMediaPreview, model.IsCheckingLink, model.ShowPlaylistPicker); };
        await model.AnalyseAsync();
        Assert.Equal((false, true, false), atMedia);
        Assert.True(model.ShowMediaPreview); Assert.False(model.IsCheckingLink); Assert.True(model.ShowPlaylistPicker); Assert.Equal(4, model.Playlist.Count);
    }

    [AvaloniaFact]
    public async Task Storyboard_heights_are_not_offered_as_download_qualities()
    {
        var fake = new UiFakes(); fake.Downloads.Info = RichInfo;
        using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri, Quality = "Smallest available" };
        await model.AnalyseAsync();
        Assert.Equal(new[] { "Best available", "Smallest available", "1080p maximum", "720p maximum" }, model.Qualities);
        Assert.Equal("Smallest available", model.Quality); // A choice made before analysis is kept (verified audit fix).
        model.Quality = "720p maximum"; Assert.Equal(720, model.BuildDownloadOptions(UiFakes.Info.Url).MaxHeight);
    }

    [AvaloniaFact]
    public async Task Low_disk_space_is_flagged_only_from_reported_sizes()
    {
        var fake = new UiFakes(); fake.Downloads.Info = UiFakes.Info with { Formats = [new("huge", "mp4", 7680, 4320, 60, "av01", "opus", 9_000_000_000_000_000, 320, null)] };
        using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri, DownloadDirectory = Path.GetTempPath() };
        Assert.False(model.IsSpaceLow);
        await model.AnalyseAsync();
        Assert.True(model.IsSpaceLow); Assert.Contains("Space may be tight", model.SpaceWarningText); Assert.EndsWith(" free", model.FreeSpaceText);
        model.Url = "https://example.com/other"; Assert.False(model.IsSpaceLow);
    }

    [AvaloniaFact]
    public async Task Low_disk_space_follows_the_chosen_mode_and_quality()
    {
        // A huge 4K picture stream and a small audio stream: only choices that download the picture warn.
        var fake = new UiFakes(); fake.Downloads.Info = UiFakes.Info with { Formats = [new("4k", "mp4", 3840, 2160, 60, "av01", "none", 9_000_000_000_000_000, null, null), new("audio", "m4a", null, null, null, "none", "mp4a", 5_000_000, 128, "en")] };
        using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri, DownloadDirectory = Path.GetTempPath() };
        model.AttachDownloadPresentation();
        await model.AnalyseAsync();
        var raised = new List<string?>(); model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.True(model.IsSpaceLow);
        model.DownloadMode = DownloadMode.Audio; Assert.False(model.IsSpaceLow); Assert.Equal("", model.SpaceWarningText);
        Assert.Contains(nameof(MainViewModel.IsSpaceLow), raised);
        model.DownloadMode = DownloadMode.VideoOnly; Assert.True(model.IsSpaceLow);
        model.DownloadMode = DownloadMode.VideoAudio; model.Quality = "Smallest available"; Assert.True(model.IsSpaceLow);
        model.Quality = "Best available"; model.DownloadMode = DownloadMode.Archive; Assert.True(model.IsSpaceLow);
    }

    [AvaloniaFact]
    public async Task Icon_buttons_draw_full_size_glyphs_and_switches_reach_the_right_edge()
    {
        var fake = new UiFakes(); fake.Downloads.AnalysisFailure = new MediaOperationException("Could not reach the media service. Check your internet connection and try again.");
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/offline" };
        var window = Show(model, 1180, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.AnalyseCommand.ExecuteAsync(null); model.DownloadMode = DownloadMode.Archive; Settle(window);
            Assert.Equal("Can't reach the site", model.AnalysisProblemTitle);
            foreach (var name in new[] { "ClearLinkButton", "DismissProblemButton", "RefreshSpaceButton" })
            {
                var glyph = window.FindNamed<Button>(name)!.GetVisualDescendants().OfType<StrokeIcon>().Single();
                Assert.True(glyph.IsEffectivelyVisible, name); Assert.True(glyph.Bounds.Width >= 13.9 && glyph.Bounds.Height >= 13.9, $"{name} glyph is {glyph.Bounds.Size}");
            }
            var zip = window.FindNamed<ToggleSwitch>("ArchiveZipSwitch")!; var row = (Control)zip.GetVisualParent()!;
            var track = zip.GetVisualDescendants().OfType<Border>().First(border => border.Name == "OuterBorder");
            var trackRight = track.TranslatePoint(new Point(track.Bounds.Width, 0), row)!.Value.X;
            Assert.InRange(trackRight, row.Bounds.Width - 1, row.Bounds.Width + 1);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Download_stays_in_view_in_a_pinned_action_bar()
    {
        var fake = new UiFakes(); fake.Downloads.Info = RichInfo;
        using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri };
        var window = Show(model, 1180, 800);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.AnalyseAsync(); Settle(window);
            var scroll = window.FindNamed<ScrollViewer>("PageScroll")!; var bar = window.FindNamed<Control>("ActionBar")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height); // the page itself scrolls
            var cases = new[] { (1180d, 800d, false, false), (1180d, 800d, false, true), (1060d, 740d, true, true), (960d, 740d, true, true), (880d, 740d, true, false), (880d, 740d, true, true), (880d, 480d, true, true) };
            foreach (var (width, height, compact, oneOffFolder) in cases)
            {
                model.JobDirectory = oneOffFolder ? Path.Combine(Path.GetTempPath(), "KeaClip one-off folder for a long trimmed path") : "";
                window.Width = width; window.Height = height; Settle(window);
                var label = $"{width}x{height}{(oneOffFolder ? " with a one-off folder" : "")}";
                foreach (var name in new[] { "DownloadButton", "DownloadEditButton", "OutputFolderText", "FreeSpaceChip", "ChooseJobFolderButton" })
                {
                    var control = window.FindNamed<Control>(name)!;
                    Assert.False(control.GetVisualAncestors().Contains(scroll), name);
                    var top = control.TranslatePoint(default, window)!.Value.Y;
                    Assert.True(top >= 0 && top + control.Bounds.Height <= window.Bounds.Height - 44, $"{name} at {label} is at {top}");
                }
                Assert.True(compact == bar.Classes.Contains("compact"), $"action bar at {label} is {bar.Bounds.Width} wide (window {window.Bounds.Width}); compact expected {compact}");
                Assert.Equal(!compact, window.FindNamed<TextBlock>("DownloadEditLabel")!.IsEffectivelyVisible);
                Assert.Equal(!compact, window.FindNamed<TextBlock>("ChooseJobFolderLabel")!.IsEffectivelyVisible);
                Assert.Equal(compact, window.FindNamed<TextBlock>("ChooseJobFolderShortLabel")!.IsEffectivelyVisible);
                Assert.Equal(oneOffFolder, window.FindNamed<Control>("UseDefaultFolderButton")!.IsEffectivelyVisible);
                // Nothing in the folder block runs under the action buttons.
                var actionsLeft = window.FindNamed<Control>("DownloadEditButton")!.TranslatePoint(default, bar)!.Value.X;
                Assert.True(window.FindNamed<TextBlock>("OutputFolderText")!.Bounds.Width > 40, $"path squeezed at {label}");
                foreach (var name in new[] { "OutputFolderText", "FreeSpaceChip", "ChooseJobFolderButton", "UseDefaultFolderButton" })
                {
                    var control = window.FindNamed<Control>(name)!;
                    if (!control.IsEffectivelyVisible) continue;
                    var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), bar)!.Value.X;
                    Assert.True(right <= actionsLeft - 8, $"{name} ends at {right}, under the actions at {actionsLeft} ({label})");
                }
            }
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public void Site_names_use_their_brand_casing_or_the_host()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        Assert.Equal("Sample creator · YouTube", Byline(model, UiFakes.Info with { Source = "YoutubeTab" }));
        Assert.Equal("Sample creator · SoundCloud", Byline(model, UiFakes.Info with { Source = "Soundcloud" }));
        Assert.Equal("Sample creator · example.com", Byline(model, UiFakes.Info with { Source = "Generic", Url = new Uri("https://www.example.com/clip") }));
        Assert.Equal("example.com", Byline(model, UiFakes.Info with { Source = null, Creator = null }));
    }

    [Fact]
    public void Enum_equality_converter_supports_radio_groups_without_overwriting_choices()
    {
        var converter = EnumEqualsConverter.Instance; var culture = CultureInfo.InvariantCulture;
        Assert.Equal(true, converter.Convert(DownloadMode.Audio, typeof(bool?), DownloadMode.Audio, culture));
        Assert.Equal(false, converter.Convert(DownloadMode.Audio, typeof(bool?), DownloadMode.Archive, culture));
        Assert.Equal(true, converter.Convert(AudioFormat.Original, typeof(bool), "Original", culture));
        Assert.Equal(true, converter.Convert(VideoCodec.Hevc, typeof(bool), "H264|Hevc", culture));
        Assert.Equal(false, converter.Convert(null, typeof(bool), DownloadMode.Audio, culture));
        Assert.Equal(DownloadMode.Archive, converter.ConvertBack(true, typeof(DownloadMode), DownloadMode.Archive, culture));
        Assert.Equal(DownloadMode.VideoOnly, converter.ConvertBack(true, typeof(DownloadMode), "VideoOnly", culture));
        Assert.Same(Avalonia.Data.BindingOperations.DoNothing, converter.ConvertBack(false, typeof(DownloadMode), DownloadMode.Archive, culture));
        Assert.Same(Avalonia.Data.BindingOperations.DoNothing, converter.ConvertBack(true, typeof(DownloadMode), "NotAMode", culture));
        Assert.Equal(true, EnumEqualsConverter.NotEqual.Convert(AudioFormat.Mp3, typeof(bool), "Original", culture));
        Assert.Same(Avalonia.Data.BindingOperations.DoNothing, EnumEqualsConverter.NotEqual.ConvertBack(true, typeof(AudioFormat), "Mp3", culture));
    }

    // Review renders: every important state of the page in both themes, plus wide and minimum windows.
    [AvaloniaFact]
    public async Task Download_page_states_render_for_review()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeaClip-Ui-DownloadView-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var existing = Path.Combine(root, "saved.mp4"); await File.WriteAllTextAsync(existing, "kept");
        using var thumbnail = PreviewImage();
        try
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                var suffix = theme.ToLowerInvariant();

                // First run, then provisioning progress.
                {
                    var fake = new UiFakes(); var components = new SetupComponents { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
                    using var model = new MainViewModel(fake.Services with { Components = components });
                    var window = Show(model);
                    try
                    {
                        await model.InitializeAsync(); model.ReducedMotion = true; model.Theme = theme; await Capture(window, $"dl-firstrun-{suffix}");
                        var provision = model.ProvisionCommand.ExecuteAsync(null); await Until(() => model.HasOperationPercent);
                        await Capture(window, $"dl-provisioning-{suffix}");
                        components.Gate.SetResult(); await provision;
                    }
                    finally { Close(window, model); }
                }

                // Empty, typed hint, analysing, analysed (+ tall), audio, archive, wide, minimum width.
                {
                    var fake = new UiFakes(); var downloads = new GatedDownloads { Info = RichInfo };
                    using var model = new MainViewModel(fake.Services with { Downloads = downloads });
                    var window = Show(model);
                    try
                    {
                        await model.InitializeAsync(); model.ReducedMotion = true; model.Theme = theme; await Capture(window, $"dl-empty-{suffix}");
                        model.Url = "kea parrot"; Assert.False(model.PrepareLinkForAction()); await Capture(window, $"dl-urlhint-{suffix}");
                        model.Url = "https://www.youtube.com/watch?v=kea";
                        downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var analysis = model.AnalyseCommand.ExecuteAsync(null); await Until(() => model.IsAnalysing);
                        await Capture(window, $"dl-analysing-{suffix}");
                        downloads.Gate.SetResult(); await analysis;
                        SetThumbnail(model, thumbnail);
                        await Capture(window, $"dl-media-{suffix}");
                        window.SetRenderScaling(2); await Capture(window, $"dl-media-200-{suffix}"); window.SetRenderScaling(1);
                        window.Height = 1650; await Capture(window, $"dl-media-tall-{suffix}");
                        window.FindNamed<Expander>("FormatDetailsExpander")!.IsExpanded = true; window.FindNamed<Expander>("DescriptionExpander")!.IsExpanded = true;
                        await Capture(window, $"dl-media-details-tall-{suffix}");
                        window.FindNamed<Expander>("FormatDetailsExpander")!.IsExpanded = false; window.FindNamed<Expander>("DescriptionExpander")!.IsExpanded = false;
                        model.DownloadMode = DownloadMode.Audio; await Capture(window, $"dl-audio-original-tall-{suffix}");
                        window.FindNamed<ComboBox>("AudioFormatPicker")!.SelectedItem = AudioFormat.Mp3; model.Quality = "Smallest available";
                        await Capture(window, $"dl-audio-mp3-tall-{suffix}");
                        window.FindNamed<ComboBox>("AudioFormatPicker")!.SelectedItem = AudioFormat.Original; model.Quality = "Best available";
                        model.DownloadMode = DownloadMode.Archive; await Capture(window, $"dl-archive-tall-{suffix}");
                        window.FindNamed<RadioButton>("ArchiveTile")!.Focus(NavigationMethod.Tab); await Capture(window, $"dl-tile-focus-tall-{suffix}");
                        window.FindNamed<RadioButton>("AudioTile")!.Focus(NavigationMethod.Tab); await Capture(window, $"dl-tile-focus-unchecked-tall-{suffix}");
                        window.FindNamed<RadioButton>("VideoAudioTile")!.Focus(NavigationMethod.Pointer); // as if clicked: no keyboard ring in later frames
                        model.DownloadMode = DownloadMode.VideoAudio;
                        window.FindNamed<Expander>("MoreOptions")!.IsExpanded = true;
                        window.FindNamed<ComboBox>("ContainerPicker")!.SelectedItem = MediaContainer.WebM; window.FindNamed<ComboBox>("VideoCodecPicker")!.SelectedItem = VideoCodec.H264;
                        window.Height = 2100; await Capture(window, $"dl-moreoptions-conflict-tall-{suffix}");
                        model.UseMkvCommand.Execute(null); window.FindNamed<ComboBox>("ContainerPicker")!.SelectedItem = MediaContainer.Auto; window.FindNamed<ComboBox>("VideoCodecPicker")!.SelectedItem = VideoCodec.Original;
                        window.FindNamed<Expander>("MoreOptions")!.IsExpanded = false;
                        window.Width = 1180; window.Height = 800; await Capture(window, $"dl-default-1180-{suffix}");
                        model.JobDirectory = root; window.Width = 1400; window.Height = 900; await Capture(window, $"dl-wide-jobfolder-{suffix}");
                        model.JobDirectory = ""; window.Width = 880; window.Height = 740; await Capture(window, $"dl-minwidth-{suffix}");
                    }
                    finally { Close(window, model); }
                }

                // Problems: sign-in required, setup needed, low space.
                {
                    var fake = new UiFakes(); fake.Downloads.AnalysisFailure = new MediaOperationException("Sign-in is required. Choose a browser you are signed into, and only save content you are allowed to access.");
                    using var model = new MainViewModel(fake.Services) { Url = "https://example.com/members-only" };
                    var window = Show(model);
                    try
                    {
                        await model.InitializeAsync(); model.ReducedMotion = true; model.Theme = theme; await model.AnalyseCommand.ExecuteAsync(null); await Capture(window, $"dl-error-signin-{suffix}");
                        fake.Downloads.AnalysisFailure = new MediaOperationException("yt-dlp is not ready. Open Components and set it up first.");
                        model.Url = "https://example.com/setup"; await model.AnalyseCommand.ExecuteAsync(null); await Capture(window, $"dl-error-setup-{suffix}");
                        fake.Downloads.AnalysisFailure = null; fake.Downloads.Info = UiFakes.Info with { Formats = [new("huge", "mp4", 7680, 4320, 60, "av01", "opus", 9_000_000_000_000_000, 320, null)] };
                        model.Url = "https://example.com/huge"; model.DownloadDirectory = Path.GetTempPath(); await model.AnalyseAsync();
                        window.Height = 1500; await Capture(window, $"dl-lowspace-tall-{suffix}");
                    }
                    finally { Close(window, model); }
                }

                // Playlist picker.
                {
                    var fake = new UiFakes(); fake.Downloads.Info = PlaylistInfo(14);
                    using var model = new MainViewModel(fake.Services) { Url = "https://example.com/list" };
                    var window = Show(model, 900, 1900);
                    try
                    {
                        await model.InitializeAsync(); model.ReducedMotion = true; model.Theme = theme; await model.AnalyseAsync(); model.RangeStart = "2"; model.RangeEnd = "5"; model.SelectRangeCommand.Execute(null);
                        await Capture(window, $"dl-playlist-tall-{suffix}");
                        window.Width = 880; await Capture(window, $"dl-playlist-minwidth-tall-{suffix}");
                    }
                    finally { Close(window, model); }
                }

                // Pasted batch and an already-saved duplicate.
                {
                    var fake = new UiFakes(); fake.Store.History.Add(new("saved", "A moment worth keeping", UiFakes.Info.Url.AbsoluteUri, "Example", DateTimeOffset.Now, existing, "VideoAudio", "Best available", 48_200_000, "Finished"));
                    using var model = new MainViewModel(fake.Services);
                    var window = Show(model); window.Height = 2400; // set after opening: the shell clamps the opening size to the screen
                    try
                    {
                        await model.InitializeAsync(); model.ReducedMotion = true; model.Theme = theme; model.Url = UiFakes.Info.Url.AbsoluteUri; await model.QueueDownloadAsync(false);
                        fake.Platform.Clipboard = "https://example.com/one\nhttps://example.com/two\nhttps://www.youtube.com/watch?v=a-very-long-identifier-that-should-trim-cleanly&list=PL1234567890"; await model.PasteAsync(); model.BatchLinks[1].Selected = true; model.SelectedTab = 0;
                        await Capture(window, $"dl-batch-duplicate-tall-{suffix}");
                    }
                    finally { Close(window, model); }
                }
            }
        }
        finally { File.Delete(existing); Directory.Delete(root, false); }
    }

    // ----- helpers -----
    private static MainWindow Show(MainViewModel model, double width = 900, double height = 740)
    {
        // Frames show final states, not mid-animation. Loading settings (InitializeAsync) turns motion back on, so each
        // test sets it again after initialising; a test about motion turns it on explicitly.
        model.ReducedMotion = true;
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show(); window.UpdateLayout();
        return window;
    }

    private static void Close(Window window, MainViewModel model) { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }

    // Window resizes and posted layout work reach the window through the dispatcher.
    private static void Settle(Window window) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.UpdateLayout();
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null); window.KeyRelease(key, RawInputModifiers.None, physical, null); Settle(window);
    }

    private static async Task PressEnterAndAnalyse(Window window, MainViewModel model)
    {
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        if (model.AnalyseCommand.ExecutionTask is { } analysis) await analysis;
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
    }

    private static string Byline(MainViewModel model, MediaInfo info)
    {
        typeof(MainViewModel).GetField("_media", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(model, info);
        return model.MediaByline;
    }

    // Just part of it: starts as the whole length, says what it will save, explains a wrong time, stays out of Archive
    // and playlists, and a new link starts over.
    [AvaloniaFact]
    public async Task Just_part_of_it_saves_a_checked_range_of_the_analysed_video()
    {
        var fake = new UiFakes(); fake.Downloads.Info = RichInfo;
        using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri };
        var window = Show(model, 900, 1700);
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; Settle(window);
            Assert.False(model.CanSavePart);
            await model.AnalyseAsync(); Settle(window);
            Assert.True(model.CanSavePart); Assert.True(window.FindNamed<Control>("SavePartRow")!.IsEffectivelyVisible);
            Click(window, window.FindNamed<ToggleSwitch>("SavePartSwitch")!); Settle(window);
            Assert.True(model.SavePart); Assert.Equal("0:00", model.PartStart); Assert.Equal("12:34", model.PartEnd);
            model.PartStart = "0:45"; model.PartEnd = "1:20"; Settle(window);
            Assert.Equal("Saves 0:35 of 12:34, from 0:45 to 1:20.", window.FindNamed<TextBlock>("PartSummaryText")!.Text);
            var options = model.BuildDownloadOptions(UiFakes.Info.Url);
            Assert.Equal(TimeSpan.FromSeconds(45), options.SectionStart); Assert.Equal(TimeSpan.FromSeconds(80), options.SectionEnd);

            model.PartEnd = "20:00"; Settle(window);
            Assert.True(model.PartHasProblem); Assert.Equal("The end is after the video ends (12:34).", model.PartSummary);
            Assert.Throws<MediaOperationException>(() => model.BuildDownloadOptions(UiFakes.Info.Url));
            model.PartEnd = "soon"; Assert.Equal("Enter an end time like 2:05 or 1:02:03.", model.PartSummary);

            model.DownloadMode = DownloadMode.Archive; Settle(window);
            Assert.False(model.CanSavePart); Assert.Null(model.BuildDownloadOptions(UiFakes.Info.Url).SectionStart);
            model.DownloadMode = DownloadMode.VideoAudio;
            model.Url = "https://example.com/other"; Settle(window);
            Assert.False(model.SavePart); Assert.Equal("", model.PartStart);
        }
        finally { Close(window, model); }
    }

    [Theory]
    [InlineData("45", 45d)]
    [InlineData("1:20", 80d)]
    [InlineData("1:20.5", 80.5)]
    [InlineData("1:02:03", 3723d)]
    [InlineData(" 0:00 ", 0d)]
    public void Clock_times_read_seconds_minutes_and_hours(string text, double seconds)
    {
        Assert.True(MainViewModel.TryParseClock(text, out var time));
        Assert.Equal(TimeSpan.FromSeconds(seconds), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1:60")]
    [InlineData("1:61:00")]
    [InlineData("-5")]
    [InlineData("1::2")]
    [InlineData("1:2:3:4")]
    [InlineData("soon")]
    public void Unclear_clock_times_are_refused(string text) => Assert.False(MainViewModel.TryParseClock(text, out _));
    // README screenshots: the analysed Download page with a neutral folder, so no real user name appears in public images.
    [AvaloniaFact]
    public async Task Readme_download_screenshots_show_no_personal_paths()
    {
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var fake = new UiFakes(); fake.Store.Settings = fake.Store.Settings with { DownloadDirectory = @"C:\Videos\KeaClip" };
            var downloads = new GatedDownloads { Info = RichInfo };
            using var model = new MainViewModel(fake.Services with { Downloads = downloads });
            var window = Show(model, 1180, 780);
            try
            {
                await model.InitializeAsync(); model.ReducedMotion = true; model.Theme = theme;
                // Demo GIF frames (900x740): empty, link pasted.
                window.Width = 900; window.Height = 740;
                await Capture(window, $"readme-step1-{theme.ToLowerInvariant()}");
                model.Url = "https://www.youtube.com/watch?v=kea";
                await Capture(window, $"readme-step2-{theme.ToLowerInvariant()}");
                await model.AnalyseCommand.ExecuteAsync(null);
                using (var early = PreviewImage()) { SetThumbnail(model, early); await Capture(window, $"readme-step3-{theme.ToLowerInvariant()}"); }
                window.Width = 1180; window.Height = 780;
                using var thumbnail = PreviewImage(); SetThumbnail(model, thumbnail);
                Assert.Equal(@"C:\Videos\KeaClip", model.DownloadDirectory);
                Assert.DoesNotContain(Environment.UserName, model.DownloadDirectory, StringComparison.OrdinalIgnoreCase);
                await Capture(window, $"readme-download-{theme.ToLowerInvariant()}");
            }
            finally { Close(window, model); }
        }
    }
    private static IEnumerable<Border> Skeletons(Window window) => window.FindNamed<Control>("MediaSkeleton")!.GetLogicalDescendants().OfType<Border>().Where(border => border.Classes.Contains("skeleton"));

    // Colour transitions (theme switch) run on wall-clock time; wait them out so frames are final.
    private static async Task Capture(Window window, string name)
    {
        Settle(window); await Task.Delay(320); Settle(window);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        Assert.True(window.FindNamed<TextBox>("UrlInput")!.IsEffectivelyVisible);
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    // Thumbnails normally arrive over HTTPS; tests never touch the network, so set a generated one.
    private static void SetThumbnail(MainViewModel model, Bitmap image) => typeof(MainViewModel).GetProperty(nameof(MainViewModel.Thumbnail))!.SetValue(model, image);

    private static WriteableBitmap PreviewImage()
    {
        const int width = 480, height = 270;
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var ridge = height * (.55 + .12 * Math.Sin(x / 47d) + .05 * Math.Sin(x / 13d));
                var near = height * (.78 + .06 * Math.Sin(x / 31d + 1));
                var t = y / (double)height;
                (double r, double g, double b) = y > near ? (34, 74, 52) : y > ridge ? (58, 96, 84) : (238 - 90 * t, 196 - 40 * t, 150 + 40 * t);
                var i = (y * width + x) * 4; pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255;
            }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        return bitmap;
    }

    private static async Task Until(Func<bool> condition) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, deadline.Token); }
}

/// <summary>Analysis that can be held open to show the loading state.</summary>
public sealed class GatedDownloads : IDownloadService
{
    public MediaInfo Info { get; set; } = UiFakes.Info;
    public TaskCompletionSource? Gate { get; set; }
    public async Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default)
    {
        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return Info with { Url = url };
    }
    public Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadResult("test-output.mp4", Info, 2_000_000));
    public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["sample"]);
}

/// <summary>Components that start missing and become ready after Prepare.</summary>
public sealed class SetupComponents : IComponentService
{
    private bool _ready;
    public TaskCompletionSource? Gate { get; set; }
    public int Provisions { get; private set; }
    public string? Find(string name) => null;
    public Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentInfo>>([new("yt-dlp", null, _ready ? "2026.09" : null, _ready), new("ffmpeg", null, _ready ? "8.0" : null, _ready), new("ffprobe", null, _ready ? "8.0" : null, _ready)]);
    public async Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Provisions++;
        progress?.Report(new(JobStage.Downloading, 40, "yt-dlp"));
        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        _ready = true;
    }
    public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
