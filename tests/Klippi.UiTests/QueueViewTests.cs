using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klippi.Core;
using Klippi.Desktop;
using Klippi.Desktop.Controls;
using Klippi.Desktop.Views;
using Xunit;

namespace Klippi.UiTests;

public sealed class QueueViewTests
{
    [AvaloniaFact]
    public async Task Queue_page_renders_empty_active_paused_mixed_and_wide_states_in_both_themes()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 3);

        // Empty: mascot, message and the way back to Download; Pause stays reachable for a batch about to be queued.
        await scene.CaptureAsync("empty");
        Assert.True(scene.Window.FindNamed<Border>("QueueEmptyState")!.IsVisible);
        Assert.False(scene.Window.FindNamed<WrapPanel>("QueueSummary")!.IsVisible);
        Assert.False(scene.Window.FindNamed<Button>("ClearFinishedButton")!.IsVisible);
        Assert.True(scene.Window.FindNamed<Button>("PauseQueueButton")!.IsEffectivelyVisible);

        // Active: three running jobs in different stages plus two waiting.
        await scene.EnqueueAsync("https://media.example.org/kea-dawn", "Kea dawn chorus over Arthur's Pass, recorded in 4K at first light");
        scene.SeedThumbnail(0);
        await scene.EnqueueAsync("https://audio.example.org/tui", "Tūī morning song", DownloadMode.Audio);
        await scene.EnqueueAsync("https://www.example.com/long", "An exceptionally long documentary title about braided rivers, alpine weather and the birds that live between them, part three of five");
        await scene.EnqueueAsync("https://video.example.net/fiordland", "Fiordland timelapse");
        await scene.EnqueueAsync("https://archive.example.net/talk", "Conservation talk (full archive)", DownloadMode.Archive);
        scene.Job("kea-dawn").Report(new(JobStage.Analysing, Message: "Checking the selected media"));
        scene.Job("kea-dawn").Report(new(JobStage.Downloading, 42.6, "Downloading media", 2_410_000, TimeSpan.FromSeconds(72)));
        scene.Job("tui").Report(new(JobStage.Downloading, 100, "Downloading media"));
        scene.Job("tui").Report(new(JobStage.Converting, Message: "Processing the selected media format"));
        scene.Job("long").Report(new(JobStage.Analysing, Message: "Checking the selected media"));
        scene.Flush();
        Assert.Equal(5, scene.Model.QueueActiveTotal);
        // Two reports inside one dispatcher cycle: the skipped-over Download step still reads as done.
        Assert.True(scene.Row(1).Steps[1].IsDone); Assert.True(scene.Row(1).Steps[2].IsCurrent); Assert.Equal("Convert", scene.Row(1).Steps[2].Label);
        Assert.Equal("Saving to Downloads", scene.Row(0).StageCaption);
        // The default window has room for the labelled Clear finished button next to a single-digit summary.
        Assert.True(scene.Window.FindNamed<TextBlock>("ClearFinishedLabel")!.IsVisible);
        await scene.CaptureAsync("active");
        await scene.CaptureAsync("active-tall", height: 1320);
        await scene.CaptureAsync("active-150", scale: 1.5);
        scene.AssertCardsFit();

        // Paused: waiting jobs show they are on hold and the toolbar says so.
        scene.Model.PauseQueueCommand.Execute(null);
        await scene.CaptureAsync("paused");
        await scene.CaptureAsync("paused-tall", height: 1320);
        Assert.Equal("Resume queue", scene.Model.PauseQueueLabel);
        Assert.True(scene.Window.FindNamed<Border>("QueuePausedNotice")!.IsVisible);
        scene.Model.PauseQueueCommand.Execute(null); scene.Flush();
        Assert.False(scene.Window.FindNamed<Border>("QueuePausedNotice")!.IsVisible);

        // Mixed: finished, failed and cancelled cards next to a conversion and an archive in progress.
        scene.Job("kea-dawn").Complete("Kea dawn chorus over Arthur's Pass [kea-dawn].mp4", 184_320_000, title: "Kea dawn chorus over Arthur's Pass, recorded in 4K at first light");
        scene.Job("tui").Fail("This site needs you to sign in. Choose your browser under More options on the Download page, then retry.");
        await scene.UntilAsync(() => scene.Row(0).IsFinished && scene.Row(1).IsFailed);
        scene.Row(2).CancelCommand.Execute(null);
        await scene.UntilAsync(() => scene.Row(2).IsCancelled && scene.Downloads.Has("fiordland") && scene.Downloads.Has("talk"));
        scene.Job("fiordland").Report(new(JobStage.Downloading, 100, "Downloading media"));
        scene.Job("fiordland").Report(new(JobStage.Verifying, Message: "Checking the downloaded file"));
        scene.Job("fiordland").Report(new(JobStage.Converting, 63, "Creating the selected video format", Eta: TimeSpan.FromMinutes(3.5)));
        scene.Job("talk").Report(new(JobStage.Downloading, 100, "Downloading media"));
        scene.Job("talk").Report(new(JobStage.Verifying, Message: "Checking the downloaded file"));
        scene.Job("talk").Report(new(JobStage.WritingMetadata, Message: "Packaging the raw archive as ZIP"));
        scene.Job("talk").Report(new(JobStage.Verifying, Message: "Checking the ZIP against the raw archive checksums"));
        scene.Flush();
        Assert.Equal((2, 1, 1), (scene.Model.QueueActiveTotal, scene.Model.QueueFinishedCount, scene.Model.QueueFailedCount));
        Assert.True(scene.Model.CanClearFinished);
        Assert.Equal("Archive", scene.Row(4).Steps[2].Label); Assert.Equal("Verifying", scene.Row(4).StateLabel);
        await scene.CaptureAsync("mixed");
        await scene.CaptureAsync("mixed-tall", height: 1320);
        scene.AssertCardsFit();
        await scene.CaptureAsync("mixed-200", scale: 2);

        // With motion on, entrance animations and the activity ring must settle into readable final states.
        scene.Model.ReducedMotion = false; scene.Flush();
        await scene.CaptureAsync("mixed-motion", height: 1320);
        Assert.All(scene.View.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("job") && b.IsEffectivelyVisible), card => Assert.Equal(1, card.Opacity));
        scene.Model.ReducedMotion = true; scene.Flush();

        // Removing a running job: it says so and offers nothing else until it has stopped.
        scene.Job("fiordland").HoldCancellation = true;
        scene.Row(3).RemoveCommand.Execute(null); scene.Flush();
        Assert.True(scene.Row(3).IsRemoving); Assert.Equal("Removing…", scene.Row(3).StateLabel);
        // A stopping job no longer promises a time left.
        Assert.False(scene.Row(3).HasEta); Assert.Equal(63, scene.Row(3).DisplayPercent);
        await scene.CaptureAsync("removing", height: 1320);
        scene.Job("fiordland").ReleaseCancellation();
        await scene.UntilAsync(() => scene.Model.Queue.Count == 4);

        // The narrowest window keeps every card inside its column; Open, Edit and Share move under the file line.
        scene.Window.Width = 880; scene.Flush();
        scene.AssertCardsFit();
        Assert.True(scene.Window.FindNamed<TextBlock>("ClearFinishedLabel")!.IsVisible);
        await scene.CaptureAsync("narrow", width: 880, height: 1100);

        // Wide monitors keep the readable column.
        await scene.CaptureAsync("wide", width: 1400, height: 900);
        Assert.True(scene.Window.FindNamed<ItemsControl>("QueueItems")!.Bounds.Width <= 1040.5);
        await scene.FinishAsync();
    }

    [AvaloniaFact]
    public async Task Stepper_follows_the_engines_real_stage_order_and_never_moves_backwards()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 4);
        await scene.EnqueueAsync("https://media.example.org/plain", "Plain download");
        var row = scene.Row(0); var job = scene.Job("plain");

        job.Report(new(JobStage.Analysing, Message: "Checking the selected media")); scene.Flush();
        Assert.Equal("Starting", row.StateLabel); Assert.Equal(0, CurrentStep(row)); Assert.True(row.IsPercentUnknown); Assert.Equal("Checking the selected media", row.StageCaption);
        Assert.Equal("Process", row.Steps[2].Label);

        job.Report(new(JobStage.Downloading, 40, "Downloading media", 2_400_000, TimeSpan.FromSeconds(72))); scene.Flush();
        Assert.Equal("Downloading", row.StateLabel); Assert.True(row.Steps[0].IsDone); Assert.Equal(1, CurrentStep(row)); Assert.True(row.Steps[2].IsPending);
        Assert.True(row.HasPercent); Assert.Equal(40, row.DisplayPercent);
        Assert.Equal("Saving to Downloads", row.StageCaption);
        Assert.Equal("2.4 MB/s", row.SpeedText); Assert.Equal("1 min 12 s left", row.EtaText); Assert.True(row.HasSpeed && row.HasEta);
        Assert.Equal("Download, current step", row.Steps[1].AutomationName);

        // 99.8% is not finished: the counter rounds down. A big drop then means the next stream started.
        job.Report(new(JobStage.Downloading, 99.8, "Downloading media")); scene.Flush();
        Assert.Equal(99, row.DisplayPercent);
        job.Report(new(JobStage.Downloading, 1.5, "Downloading media")); scene.Flush();
        Assert.Equal("Stream 2 · saving to Downloads", row.StageCaption);
        // Refreshes caused by other rows must not count extra streams.
        scene.Model.ClearCompletedCommand.Execute(null); scene.Flush();
        Assert.Equal("Stream 2 · saving to Downloads", row.StageCaption);

        job.Report(new(JobStage.Merging, Message: "Merging audio and video")); scene.Flush();
        Assert.Equal("Merging", row.StateLabel); Assert.Equal("Merge", row.Steps[2].Label); Assert.Equal(2, CurrentStep(row)); Assert.True(row.Steps[1].IsDone);
        Assert.False(row.HasPercent); Assert.False(row.HasSpeed); Assert.Equal("Merging audio and video", row.StageCaption);

        job.Report(new(JobStage.Verifying, Message: "Checking the downloaded file")); scene.Flush();
        Assert.Equal("Verifying", row.StateLabel); Assert.Equal("Check", row.Steps[2].Label); Assert.Equal(2, CurrentStep(row));

        job.Report(new(JobStage.Finished, 100, "Download complete and checked")); scene.Flush();
        Assert.Equal("Finishing", row.StateLabel); Assert.Equal(3, CurrentStep(row)); Assert.True(row.Steps[2].IsDone);

        scene.Fake.Store.HistoryFailure = new IOException("History storage is full.");
        job.Complete("Real engine title [plain].mp4", 2_000_000, title: "Real engine title");
        await scene.UntilAsync(() => row.IsFinished);
        Assert.All(row.Steps, step => Assert.True(step.IsDone));
        Assert.True(row.HasFinishWarning); Assert.Contains("history could not be saved", row.FinishWarning);
        Assert.Equal("Real engine title", row.DisplayTitle); Assert.Equal("2 MB", row.ResultSize); Assert.Equal("Real engine title [plain].mp4", row.ResultFileName);
        Assert.Equal("Finished", row.StateLabel); Assert.False(row.IsRunning); Assert.True(row.HasResult); Assert.False(row.CanCancel); Assert.True(row.CanRemove);

        // The engine's real orders: the downloaded file is checked before a conversion, remux or archive runs,
        // and each of those checks its own result again. The stepper must only ever move forward.
        await AssertSequenceAsync(scene, "convert", DownloadMode.VideoAudio,
            (new(JobStage.Downloading, 100, "Downloading media"), "Process"),
            (new(JobStage.Verifying, Message: "Checking the downloaded file"), "Check"),
            (new(JobStage.Converting, Message: "Creating the selected video format"), "Convert"),
            (new(JobStage.Converting, 0, "Exporting the selected clip"), "Convert"),
            (new(JobStage.Converting, 63, "Exporting the selected clip"), "Convert"),
            (new(JobStage.Verifying, Message: "Checking the exported media"), "Convert"),
            (new(JobStage.Finished, 100, "Export complete and checked"), "Convert"),
            (new(JobStage.Finished, 100, "Download complete and checked"), "Convert"));
        await AssertSequenceAsync(scene, "remux", DownloadMode.VideoAudio,
            (new(JobStage.Downloading, 100, "Downloading media"), "Process"),
            (new(JobStage.Verifying, Message: "Checking the downloaded file"), "Check"),
            (new(JobStage.Merging, Message: "Putting the source streams in the selected container without re-encoding"), "Repack"),
            (new(JobStage.Converting, 0, "Copying media without re-encoding"), "Repack"),
            (new(JobStage.Verifying, Message: "Checking the exported media"), "Repack"),
            (new(JobStage.Finished, 100, "Download complete and checked"), "Repack"));
        await AssertSequenceAsync(scene, "archive", DownloadMode.Archive,
            (new(JobStage.Downloading, 100, "Downloading media"), "Process"),
            (new(JobStage.Verifying, Message: "Checking the downloaded file"), "Check"),
            (new(JobStage.WritingMetadata, Message: "Writing source information and checksums"), "Archive"),
            (new(JobStage.WritingMetadata, Message: "Packaging the raw archive as ZIP"), "Archive"),
            (new(JobStage.Verifying, Message: "Checking the ZIP against the raw archive checksums"), "Archive"),
            (new(JobStage.Finished, 100, "Archive complete and checked"), "Archive"));

        // Stages reported between two UI refreshes are still seen: the check before this merge marks it as a remux.
        await scene.EnqueueAsync("https://media.example.org/burst", "Burst");
        var burst = scene.Job("burst");
        burst.Report(new(JobStage.Downloading, 100, "Downloading media"));
        burst.Report(new(JobStage.Verifying, Message: "Checking the downloaded file"));
        burst.Report(new(JobStage.Merging, Message: "Putting the source streams in the selected container without re-encoding"));
        scene.Flush();
        Assert.Equal("Repack", scene.Model.Queue.Single(r => r.Title == "Burst").Steps[2].Label);
        await scene.FinishAsync();
    }

    [AvaloniaFact]
    public async Task Retry_restarts_the_same_card_in_place_with_a_fresh_stepper()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 1);
        await scene.EnqueueAsync("https://media.example.org/flaky", "Flaky source");
        await scene.EnqueueAsync("https://media.example.org/next", "Next in line");
        scene.SeedThumbnail(0);
        var card = scene.Row(0);
        scene.Job("flaky").Report(new(JobStage.Downloading, 100, "Downloading media"));
        scene.Job("flaky").Report(new(JobStage.Verifying, Message: "Checking the downloaded file")); scene.Flush();
        scene.Job("flaky").Fail("The site refused the request. Try again later.");
        await scene.UntilAsync(() => card.IsFailed && scene.Downloads.Has("next"));
        Assert.Equal("The site refused the request. Try again later.", card.FailureReason);
        Assert.Equal(1, scene.Model.QueueFailedCount); Assert.True(scene.Model.HasQueueFailures); Assert.False(card.ShowRetry); Assert.True(card.CanRetry);

        // Retry keeps the card, its place, its thumbnail and its id; the stepper starts again from the beginning.
        card.RetryCommand.Execute(null); scene.Flush();
        Assert.Same(card, scene.Row(0)); Assert.True(card.IsQueued); Assert.True(card.HasThumbnail); Assert.Equal(2, scene.Model.Queue.Count);
        Assert.Equal(0, scene.Model.QueueFailedCount); Assert.False(scene.Model.HasQueueFailures);
        // A second click cannot start a duplicate; it explains instead.
        card.RetryCommand.Execute(null);
        Assert.Equal("This download is already queued or running.", scene.Model.Status);

        scene.Job("next").Complete("next.mp4", 1_000_000);
        await scene.UntilAsync(() => card.IsRunning && scene.Downloads.Requests == 3);
        Assert.Equal(0, CurrentStep(card)); Assert.Equal("Process", card.Steps[2].Label); Assert.True(card.Steps[1].IsPending);

        // A cancelled job offers the same labelled Retry.
        card.CancelCommand.Execute(null);
        await scene.UntilAsync(() => card.IsCancelled);
        Assert.True(card.ShowRetry); Assert.True(card.CanRemove);
        card.RetryCommand.Execute(null);
        await scene.UntilAsync(() => scene.Downloads.Requests == 4);
        Assert.Same(card, scene.Row(0)); Assert.True(card.IsRunning);
        await scene.FinishAsync();
    }

    [AvaloniaFact]
    public async Task Header_summary_queue_positions_and_card_actions_follow_the_queue()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 1);
        await scene.EnqueueAsync("https://media.example.org/a", "A");
        await scene.EnqueueAsync("https://media.example.org/b", "B");
        await scene.EnqueueAsync("https://media.example.org/c", "C");
        Assert.True(scene.Model.HasQueueItems); Assert.Equal(3, scene.Model.QueueActiveTotal); Assert.False(scene.Model.CanClearFinished);
        Assert.True(scene.Row(0).IsRunning);
        Assert.Equal("Waiting · next to start", scene.Row(1).Slot.Caption); Assert.False(scene.Row(1).Slot.CanMoveUp); Assert.True(scene.Row(1).Slot.CanMoveDown);
        Assert.Equal("Waiting · 2nd in line", scene.Row(2).Slot.Caption); Assert.True(scene.Row(2).Slot.CanMoveUp); Assert.False(scene.Row(2).Slot.CanMoveDown);

        // Waiting jobs offer Remove only (it cancels them); running jobs offer Cancel and "cancel and remove".
        Assert.False(scene.Row(1).ShowCancel); Assert.True(scene.Row(1).CanRemove); Assert.Equal("Remove from the queue", scene.Row(1).RemoveHint);
        Assert.True(scene.Row(0).ShowCancel); Assert.True(scene.Row(0).CanRemove); Assert.Equal("Cancel and remove from the list", scene.Row(0).RemoveHint);

        var c = scene.Row(2); c.MoveUpCommand.Execute(null);
        Assert.Same(c, scene.Row(1)); Assert.Equal("Waiting · next to start", c.Slot.Caption); Assert.Equal("Waiting · 2nd in line", scene.Row(2).Slot.Caption);

        scene.Job("a").Complete("a.mp4", 1_000_000);
        await scene.UntilAsync(() => scene.Row(0).IsFinished && c.IsRunning);
        Assert.Equal(1, scene.Model.QueueFinishedCount); Assert.True(scene.Model.CanClearFinished); Assert.Equal(2, scene.Model.QueueActiveTotal);
        Assert.Equal("Waiting · next to start", scene.Row(2).Slot.Caption);

        // Clear finished keeps active work; removing a waiting job takes it off the list.
        scene.Model.ClearCompletedCommand.Execute(null);
        Assert.Equal(2, scene.Model.Queue.Count); Assert.False(scene.Model.CanClearFinished); Assert.Equal(0, scene.Model.QueueFinishedCount);
        scene.Row(1).RemoveCommand.Execute(null);
        Assert.Same(c, Assert.Single(scene.Model.Queue)); Assert.Equal(1, scene.Model.QueueActiveTotal);

        // Removing the running job: it reads "Removing…" with no other actions until the download has stopped.
        scene.Job("c").HoldCancellation = true;
        c.RemoveCommand.Execute(null); scene.Flush();
        Assert.True(c.IsRemoving); Assert.Equal("Removing…", c.StateLabel); Assert.False(c.IsLive); Assert.True(c.IsChipQuiet); Assert.False(c.IsChipAccent);
        Assert.False(c.ShowCancel); Assert.False(c.CanRemove); Assert.Equal("Stopping, then removing it from the list", c.StageCaption);
        scene.Job("c").ReleaseCancellation();
        await scene.UntilAsync(() => scene.Model.Queue.Count == 0);
        Assert.False(scene.Model.HasQueueItems);
        await scene.FinishAsync();
    }

    [AvaloniaFact]
    public async Task Empty_state_returns_to_download_and_infinite_animations_run_only_on_screen()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 1, reducedMotion: false);
        var empty = scene.Window.FindNamed<Border>("QueueEmptyState")!;
        Assert.True(empty.IsVisible);
        scene.Click(scene.Window.FindNamed<Button>("QueueGoToDownload")!);
        Assert.Equal(0, scene.Model.SelectedTab);

        await scene.EnqueueAsync("https://media.example.org/spin", "Spinner");
        scene.Job("spin").Report(new(JobStage.Analysing, Message: "Checking the selected media")); scene.Flush();
        scene.Model.SelectedTab = 1; scene.Layout();
        Assert.False(empty.IsVisible); Assert.True(scene.View.IsOnScreen);
        var ring = scene.View.GetVisualDescendants().OfType<ProgressRing>().Single(r => r.IsVisible);
        var dot = scene.View.GetVisualDescendants().OfType<Ellipse>().Single(e => e.Classes.Contains("live"));
        Assert.True(ring.IsIndeterminate); Assert.True(dot.IsVisible);

        // Another page, a minimised window and a window hidden to the tray all stop the animations.
        scene.Model.SelectedTab = 2; scene.Layout();
        Assert.False(ring.IsIndeterminate); Assert.False(dot.IsVisible);
        scene.Model.SelectedTab = 1; scene.Layout();
        Assert.True(ring.IsIndeterminate);
        scene.Window.WindowState = WindowState.Minimized; scene.Layout();
        Assert.False(scene.View.IsOnScreen); Assert.False(ring.IsIndeterminate); Assert.False(dot.IsVisible);
        scene.Window.WindowState = WindowState.Normal; scene.Layout();
        Assert.True(ring.IsIndeterminate); Assert.True(dot.IsVisible);
        scene.Window.Hide(); scene.Layout();
        Assert.False(scene.View.IsOnScreen); Assert.False(ring.IsIndeterminate);
        scene.Window.Show(); scene.Layout();
        Assert.True(scene.View.IsOnScreen); Assert.True(ring.IsIndeterminate);

        scene.Model.ReducedMotion = true; scene.Layout();
        Assert.False(ring.IsIndeterminate); Assert.True(scene.Row(0).ShowWaitIcon);

        // Known percent replaces the ring with the counting number.
        scene.Model.ReducedMotion = false;
        scene.Job("spin").Report(new(JobStage.Downloading, 12, "Downloading media")); scene.Flush(); scene.Layout();
        Assert.False(ring.IsVisible);
        Assert.Contains(scene.View.GetVisualDescendants().OfType<AnimatedNumberText>(), number => number.IsEffectivelyVisible);
        await scene.FinishAsync();
    }

    [AvaloniaFact]
    public async Task Progress_glides_forward_but_snaps_back_when_a_new_stream_or_stage_starts()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 1, reducedMotion: false);
        await scene.EnqueueAsync("https://media.example.org/streams", "Streams");
        var job = scene.Job("streams");
        ProgressBar Bar() => scene.View.GetVisualDescendants().OfType<ProgressBar>().Single(b => b.Classes.Contains("job-bar"));
        AnimatedNumberText Number() => scene.View.GetVisualDescendants().OfType<AnimatedNumberText>().Single();

        job.Report(new(JobStage.Downloading, 40, "Downloading media")); scene.Flush();
        Assert.InRange(await SampleAsync(() => Bar().Value), 0.01, 39.9);
        await scene.SettleAsync();
        Assert.Equal(40, Bar().Value, 3);
        job.Report(new(JobStage.Downloading, 99.6, "Downloading media")); scene.Flush();
        await scene.SettleAsync();
        Assert.Equal(99.6, Bar().Value, 3); Assert.Equal(99, Number().Value, 3);

        // The audio stream starts: the bar and counter restart at once instead of sweeping back from 99%.
        job.Report(new(JobStage.Downloading, 1.5, "Downloading media")); scene.Flush();
        Assert.Equal(1.5, await SampleAsync(() => Bar().Value), 3); Assert.Equal(1, Number().Value, 3);
        await scene.SettleAsync();
        job.Report(new(JobStage.Downloading, 100, "Downloading media")); scene.Flush();
        await scene.SettleAsync();

        // A stage without a percent empties the fill but keeps the track, so the card keeps its shape.
        job.Report(new(JobStage.Merging, Message: "Merging audio and video")); scene.Flush();
        var bar = Bar();
        Assert.Equal(0, bar.Value, 3); Assert.Equal(1, bar.Opacity); Assert.True(bar.IsEffectivelyVisible);
        await scene.FinishAsync();
    }

    [AvaloniaFact]
    public async Task Long_queues_are_virtualized_and_a_double_digit_summary_fits_the_narrowest_window()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 1);
        for (var i = 0; i < 40; i++)
        {
            scene.Model.Url = "https://media.example.org/item-" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            await scene.Model.QueueDownloadAsync(false);
        }
        scene.Flush();
        Assert.Equal(40, scene.Model.Queue.Count);
        Assert.Equal("Waiting · 39th in line", scene.Row(39).Slot.Caption);
        var list = scene.Window.FindNamed<ItemsControl>("QueueItems")!;
        int Realized() => list.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("job") && b.IsEffectivelyVisible);
        Assert.InRange(Realized(), 1, 15);

        var scroller = scene.View.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, scroller.Extent.Height); scene.Flush(); scene.Flush();
        var last = scene.Row(39);
        Assert.Contains(list.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("job") && b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, last));
        Assert.InRange(Realized(), 1, 15);
        scroller.Offset = default; scene.Flush();

        // Finish 24 and fail 3 one after another: "13 active · 24 finished · 3 failed".
        for (var i = 0; i < 27; i++)
        {
            var key = "item-" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            await scene.UntilAsync(() => scene.Downloads.Has(key));
            if (i < 24) scene.Job(key).Complete(key + ".mp4", 1_000_000); else scene.Job(key).Fail("The site refused the request.");
            await scene.UntilAsync(() => !scene.Downloads.Has(key));
        }
        await scene.UntilAsync(() => scene.Model.QueueFinishedCount == 24 && scene.Model.QueueFailedCount == 3);
        Assert.Equal(13, scene.Model.QueueActiveTotal);

        // At the narrowest window the Clear finished label gives way before the summary would wrap,
        // also with the longer "Resume queue"; on a wider window it is back.
        var summary = scene.Window.FindNamed<WrapPanel>("QueueSummary")!;
        var clearLabel = scene.Window.FindNamed<TextBlock>("ClearFinishedLabel")!;
        var toolbar = scene.Window.FindNamed<QueueToolbarPanel>("QueueToolbar")!;
        scene.Window.Width = 880; scene.Flush();
        Assert.True(summary.Bounds.Height < 40, "The queue summary wrapped at the minimum window width.");
        await scene.CaptureAsync("summary-880", width: 880);
        scene.Model.PauseQueueCommand.Execute(null); scene.Window.Width = 880; scene.Flush();
        Assert.True(summary.Bounds.Height < 40, "The queue summary wrapped next to Resume new starts.");
        // The label gives way only when it would not fit (how much fits depends on the font): never both.
        Assert.Equal(!clearLabel.IsVisible, toolbar.IsCompact);
        Assert.Equal("Clear finished", AutomationProperties.GetName(scene.Window.FindNamed<Button>("ClearFinishedButton")!));
        await scene.CaptureAsync("summary-880-paused", width: 880);
        scene.Model.PauseQueueCommand.Execute(null);
        scene.Window.Width = 1180; scene.Flush();
        Assert.True(clearLabel.IsVisible); Assert.False(toolbar.IsCompact); Assert.True(summary.Bounds.Height < 40);
        await scene.FinishAsync();
    }

    [AvaloniaFact]
    public async Task Every_queue_control_has_a_meaningful_accessible_name()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 2);
        await scene.EnqueueAsync("https://media.example.org/done", "Done item");
        await scene.EnqueueAsync("https://media.example.org/broken", "Broken item");
        await scene.EnqueueAsync("https://media.example.org/running", "Running item");
        await scene.EnqueueAsync("https://media.example.org/waiting", "Waiting item");
        scene.Job("done").Complete("Done item.mp4", 1_000_000);
        scene.Job("broken").Fail("The site refused the request.");
        await scene.UntilAsync(() => scene.Row(0).IsFinished && scene.Row(1).IsFailed && scene.Downloads.Has("running") && scene.Downloads.Has("waiting"));
        scene.Job("running").Report(new(JobStage.Downloading, 30, "Downloading media"));
        scene.Row(3).CancelCommand.Execute(null);
        await scene.UntilAsync(() => scene.Row(3).IsCancelled);
        scene.Window.Height = 1600; scene.Flush();

        var buttons = scene.View.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
        Assert.True(buttons.Length >= 10);
        foreach (var button in buttons)
        {
            var name = ControlAutomationPeer.CreatePeerForElement(button).GetName();
            Assert.False(string.IsNullOrWhiteSpace(name), "A queue button has no accessible name.");
            Assert.DoesNotContain("Avalonia.", name);
        }
        string[] Names() => buttons.Select(b => AutomationProperties.GetName(b) ?? "").ToArray();
        Assert.Contains("Open Done item", Names()); Assert.Contains("Retry Broken item", Names()); Assert.Contains("Retry Waiting item", Names());
        Assert.Contains("Cancel downloading Running item", Names());
        var bar = scene.View.GetVisualDescendants().OfType<ProgressBar>().Single(b => b.IsEffectivelyVisible);
        Assert.Equal("Progress of Running item", AutomationProperties.GetName(bar));
        Assert.Contains(scene.View.GetVisualDescendants().OfType<StackPanel>(), panel => AutomationProperties.GetName(panel) == "Download, current step");
        await scene.FinishAsync();
    }

    [Fact]
    public void Eta_and_ordinal_text_stay_short_and_unambiguous()
    {
        Assert.Equal("45 s", QueueRow.FormatEta(TimeSpan.FromSeconds(44.2)));
        Assert.Equal("1 min 12 s", QueueRow.FormatEta(TimeSpan.FromSeconds(72)));
        Assert.Equal("2 min", QueueRow.FormatEta(TimeSpan.FromSeconds(120)));
        Assert.Equal("35 min", QueueRow.FormatEta(TimeSpan.FromSeconds(34 * 60 + 5)));
        Assert.Equal("1 h 3 min", QueueRow.FormatEta(new TimeSpan(1, 2, 3)));
        Assert.Equal("2 h", QueueRow.FormatEta(TimeSpan.FromHours(2)));
        Assert.Equal(new[] { "1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "102nd" }, new[] { 1, 2, 3, 4, 11, 12, 13, 21, 102 }.Select(QueueRow.Ordinal));
    }

    /// <summary>Reads a value one short moment and one animation frame later, when a running transition has visibly moved.</summary>
    private static async Task<double> SampleAsync(Func<double> read)
    {
        await Task.Delay(60);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return read();
    }

    private static int CurrentStep(QueueRow row) => row.Steps.ToList().FindIndex(step => step.IsCurrent);

    private static async Task AssertSequenceAsync(QueueScene scene, string key, DownloadMode mode, params (JobProgress Report, string Label)[] reports)
    {
        await scene.EnqueueAsync("https://media.example.org/" + key, key, mode);
        var row = scene.Model.Queue.Single(r => r.Title == key); var job = scene.Job(key);
        var previous = 0;
        foreach (var (report, label) in reports)
        {
            job.Report(report); scene.Flush();
            var step = CurrentStep(row);
            Assert.True(step >= previous, $"{key}: the stepper moved back from step {previous} to {step} at '{report.Message}'.");
            Assert.Equal(label, row.Steps[2].Label);
            previous = step;
        }
        Assert.Equal(3, previous);
    }
}

/// <summary>A main window on the Queue page backed by a download service the test drives step by step.</summary>
internal sealed class QueueScene : IAsyncDisposable
{
    private readonly string _root;
    private bool _finished;

    private QueueScene(UiFakes fake, ScriptedDownloads downloads, MainViewModel model, MainWindow window, string root)
    {
        Fake = fake; Downloads = downloads; Model = model; Window = window; _root = root;
    }

    public UiFakes Fake { get; }
    public ScriptedDownloads Downloads { get; }
    public MainViewModel Model { get; }
    public MainWindow Window { get; }
    public QueueView View => Window.FindNamed<QueueView>("QueuePage")!;

    public static async Task<QueueScene> CreateAsync(int concurrent, bool reducedMotion = true)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KeaClip-Ui-Queue-" + Guid.NewGuid().ToString("N"));
        var downloadsFolder = System.IO.Path.Combine(root, "Downloads");
        Directory.CreateDirectory(downloadsFolder);
        var fake = new UiFakes();
        fake.Store.Settings = fake.Store.Settings with { ConcurrentDownloads = concurrent, ReducedMotion = reducedMotion, DownloadDirectory = downloadsFolder };
        var downloads = new ScriptedDownloads();
        var model = new MainViewModel(fake.Services with { Downloads = downloads });
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 };
        window.Show();
        await model.InitializeAsync();
        model.SelectedTab = 1;
        var scene = new QueueScene(fake, downloads, model, window, root);
        scene.Layout();
        return scene;
    }

    public QueueRow Row(int index) => Model.Queue[index];
    public ScriptedJob Job(string key) => Downloads.Job(key);

    public async Task EnqueueAsync(string url, string title, DownloadMode mode = DownloadMode.VideoAudio)
    {
        var expected = Model.Queue.Count + 1;
        Downloads.Titles[url] = title;
        Model.DownloadMode = mode;
        Model.Url = url;
        await Model.QueueDownloadAsync(false);
        await UntilAsync(() => Model.Queue.Count == expected);
        Model.DownloadMode = DownloadMode.VideoAudio;
        if (Row(expected - 1).IsRunning) await UntilAsync(() => Downloads.Has(new Uri(url).AbsolutePath.Trim('/')));
        Flush();
    }

    /// <summary>Seeds a synthetic 16:9 thumbnail for a row without any network access.</summary>
    public void SeedThumbnail(int index)
    {
        var row = Row(index);
        var uri = new Uri("https://thumbnails.example.org/" + row.Id.ToString("N") + ".jpg");
        var bitmap = new RenderTargetBitmap(new PixelSize(320, 180));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new LinearGradientBrush { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(0, 1, RelativeUnit.Relative), GradientStops = { new(Color.Parse("#2E5C73"), 0), new(Color.Parse("#E8A867"), 1) } }, new Rect(0, 0, 320, 180));
            var ridge = new StreamGeometry();
            using (var g = ridge.Open()) { g.BeginFigure(new(0, 180), true); g.LineTo(new(0, 120)); g.LineTo(new(70, 70)); g.LineTo(new(120, 105)); g.LineTo(new(190, 40)); g.LineTo(new(260, 100)); g.LineTo(new(320, 80)); g.LineTo(new(320, 180)); g.EndFigure(true); }
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#1E3B33")), null, ridge);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#F6E3B4")), null, new Point(250, 42), 16, 16);
        }
        var type = typeof(MainViewModel);
        ((Dictionary<string, Bitmap>)type.GetField("_thumbnailCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Model)!)[uri.AbsoluteUri] = bitmap;
        ((Dictionary<Guid, Uri>)type.GetField("_jobThumbnails", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Model)!)[row.Id] = uri;
        row.ThumbnailChanged();
    }

    public void Flush() { Dispatcher.UIThread.RunJobs(); Layout(); }

    /// <summary>Lets colour transitions and entrance animations reach their final values before a capture.</summary>
    public async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++) { await Task.Delay(150); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Flush(); }
    }

    /// <summary>Clicks a button with the mouse, so its real command binding runs.</summary>
    public void Click(Button button)
    {
        Layout();
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value;
        Window.MouseDown(center, MouseButton.Left); Window.MouseUp(center, MouseButton.Left);
        Flush();
    }

    public void Layout() { Window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Window.UpdateLayout(); }

    public async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
        Flush();
    }

    public async Task CaptureAsync(string name, double width = 900, double height = 740, double scale = 1)
    {
        Window.Width = width; Window.Height = height; Window.SetRenderScaling(scale);
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        foreach (var theme in new[] { "Dark", "Light" })
        {
            Model.Theme = theme; Flush();
            await SettleAsync();
            using var frame = Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (string.IsNullOrWhiteSpace(output)) continue;
            Directory.CreateDirectory(output);
            frame.Save(System.IO.Path.Combine(output, $"queue-state-{name}-{theme.ToLowerInvariant()}.png"), PngBitmapEncoderOptions.Default);
        }
        Window.Width = 900; Window.Height = 740; Window.SetRenderScaling(1); Model.Theme = "Dark"; Flush();
    }

    /// <summary>Every realized card stays inside the list column and titles never exceed two lines.</summary>
    public void AssertCardsFit()
    {
        var list = Window.FindNamed<ItemsControl>("QueueItems")!;
        var cards = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("job") && b.IsEffectivelyVisible).ToArray();
        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            Assert.True(card.Bounds.Width <= list.Bounds.Width + 0.5, "A queue card is wider than its list.");
            foreach (var control in card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBlock or Button))
            {
                var origin = control.TranslatePoint(new Point(control.Bounds.Width, 0), card);
                Assert.True(origin is { } point && point.X <= card.Bounds.Width + 0.5, $"'{(control as TextBlock)?.Text ?? control.GetType().Name}' overflows its card.");
                if (control is TextBlock text && text.Classes.Contains("job-title")) Assert.True(text.TextLayout.TextLines.Count <= 2, "Titles wrap to at most two lines.");
            }
        }
    }

    public async Task FinishAsync()
    {
        _finished = true;
        foreach (var job in Downloads.All) job.ReleaseCancellation();
        foreach (var row in Model.Queue.Where(r => r.CanCancel).ToArray()) row.CancelCommand.Execute(null);
        await Model.ShutdownAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished) await FinishAsync();
        Model.CloseBehavior = CloseBehavior.Exit;
        Window.Close();
        Model.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A download service whose jobs wait for the test to report progress, complete, fail or cancel them.</summary>
internal sealed class ScriptedDownloads : IDownloadService
{
    private readonly ConcurrentDictionary<string, ScriptedJob> _jobs = new(StringComparer.Ordinal);
    private int _requests;
    public ConcurrentDictionary<string, string> Titles { get; } = new(StringComparer.Ordinal);
    public int Requests => Volatile.Read(ref _requests);
    public IEnumerable<ScriptedJob> All => _jobs.Values;

    public bool Has(string key) => _jobs.TryGetValue(key, out var job) && !job.Completion.Task.IsCompleted;
    public ScriptedJob Job(string key) => _jobs[key];

    public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default)
        => Task.FromResult(UiFakes.Info with { Url = url, Id = url.AbsolutePath.Trim('/'), Title = Titles.TryGetValue(url.AbsoluteUri, out var title) ? title : "Sample media" });

    public async Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requests);
        var job = new ScriptedJob(options, progress!);
        _jobs[options.Url.AbsolutePath.Trim('/')] = job;
        await using var registration = cancellationToken.Register(job.RequestCancellation);
        return await job.Completion.Task;
    }

    public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["sample"]);
}

internal sealed class ScriptedJob(DownloadOptions options, IProgress<JobProgress> progress)
{
    private volatile bool _cancelRequested;
    public DownloadOptions Options { get; } = options;
    public TaskCompletionSource<DownloadResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Keeps a cancelled job running until <see cref="ReleaseCancellation"/>, like a download that takes a moment to stop.</summary>
    public bool HoldCancellation { get; set; }
    public void Report(JobProgress value) => progress.Report(value);
    public void Complete(string fileName, long size, string? title = null)
        => Completion.SetResult(new(System.IO.Path.Combine(Options.OutputDirectory, fileName), UiFakes.Info with { Url = Options.Url, Title = title ?? System.IO.Path.GetFileNameWithoutExtension(fileName) }, size));
    public void Fail(string reason) => Completion.SetException(new MediaOperationException(reason));
    public void RequestCancellation() { _cancelRequested = true; if (!HoldCancellation) Completion.TrySetCanceled(); }
    public void ReleaseCancellation() { HoldCancellation = false; if (_cancelRequested) Completion.TrySetCanceled(); }
}
