namespace Klippi.Core;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default);
}
public interface IComponentService
{
    string? Find(string name);
    Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default);
    Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
    Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
    Task RollbackAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromException(new MediaOperationException("No previous component version is available to restore."));
}
public interface IDownloadService
{
    Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default);
    Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default);
}
public interface IMediaService
{
    Task<ProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);
    Task<ExportResult> ExportAsync(ExportOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EncoderInfo>> EncodersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<float>> WaveformAsync(string path, int points = 600, CancellationToken cancellationToken = default);
    /// <summary>One still frame as PNG, <paramref name="width"/> pixels wide (for the crop preview); null when there's no picture.</summary>
    Task<byte[]?> FrameAsync(string path, TimeSpan at, int width, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    ExportEstimate Estimate(ProbeInfo probe, ExportOptions options);
}
public interface ISharingService
{
    Task<ShareResult> ShareAsync(string path, string? title = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
}
public interface ILocalStore
{
    // Upper bound for AppSettings.HistoryRetentionDays (about 100 years); larger values are rejected when saving.
    const int MaxHistoryRetentionDays = 36500;
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken = default);
    Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default);
    Task RemoveHistoryAsync(string id, CancellationToken cancellationToken = default);
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
    Task PruneHistoryAsync(int? retentionDays, CancellationToken cancellationToken = default);
    // Plain-language notice that saved data was restored from a backup or that unreadable history was set aside and a new
    // one started; null when nothing needs telling. TakeWarning returns the notice and clears it so it is shown once.
    string? LastWarning => null;
    string? TakeWarning() => null;
    // Unfinished queue jobs kept across restarts. Null when nothing was saved (or an unreadable file was set aside).
    Task<SavedQueue?> LoadQueueAsync(CancellationToken cancellationToken = default) => Task.FromResult<SavedQueue?>(null);
    Task SaveQueueAsync(SavedQueue queue, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
