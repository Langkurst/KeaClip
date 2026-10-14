using Avalonia.Headless.XUnit;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Quiet yt-dlp updates: checked a minute after start and daily, never while something downloads or is busy, and a
// failed check stays quiet (recorded in diagnostics) with the working copy kept.
public sealed class DownloaderUpdateTests
{
    private sealed class QuietComponents : IComponentService
    {
        public string Version { get; set; } = "2026.08.19";
        public string? NextVersion { get; set; }
        public Exception? Failure { get; set; }
        public int Updates { get; private set; }
        public string? Find(string name) => null;
        public Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentInfo>>([new("yt-dlp", null, Version, true), new("ffmpeg", null, "9.0.2", true)]);
        public Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Updates++;
            if (Failure is { } failure) return Task.FromException(failure);
            if (NextVersion is { } next) Version = next;
            return Task.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task An_idle_app_picks_up_a_newer_downloader_quietly()
    {
        var components = new QuietComponents { NextVersion = "2026.10.10" };
        using var model = new MainViewModel(new UiFakes().Services with { Components = components }) { DownloaderCheckDelay = TimeSpan.FromHours(1) };
        await model.InitializeAsync();
        var status = model.Status;
        Assert.True(await model.QuietlyUpdateDownloaderAsync(CancellationToken.None));
        Assert.Equal(1, components.Updates);
        Assert.Contains(model.Components, component => component.Name == "yt-dlp" && component.Version == "2026.10.10");
        Assert.Equal(status, model.Status);
        // Already the newest: asked again, nothing changes.
        Assert.False(await model.QuietlyUpdateDownloaderAsync(CancellationToken.None));
        Assert.Equal(2, components.Updates);
    }

    [AvaloniaFact]
    public async Task Nothing_is_checked_while_a_download_runs_and_a_failure_stays_quiet()
    {
        var components = new QuietComponents { NextVersion = "2026.10.10" };
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services with { Components = components }) { DownloaderCheckDelay = TimeSpan.FromHours(1), Url = UiFakes.Info.Url.AbsoluteUri };
        await model.InitializeAsync();
        await model.QueueDownloadAsync(false);
        await Until(() => model.HasActiveQueue);
        Assert.False(await model.QuietlyUpdateDownloaderAsync(CancellationToken.None));
        Assert.Equal(0, components.Updates);
        model.Queue[0].CancelCommand.Execute(null);
        await Until(() => !model.HasActiveQueue && !model.IsBusy);

        components.Failure = new MediaOperationException("GitHub could not be reached.");
        var status = model.Status;
        Assert.False(await model.QuietlyUpdateDownloaderAsync(CancellationToken.None));
        Assert.Equal(1, components.Updates);
        Assert.Equal(status, model.Status);
        Assert.Contains("GitHub could not be reached", model.Diagnostics);
        Assert.Contains(model.Components, component => component.Version == "2026.08.19");
        await model.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task The_schedule_checks_after_its_delay_and_stops_when_the_app_closes()
    {
        var components = new QuietComponents();
        var model = new MainViewModel(new UiFakes().Services with { Components = components }) { DownloaderCheckDelay = TimeSpan.FromMilliseconds(50), DownloaderCheckInterval = TimeSpan.FromHours(24) };
        await model.InitializeAsync();
        await Until(() => components.Updates == 1);
        model.Dispose();
        await model.DownloaderScheduleTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, components.Updates);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
