using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Klippi.Core;
using Klippi.Desktop;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.UiTests;
public sealed class PolishTests
{
    [AvaloniaFact]
    public async Task Settings_save_failure_restores_startup_and_retry_remains_available()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.StartWithWindows = true; fake.Store.SettingsFailure = new IOException("Disk unavailable");
        await model.SaveSettingsAsync(); Assert.False(fake.Store.Settings.StartWithWindows); Assert.Equal(new[] { true, false }, fake.Platform.StartupWrites);
        fake.Store.SettingsFailure = null; await model.SaveSettingsAsync(); Assert.True(fake.Store.Settings.StartWithWindows); Assert.Equal(new[] { true, false, true }, fake.Platform.StartupWrites);
    }

    [AvaloniaFact]
    public async Task Output_kind_and_job_override_preserve_saved_folder_choices()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        model.AudioDirectory = Path.Combine(root, "music"); model.ArchivesDirectory = Path.Combine(root, "archives");
        model.DownloadMode = DownloadMode.Audio; Assert.Equal(model.AudioDirectory, model.BuildDownloadOptions(UiFakes.Info.Url).OutputDirectory);
        model.JobDirectory = Path.Combine(root, "one-job"); Assert.Equal(model.JobDirectory, model.BuildDownloadOptions(UiFakes.Info.Url).OutputDirectory);
        model.ResetJobFolderCommand.Execute(null); model.DownloadMode = DownloadMode.Archive; model.VideoCodec = VideoCodec.Hevc; model.HdrToSdr = true; model.Quality = "480p maximum"; model.DownloadFps = "30";
        var archive = model.BuildDownloadOptions(UiFakes.Info.Url); Assert.Equal(model.ArchivesDirectory, archive.OutputDirectory); Assert.Null(archive.MaxHeight); Assert.Null(archive.Fps); Assert.False(archive.HdrToSdr); Assert.Equal(VideoCodec.Original, archive.VideoCodec);
        await model.SaveSettingsAsync(); using var restarted = new MainViewModel(fake.Services); await restarted.InitializeAsync(); Assert.Equal(model.AudioDirectory, restarted.AudioDirectory); Assert.Equal(model.ArchivesDirectory, restarted.ArchivesDirectory); Assert.Empty(restarted.JobDirectory);
    }

    [AvaloniaFact]
    public async Task Lossless_audio_uses_source_codec_container_and_ignores_conversion_controls()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        model.AudioOnly = true; model.Normalize = true; model.FadeIn = "invalid old exact-mode value"; model.ClipMode = ClipMode.Lossless; fake.Platform.SavePath = "clip.m4a";
        await model.ExportAsync(); var options = Assert.Single(fake.Media.Exports); Assert.Equal(AudioFormat.Original, options.AudioFormat); Assert.False(options.Normalize); Assert.Equal(ClipMode.Lossless, options.ClipMode);
    }

    [AvaloniaFact]
    public async Task Diagnostics_remove_credentials_share_keys_and_personal_paths()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = "https://example.com/media" };
        fake.Downloads.AnalysisFailure = new MediaOperationException("Analysis failed", "Authorization: Bearer private-token\nhttps://file.kiwi/file#decryption-key\nC:\\Users\\Private\\Videos\\private.mp4");
        await model.AnalyseAsync(); await model.CopyDiagnosticsCommand.ExecuteAsync(null);
        Assert.Contains(AppIdentity.Version, fake.Platform.Clipboard); Assert.DoesNotContain("private-token", fake.Platform.Clipboard); Assert.DoesNotContain("decryption-key", fake.Platform.Clipboard); Assert.DoesNotContain("Private", fake.Platform.Clipboard); Assert.Contains("[local path]", fake.Platform.Clipboard);
    }

    [AvaloniaFact]
    public async Task Finished_download_still_opens_editor_when_history_refresh_fails()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = "https://example.com/media" };
        fake.Store.HistoryLoadFailure = new IOException("History unavailable"); await model.QueueDownloadAsync(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!model.HasEditor) await Task.Delay(10, deadline.Token);
        Assert.True(model.IsEditor); Assert.Single(fake.Downloads.Requests);
    }

    [AvaloniaFact]
    public async Task Short_work_area_keeps_navigation_and_all_pages_renderable()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); var window = new MainWindow { DataContext = model, Width = 900, Height = 480 }; window.Show();
        try { await model.InitializeAsync(); foreach (var tab in Enumerable.Range(0, 5)) { model.SelectedTab = tab; window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); Assert.True(window.Bounds.Height <= 480); Assert.NotNull(window.FindNamed<Button>("SettingsNav")); } }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Pasted_batch_requires_selection_and_separate_queue_consent()
    {
        var fake = new UiFakes(); fake.Platform.Clipboard = "https://example.com/one\nhttps://example.com/two\nhttps://example.com/one";
        using var model = new MainViewModel(fake.Services); await model.PasteAsync();
        Assert.Equal(2, model.BatchLinks.Count); Assert.All(model.BatchLinks, p => Assert.False(p.Selected)); Assert.Empty(fake.Downloads.Requests); Assert.Equal(0, fake.Downloads.Analyses);
        await model.QueueBatchAsync(); Assert.Empty(fake.Downloads.Requests); Assert.Contains("Select at least one", model.Status);
        model.SelectBatchCommand.Execute(null); Assert.Contains("2 selected", model.BatchSummary); await model.QueueBatchAsync(); await Until(() => fake.Downloads.Requests.Count == 2);
        Assert.False(model.HasBatch); Assert.Equal(new[] { "https://example.com/one", "https://example.com/two" }, fake.Downloads.Requests.Select(p => p.Url.AbsoluteUri));
    }

    [AvaloniaFact]
    public async Task Invalid_batch_and_playlist_ranges_do_not_add_jobs()
    {
        var fake = new UiFakes(); fake.Platform.Clipboard = "https://example.com/one\nfile:///unsafe";
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/playlist" }; await model.PasteAsync(); Assert.Empty(model.BatchLinks); Assert.Empty(fake.Downloads.Requests);
        fake.Downloads.Info = UiFakes.Info with { Entries = Enumerable.Range(1, 5).Select(index => new PlaylistEntry(index.ToString(), "Entry " + index, new Uri("https://example.com/" + index), null)).ToArray() };
        await model.AnalyseAsync(); model.RangeStart = "2"; model.RangeEnd = "4"; model.SelectRangeCommand.Execute(null);
        Assert.Equal(new[] { false, true, true, true, false }, model.Playlist.Select(p => p.Selected)); Assert.Contains("3 selected", model.PlaylistSummary); Assert.Empty(fake.Downloads.Requests);
        model.RangeEnd = "6"; model.SelectRangeCommand.Execute(null); Assert.Contains("within the playlist", model.Status); Assert.Empty(fake.Downloads.Requests);
    }

    [AvaloniaTheory]
    [InlineData("open")]
    [InlineData("skip")]
    [InlineData("again")]
    [InlineData("replace")]
    public async Task Duplicate_choices_preserve_existing_media_until_deliberate_resolution(string choice)
    {
        var root = Path.Combine(Path.GetTempPath(), "KeaClip-Ui-Duplicate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var existing = Path.Combine(root, "saved.mp4"); await File.WriteAllTextAsync(existing, "original bytes");
        try
        {
            var fake = new UiFakes(); fake.Downloads.Info = UiFakes.Info with { Title = new string('A', 200) }; fake.Store.History.Add(new("existing", "Saved media", "https://example.com/video", "Example", DateTimeOffset.Now, existing, "VideoAudio", "Best available", 14, "Finished"));
            using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" }; await model.InitializeAsync(); await model.QueueDownloadAsync(false);
            var pending = Assert.Single(model.DuplicateChoices); Assert.Empty(fake.Downloads.Requests); Assert.Equal("original bytes", await File.ReadAllTextAsync(existing));
            model.ResolveDuplicate(pending, choice); Assert.False(model.HasDuplicates);
            if (choice is "open" or "skip") { Assert.Empty(fake.Downloads.Requests); Assert.Equal(choice == "open" ? new[] { existing } : [], fake.Platform.OpenedFiles); }
            else { await Until(() => fake.Downloads.Requests.Count == 1); var options = Assert.Single(fake.Downloads.Requests); Assert.Equal(choice == "replace", options.ReplaceExisting); if (choice == "again") { Assert.Equal("saved (2)", options.NamingTemplate); Assert.True(options.NamingTemplate!.Length <= 140, "Separate-copy identity must survive the engine's filename length cap."); } else { Assert.Equal(root, options.OutputDirectory); Assert.Equal("saved", options.NamingTemplate); } }
            Assert.Equal("original bytes", await File.ReadAllTextAsync(existing));
        }
        finally { File.Delete(existing); Directory.Delete(root, false); }
    }

    [AvaloniaFact]
    public async Task Existing_output_without_history_is_detected_and_archive_cannot_replace()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeaClip-Ui-Output-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var file = Path.Combine(root, UiFakes.Info.Title + " [" + UiFakes.Info.Id + "].mp4"); await File.WriteAllTextAsync(file, "kept");
        try { var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = UiFakes.Info.Url.AbsoluteUri, DownloadDirectory = root }; await model.QueueDownloadAsync(false); var pending = Assert.Single(model.DuplicateChoices); Assert.Empty(fake.Downloads.Requests); Assert.Equal(file, pending.Existing.FilePath); model.ResolveDuplicate(pending, "skip");
            // A plain saved file never stops an archive; an existing archive of the same media asks, without Replace.
            var archived = Path.Combine(root, DownloadService.OutputBaseName(UiFakes.Info) + " Archive"); Directory.CreateDirectory(archived); var archivedMedia = Path.Combine(archived, DownloadService.OutputBaseName(UiFakes.Info) + ".webm"); await File.WriteAllTextAsync(archivedMedia, "archived");
            model.DownloadMode = DownloadMode.Archive; await model.QueueDownloadAsync(false); var archive = Assert.Single(model.DuplicateChoices); Assert.False(archive.CanReplace); Assert.Equal(archivedMedia, archive.Existing.FilePath); Assert.Empty(fake.Downloads.Requests); Assert.Equal("kept", await File.ReadAllTextAsync(file)); }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Lossless_and_archive_disable_conversion_controls_and_space_is_truthful()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        var window = new MainWindow { DataContext = model, Width = 900, Height = 480 }; window.Show();
        try { model.ClipMode = ClipMode.Lossless; window.UpdateLayout(); Assert.False(window.FindNamed<Grid>("ExportConversionControls")!.IsEnabled); model.ClipMode = ClipMode.Exact; window.UpdateLayout(); Assert.True(window.FindNamed<Grid>("ExportConversionControls")!.IsEnabled); Assert.Contains("available", model.AvailableSpace); Assert.DoesNotContain("unavailable", model.AvailableSpace); model.JobDirectory = "\0bad"; Assert.Contains("unavailable", model.AvailableSpace); }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Everyday_choice_states_render_without_overlapping_short_navigation()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeaClip-Ui-Review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var existing = Path.Combine(root, "saved.mp4"); await File.WriteAllTextAsync(existing, "kept");
        var fake = new UiFakes(); fake.Store.History.Add(new("saved", "A saved clip", UiFakes.Info.Url.AbsoluteUri, "Example", DateTimeOffset.Now, existing, "VideoAudio", "Best available", 4, "Finished"));
        using var model = new MainViewModel(fake.Services); var window = new MainWindow { DataContext = model, Width = 900, Height = 480 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.Url = UiFakes.Info.Url.AbsoluteUri; await model.QueueDownloadAsync(false); fake.Platform.Clipboard = "https://example.com/one\nhttps://example.com/two"; await model.PasteAsync(); model.SelectedTab = 0;
            foreach (var theme in new[] { "Dark", "Light" })
            {
                model.Theme = theme; window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                var nav = window.FindNamed<ScrollViewer>("NavigationScroll")!; var settings = window.FindNamed<Button>("SettingsNav")!; Assert.True(nav.Extent.Height >= nav.Viewport.Height);
                Assert.True(settings.Bounds.Bottom <= ((Control)settings.Parent!).Bounds.Height + 1);
                var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY"); if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "batch-duplicate-short-" + theme.ToLowerInvariant() + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
            }
        }
        finally { window.Close(); File.Delete(existing); Directory.Delete(root, false); }
    }

    private static async Task Until(Func<bool> condition) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, deadline.Token); }
}
