using Klippi.Core;

namespace Klippi.Desktop;

// Quiet downloader updates. Sites change often and an old yt-dlp is the usual reason a download suddenly fails, so
// KeaClip asks for the latest official release a minute after it starts and then once a day. The component service
// downloads only when the official release differs from the verified copy in use, and keeps that copy if anything
// fails. It never runs while something downloads or another task is busy, and it never interrupts the user.
public sealed partial class MainViewModel
{
    internal TimeSpan DownloaderCheckDelay { get; set; } = TimeSpan.FromSeconds(60);
    internal TimeSpan DownloaderCheckInterval { get; set; } = TimeSpan.FromHours(24);
    private CancellationTokenSource? _downloaderSchedule;
    internal Task? DownloaderScheduleTask { get; private set; }

    private void StartDownloaderUpdates()
    {
        if (_disposed) return;
        _downloaderSchedule?.Cancel();
        var schedule = _downloaderSchedule = new CancellationTokenSource();
        DownloaderScheduleTask = RunDownloaderScheduleAsync(schedule.Token);
    }

    private void StopDownloaderUpdates() => _downloaderSchedule?.Cancel();

    private async Task RunDownloaderScheduleAsync(CancellationToken token)
    {
        try
        {
            var delay = DownloaderCheckDelay;
            while (!token.IsCancellationRequested && !_disposed)
            {
                await Task.Delay(delay, token);
                delay = DownloaderCheckInterval;
                await QuietlyUpdateDownloaderAsync(token);
            }
        }
        catch (OperationCanceledException) { /* Closing. */ }
    }

    /// <summary>One quiet check; true when a newer yt-dlp was installed. Skipped while anything is busy or downloading.</summary>
    internal async Task<bool> QuietlyUpdateDownloaderAsync(CancellationToken token)
    {
        if (_disposed || !ComponentsReady || IsBusy || HasActiveQueue) return false;
        var before = DownloaderVersion();
        try { await _services.Components.UpdateDownloaderAsync(null, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { CaptureDiagnostics(error); return false; }
        if (_disposed) return false;
        try { await InspectComponentsAsync(token); }
        catch (Exception error) when (error is not OperationCanceledException) { CaptureDiagnostics(error); return false; }
        return DownloaderVersion() is { } after && after != before;
    }

    private string? DownloaderVersion() => Components.FirstOrDefault(component => component.Name == "yt-dlp")?.Version;
}
