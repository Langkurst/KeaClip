using Avalonia.Automation;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Klippi.Core;
using Klippi.Desktop;
using Klippi.Desktop.Views;
using LibVLCSharp.Shared;
using Xunit;

namespace Klippi.UiTests;

public sealed class EditorViewTests
{
    // ===== Rendered evidence (PNG files are written only when KEACLIP_UI_EVIDENCE_DIRECTORY is set) =====

    [AvaloniaFact]
    public async Task Editor_states_render_cleanly_in_both_themes()
    {
        using var media = new TempMedia();
        var fake = new EditorFakes(); fake.Store.History.Add(new("recent", "Weekend highlights.mp4", null, "Local", DateTimeOffset.UtcNow, media.Path, "VideoAudio", "Best available", 48_000_000, "Finished"));
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            // Loading settings resets ReducedMotion, so set it afterwards: captures then show final states.
            await model.InitializeAsync(); model.ReducedMotion = true; model.SelectedTab = 3;
            foreach (var theme in Themes)
            {
                model.Theme = theme;
                Capture(window, $"editor-empty-{theme.ToLowerInvariant()}");
                Assert.True(window.FindNamed<Border>("EmptyState")!.IsEffectivelyVisible);
            }
            // Nothing below the drop zone: no empty row gap.
            Assert.Equal(0, window.FindNamed<Grid>("EditorLayout")!.RowSpacing); Assert.False(window.FindNamed<StackPanel>("OutputColumn")!.IsVisible);
            var dropZone = window.FindNamed<Panel>("DropZone")!; dropZone.Classes.Add("dragover"); model.Theme = "Dark"; Capture(window, "editor-empty-dragover-dark"); dropZone.Classes.Remove("dragover");

            // Opening: the probe is still running.
            fake.Media.ProbeGate = new(); fake.Media.WaveformGate = new();
            var opening = model.OpenEditorAsync("fixture.mp4");
            Assert.True(model.IsOpeningEditor);
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-opening-{theme.ToLowerInvariant()}"); }
            fake.Media.ProbeGate.SetResult();
            await Until(() => model.HasEditor);
            Assert.True(model.IsWaveformLoading); Assert.False(model.IsOpeningEditor);
            model.Theme = "Dark"; window.Height = 1400; Capture(window, "editor-waveform-loading-dark");
            fake.Media.WaveformGate.SetResult(); await opening;
            Assert.False(model.IsWaveformLoading);

            // Populated video, narrow single column, whole page.
            window.Height = 740;
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-video-{theme.ToLowerInvariant()}"); }
            // The default window: the preview gives way so the whole trim card (timeline, Start/End, length) is on screen.
            window.Width = 1180; window.Height = 800; model.Theme = "Dark"; Capture(window, "editor-default-window-dark");
            var page = window.FindNamed<EditorView>("EditorPage")!; var endBox = window.FindNamed<TextBox>("EndTimeBox")!;
            var endBottom = endBox.TranslatePoint(new Point(0, endBox.Bounds.Height), page)!.Value.Y;
            Assert.True(endBottom <= page.Bounds.Height, $"Start/End row ends at {endBottom}, page viewport is {page.Bounds.Height}");
            Assert.True(window.FindNamed<Klippi.Desktop.Controls.AspectFrame>("PreviewFrame")!.Bounds.Height >= 180);
            window.Width = 900; window.Height = 2300;
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-video-page-{theme.ToLowerInvariant()}"); }
            window.FindNamed<Expander>("AdvancedExpander")!.IsExpanded = true; window.FindNamed<Expander>("AudioClipExpander")!.IsExpanded = true;
            window.Height = 2900; model.Theme = "Dark"; Capture(window, "editor-video-expanded-dark");
            model.Theme = "Light"; Capture(window, "editor-video-expanded-light");
            window.FindNamed<Expander>("AdvancedExpander")!.IsExpanded = false; window.FindNamed<Expander>("AudioClipExpander")!.IsExpanded = false;

            // Timeline zoomed in with a hover label.
            window.Height = 1400; model.Theme = "Dark"; window.UpdateLayout();
            var waveform = window.FindNamed<WaveformControl>("Waveform")!;
            model.Seek(.42); waveform.ZoomIn(); waveform.ZoomIn(); window.UpdateLayout();
            Assert.True(waveform.Zoom > 2);
            window.MouseMove(waveform.TranslatePoint(new Point(waveform.Bounds.Width * .7, 80), window)!.Value);
            Capture(window, "editor-timeline-zoomed-dark");
            model.Theme = "Light"; Capture(window, "editor-timeline-zoomed-light");
            window.MouseMove(new Point(2, 2)); waveform.ResetZoom();

            // Edge cases: invalid range, lossless, target size and the delete-original warning.
            model.Theme = "Dark"; window.Height = 2300;
            model.StartTime = "00:00:08.000"; model.EndTime = "00:00:02.000"; model.DeleteOriginal = true;
            Assert.False(model.IsSelectionValid); Assert.True(model.HasEstimateProblem);
            Capture(window, "editor-invalid-range-dark");
            model.StartTime = "00:00:01.000"; model.EndTime = "00:00:04.500"; model.DeleteOriginal = false; model.AttachmentPreset = "10 MB";
            Assert.True(model.IsTargetSizeCompression); Assert.True(model.HasEditorEstimate);
            Capture(window, "editor-target-size-dark");
            model.ClipMode = ClipMode.Lossless; Capture(window, "editor-lossless-dark");
            model.ClipMode = ClipMode.Exact; model.AttachmentPreset = "Custom target"; model.Compression = CompressionPreset.Off;

            // Wide window: the export column moves beside the preview.
            window.Width = 1400; window.Height = 1000;
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-wide-{theme.ToLowerInvariant()}"); }
            var output = window.FindNamed<StackPanel>("OutputColumn")!; Assert.Equal(1, Grid.GetColumn(output));
            window.Width = 900; Settle(window); Assert.Equal(0, Grid.GetColumn(output)); Assert.Equal(1, Grid.GetRow(output));
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Audio_only_and_silent_video_render_purpose_built_previews()
    {
        var fake = new EditorFakes { };
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true;
            fake.Media.HasVideo = false; await model.OpenEditorAsync("song.m4a");
            Assert.True(model.IsAudioOnlyMedia); Assert.True(model.AudioOnly); Assert.False(window.FindNamed<RadioButton>("VideoOutputTile")!.IsEnabled);
            Assert.False(window.FindNamed<Border>("InlinePreviewHost")!.IsVisible);
            Assert.IsType<LibVLCSharp.Avalonia.VideoView>(window.FindNamed<Border>("InlinePreviewHost")!.Child);
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-audio-{theme.ToLowerInvariant()}"); }
            window.Height = 1300; model.Theme = "Dark"; Capture(window, "editor-audio-page-dark");

            fake.Media.HasVideo = true; fake.Media.HasAudio = false; await model.OpenEditorAsync("silent.mp4");
            Assert.Empty(model.Waveform); Assert.Contains("No audio track", model.WaveformPlaceholder);
            Assert.True(window.FindNamed<Border>("InlinePreviewHost")!.IsVisible);
            Capture(window, "editor-silent-video-dark");
            var videoTile = window.FindNamed<RadioButton>("VideoOutputTile")!; var audioTile = window.FindNamed<RadioButton>("AudioOutputTile")!;
            Assert.False(model.AudioOnly); Assert.True(videoTile.IsEnabled);
            Assert.True(videoTile.IsChecked == true, "video tile " + videoTile.IsChecked);
            Assert.True(audioTile.IsChecked == false, "audio tile " + audioTile.IsChecked + " classes " + string.Join(',', audioTile.Classes));

            // A portrait phone clip gets a taller frame instead of a 16:9 box with wide bars.
            fake.Media.HasAudio = true; fake.Media.Width = 1080; fake.Media.Height = 1920; await model.OpenEditorAsync("phone.mp4");
            Assert.Equal(9d / 16d, model.PreviewAspect, 6);
            window.Height = 900; Capture(window, "editor-portrait-dark");
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Export_and_share_progress_then_results_render_inline()
    {
        using var export = new TempMedia();
        var fake = new EditorFakes(); fake.Platform.SavePath = export.Path; fake.Media.ExportGate = new();
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 2300;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); model.Theme = "Dark";
            model.StartTime = "00:00:02.000"; model.EndTime = "00:00:06.000";
            var exporting = model.ExportAsync();
            await Until(() => model.IsExporting && model.HasOperationPercent);
            Assert.False(window.FindNamed<Button>("ExportButton")!.IsEffectivelyEnabled);
            Assert.False(model.ExportProgressIndeterminate);
            Capture(window, "editor-exporting-dark");
            fake.Media.ExportGate.SetResult(); await exporting;
            await Until(() => { Dispatcher.UIThread.RunJobs(); return model.HasLastExport; });
            Assert.Equal(Path.GetFileName(export.Path), model.LastExportName);
            Assert.False(model.ExportProgressIndeterminate);

            fake.Sharing.Gate = new();
            var sharing = model.ShareLastExportCommand.ExecuteAsync(null);
            await Until(() => model.IsSharing);
            Capture(window, "editor-sharing-dark");
            fake.Sharing.Gate.SetResult(); await sharing;
            Assert.Equal(export.Path, fake.Sharing.LastPath); Assert.True(model.HasShareUrl);
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-shared-{theme.ToLowerInvariant()}"); }

            model.OpenLastExportCommand.Execute(null); model.RevealLastExportCommand.Execute(null);
            Assert.Equal(export.Path, Assert.Single(fake.Platform.OpenedFiles)); Assert.Equal(export.Path, Assert.Single(fake.Platform.RevealedFiles));
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    // ===== Behaviour =====

    [AvaloniaFact]
    public void Timeline_zoom_keeps_trim_and_seek_ratios_in_full_media_space()
    {
        var control = new WaveformControl { Samples = Enumerable.Range(0, 600).Select(i => (float)Math.Abs(Math.Sin(i / 9d))).ToArray(), StartRatio = .3, EndRatio = .8, Duration = TimeSpan.FromSeconds(60), PlayheadRatio = .5 };
        var window = new Window { Width = 800, Height = 140, Content = control }; window.Show(); window.UpdateLayout();
        try
        {
            var width = control.Bounds.Width; var y = control.Bounds.Height - 30; var seek = -1d; control.SeekRequested += ratio => seek = ratio;
            Assert.False(control.CanZoomOut); Assert.True(control.CanZoomIn);
            control.ZoomIn(); // Anchored on the in-view playhead (0.5) with factor 1.6.
            Assert.Equal(1.6, control.Zoom, 3); Assert.True(control.CanZoomOut);
            var viewStart = .5 - .5 / 1.6;
            window.MouseDown(new(0, y), MouseButton.Left); window.MouseUp(new(0, y), MouseButton.Left);
            Assert.Equal(viewStart, seek, 3);
            window.MouseDown(new(width / 2, y), MouseButton.Left); window.MouseUp(new(width / 2, y), MouseButton.Left);
            Assert.Equal(.5, seek, 3);
            // Pressing a grip only picks it up: a click on its middle leaves the trim point where it was.
            var startX = (.3 - viewStart) * 1.6 * width;
            window.MouseDown(new(startX + 6, y), MouseButton.Left); window.MouseUp(new(startX + 6, y), MouseButton.Left);
            Assert.Equal(.3, control.StartRatio, 6);
            // Drag the start grip (drawn at its zoomed position) by the grabbed point: the ratio stays absolute.
            window.MouseDown(new(startX + 2, y), MouseButton.Left); window.MouseMove(new(width / 2 + 2, y)); window.MouseUp(new(width / 2 + 2, y), MouseButton.Left);
            Assert.Equal(.5, control.StartRatio, 3); Assert.Equal(.8, control.EndRatio);
            // Ctrl+wheel zooms around the pointer; plain wheel pans once zoomed.
            var before = control.Zoom;
            window.MouseWheel(new(width / 4, y), new Vector(0, 1), RawInputModifiers.Control);
            Assert.True(control.Zoom > before);
            seek = -1; window.MouseDown(new(width / 4, y), MouseButton.Left); window.MouseUp(new(width / 4, y), MouseButton.Left);
            var anchored = seek;
            window.MouseWheel(new(width / 4, y), new Vector(0, -1), RawInputModifiers.None);
            window.MouseDown(new(width / 4, y), MouseButton.Left); window.MouseUp(new(width / 4, y), MouseButton.Left);
            Assert.True(seek > anchored, "Wheel down should pan later in the media while zoomed.");
            control.ResetZoom(); Assert.Equal(1, control.Zoom);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Button_zoom_eases_with_motion_and_snaps_with_reduced_motion()
    {
        var control = new WaveformControl { Samples = [.2f, .6f, .4f], Duration = TimeSpan.FromSeconds(60), PlayheadRatio = .5 };
        var window = new Window { Width = 800, Height = 140, Content = control }; window.Show(); window.UpdateLayout();
        try
        {
            window.Classes.Add("motion");
            control.ZoomIn(); control.ZoomIn();
            Assert.Equal(1, control.Zoom); Assert.True(FrameRequested(control)); // Eases toward 1.6² instead of jumping.
            await Until(() => { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); return Math.Abs(control.Zoom - 2.56) < 1e-9; });
            Assert.False(FrameRequested(control)); // The loop stops once the zoom lands.
            var seek = -1d; control.SeekRequested += ratio => seek = ratio;
            var middle = control.Bounds.Width / 2; window.MouseDown(new(middle, 100), MouseButton.Left); window.MouseUp(new(middle, 100), MouseButton.Left);
            Assert.Equal(.5, seek, 3); // The playhead anchor stayed under the same pixel.
            window.Classes.Remove("motion");
            control.ZoomOut(); Assert.Equal(1.6, control.Zoom, 6); Assert.False(FrameRequested(control));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Plain_wheel_over_an_unzoomed_timeline_reaches_the_page_scroller()
    {
        var control = new WaveformControl { Samples = [.2f, .4f], Duration = TimeSpan.FromSeconds(30) };
        var window = new Window { Width = 600, Height = 140, Content = control }; window.Show(); window.UpdateLayout();
        try
        {
            var reached = 0; window.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => reached++);
            window.MouseWheel(new(100, 80), new Vector(0, -1), RawInputModifiers.None);
            Assert.Equal(1, reached);
            window.MouseWheel(new(100, 80), new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(1, reached); Assert.True(control.Zoom > 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Timeline_keyboard_marks_in_and_out_and_nudges_the_active_handle()
    {
        var control = new WaveformControl { Samples = [.2f, .5f, .3f], StartRatio = 0, EndRatio = 1, Duration = TimeSpan.FromSeconds(10), PlayheadRatio = .25 };
        var window = new Window { Width = 600, Height = 140, Content = control }; window.Show(); window.UpdateLayout();
        try
        {
            var seek = -1d; control.SeekRequested += ratio => seek = ratio;
            control.Focus();
            Press(window, Key.I); Assert.Equal(.25, control.StartRatio, 4);
            Press(window, Key.Right); Assert.Equal(.26, control.StartRatio, 4);                      // 0.1 s
            Press(window, Key.Right, RawInputModifiers.Shift); Assert.Equal(.36, control.StartRatio, 4); // 1 s
            Assert.Equal(-1, seek);
            control.PlayheadRatio = .75; Press(window, Key.O); Assert.Equal(.75, control.EndRatio, 4);
            Press(window, Key.Left); Assert.Equal(.74, control.EndRatio, 4);
            Press(window, Key.Escape); Press(window, Key.Right); Assert.Equal(.76, seek, 4);              // No handle: the playhead moves.
            control.PlayheadRatio = .9; Press(window, Key.I);                                            // In after out resets out to the end.
            Assert.Equal(.9, control.StartRatio, 4); Assert.Equal(1, control.EndRatio, 4);
            Press(window, Key.Home); Assert.Equal(.9, seek, 4);
            // Ctrl+O belongs to the app (open media), not to the timeline.
            control.PlayheadRatio = .95; window.KeyPress(Key.O, RawInputModifiers.Control, PhysicalKey.O, "o"); window.KeyRelease(Key.O, RawInputModifiers.Control, PhysicalKey.O, "o");
            Assert.Equal(1, control.EndRatio, 4);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Playing_refreshes_the_clock_every_frame_and_stops_when_paused()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 900 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); window.UpdateLayout();
            Assert.Equal("0:00", model.PlaybackClockText); Assert.Equal("0:10", model.DurationClockText);
            model.PlayCommand.Execute(null);
            for (var i = 0; i < 6; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            Assert.True(fake.Playback.Ticks >= 4, $"expected a tick per frame while playing, got {fake.Playback.Ticks}");
            model.PlayCommand.Execute(null);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            var paused = fake.Playback.Ticks;
            for (var i = 0; i < 6; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(paused, fake.Playback.Ticks);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Tiles_timecodes_and_keys_drive_the_view_model()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 2300;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); window.UpdateLayout();
            // Cut (Lossless / Exact) lives under Advanced: Exact is the default and right for nearly everyone.
            Assert.False(window.FindNamed<Expander>("AdvancedExpander")!.IsExpanded);
            window.FindNamed<Expander>("AdvancedExpander")!.IsExpanded = true; window.UpdateLayout();
            model.DeleteOriginal = true; window.UpdateLayout(); Assert.True(window.FindNamed<StackPanel>("DeleteOriginalReminder")!.IsVisible); model.DeleteOriginal = false;
            Click(window, window.FindNamed<RadioButton>("LosslessTile")!);
            Assert.Equal(ClipMode.Lossless, model.ClipMode); Assert.False(window.FindNamed<Grid>("ExportConversionControls")!.IsEnabled);
            Assert.Equal("Lossless copy", model.EstimateQualityText);
            Click(window, window.FindNamed<RadioButton>("ExactTile")!);
            Assert.Equal(ClipMode.Exact, model.ClipMode); Assert.True(window.FindNamed<Grid>("ExportConversionControls")!.IsEnabled);
            Click(window, window.FindNamed<RadioButton>("AudioOutputTile")!); Assert.True(model.AudioOnly); Assert.Equal("Export audio clip", model.ExportButtonText);
            model.AudioOnly = false; Assert.True(window.FindNamed<RadioButton>("VideoOutputTile")!.IsChecked); Assert.False(window.FindNamed<RadioButton>("AudioOutputTile")!.IsChecked);

            // Typing a timecode does not move the handles until Enter (or focus loss) commits it.
            var box = window.FindNamed<TextBox>("StartTimeBox")!; var waveform = window.FindNamed<WaveformControl>("Waveform")!;
            box.Focus(); box.Text = "00:00:0"; Assert.Equal("00:00:00.000", model.StartTime); Assert.Equal(0, waveform.StartRatio);
            box.Text = "00:00:02.500"; Press(window, Key.Enter);
            Assert.Equal("00:00:02.500", model.StartTime); Assert.Equal(.25, waveform.StartRatio, 3); Assert.Equal("0:07.5", model.SelectionDurationText);
            Assert.Equal("0:02.5", box.Text);
            // The fields show tenths, but leaving one unchanged keeps the precise time behind it.
            model.StartTime = "00:00:02.537"; Assert.Equal("0:02.5", box.Text);
            box.Focus(); Press(window, Key.Enter); Assert.Equal("00:00:02.537", model.StartTime);
            model.StartTime = "00:00:02.500";
            // Exact without compression has no reliable size: say so instead of showing a number.
            Assert.True(model.HasUnknownSize); Assert.False(model.HasEstimatedSize); Assert.Equal("2 MB", model.EstimateOriginalText);
            window.UpdateLayout();
            Assert.True(window.FindNamed<TextBlock>("EstimateUnknown")!.IsEffectivelyVisible); Assert.False(window.FindNamed<TextBlock>("EstimateOutput")!.IsEffectivelyVisible);
            model.ClipMode = ClipMode.Lossless; Assert.True(model.HasEstimatedSize); Assert.Equal("≈ 1.5 MB", model.EstimateOutputText); model.ClipMode = ClipMode.Exact;

            // I and O work from editor buttons, but never while typing in a field.
            fake.Playback.Seek(.6); box.Focus(); Press(window, Key.O); Assert.Equal("00:00:10.000", model.EndTime);
            window.FindNamed<Button>("PlayButton")!.Focus(); Press(window, Key.O); Assert.Equal("00:00:06.000", model.EndTime);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Mute_silences_playback_and_a_volume_change_unmutes()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("fixture.mp4"); Assert.Equal(80, fake.Playback.LastVolume);
        model.ToggleMuteCommand.Execute(null); Assert.True(model.IsMuted); Assert.True(model.ShowMutedIcon); Assert.Equal(0, fake.Playback.LastVolume);
        await model.OpenEditorAsync("other.mp4"); Assert.True(model.IsMuted); Assert.Equal(0, fake.Playback.LastVolume);
        model.ToggleMuteCommand.Execute(null); Assert.False(model.IsMuted); Assert.Equal(80, fake.Playback.LastVolume);
        model.ToggleMuteCommand.Execute(null); model.Volume = 35; Assert.False(model.IsMuted); Assert.Equal(35, fake.Playback.LastVolume);
        model.Volume = 0; Assert.True(model.ShowMutedIcon); model.ToggleMuteCommand.Execute(null); model.ToggleMuteCommand.Execute(null);
        Assert.False(model.IsMuted); Assert.True(model.Volume > 0); Assert.Equal(model.Volume, fake.Playback.LastVolume);
    }

    [AvaloniaFact]
    public async Task Share_result_from_the_library_stays_visible_without_an_open_file()
    {
        using var media = new TempMedia();
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 1200 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; model.SelectedTab = 2;
            await model.ShareFileAsync(media.Path); window.UpdateLayout();
            Assert.True(model.IsEditor); Assert.False(model.HasEditor); Assert.True(model.ShowShareCard);
            Assert.True(window.FindNamed<Border>("ShareCard")!.IsEffectivelyVisible); Assert.False(window.FindNamed<Border>("ExportCard")!.IsEffectivelyVisible);
            Assert.True(window.FindNamed<TextBox>("ShareLinkBox")!.IsEffectivelyVisible);
            model.Theme = "Dark"; Capture(window, "editor-share-without-file-dark");
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    // Your recordings: a new clip in Videos shows up when the editor is empty, opens with one click, and the card
    // steps aside once something is loaded.
    [AvaloniaFact]
    public async Task New_recordings_are_listed_and_open_in_the_editor()
    {
        var videos = Path.Combine(Path.GetTempPath(), "KeaClipVideos-" + Guid.NewGuid().ToString("N"));
        var clip = Path.Combine(videos, "Captures", "Rift 2026-10-14 11-30-00.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(clip)!); File.WriteAllBytes(clip, new byte[300 * 1024]);
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services) { RecordingsFolder = () => videos };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; model.SelectedTab = 3;
            await Until(() => model.HasRecentRecordings);
            window.UpdateLayout();
            var card = window.FindNamed<Border>("RecordingsCard")!;
            Assert.True(card.IsEffectivelyVisible);
            var item = Assert.Single(model.RecentRecordings);
            Assert.Equal("Rift 2026-10-14 11-30-00", item.Title);
            Assert.StartsWith("Xbox Game Bar · ", item.Detail); Assert.Contains(" KB · Today ", item.Detail);
            await item.EditCommand.ExecuteAsync(null);
            Assert.True(model.HasEditor); Assert.Equal(clip, model.EditorPath);
            window.UpdateLayout();
            Assert.False(model.HasRecentRecordings); Assert.False(card.IsEffectivelyVisible);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); try { Directory.Delete(videos, true); } catch (IOException) { } }
    }
    // Export as GIF: the selection goes out as a GIF (never touching the original), lands in history, and a selection
    // over a minute is refused before the save dialog.
    [AvaloniaFact]
    public async Task Export_as_gif_sends_the_selection_and_refuses_more_than_a_minute()
    {
        var fake = new EditorFakes(); fake.Media.Duration = TimeSpan.FromSeconds(90);
        var output = Path.Combine(Path.GetTempPath(), "keaclip-gif-" + Guid.NewGuid().ToString("N") + ".gif");
        fake.Platform.SavePath = output;
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 1700;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); model.SelectedTab = 3; window.UpdateLayout();
            var button = window.FindNamed<Button>("ExportGifButton")!;
            Assert.True(button.IsEffectivelyVisible); Assert.True(model.CanExportGif);
            model.StartTime = "00:00:10"; model.EndTime = "00:00:14.5"; window.UpdateLayout();
            // The button sits low in the Export card; scroll to it as a person would.
            button.BringIntoView(); window.UpdateLayout();
            Click(window, button);
            await Until(() => { Dispatcher.UIThread.RunJobs(); return fake.Media.Exports.Count == 1 && !model.IsExporting; });
            var options = Assert.Single(fake.Media.Exports);
            Assert.True(options.Gif); Assert.Equal(output, options.OutputPath);
            Assert.Equal(TimeSpan.FromSeconds(10), options.Start); Assert.Equal(TimeSpan.FromSeconds(14.5), options.End);
            Assert.False(options.DeleteOriginalAfterSuccess);
            Assert.StartsWith("GIF ready: ", model.Status);
            Assert.Contains(fake.Store.History, entry => entry.Mode == "GIF" && entry.FilePath == output);

            model.StartTime = "00:00:00"; model.EndTime = "00:01:15"; window.UpdateLayout();
            await model.ExportGifAsync();
            Assert.Single(fake.Media.Exports);
            Assert.Contains("60 seconds", model.Status);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }
    // Turned off in Settings: the list empties at once, the choice is saved, and the folder is not read again.
    [AvaloniaFact]
    public async Task Turning_recordings_off_empties_the_list_and_stops_reading_videos()
    {
        var videos = Path.Combine(Path.GetTempPath(), "KeaClipVideos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(videos); File.WriteAllBytes(Path.Combine(videos, "clip.mp4"), new byte[300 * 1024]);
        var fake = new EditorFakes(); var reads = 0;
        using var model = new MainViewModel(fake.Services) { RecordingsFolder = () => { reads++; return videos; } };
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 3; await model.RefreshRecentRecordingsAsync();
            Assert.True(model.HasRecentRecordings);
            model.ShowRecentRecordings = false;
            await Until(() => !model.HasRecentRecordings);
            await model.SettingsPreferenceSaveTask; Assert.False(fake.Store.Settings.ShowRecentRecordings);
            var before = reads;
            await model.RefreshRecentRecordingsAsync();
            Assert.Empty(model.RecentRecordings);
            Assert.True(reads <= before + 1);
        }
        finally { try { Directory.Delete(videos, true); } catch (IOException) { } }
    }

    [AvaloniaFact]
    public async Task Without_a_supplied_videos_folder_nothing_is_scanned()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync(); model.SelectedTab = 3;
        Assert.Equal("", model.RecordingsFolder());
        await model.RefreshRecentRecordingsAsync();
        Assert.False(model.HasRecentRecordings);
    }

    // Fit for: one click aims an exact cut just under a sharing limit and lights up; clicking it again turns it off.
    [AvaloniaFact]
    public async Task Fit_for_discord_targets_just_under_ten_megabytes_and_toggles_off()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 1600;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); model.SelectedTab = 3; window.UpdateLayout();
            model.ClipMode = ClipMode.Lossless; window.UpdateLayout();
            var discord = window.FindNamed<Button>("FitDiscordButton")!; var nitro = window.FindNamed<Button>("FitNitroButton")!;
            Assert.DoesNotContain("selected", discord.Classes);
            Click(window, discord);
            Assert.Equal("10 MB", model.AttachmentPreset); Assert.Equal("9.5", model.TargetSizeMb);
            Assert.Equal(ClipMode.Exact, model.ClipMode); Assert.Equal(CompressionPreset.TargetSize, model.Compression);
            Assert.Equal(10, model.FitTargetMb); window.UpdateLayout();
            Assert.Contains("selected", discord.Classes); Assert.DoesNotContain("selected", nitro.Classes);
            Click(window, nitro);
            Assert.Equal("500 MB", model.AttachmentPreset); Assert.Equal("475", model.TargetSizeMb);
            Assert.DoesNotContain("selected", discord.Classes); Assert.Contains("selected", nitro.Classes);
            Click(window, nitro);
            Assert.Equal(0, model.FitTargetMb); Assert.Equal(CompressionPreset.Off, model.Compression); Assert.DoesNotContain("selected", nitro.Classes);
            Assert.Equal("Fit for Discord, 10 MB", AutomationProperties.GetName(discord));
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }
    [AvaloniaFact]
    public async Task Idle_editor_does_not_request_animation_frames()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 1400;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); model.SelectedTab = 3; window.UpdateLayout();
            var waveform = window.FindNamed<WaveformControl>("Waveform")!;
            Assert.False(model.IsPlaying); Assert.False(model.AnimateEditorLoading); Assert.False(model.ExportProgressIndeterminate); Assert.False(model.ShareProgressIndeterminate);
            Assert.False(FrameRequested(waveform));
            model.PlayCommand.Execute(null); Assert.True(model.IsPlaying); Assert.True(FrameRequested(waveform));
            model.PlayCommand.Execute(null); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.False(model.IsPlaying); Assert.False(FrameRequested(waveform));
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Exporting_with_delete_original_keeps_the_result_and_share_actions()
    {
        using var export = new TempMedia();
        var fake = new EditorFakes(); fake.Platform.SavePath = export.Path; fake.Media.DeleteOriginal = true;
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 1500;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4");
            model.DeleteOriginal = true;
            await model.ExportAsync();
            await Until(() => { Dispatcher.UIThread.RunJobs(); return model.HasLastExport; });
            Assert.False(model.HasEditor); Assert.True(model.ShowOriginalDeletedNote); Assert.True(model.ShowShareCard); Assert.False(model.ShowShareOriginalInstead);
            window.UpdateLayout();
            Assert.True(window.FindNamed<Border>("ResultCard")!.IsEffectivelyVisible);
            Assert.True(window.FindNamed<Button>("ShareExportButton")!.IsEffectivelyVisible);
            Assert.True(window.FindNamed<Button>("EditLastExportButton")!.IsEffectivelyVisible);
            Assert.True(window.FindNamed<Border>("EmptyState")!.IsEffectivelyVisible);
            // The finished clip is the news, so it sits above the drop zone.
            Assert.Equal(0, Grid.GetRow(window.FindNamed<StackPanel>("OutputColumn")!)); Assert.Equal(1, Grid.GetRow(window.FindNamed<StackPanel>("StartColumn")!));
            Assert.Equal(18, window.FindNamed<Grid>("EditorLayout")!.RowSpacing);
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-after-delete-original-{theme.ToLowerInvariant()}"); }
            window.Width = 1400; window.Height = 1000; model.Theme = "Dark"; Capture(window, "editor-after-delete-original-wide-dark");
            Assert.Equal(1, Grid.GetColumn(window.FindNamed<StackPanel>("OutputColumn")!));

            await model.EditLastExportCommand.ExecuteAsync(null);
            Assert.True(model.HasEditor); Assert.Equal(export.Path, model.EditorPath);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Opening_another_file_resets_the_timeline_view()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 1400;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("first.mp4"); window.UpdateLayout();
            var waveform = window.FindNamed<WaveformControl>("Waveform")!;
            waveform.ZoomIn(); waveform.ZoomIn(); Assert.True(waveform.Zoom > 2);
            await model.OpenEditorAsync("second.mp4"); // The same length as the first file.
            Assert.Equal(1, waveform.Zoom); Assert.False(waveform.CanZoomOut);
            waveform.ZoomIn(); Assert.True(waveform.Zoom > 1);
            await model.OpenEditorAsync("second.mp4"); // The same file again.
            Assert.Equal(1, waveform.Zoom);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public void Timeline_is_an_accessible_slider_that_seeks()
    {
        var control = new WaveformControl { Samples = [.2f, .5f, .3f], StartRatio = .2, EndRatio = .6, Duration = TimeSpan.FromSeconds(50), PlayheadRatio = .5 };
        var window = new Window { Width = 600, Height = 140, Content = control }; window.Show(); window.UpdateLayout();
        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(control);
            Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
            var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer);
            Assert.Equal(50, range.Maximum); Assert.Equal(25, range.Value, 3); Assert.Equal(.1, range.SmallChange, 6);
            Assert.Contains("0:10.00", peer.GetHelpText()); Assert.Contains("0:30.00", peer.GetHelpText());
            var seek = -1d; control.SeekRequested += ratio => seek = ratio;
            range.SetValue(40); Assert.Equal(.8, seek, 6);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Zoomed_overview_strip_pans_and_a_grip_dragged_to_the_edge_auto_pans()
    {
        var control = new WaveformControl { Samples = Enumerable.Range(0, 600).Select(i => (float)Math.Abs(Math.Sin(i / 9d))).ToArray(), StartRatio = .1, EndRatio = .9, Duration = TimeSpan.FromSeconds(60), PlayheadRatio = .1 };
        var window = new Window { Width = 800, Height = 140, Content = control }; window.Show(); window.UpdateLayout();
        try
        {
            control.ZoomIn(); control.ZoomIn();
            var zoom = control.Zoom; var span = 1 / zoom; Assert.Equal(2.56, zoom, 6);
            var width = control.Bounds.Width; var height = control.Bounds.Height; var seek = -1d; control.SeekRequested += ratio => seek = ratio;

            // Dragging the start grip into the right edge zone keeps panning towards it, frame by frame.
            var viewStart = .1 - .1 / zoom; var startX = (.1 - viewStart) * zoom * width;
            window.MouseDown(new(startX + 2, 80), MouseButton.Left); window.MouseMove(new(width - 4, 80));
            for (var frame = 0; frame < 12; frame++) { Thread.Sleep(20); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            window.MouseUp(new(width - 4, 80), MouseButton.Left);
            // Without auto-pan the grip could not pass the end of the initially visible window.
            Assert.True(control.StartRatio > viewStart + span + .02, $"auto-pan moved the grip to {control.StartRatio}; the view first ended at {viewStart + span}");
            Assert.Equal(-1, seek); Assert.Equal(zoom, control.Zoom, 6);

            // Clicking the overview strip centres the view there instead of moving the playhead.
            window.MouseDown(new(width * .05, height - 4), MouseButton.Left); window.MouseUp(new(width * .05, height - 4), MouseButton.Left);
            Assert.Equal(-1, seek);
            window.MouseDown(new(0, 80), MouseButton.Left); window.MouseUp(new(0, 80), MouseButton.Left);
            Assert.Equal(0, seek, 6); // The view is back at the start of the file.
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Page_shortcuts_follow_the_timeline_and_leave_text_entry_alone()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 1400;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); window.UpdateLayout();
            var waveform = window.FindNamed<WaveformControl>("Waveform")!;
            // After clicking a zoom button, + and − keep working.
            Click(window, window.FindNamed<Button>("ZoomInButton")!); var zoom = waveform.Zoom; Assert.True(zoom > 1);
            Press(window, Key.OemPlus); Assert.True(waveform.Zoom > zoom);
            Press(window, Key.OemMinus); Assert.Equal(zoom, waveform.Zoom, 6);
            waveform.ResetZoom();

            // Left/Right from a focused button step 0.1 s (Shift: 1 s), like the focused timeline, not 1% of the file.
            model.Seek(.5); window.FindNamed<Button>("PlayButton")!.Focus();
            Press(window, Key.Right); Assert.Equal(.51, model.PlaybackPosition, 6);
            Press(window, Key.Left, RawInputModifiers.Shift); Assert.Equal(.41, model.PlaybackPosition, 6);
            // Text fields keep their own keys.
            window.FindNamed<TextBox>("StartTimeBox")!.Focus(); Press(window, Key.Right); Press(window, Key.OemPlus);
            Assert.Equal(.41, model.PlaybackPosition, 6); Assert.Equal(1, waveform.Zoom);

            // Set-at-playhead buttons use the drawn playhead and the timeline's crossing rule.
            model.Seek(.8); model.EndTime = "00:00:06.000";
            Click(window, window.FindNamed<Button>("MarkStartButton")!);
            Assert.Equal("00:00:08.000", model.StartTime); Assert.Equal("00:00:10.000", model.EndTime); Assert.True(model.IsSelectionValid);
            model.Seek(.3); window.FindNamed<Button>("PlayButton")!.Focus(); Press(window, Key.O);
            Assert.Equal("00:00:00.000", model.StartTime); Assert.Equal("00:00:03.000", model.EndTime);

            // Audio has no picture, so F11 does not open an empty fullscreen window.
            fake.Media.HasVideo = false; await model.OpenEditorAsync("song.m4a"); window.UpdateLayout();
            window.FindNamed<Button>("PlayButton")!.Focus(); Press(window, Key.F11);
            Assert.False(model.PreviewFullscreen);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Export_needs_a_valid_range_and_the_track_it_keeps()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show(); window.Height = 2300;
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); window.UpdateLayout();
            var export = window.FindNamed<Button>("ExportButton")!;
            Assert.True(model.CanExport); Assert.True(export.IsEffectivelyEnabled); Assert.Contains("primary", export.Classes);
            Assert.Equal("Export video clip", Avalonia.Automation.AutomationProperties.GetName(export));
            model.StartTime = "00:00:08.000"; model.EndTime = "00:00:02.000"; window.UpdateLayout();
            Assert.False(model.CanExport); Assert.False(export.IsEffectivelyEnabled);
            Assert.True(window.FindNamed<Border>("EstimateProblem")!.IsEffectivelyVisible); Assert.False(window.FindNamed<Border>("EstimateCard")!.IsEffectivelyVisible);
            model.EndTime = "00:00:09.000"; window.UpdateLayout(); Assert.True(model.CanExport); Assert.True(export.IsEffectivelyEnabled);

            // A video without sound cannot make an audio clip; the disabled tile says why.
            fake.Media.HasAudio = false; await model.OpenEditorAsync("silent.mp4"); window.UpdateLayout();
            var audioTile = window.FindNamed<RadioButton>("AudioOutputTile")!;
            Assert.False(audioTile.IsEnabled); Assert.Contains("no audio track", ToolTip.GetTip(audioTile) as string);
            Assert.Null(ToolTip.GetTip(window.FindNamed<RadioButton>("VideoOutputTile")!));
            Assert.False(window.FindNamed<Button>("SoundboardShortcut")!.IsVisible);
            model.AudioOnly = true; Assert.False(model.CanExport);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Messaging_preset_never_shows_a_stale_size()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync(); await model.OpenEditorAsync("fixture.mp4");
        model.AttachmentPreset = "25 MB"; Assert.Equal(CompressionPreset.TargetSize, model.Compression); Assert.Equal("23.75", model.TargetSizeMb);
        model.Compression = CompressionPreset.Off; Assert.Equal("Custom target", model.AttachmentPreset);
        model.AttachmentPreset = "25 MB"; Assert.Equal(CompressionPreset.TargetSize, model.Compression); // Picking it again applies it again.
        model.TargetSizeMb = "12"; Assert.Equal("Custom target", model.AttachmentPreset); Assert.Equal(CompressionPreset.TargetSize, model.Compression);
        model.AttachmentPreset = "10 MB"; model.ClipMode = ClipMode.Lossless; Assert.Equal("Custom target", model.AttachmentPreset);
        Assert.Equal("No preset", new EditorPresetLabelConverter().Convert("Custom target", typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("25 MB", new EditorPresetLabelConverter().Convert("25 MB", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public async Task Portrait_and_rotated_videos_get_a_matching_preview_shape()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        fake.Media.Width = 1080; fake.Media.Height = 1920; await model.OpenEditorAsync("phone.mp4");
        Assert.Equal(9d / 16d, model.PreviewAspect, 6);
        fake.Media.Width = 1920; fake.Media.Height = 1080; fake.Media.TechnicalJson = """{"streams":[{"codec_type":"video","side_data_list":[{"rotation":-90}]}]}""";
        await model.OpenEditorAsync("rotated.mp4");
        Assert.Equal(9d / 16d, model.PreviewAspect, 6);
        fake.Media.TechnicalJson = """{"streams":[{"codec_type":"video","tags":{"rotate":"180"}}]}"""; await model.OpenEditorAsync("upside-down.mp4");
        Assert.Equal(16d / 9d, model.PreviewAspect, 6);
        fake.Media.TechnicalJson = "not json"; fake.Media.Width = 4000; fake.Media.Height = 1000; await model.OpenEditorAsync("ultrawide.mp4");
        Assert.Equal(21d / 9d, model.PreviewAspect, 6); // Clamped so extreme shapes stay usable.
        fake.Media.HasVideo = false; await model.OpenEditorAsync("song.m4a"); Assert.Equal(2.75, model.PreviewAspect, 6);
    }

    [AvaloniaFact]
    public async Task Status_messages_during_an_open_do_not_hide_the_loading_state()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; model.SelectedTab = 3;
            fake.Media.ProbeGate = new(); var opening = model.OpenEditorAsync("fixture.mp4");
            Assert.True(model.IsOpeningEditor);
            model.Status = "Link copied. Paste it where you like.";
            Assert.True(model.IsOpeningEditor);
            fake.Media.ProbeGate.SetResult(); await opening;
            Assert.False(model.IsEditorLoading); Assert.False(model.IsOpeningEditor); Assert.False(model.IsWaveformLoading);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task A_preview_error_explains_itself_instead_of_a_black_frame()
    {
        var fake = new EditorFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = true; await model.OpenEditorAsync("fixture.mp4"); window.UpdateLayout();
            Assert.True(model.ShowPreviewSurface); Assert.False(window.FindNamed<Border>("PreviewErrorPanel")!.IsVisible);
            fake.Playback.LastError = "Preview could not play this file.";
            Assert.True(model.HasPreviewError); Assert.False(model.ShowPreviewSurface); Assert.Contains("Trimming and export still work", model.Status);
            window.UpdateLayout();
            Assert.True(window.FindNamed<Border>("PreviewErrorPanel")!.IsEffectivelyVisible); Assert.False(window.FindNamed<Border>("InlinePreviewHost")!.IsVisible);
            Assert.IsType<LibVLCSharp.Avalonia.VideoView>(window.FindNamed<Border>("InlinePreviewHost")!.Child);
            foreach (var theme in Themes) { model.Theme = theme; Capture(window, $"editor-preview-error-{theme.ToLowerInvariant()}"); }
            fake.Playback.LastError = null; Assert.True(model.ShowPreviewSurface);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Long_files_get_their_detailed_waveform_from_the_one_decode_of_the_open()
    {
        // Review finding 21: the open asked for a 600-point overview and then decoded the whole
        // file again for the detail ([600, 3600]); one decode serves any point count, so the open
        // asks for the detail straight away. The background pass that yields to other work is
        // covered in ReviewFixEditorTests (it still retries when the open's waveform failed).
        var fake = new EditorFakes(); fake.Media.Duration = TimeSpan.FromMinutes(5);
        using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("long.mp4");
        Assert.Equal(3600, model.Waveform.Count); Assert.Equal([3600], fake.Media.WaveformRequests); // About 12 points per second.

        await model.ExportAsync(); // Already detailed: nothing to fetch again.
        Assert.Single(fake.Media.WaveformRequests);
    }

    [AvaloniaFact]
    public async Task Library_search_does_not_rescan_recent_files()
    {
        using var media = new TempMedia();
        var fake = new EditorFakes(); fake.Store.History.Add(new("recent", "Weekend highlights.mp4", null, "Local", DateTimeOffset.UtcNow, media.Path, "VideoAudio", "Best available", 48_000_000, "Finished"));
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 3;
            await Until(() => { Dispatcher.UIThread.RunJobs(); return model.HasRecentMedia; });
            var recent = model.RecentMedia;
            model.HistorySearch = "week"; model.HistorySearch = "zzz"; model.HistorySearch = "";
            await Task.Delay(60); Dispatcher.UIThread.RunJobs();
            Assert.Same(recent, model.RecentMedia);
            Assert.Equal("Edit Weekend highlights.mp4", Assert.Single(recent).EditLabel);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    // ===== Native playback (opt-in: KEACLIP_NATIVE_PLAYBACK_TESTS=1 and ffmpeg on PATH) =====

    // The trimmed VLC (KeaClip leaves out network, streaming-out, encoder and subtitle plugins) must still decode every
    // common local format: each fixture has to reach Playing with decoded frames and sound, not merely open.
    [AvaloniaTheory]
    [InlineData("h264.mp4", "-c:v libx264 -pix_fmt yuv420p -c:a aac", true)]
    [InlineData("hevc.mkv", "-c:v libx265 -pix_fmt yuv420p -c:a libopus", true)]
    [InlineData("vp9.webm", "-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -c:a libvorbis", true)]
    [InlineData("av1.webm", "-c:v libaom-av1 -cpu-used 8 -row-mt 1 -pix_fmt yuv420p -c:a libopus", true)]
    [InlineData("mpeg4.avi", "-c:v mpeg4 -c:a libmp3lame", true)]
    [InlineData("song.mp3", "-vn -c:a libmp3lame", false)]
    [InlineData("song.m4a", "-vn -c:a aac", false)]
    [InlineData("song.flac", "-vn -c:a flac", false)]
    [InlineData("song.wav", "-vn -c:a pcm_s16le", false)]
    [InlineData("song.ogg", "-vn -c:a libvorbis", false)]
    [InlineData("song.opus", "-vn -c:a libopus", false)]
    public async Task Native_preview_decodes_common_formats_with_the_trimmed_vlc(string name, string codecs, bool video)
    {
        var ffmpeg = NativeFixtureTool();
        if (Environment.GetEnvironmentVariable("KEACLIP_NATIVE_PLAYBACK_TESTS") != "1" || ffmpeg is null) Assert.Skip("Set KEACLIP_NATIVE_PLAYBACK_TESTS=1 with ffmpeg on PATH to drive real LibVLC playback.");
        var folder = Path.Combine(Path.GetTempPath(), "keaclip-formats-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        try
        {
            string[] inputs = video ? ["-f", "lavfi", "-i", "testsrc=size=320x180:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"] : ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"];
            using (var generate = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg!, ["-hide_banner", "-loglevel", "error", "-y", .. inputs, "-t", "4", .. codecs.Split(' '), "-shortest", path]) { UseShellExecute = false })!)
                await generate.WaitForExitAsync();
            Assert.True(File.Exists(path), "ffmpeg did not create " + name);

            using var service = new VlcPlaybackService("--vout=dummy", "--aout=dummy", "--avcodec-hw=none");
            service.Open(path);
            var player = service.NativePlayer!;
            service.Toggle();
            await NativeUntil(() => player.State == VLCState.Playing && player.Time > 1200, () => $"{name}: state {player.State}, time {player.Time}, error {service.LastError}");
            var stats = player.Media!.Statistics;
            Assert.True(stats.DecodedAudio > 0, $"{name}: no audio decoded");
            if (video) Assert.True(stats.DecodedVideo > 0, $"{name}: no video decoded");
            Assert.Null(service.LastError);
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    [AvaloniaFact]
    public async Task Native_preview_loads_paused_and_applies_seeks_before_the_first_play()
    {
        var ffmpeg = NativeFixtureTool();
        if (Environment.GetEnvironmentVariable("KEACLIP_NATIVE_PLAYBACK_TESTS") != "1" || ffmpeg is null) Assert.Skip("Set KEACLIP_NATIVE_PLAYBACK_TESTS=1 with ffmpeg on PATH to drive real LibVLC playback.");
        var folder = Path.Combine(Path.GetTempPath(), "keaclip-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fixture.mp4");
        try
        {
            using (var generate = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg!, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path]) { UseShellExecute = false })!)
                await generate.WaitForExitAsync();
            Assert.True(File.Exists(path), "ffmpeg did not create the fixture");

            using var service = new VlcPlaybackService("--vout=dummy", "--aout=dummy", "--avcodec-hw=none");
            service.Open(path);
            var player = service.NativePlayer!;
            // A seek before the input is ready is shown at once and applied once VLC has opened the file.
            service.Seek(.5);
            Assert.Equal(.5, service.Position, 6); Assert.False(service.IsPlaying);
            await NativeUntil(() => player.State == VLCState.Paused && Math.Abs(player.Time - 3000) < 400, () => $"state {player.State}, time {player.Time}");
            Assert.False(service.IsPlaying); Assert.Null(service.LastError);

            service.Toggle();
            await NativeUntil(() => player.State == VLCState.Playing && player.Time > 3300, () => $"state {player.State}, time {player.Time}");
            Assert.True(service.IsPlaying);
            service.Toggle();
            await NativeUntil(() => player.State == VLCState.Paused, () => $"state {player.State}");
            Assert.False(service.IsPlaying);

            // Seeking while paused moves the player too (marking at the playhead depends on it).
            service.Seek(.25);
            await NativeUntil(() => Math.Abs(player.Time - 1500) < 400, () => $"time {player.Time}");

            // Play to the end: the UI leaves the playing state, and a later seek restarts the input paused there.
            service.Seek(.92); service.Toggle();
            await NativeUntil(() => !service.IsPlaying && player.State is VLCState.Ended or VLCState.Stopped, () => $"state {player.State}, playing {service.IsPlaying}");
            service.Seek(.5);
            await NativeUntil(() => player.State == VLCState.Paused && Math.Abs(player.Time - 3000) < 400, () => $"state {player.State}, time {player.Time}");
            Assert.False(service.IsPlaying); Assert.Null(service.LastError);
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string? NativeFixtureTool()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task NativeUntil(Func<bool> condition, Func<string> describe)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("Timed out waiting for native playback: " + describe());
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    // ===== Helpers =====

    private static readonly string[] Themes = ["Dark", "Light"];

    private static bool FrameRequested(WaveformControl control) => (bool)typeof(WaveformControl).GetField("_frameRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null); window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    // Colour fades (tile and button brushes) run even with reduced motion, so let them finish
    // before capturing; otherwise evidence can show a half-faded state that users never see settle.
    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); if (pass < 2) Thread.Sleep(120); }
    }

    private static void Capture(Window window, string name)
    {
        Settle(window);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TempMedia : IDisposable
    {
        public TempMedia() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keaclip-editor-" + Guid.NewGuid().ToString("N") + ".mp4"); File.WriteAllText(Path, "owned test fixture"); }
        public string Path { get; }
        public void Dispose() { try { File.Delete(Path); } catch (IOException) { } }
    }

    // ===== Fakes (nested so they cannot collide with other test files) =====

    private sealed class EditorFakes
    {
        public FakeDownloads Downloads { get; } = new();
        public GatedMedia Media { get; } = new();
        public GatedSharing Sharing { get; } = new();
        public FakeStore Store { get; } = new();
        public RecordingPlatform Platform { get; } = new();
        public RecordingPlayback Playback { get; } = new();
        public DesktopServices Services => new(Downloads, Media, Sharing, Store, new FakeComponents(), Platform, Playback, "test-owned-data");
    }

    private sealed class GatedMedia : IMediaService
    {
        public bool HasVideo { get; set; } = true;
        public bool HasAudio { get; set; } = true;
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public string? TechnicalJson { get; set; }
        public TaskCompletionSource? ProbeGate { get; set; }
        public TaskCompletionSource? WaveformGate { get; set; }
        /// <summary>Holds only the detailed (second) waveform pass, so the overview loads normally.</summary>
        public TaskCompletionSource? DetailGate { get; set; }
        public TaskCompletionSource? ExportGate { get; set; }
        public bool DeleteOriginal { get; set; }
        public List<ExportOptions> Exports { get; } = [];
        public List<int> WaveformRequests { get; } = [];
        public int CancelledWaveforms { get; private set; }
        public async Task<ProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            if (ProbeGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return new ProbeInfo(path, Duration, 2_000_000, HasVideo, HasAudio, HasVideo ? Width : null, HasVideo ? Height : null, HasVideo ? 30 : null, HasVideo ? "h264" : null, HasAudio ? "aac" : null, HasAudio ? 48000 : null, HasAudio ? 2 : null, TechnicalJson);
        }
        public async Task<IReadOnlyList<float>> WaveformAsync(string path, int points = 600, CancellationToken cancellationToken = default)
        {
            WaveformRequests.Add(points);
            try
            {
                if (WaveformGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
                if (points > 600 && DetailGate is { } detail) await detail.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { CancelledWaveforms++; throw; }
            return Enumerable.Range(0, points).Select(i => (float)(Math.Abs(Math.Sin(i / 11d)) * (.35 + .65 * Math.Abs(Math.Sin(i / 67d))))).ToArray();
        }
        public async Task<ExportResult> ExportAsync(ExportOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Exports.Add(options); progress?.Report(new(JobStage.Converting, 42, "Encoding"));
            if (ExportGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return new(options.OutputPath, await ProbeAsync(options.OutputPath, cancellationToken) with { SizeBytes = 640_000 }, DeleteOriginal && options.DeleteOriginalAfterSuccess);
        }
        public Task<IReadOnlyList<EncoderInfo>> EncodersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EncoderInfo>>([new("libx264", "CPU H.264", false), new("h264_amf", "AMD AMF H.264", true), new("hevc_amf", "AMD AMF HEVC", true), new("libsvtav1", "CPU AV1", false)]);
        // Mirrors MediaService.Estimate's branches: a size target, a lossless share of the source,
        // a lossy audio bitrate, otherwise no reliable size until the export runs.
        public ExportEstimate Estimate(ProbeInfo probe, ExportOptions options)
        {
            var start = options.Start ?? TimeSpan.Zero; var end = options.End ?? probe.Duration;
            if (start < TimeSpan.Zero || end <= start || end > probe.Duration) throw new MediaOperationException("Choose a start/end inside the media duration.");
            var duration = end - start;
            var target = options.Compression == CompressionPreset.TargetSize ? options.TargetBytes : null;
            if (options.DiscordSoundboard) target = Math.Min(target ?? long.MaxValue, options.SoundboardTargetBytes);
            if (target is { } bytes) return new(duration, bytes, options.AudioOnly ? null : 900, "Target is an estimate; the actual exported size is checked before completion.");
            if (options.ClipMode == ClipMode.Lossless) return new(duration, (long)Math.Ceiling(probe.SizeBytes * duration.TotalSeconds / probe.Duration.TotalSeconds), null, "Estimated from the source average bitrate.");
            if (options.AudioOnly && options.AudioFormat is AudioFormat.Mp3 or AudioFormat.Aac or AudioFormat.Opus or AudioFormat.Ogg) return new(duration, (long)Math.Ceiling(duration.TotalSeconds * options.AudioKbps * 1000 / 8 * 1.03), null, "Estimated audio size.");
            return new(duration, null, null, "");
        }
    }

    private sealed class GatedSharing : ISharingService
    {
        public TaskCompletionSource? Gate { get; set; }
        public string? LastPath { get; private set; }
        public async Task<ShareResult> ShareAsync(string path, string? title = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LastPath = path; progress?.Report(new(JobStage.Uploading, 63, "Uploading"));
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return new("https://file.kiwi/example#decryption-secret", "folder", "file", 24, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), "checksum", null);
        }
    }

    private sealed class RecordingPlatform : IDesktopPlatform
    {
        public string? SavePath { get; set; }
        public string? Clipboard { get; private set; }
        public List<string> OpenedFiles { get; } = [];
        public List<string> RevealedFiles { get; } = [];
        public void Attach(Window window) { }
        public Task<string?> PickMediaAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveMediaAsync(string name, string extension) => Task.FromResult(SavePath);
        public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(null);
        public Task CopyAsync(string value) { Clipboard = value; return Task.CompletedTask; }
        public void OpenFile(string path) => OpenedFiles.Add(path);
        public void RevealFile(string path) => RevealedFiles.Add(path);
        public void OpenFolder(string path) { }
        public void OpenUri(Uri uri) { }
        public void SetStartup(bool enabled) { }
    }

    private sealed class RecordingPlayback : IPlaybackService
    {
        public int Ticks { get; private set; }
        public void Tick() => Ticks++;
        private string? _lastError;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string? LastError { get => _lastError; set { _lastError = value; PropertyChanged?.Invoke(this, new(nameof(LastError))); } }
        public MediaPlayer? NativePlayer => null;
        public bool IsPlaying { get; private set; }
        public double Position { get; private set; }
        public TimeSpan Time => TimeSpan.FromSeconds(Position * 10);
        public int LastVolume { get; private set; } = -1;
        public void Open(string path) { Position = 0; }
        public void Toggle() { IsPlaying = !IsPlaying; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); }
        public void Seek(double position) { Position = position; PropertyChanged?.Invoke(this, new(nameof(Position))); }
        public void SetVolume(int value) => LastVolume = value;
        public void Stop() { IsPlaying = false; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); }
        public void Close() { IsPlaying = false; }
        public void Dispose() { }
    }
}
