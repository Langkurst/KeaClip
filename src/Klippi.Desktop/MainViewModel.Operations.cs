using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

/// <summary>Independent areas of work. Each lane runs one operation at a time.</summary>
public enum OperationLane { Download, Editor, Share, Maintenance }

// Long operations run in lanes instead of behind one app-wide busy gate, so a long
// file.kiwi upload or export no longer locks Analyse, Download, Settings or the editor.
public sealed partial class MainViewModel
{
    private sealed class LaneState
    {
        public CancellationTokenSource? Cancellation;
        public Task Finished = Task.CompletedTask;
        public double? Percent;
    }

    private readonly Dictionary<OperationLane, LaneState> _lanes = Enum.GetValues<OperationLane>().ToDictionary(lane => lane, _ => new LaneState());
    private string? _pendingEditorPath;
    private IRelayCommand? _cancelDownloadLane, _cancelEditorLane, _cancelShareLane, _cancelMaintenanceLane;

    /// <summary>True while any lane is working (status ring, window "busy" class).</summary>
    public bool IsBusy => _lanes.Values.Any(lane => lane.Cancellation is not null);
    public bool NotBusy => !IsBusy;
    public bool CanDownload => !IsLaneBusy(OperationLane.Download);
    public bool CanEdit => !IsLaneBusy(OperationLane.Editor);
    public bool CanShare => !IsLaneBusy(OperationLane.Share);
    public bool CanMaintain => !IsLaneBusy(OperationLane.Maintenance);
    public bool IsMaintaining => IsLaneBusy(OperationLane.Maintenance);
    public double ExportPercent => _lanes[OperationLane.Editor].Percent ?? 0;
    public bool HasExportPercent => _lanes[OperationLane.Editor].Percent is not null;
    public double SharePercent => _lanes[OperationLane.Share].Percent ?? 0;
    public bool HasSharePercent => _lanes[OperationLane.Share].Percent is not null;
    public double MaintenancePercent => _lanes[OperationLane.Maintenance].Percent ?? 0;
    public bool HasMaintenancePercent => _lanes[OperationLane.Maintenance].Percent is not null;

    public IRelayCommand CancelDownloadOperationCommand => _cancelDownloadLane ??= new RelayCommand(() => CancelLane(OperationLane.Download));
    public IRelayCommand CancelEditorOperationCommand => _cancelEditorLane ??= new RelayCommand(() => CancelLane(OperationLane.Editor));
    public IRelayCommand CancelShareOperationCommand => _cancelShareLane ??= new RelayCommand(() => CancelLane(OperationLane.Share));
    public IRelayCommand CancelMaintenanceOperationCommand => _cancelMaintenanceLane ??= new RelayCommand(() => CancelLane(OperationLane.Maintenance));

    private bool IsLaneBusy(OperationLane lane) => _lanes[lane].Cancellation is not null;
    private void CancelLane(OperationLane lane) => _lanes[lane].Cancellation?.Cancel();
    private void CancelAllLanes() { foreach (var lane in _lanes.Values) lane.Cancellation?.Cancel(); }
    // The header's Cancel stops the work in front of the user. An upload cannot resume, so it
    // is stopped here only when nothing else is running; the share card's own Cancel always can.
    private void CancelCurrentOperations()
    {
        var others = _lanes.Where(pair => pair.Key != OperationLane.Share && pair.Value.Cancellation is not null).Select(pair => pair.Value).ToList();
        if (others.Count == 0) { CancelLane(OperationLane.Share); return; }
        foreach (var state in others) state.Cancellation?.Cancel();
    }
    private Task AllLanesFinished() => Task.WhenAll(_lanes.Values.Select(lane => lane.Finished));

    private async Task RunAsync(OperationLane lane, Func<CancellationToken, Task> action)
    {
        if (_disposed) return;
        var state = _lanes[lane];
        if (state.Cancellation is not null) { Status = LaneBusyMessage(lane); return; }
        using var cancellation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Cancellation = cancellation; state.Finished = finished.Task;
        LaneChanged(lane);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "Cancelled. Anything that already finished is in your queue or library."; }
        catch (Exception error) { CaptureDiagnostics(error); Status = FriendlyError(error); }
        finally
        {
            // Store recovery notices (for example history restored from a backup) go first:
            // the status bar is one line and trims its end.
            if (_services.Store.TakeWarning() is { } notice) Status = MediaValidation.Redact(notice) + " " + Status;
            state.Cancellation = null; state.Percent = null;
            LaneChanged(lane);
            finished.TrySetResult();
        }
    }

    private static string LaneBusyMessage(OperationLane lane) => lane switch
    {
        OperationLane.Download => "Still checking the current link. Wait a moment or cancel it first.",
        OperationLane.Editor => "The editor is still working. Wait for it to finish or cancel the export.",
        OperationLane.Share => "An upload is already running. Wait for it to finish or cancel it first.",
        _ => "A settings or component task is still running. Wait for it to finish or cancel it first.",
    };

    /// <summary>
    /// Progress for the operation now running in <paramref name="lane"/>. Reports are applied on the UI
    /// thread only while that same operation runs and until <see cref="OperationProgress.Close"/>: a
    /// report still queued when the service call returned, or one arriving after the lane moved on,
    /// must never overwrite the final status ("Export verified") or revive the progress bar.
    /// </summary>
    /// <param name="transferBytes">For an upload, the file size, so the status can show the amount sent.</param>
    private OperationProgress Progress(OperationLane lane, long? transferBytes = null) => new(this, lane, transferBytes);

    internal sealed class OperationProgress : IProgress<JobProgress>
    {
        private readonly MainViewModel _owner;
        private readonly OperationLane _lane;
        private readonly CancellationTokenSource? _operation;
        private readonly long? _transferBytes;
        private readonly IProgress<JobProgress> _dispatch;
        private volatile bool _closed;
        private double? _lastUploadPercent;

        internal OperationProgress(MainViewModel owner, OperationLane lane, long? transferBytes)
        {
            _owner = owner; _lane = lane; _operation = owner._lanes[lane].Cancellation; _transferBytes = transferBytes is > 0 ? transferBytes : null;
            // Progress<T> captures the UI context here, so Apply always runs on the UI thread.
            _dispatch = new Progress<JobProgress>(Apply);
        }

        public void Report(JobProgress value) { if (!_closed) _dispatch.Report(value); }

        /// <summary>Ignores every later report, including ones already queued for the UI thread.</summary>
        public void Close() => _closed = true;

        private void Apply(JobProgress p)
        {
            if (_closed || _owner._disposed || _operation is null || !ReferenceEquals(_owner._lanes[_lane].Cancellation, _operation)) return;
            var percent = p.Percent is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : (double?)null;
            // An upload that pauses to retry reports no percentage; the bytes already sent still count,
            // so the last value stays on screen instead of an indeterminate bar.
            if (p.Stage == JobStage.Uploading) { percent ??= _lastUploadPercent; _lastUploadPercent = percent; }
            var detail = Detail(p, percent);
            _owner.Status = string.Join(" · ", new[] { StageLabel(p.Stage), percent is { } shown ? $"{shown:0.#}%" : "", detail }.Where(part => part.Length > 0));
            if (_lane == OperationLane.Share) _owner.ShareProgressText = detail;
            _owner._lanes[_lane].Percent = percent; _owner.LanePercentChanged(_lane); _owner.SetOperationPercent(percent);
        }

        // The status reads "Uploading · 37% · 12.4 MB of 35 MB" while an upload moves and
        // "Stage · 42% · message" otherwise. The routine "Uploading encrypted data…" repeats the
        // stage, so an upload shows the amount sent instead; anything else (a retry notice) is kept.
        private string Detail(JobProgress p, double? percent)
        {
            var parts = new List<string>(2);
            var uploading = p.Stage == JobStage.Uploading && _transferBytes is not null && percent is not null;
            if (uploading) parts.Add($"{Size((long)Math.Round(_transferBytes!.Value * percent!.Value / 100))} of {Size(_transferBytes.Value)}");
            var message = MediaValidation.Redact(p.Message).Trim();
            if (message.Length > 0 && !(uploading && message.StartsWith("Uploading", StringComparison.OrdinalIgnoreCase))) parts.Add(message);
            return string.Join(" · ", parts);
        }
    }

    internal static string StageLabel(JobStage stage) => stage switch
    {
        JobStage.WritingMetadata => "Writing metadata",
        _ => stage.ToString(),
    };

    private void LaneChanged(OperationLane lane)
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(NotBusy)); OnPropertyChanged(nameof(AnimateBusyIndicator));
        OnPropertyChanged(nameof(CanCleanTemporaryFiles)); CleanTemporaryFilesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(lane switch { OperationLane.Download => nameof(CanDownload), OperationLane.Editor => nameof(CanEdit), OperationLane.Share => nameof(CanShare), _ => nameof(CanMaintain) });
        if (lane == OperationLane.Maintenance) OnPropertyChanged(nameof(IsMaintaining));
        LanePercentChanged(lane);
        if (!IsBusy) SetOperationPercent(null);
        // "Download & edit" that finished while the editor was busy opens now.
        if (lane == OperationLane.Editor && !IsLaneBusy(lane) && _pendingEditorPath is { } pending && !_disposed)
        {
            _pendingEditorPath = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_disposed) _ = OpenEditorAsync(pending); });
        }
    }

    private void LanePercentChanged(OperationLane lane)
    {
        switch (lane)
        {
            case OperationLane.Editor: OnPropertyChanged(nameof(ExportPercent)); OnPropertyChanged(nameof(HasExportPercent)); break;
            case OperationLane.Share: OnPropertyChanged(nameof(SharePercent)); OnPropertyChanged(nameof(HasSharePercent)); break;
            case OperationLane.Maintenance: OnPropertyChanged(nameof(MaintenancePercent)); OnPropertyChanged(nameof(HasMaintenancePercent)); break;
        }
    }

    /// <summary>Opens a finished download in the editor now, or as soon as the editor is free.</summary>
    private Task RequestEditorOpenAsync(string path)
    {
        if (CanEdit) return OpenEditorAsync(path);
        _pendingEditorPath = path;
        Status = "Download finished. It will open in the editor as soon as the current editor task ends.";
        return Task.CompletedTask;
    }
}
