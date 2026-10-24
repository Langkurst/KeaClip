using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// 0.5: links that point to a moment, quick "keep the ending" picks for game replays, and Make it louder up front.
public sealed class QuickClipTests
{
    private static MediaInfo LongVideo => UiFakes.Info with { Duration = TimeSpan.FromMinutes(10) };

    [AvaloniaFact]
    public async Task Analysing_a_link_to_a_moment_sets_just_part_around_it_and_says_why()
    {
        var fake = new UiFakes(); fake.Downloads.Info = LongVideo;
        using var model = new MainViewModel(fake.Services) { Url = "https://www.youtube.com/watch?v=kea&t=1m30s" };
        await model.InitializeAsync();
        Assert.Contains("points to 1:30", model.UrlHint); // before Analyse: what it will do
        await model.AnalyseCommand.ExecuteAsync(null);
        Assert.True(model.SavePart);
        Assert.Equal("1:15", model.PartStart); Assert.Equal("2:00", model.PartEnd);
        Assert.Contains("Set around 1:30", model.PartSummary);
        Assert.Equal("", model.UrlHint);

        await model.QueueDownloadAsync(false);
        await WaitUntil(() => fake.Downloads.Requests.Count > 0);
        var request = Assert.Single(fake.Downloads.Requests);
        Assert.Equal(TimeSpan.FromSeconds(75), request.SectionStart); Assert.Equal(TimeSpan.FromSeconds(120), request.SectionEnd);
    }

    [AvaloniaFact]
    public async Task A_moment_link_sent_straight_to_Download_still_saves_the_whole_video()
    {
        var fake = new UiFakes(); fake.Downloads.Info = LongVideo;
        using var model = new MainViewModel(fake.Services) { Url = "https://www.youtube.com/watch?v=kea&t=1m30s" };
        await model.InitializeAsync();
        await model.QueueDownloadAsync(false);
        await WaitUntil(() => fake.Downloads.Requests.Count > 0);
        Assert.False(Assert.Single(fake.Downloads.Requests).HasSection);
    }

    [AvaloniaFact]
    public async Task A_moment_near_the_start_or_end_stays_inside_the_video()
    {
        var fake = new UiFakes(); fake.Downloads.Info = UiFakes.Info with { Duration = TimeSpan.FromSeconds(40) };
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/v?t=5" };
        await model.InitializeAsync(); await model.AnalyseCommand.ExecuteAsync(null);
        Assert.Equal("0:00", model.PartStart); Assert.Equal("0:35", model.PartEnd);
        model.Url = "https://example.com/v?t=30"; await model.AnalyseCommand.ExecuteAsync(null);
        Assert.Equal("0:15", model.PartStart); Assert.Equal("0:40", model.PartEnd);
        model.Url = "https://example.com/v?t=5m"; await model.AnalyseCommand.ExecuteAsync(null); // past the end: ignored
        Assert.False(model.SavePart);
    }

    [AvaloniaFact]
    public async Task Quick_picks_keep_the_end_of_a_replay_and_make_it_louder_is_one_switch()
    {
        var fake = new UiFakes(); fake.Media.Duration = TimeSpan.FromMinutes(2);
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 900 }; window.Show();
        try
        {
            await model.InitializeAsync(); await model.OpenEditorAsync("replay.mp4"); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindNamed<WrapPanel>("QuickPicks")!.IsEffectivelyVisible);
            model.KeepLastCommand.Execute("30");
            Assert.Equal("00:01:30.000", model.StartTime); Assert.Equal("00:02:00.000", model.EndTime);
            model.KeepWholeCommand.Execute(null);
            Assert.Equal("00:00:00.000", model.StartTime);

            var louder = window.FindNamed<ToggleSwitch>("LouderSwitch")!;
            Assert.True(louder.IsEffectivelyVisible);
            louder.IsChecked = true; Assert.True(model.Normalize); // the same setting as Audio clip, Even out loudness
            if (Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY") is { Length: > 0 } output)
            {
                model.Theme = "Dark"; model.KeepLastCommand.Execute("30");
                window.FindNamed<WrapPanel>("QuickPicks")!.BringIntoView(); Dispatcher.UIThread.RunJobs(); await Task.Delay(300); Dispatcher.UIThread.RunJobs();
                using (var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)) frame!.Save(Path.Combine(output, "quick-picks-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                louder.BringIntoView(); Dispatcher.UIThread.RunJobs(); await Task.Delay(300); Dispatcher.UIThread.RunJobs();
                using (var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)) frame!.Save(Path.Combine(output, "louder-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Vertical_shape_shows_the_kept_window_on_a_still_frame_and_reaches_the_export()
    {
        var fake = new UiFakes(); fake.Media.Frame = Frame(480, 270);
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 900 }; window.Show();
        try
        {
            await model.InitializeAsync(); await model.OpenEditorAsync("game.mp4"); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindNamed<StackPanel>("ShapeRow")!.IsEffectivelyVisible);
            Assert.False(model.ShowShapeEditor);

            model.SetShapeCommand.Execute("Vertical");
            await WaitUntil(() => model.HasShapePreview);
            Assert.True(model.ShapeCutsWidth); Assert.True(model.ShapeNeedsCrop);
            Assert.Equal(9d / 16 / (16d / 9), model.ShapeKeptLength.Value, 3); // 1920x1080 -> about a third of the width stays
            model.ExportShapePosition = 0;
            Assert.Equal(0.0001, model.ShapeBeforeLength.Value, 4); // slid all the way left
            Assert.Equal(FrameShape.Vertical, model.BuildExportOptions("out.mp4").Shape);
            Assert.Equal(0, model.BuildExportOptions("out.mp4").ShapePosition);

            if (Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY") is { Length: > 0 } output)
            {
                model.Theme = "Dark"; model.ExportShapePosition = 0.6;
                window.FindNamed<StackPanel>("ShapeRow")!.BringIntoView(); Dispatcher.UIThread.RunJobs(); await Task.Delay(300); Dispatcher.UIThread.RunJobs();
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                frame!.Save(Path.Combine(output, "shape-vertical-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            model.AudioOnly = true; // a sound-only clip has no picture to crop
            Assert.False(model.ShowShapeChoice);
            Assert.Equal(FrameShape.Original, model.BuildExportOptions("out.mp3").Shape);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Loudest_hops_between_the_marked_loud_moments()
    {
        var fake = new UiFakes(); fake.Media.Duration = TimeSpan.FromMinutes(2);
        // The fake waveform is a gentle wave; two loud bursts are placed in it.
        fake.Media.WaveformOverride = points => Enumerable.Range(0, points).Select(i => i * 120d / points is >= 70 and < 85 ? 0.95f : i * 120d / points is >= 20 and < 30 ? 0.7f : 0.08f).ToArray();
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 900 }; window.Show();
        try
        {
            await model.InitializeAsync(); await model.OpenEditorAsync("replay.mp4"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, model.LoudHighlights.Count);
            Assert.Equal(2, window.FindNamed<WaveformControl>("Waveform")!.Highlights!.Count);
            model.KeepLoudestCommand.Execute(null);
            Assert.InRange(TimeSpan.Parse(model.StartTime).TotalSeconds, 69, 72);
            Assert.Contains("1 of 2", model.Status);
            model.KeepLoudestCommand.Execute(null);
            Assert.InRange(TimeSpan.Parse(model.StartTime).TotalSeconds, 15, 21);
            Assert.Contains("2 of 2", model.Status);
            model.KeepLoudestCommand.Execute(null); // back to the loudest
            Assert.InRange(TimeSpan.Parse(model.StartTime).TotalSeconds, 69, 72);
            if (Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY") is { Length: > 0 } output)
            {
                model.Theme = "Dark"; window.FindNamed<WaveformControl>("Waveform")!.BringIntoView(); Dispatcher.UIThread.RunJobs(); await Task.Delay(300); Dispatcher.UIThread.RunJobs();
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                frame!.Save(Path.Combine(output, "loud-marks-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Make_it_a_Short_sets_vertical_louder_and_the_loudest_moment_of_a_long_recording()
    {
        var fake = new UiFakes(); fake.Media.Duration = TimeSpan.FromMinutes(3);
        fake.Media.WaveformOverride = points => Enumerable.Range(0, points).Select(i => i * 180d / points is >= 120 and < 135 ? 0.95f : 0.08f).ToArray();
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        await model.InitializeAsync(); await model.OpenEditorAsync("long-replay.mp4");
        Assert.True(model.ShowMakeShort);
        model.MakeShortCommand.Execute(null);
        Assert.Equal(FrameShape.Vertical, model.ExportShape);
        Assert.True(model.Normalize);
        Assert.InRange(TimeSpan.Parse(model.StartTime).TotalSeconds, 119, 122);
        Assert.Contains("Ready for TikTok", model.Status);
        var options = model.BuildExportOptions("out.mp4");
        Assert.Equal(FrameShape.Vertical, options.Shape); Assert.True(options.Normalize);
        Assert.Equal("long-replay - vertical", MainViewModel.FreeClipName("long-replay", "mp4", null, "vertical"));
    }

    // A small landscape "game" frame: sky, hills and a bright marker right of centre.
    private static byte[] Frame(int width, int height)
    {
        using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(width, height), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var hill = height * (0.6 + 0.1 * Math.Sin(x / 40d));
                    var marker = Math.Abs(x - width * 0.62) < 14 && Math.Abs(y - height * 0.5) < 30;
                    (int r, int g, int b) = marker ? (240, 120, 40) : y > hill ? (40, 90, 60) : (90, 140, 200);
                    var i = (y * width + x) * 4; pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255;
                }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); return stream.ToArray();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < limit) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(condition());
    }
}
