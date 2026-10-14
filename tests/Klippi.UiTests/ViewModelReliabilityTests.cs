using Avalonia.Headless.XUnit;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Regression tests for verified audit findings in MainViewModel.
public sealed class ViewModelReliabilityTests
{
    [AvaloniaFact]
    public async Task A_running_upload_does_not_block_analysing_or_downloading()
    {
        var fake = new UiFakes(); fake.Sharing.Gate = new TaskCompletionSource();
        using var model = new MainViewModel(fake.Services);
        var share = model.ShareFileAsync("fixture.mp4");
        Assert.False(model.CanShare); Assert.True(model.CanDownload);
        model.Url = "https://example.com/video"; await model.AnalyseAsync();
        Assert.True(model.HasMedia); // Before lanes, the global busy gate refused this.
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        fake.Sharing.Gate.SetResult(); await share;
        Assert.True(model.CanShare); Assert.False(model.IsBusy);
    }

    [AvaloniaFact]
    public async Task Quality_chosen_before_analysis_survives_download_without_analyse()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video", Quality = "Smallest available" };
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        Assert.True(fake.Downloads.Requests.Single().SmallestFile);
        Assert.Equal("Smallest available", model.Quality);
    }

    [AvaloniaFact]
    public async Task The_same_link_is_not_queued_twice_while_it_is_still_downloading()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" };
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        await model.QueueDownloadAsync(false); await Task.Delay(50);
        Assert.Single(fake.Downloads.Requests); Assert.Single(model.Queue);
        Assert.Contains("already in your queue", model.Status);
    }

    [AvaloniaFact]
    public async Task Folder_for_this_download_applies_only_to_that_request()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" };
        var custom = Path.Combine(Path.GetTempPath(), "keaclip-job-folder");
        model.JobDirectory = custom; await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        Assert.Equal(custom, fake.Downloads.Requests.Single().OutputDirectory);
        Assert.Equal("", model.JobDirectory); Assert.Equal(model.DownloadDirectory, model.SelectedOutputDirectory);
    }

    [AvaloniaFact]
    public async Task A_cleared_link_box_is_not_refilled_with_the_same_clipboard_link()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        fake.Platform.Clipboard = "https://www.youtube.com/watch?v=clip";
        await model.DetectClipboardAsync(); Assert.Equal("https://www.youtube.com/watch?v=clip", model.Url);
        model.Url = ""; await model.DetectClipboardAsync(); Assert.Equal("", model.Url);
        fake.Platform.Clipboard = "https://vimeo.com/new"; await model.DetectClipboardAsync(); Assert.Equal("https://vimeo.com/new", model.Url);
    }

    [AvaloniaFact]
    public async Task A_busy_clipboard_never_crashes_copy_actions()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.ShareFileAsync("fixture.mp4"); Assert.False(string.IsNullOrEmpty(model.ShareUrl));
        fake.Platform.CopyFailure = new InvalidOperationException("OpenClipboard failed");
        await model.CopyShareCommand.ExecuteAsync(null); await model.CopyPathAsync("fixture.mp4");
        Assert.Contains("clipboard is busy", model.Status); Assert.False(string.IsNullOrEmpty(model.ShareUrl));
    }

    [AvaloniaFact]
    public async Task A_file_that_fails_to_probe_leaves_the_previous_editor_file_intact()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("first.mp4"); Assert.Equal("first.mp4", model.EditorTitle);
        fake.Media.ProbeFails = path => path == "broken.mp4";
        await model.OpenEditorAsync("broken.mp4");
        Assert.Equal("first.mp4", model.EditorTitle); Assert.True(model.HasEditor); Assert.NotEmpty(model.Waveform); Assert.Contains("FFmpeg could not process", model.Status);
    }

    [AvaloniaFact]
    public async Task A_waveform_failure_opens_the_new_file_without_the_old_waveform()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("first.mp4"); Assert.NotEmpty(model.Waveform);
        fake.Media.WaveformFails = path => path == "second.mp4";
        await model.OpenEditorAsync("second.mp4");
        // Never the new title over the old file's sound: the file opens, its waveform is simply absent.
        Assert.Equal("second.mp4", model.EditorTitle); Assert.Empty(model.Waveform); Assert.Contains("waveform could not be drawn", model.Status);
    }

    [AvaloniaFact]
    public async Task Opening_a_file_keeps_the_last_share_link()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.ShareFileAsync("shared.mp4"); var link = model.ShareUrl;
        await model.OpenEditorAsync("other.mp4");
        Assert.Equal(link, model.ShareUrl); Assert.Equal("shared.mp4", model.ShareFileName);
    }

    [AvaloniaFact]
    public async Task Exact_audio_clip_with_original_format_is_rejected_before_the_save_dialog()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        model.ClipMode = ClipMode.Exact; model.AudioOnly = true; model.ExportAudioFormat = AudioFormat.Original;
        await model.ExportAsync();
        Assert.Equal(0, fake.Platform.SaveDialogs); Assert.Empty(fake.Media.Exports); Assert.Contains("choose an audio format", model.Status);
    }

    [AvaloniaFact]
    public async Task Malformed_timecode_gets_a_format_hint_not_a_range_error()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        model.StartTime = "1m5s"; await model.ExportAsync();
        Assert.Contains("isn't a timecode", model.Status); Assert.Equal(0, fake.Platform.SaveDialogs);
    }

    [AvaloniaFact]
    public async Task History_search_updates_the_list_after_typing_pauses()
    {
        var fake = new UiFakes();
        fake.Store.History.Add(new("a", "Kea at Arthur's Pass", null, "Local", DateTimeOffset.UtcNow, "a.mp4", "VideoAudio", "Best", 1, "Finished"));
        fake.Store.History.Add(new("b", "Tui song", null, "Local", DateTimeOffset.UtcNow.AddMinutes(-1), "b.mp3", "Audio", "Best", 1, "Finished"));
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.Equal(2, model.History.Count);
        var first = model.History[0];
        model.HistorySearch = "tui"; await WaitUntil(() => model.History.Count == 1);
        Assert.Equal("Tui song", model.History.Single().Title);
        model.HistorySearch = ""; await WaitUntil(() => model.History.Count == 2);
        Assert.Same(first.Entry, model.History[0].Entry);
    }

    [AvaloniaFact]
    public async Task Store_recovery_notice_is_shown_once_in_the_status()
    {
        var fake = new UiFakes(); fake.Store.Warning = "Your history was restored from its backup copy.";
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.StartsWith("Your history was restored", model.Status); Assert.Null(fake.Store.Warning);
    }

    [AvaloniaFact]
    public async Task Unreadable_settings_never_switch_clipboard_detection_on()
    {
        var fake = new UiFakes(); fake.Store.SettingsLoadFailure = new IOException("settings.json is locked");
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.Contains("settings could not be read", model.Status);
        fake.Platform.Clipboard = "https://example.com/private"; await model.DetectClipboardAsync();
        Assert.Equal("", model.Url);
    }

    [AvaloniaFact]
    public async Task Links_saved_by_older_versions_still_raise_the_duplicate_prompt()
    {
        var fake = new UiFakes(); var path = Path.Combine(Path.GetTempPath(), "keaclip-dup-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllTextAsync(path, "owned fixture");
        try
        {
            fake.Store.History.Add(new("old", "Sample", "https://www.youtube.com/watch?v=abc123&t=42s&list=PL1&index=3", "YouTube", DateTimeOffset.UtcNow, path, "VideoAudio", "Best available", 13, "Finished"));
            fake.Downloads.Info = UiFakes.Info with { Url = new("https://www.youtube.com/watch?v=abc123") };
            using var model = new MainViewModel(fake.Services) { Url = "https://www.youtube.com/watch?v=abc123" }; await model.InitializeAsync();
            await model.QueueDownloadAsync(false);
            Assert.True(model.HasDuplicates); Assert.Empty(fake.Downloads.Requests);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task A_job_event_refreshes_only_its_own_queue_row()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        fake.Downloads.Info = UiFakes.Info with { Entries = [new("1", "One", new("https://example.com/1"), null), new("2", "Two", new("https://example.com/2"), null), new("3", "Three", new("https://example.com/3"), null)] };
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/list" };
        await model.AnalyseAsync(); foreach (var entry in model.Playlist) entry.Selected = true;
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1 && model.Queue.Count == 3);
        await Task.Delay(50);
        var untouched = model.Queue[2]; var notifications = 0; untouched.PropertyChanged += (_, _) => notifications++;
        model.Queue[0].CancelCommand.Execute(null); // Job 1 stops, job 2 starts; job 3 is unchanged.
        await WaitUntil(() => fake.Downloads.Requests.Count == 2); await Task.Delay(100);
        Assert.Equal(0, notifications);
        Assert.Equal(["One", "Two", "Three"], model.Queue.Select(row => row.Title));
    }

    [AvaloniaFact]
    public async Task Component_update_reports_the_services_own_result()
    {
        var fake = new UiFakes(); fake.Components.UpdateMessage = "yt-dlp is already up to date (2026.08.19).";
        using var model = new MainViewModel(fake.Services);
        await model.UpdateDownloaderCommand.ExecuteAsync(null);
        Assert.Equal("yt-dlp is already up to date (2026.08.19).", model.Status);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
