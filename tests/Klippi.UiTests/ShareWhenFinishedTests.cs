using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Share when finished: an opt-in upload after the user's own download, through the same encrypted
// File.Kiwi share, one upload at a time, with the link kept in memory only.
public sealed class ShareWhenFinishedTests
{
    private const string Link = "https://file.kiwi/example#decryption-secret";

    [AvaloniaFact]
    public async Task A_finished_download_is_shared_its_link_copied_and_the_secret_kept_in_memory_only()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        Assert.False(new AppSettings().ShareWhenFinished);
        Assert.False(model.ShareWhenFinished);
        // Turning it on never downloads anything by itself.
        model.ShareWhenFinished = true; Assert.Empty(fake.Downloads.Requests); Assert.Equal(0, fake.Sharing.Calls);

        model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
        Assert.Contains("shared with an encrypted File.Kiwi link when it finishes", model.Status);
        await WaitUntil(() => fake.Sharing.Calls == 1 && model.Status.StartsWith("Shared: link copied", StringComparison.Ordinal));
        Assert.Equal("test-output.mp4", Assert.Single(fake.Sharing.Paths));
        Assert.Equal(Link, fake.Platform.Clipboard);
        // An automatic share stays on the page the user is on.
        Assert.Equal(1, model.SelectedTab);

        var row = Assert.Single(model.Queue);
        Assert.True(row.IsFinished); Assert.True(row.HasShareLink); Assert.False(row.ShowShareProgress); Assert.False(row.ShowShareFailed);
        fake.Platform.Clipboard = ""; await row.CopyLinkCommand.ExecuteAsync(null);
        Assert.Equal(Link, fake.Platform.Clipboard);
        var record = Assert.Single(model.History); Assert.True(record.HasShareLink);
        fake.Platform.Clipboard = ""; await record.CopyShareLinkCommand.ExecuteAsync(null);
        Assert.Equal(Link, fake.Platform.Clipboard);

        // The secret after '#' is never written anywhere: history, the queue file, diagnostics or the status line.
        Assert.DoesNotContain(fake.Store.History, entry => JsonSerializer.Serialize(entry).Contains("decryption-secret", StringComparison.Ordinal));
        lock (fake.Store.QueueSaves) Assert.All(fake.Store.QueueSaves, saved => Assert.DoesNotContain("decryption-secret", JsonSerializer.Serialize(saved), StringComparison.Ordinal));
        Assert.DoesNotContain("decryption-secret", model.Diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("decryption-secret", model.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task With_the_switch_off_a_finished_download_is_not_shared()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
        await WaitUntil(() => model.Queue.Count == 1 && model.Queue[0].IsFinished && model.History.Count == 1);
        await Task.Delay(100);
        Assert.Equal(0, fake.Sharing.Calls);
        Assert.False(model.Queue[0].HasShareLink); Assert.Equal(AutoShareState.None, model.Queue[0].ShareState);
    }

    [AvaloniaFact]
    public async Task Automatic_shares_wait_for_a_running_upload_and_then_run_one_after_another()
    {
        var fake = new UiFakes(); fake.Downloads.ResultPathFor = options => options.Url.Segments[^1] + ".mp4";
        using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        // A share the user started holds the Share lane.
        fake.Sharing.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = model.ShareFileAsync("manual.mp4");
        await WaitUntil(() => fake.Sharing.Calls == 1);

        model.ShareWhenFinished = true;
        model.Url = "https://example.com/one"; await model.QueueDownloadAsync(false);
        model.Url = "https://example.com/two"; await model.QueueDownloadAsync(false);
        await WaitUntil(() => model.Queue.Count == 2 && model.Queue.All(row => row.IsFinished && row.ShareState == AutoShareState.Waiting));
        Assert.Equal(1, fake.Sharing.Calls);
        Assert.All(model.Queue, row => { Assert.True(row.ShowShareProgress); Assert.Contains("waiting", row.ShareProgressText, StringComparison.OrdinalIgnoreCase); });

        fake.Sharing.Gate.SetResult(); await manual;
        await WaitUntil(() => fake.Sharing.Calls == 3 && model.Queue.All(row => row.HasShareLink));
        Assert.Equal(["manual.mp4", "one.mp4", "two.mp4"], fake.Sharing.Paths);
        Assert.StartsWith("Shared: link copied", model.Status, StringComparison.Ordinal);
        Assert.All(model.Queue, row => Assert.False(row.ShowShareFailed));
    }

    [AvaloniaFact]
    public async Task A_failed_share_keeps_the_download_and_offers_Retry_share()
    {
        var fake = new UiFakes(); fake.Sharing.Failure = new MediaOperationException("File.Kiwi could not be reached.");
        using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        model.ShareWhenFinished = true; model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
        await WaitUntil(() => model.Queue.Count == 1 && model.Queue[0].ShowShareFailed);

        var row = model.Queue[0];
        Assert.True(row.IsFinished); Assert.Equal(JobState.Finished, row.State); Assert.Single(fake.Store.History);
        Assert.Contains("File.Kiwi could not be reached.", row.ShareFailureText);
        Assert.False(row.HasShareLink);
        Assert.Contains("could not be shared", model.Status); Assert.Contains("Retry share", model.Status);

        fake.Sharing.Failure = null;
        row.RetryShareCommand.Execute(null);
        await WaitUntil(() => row.HasShareLink);
        Assert.Equal(2, fake.Sharing.Calls); Assert.False(row.ShowShareFailed);
        Assert.Equal(Link, fake.Platform.Clipboard);
    }

    [AvaloniaFact]
    public async Task A_very_large_file_waits_for_the_usual_confirmation_instead_of_uploading()
    {
        var file = Path.Combine(Path.GetTempPath(), "keaclip-large-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        try
        {
            var fake = new UiFakes(); fake.Downloads.ResultPath = file;
            using var model = new MainViewModel(fake.Services) { LargeShareThresholdBytes = 1024, LargeShareRepeatDelay = TimeSpan.Zero };
            await model.InitializeAsync();
            model.ShareWhenFinished = true; model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
            await WaitUntil(() => model.Queue.Count == 1 && model.Queue[0].ShowShareConfirm);
            Assert.Equal(0, fake.Sharing.Calls); Assert.True(model.HasPendingLargeShare);
            Assert.Contains("not shared automatically", model.Status);

            // The row's Share confirms the waiting prompt, exactly as a second Share does elsewhere.
            var row = model.Queue[0];
            await row.ShareCommand.ExecuteAsync(null);
            await WaitUntil(() => row.HasShareLink);
            Assert.Equal(1, fake.Sharing.Calls); Assert.False(row.ShowShareConfirm);
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task The_Settings_default_is_saved_and_starts_the_Download_switch()
    {
        var fake = new UiFakes();
        using (var model = new MainViewModel(fake.Services))
        {
            await model.InitializeAsync();
            Assert.False(model.ShareWhenFinishedDefault);
            model.ShareWhenFinishedDefault = true;
            await model.SettingsPreferenceSaveTask;
            Assert.True(fake.Store.Settings.ShareWhenFinished);
            Assert.True(model.ShareWhenFinished);
        }
        using var next = new MainViewModel(fake.Services);
        await next.InitializeAsync();
        Assert.True(next.ShareWhenFinishedDefault); Assert.True(next.ShareWhenFinished);
        // The Download switch is for the next downloads only: turning it off leaves the saved default alone.
        next.ShareWhenFinished = false;
        await next.SettingsPreferenceSaveTask;
        Assert.True(fake.Store.Settings.ShareWhenFinished);
    }

    [AvaloniaFact]
    public async Task Pages_bind_the_switches_and_show_Copy_link_and_Retry_share_only_when_they_apply()
    {
        var fake = new UiFakes(); fake.Sharing.Failure = new MediaOperationException("File.Kiwi could not be reached.");
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 1100, Height = 820 }; window.Show();
        try
        {
            await model.InitializeAsync(); await Settle(window);
            var download = Find<ToggleSwitch>(window, "ShareWhenFinishedSwitch");
            Assert.False(download.IsChecked);
            download.IsChecked = true; Assert.True(model.ShareWhenFinished);

            model.SelectedTab = 4; await Settle(window);
            var setting = Find<ToggleSwitch>(window, "ShareWhenFinishedDefaultSwitch");
            Assert.False(setting.IsChecked);
            setting.IsChecked = true; Assert.True(model.ShareWhenFinishedDefault);
            await model.SettingsPreferenceSaveTask;

            model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
            model.SelectedTab = 1;
            await WaitUntil(() => model.Queue.Count == 1 && model.Queue[0].ShowShareFailed); await Settle(window);
            Assert.True(Find<Button>(window, "RetryShareButton").IsEffectivelyVisible);
            Assert.False(Find<Button>(window, "CopyShareLinkButton").IsEffectivelyVisible);

            fake.Sharing.Failure = null;
            Find<Button>(window, "RetryShareButton").Command!.Execute(null);
            await WaitUntil(() => model.Queue[0].HasShareLink); await Settle(window);
            Assert.True(Find<Button>(window, "CopyShareLinkButton").IsEffectivelyVisible);
            Assert.False(Find<Border>(window, "ShareFailedNotice").IsEffectivelyVisible);

            model.SelectedTab = 2; await Settle(window);
            Assert.True(Find<Button>(window, "CopyShareLinkItemButton").IsVisible);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible)
           ?? window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
           ?? window.FindNamed<T>(name) ?? throw new InvalidOperationException(name + " was not found.");

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); await Task.Delay(15); }
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
