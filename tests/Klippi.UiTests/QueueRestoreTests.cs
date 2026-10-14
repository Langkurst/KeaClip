using System.Text.Json;
using Avalonia.Headless.XUnit;
using Klippi.Core;
using Klippi.Desktop;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.UiTests;

// The queue survives a restart: unfinished jobs are saved with their options, come back in order,
// respect the pause and concurrency, and never carry private link values.
public sealed class QueueRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keaclip-queue-ui-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;

    public QueueRestoreTests() { _folder = Path.Combine(_root, "Downloads"); Directory.CreateDirectory(_folder); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [AvaloniaFact]
    public async Task Unfinished_jobs_are_saved_in_order_with_their_options_and_without_private_link_values()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        model.DownloadDirectory = _folder; model.DownloadMode = DownloadMode.Audio; model.AudioFormat = AudioFormat.Mp3;
        model.ShareWhenFinished = true;
        model.Url = "https://example.com/first"; await model.QueueDownloadAsync(false);
        model.ShareWhenFinished = false;
        model.Url = "https://example.com/second?si=tracking"; await model.QueueDownloadAsync(false);
        model.Url = "https://example.com/third?token=abc123secret"; await model.QueueDownloadAsync(false);
        await ShareWhenFinishedTests.WaitUntil(() => fake.Downloads.Requests.Count == 1 && model.Queue.Count == 3);
        await model.QueueSaveTask;

        var saved = Assert.IsType<SavedQueue>(fake.Store.Queue);
        Assert.False(saved.Paused);
        Assert.Equal(3, saved.Jobs.Count);
        // The running job is saved as waiting, so it starts again next time.
        var first = saved.Jobs[0]!; Assert.False(first.Failed); Assert.True(first.ShareWhenFinished);
        Assert.Equal("https://example.com/first", first.Options!.Url.AbsoluteUri);
        Assert.Equal(_folder, first.Options.OutputDirectory); Assert.Equal(DownloadMode.Audio, first.Options.Mode); Assert.Equal(AudioFormat.Mp3, first.Options.AudioFormat);
        var second = saved.Jobs[1]!; Assert.False(second.ShareWhenFinished);
        Assert.Equal("https://example.com/second", second.Options!.Url.AbsoluteUri);
        var third = saved.Jobs[2]!; Assert.True(third.LinkWithheld); Assert.Null(third.Options);
        Assert.DoesNotContain("abc123secret", JsonSerializer.Serialize(saved), StringComparison.Ordinal);

        // Closing cancels the work, but the file keeps the jobs as they were.
        model.Dispose(); await model.ShutdownAsync(); await model.QueueSaveTask;
        Assert.Equal(3, fake.Store.Queue!.Jobs.Count);
        Assert.Equal("https://example.com/first", fake.Store.Queue.Jobs[0]!.Options!.Url.AbsoluteUri);
    }

    [AvaloniaFact]
    public async Task Closing_saves_a_change_the_window_has_not_shown_yet()
    {
        var fake = new UiFakes();
        fake.Downloads.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Downloads.DownloadFailure = new MediaOperationException("The site refused the request.");
        var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        model.DownloadDirectory = _folder; model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
        await ShareWhenFinishedTests.WaitUntil(() => fake.Downloads.Requests.Count == 1);
        await model.QueueSaveTask;
        Assert.False(Assert.Single(fake.Store.Queue!.Jobs)!.Failed);

        // The download fails on its own thread; the UI thread is kept busy, so the queue page has not caught up yet when the app closes.
        fake.Downloads.DownloadGate.SetResult();
        Assert.True(SpinWait.SpinUntil(() => fake.Downloads.Threw, TimeSpan.FromSeconds(5)));
        Thread.Sleep(300);
        model.Dispose(); await model.ShutdownAsync();
        var saved = Assert.Single(fake.Store.Queue!.Jobs)!;
        Assert.True(saved.Failed); Assert.Equal("The site refused the request.", saved.Error);
    }

    [AvaloniaFact]
    public async Task Finished_jobs_leave_the_saved_queue()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        model.DownloadDirectory = _folder;
        model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
        await ShareWhenFinishedTests.WaitUntil(() => model.Queue.Count == 1 && model.Queue[0].IsFinished);
        await ShareWhenFinishedTests.WaitUntil(() => fake.Store.Queue is { Jobs.Count: 0 });
        lock (fake.Store.QueueSaves) Assert.Contains(fake.Store.QueueSaves, saved => saved.Jobs.Count == 1);
    }

    [AvaloniaFact]
    public async Task Saved_jobs_come_back_in_order_paused_with_failures_kept_and_gone_folders_dropped()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        fake.Store.Queue = new SavedQueue
        {
            Paused = true,
            Jobs =
            [
                Job("https://example.com/a", "A", _folder, share: true),
                Job("https://example.com/b", "B", _folder),
                Job("https://example.com/c", "C", Path.Combine(_root, "gone")),
                new SavedQueueJob { LinkWithheld = true },
                Job("https://example.com/d", "D", _folder) with { Failed = true, Error = "The site refused the request." },
                null,
            ],
        };
        using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();

        Assert.Equal(["A", "B", "D"], model.Queue.Select(row => row.Title));
        Assert.True(model.QueuePaused);
        Assert.Empty(fake.Downloads.Requests);
        Assert.True(model.Queue[0].IsQueued); Assert.True(model.Queue[1].IsQueued);
        Assert.True(model.Queue[2].IsFailed); Assert.Contains("The site refused the request.", model.Queue[2].FailureReason);
        Assert.Contains("Restored 3 downloads from your last session.", model.Status);
        Assert.Contains("paused", model.Status);
        Assert.Contains("1 saved download was not restored because its save folder is gone", model.Status);
        Assert.Contains("private sign-in or session values", model.Status);
        Assert.Contains("could not be read", model.Status);

        // The dropped entries are reported once: the file now holds what was restored.
        await model.QueueSaveTask;
        Assert.Equal(["https://example.com/a", "https://example.com/b", "https://example.com/d"], fake.Store.Queue!.Jobs.Select(job => job!.Options!.Url.AbsoluteUri));
        Assert.True(fake.Store.Queue.Jobs[0]!.ShareWhenFinished); Assert.True(fake.Store.Queue.Jobs[2]!.Failed);

        // A removed job leaves the saved queue too.
        model.Queue[1].RemoveCommand.Execute(null);
        await model.QueueSaveTask;
        Assert.Equal(["https://example.com/a", "https://example.com/d"], fake.Store.Queue!.Jobs.Select(job => job!.Options!.Url.AbsoluteUri));

        // Resuming starts them in order, one at a time (the default concurrency); the pause is saved as well.
        model.PauseQueueCommand.Execute(null);
        await model.QueueSaveTask;
        Assert.False(fake.Store.Queue!.Paused);
        await ShareWhenFinishedTests.WaitUntil(() => fake.Downloads.Requests.Count == 1);
        await Task.Delay(100);
        Assert.Equal("https://example.com/a", Assert.Single(fake.Downloads.Requests).Url.AbsoluteUri);
        Assert.Equal(_folder, fake.Downloads.Requests[0].OutputDirectory);

        // A restored failure starts again only when Retry is chosen.
        Assert.True(model.Queue[1].IsFailed);
        model.Queue[1].RetryCommand.Execute(null);
        Assert.True(model.Queue[1].IsQueued);
    }

    [AvaloniaFact]
    public async Task Restored_jobs_start_by_themselves_when_the_queue_was_not_paused_and_keep_Share_when_finished()
    {
        var fake = new UiFakes();
        fake.Store.Queue = new SavedQueue { Jobs = [Job("https://example.com/a", "A", _folder, share: true)] };
        using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        Assert.False(model.QueuePaused);
        await ShareWhenFinishedTests.WaitUntil(() => fake.Sharing.Calls == 1 && model.Queue.Count == 1 && model.Queue[0].HasShareLink);
        Assert.Equal("https://example.com/a", Assert.Single(fake.Downloads.Requests).Url.AbsoluteUri);
    }

    [AvaloniaFact]
    public async Task An_unreadable_queue_file_is_set_aside_reported_once_and_the_queue_starts_empty()
    {
        var data = Path.Combine(_root, "Data"); Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "queue.json"), "{ this is not a queue");
        var fake = new UiFakes();
        using (var model = new MainViewModel(fake.Services with { Store = new JsonLocalStore(data) }))
        {
            await model.InitializeAsync();
            Assert.Empty(model.Queue);
            Assert.Contains("Your saved queue could not be read", model.Status);
            await model.QueueSaveTask;
        }
        Assert.Single(Directory.GetFiles(data, "queue.json.corrupt-*"));
        using var again = new MainViewModel(fake.Services with { Store = new JsonLocalStore(data) });
        await again.InitializeAsync();
        Assert.DoesNotContain("saved queue", again.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(again.Queue);
    }

    [AvaloniaFact]
    public async Task A_queue_file_that_cannot_be_opened_is_left_alone_and_never_overwritten()
    {
        var fake = new UiFakes(); fake.Store.QueueLoadFailure = new IOException("The process cannot access the file because it is being used by another process.");
        using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        Assert.Contains("Your saved queue could not be opened", model.Status);
        model.DownloadDirectory = _folder; model.Url = "https://example.com/video"; await model.QueueDownloadAsync(false);
        await ShareWhenFinishedTests.WaitUntil(() => model.Queue.Count == 1 && model.Queue[0].IsFinished);
        await model.QueueSaveTask;
        lock (fake.Store.QueueSaves) Assert.Empty(fake.Store.QueueSaves);
    }

    private static SavedQueueJob Job(string url, string title, string folder, bool share = false)
        => new() { Title = title, Options = new DownloadOptions { Url = new Uri(url), OutputDirectory = folder }, ShareWhenFinished = share };
}
