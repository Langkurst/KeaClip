using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Infrastructure;

namespace Klippi.Desktop;

// Queue page presentation: header summary counts, queue positions for waiting jobs, the
// per-attempt stage history behind each card's stepper and the empty-state shortcut back
// to Download.
public sealed partial class MainViewModel
{
    private static readonly string[] QueueSummaryProperties =
        [nameof(HasQueueItems), nameof(QueueActiveTotal), nameof(HasQueueActive), nameof(QueueFinishedCount), nameof(HasQueueFinished), nameof(QueueFailedCount), nameof(HasQueueFailures), nameof(CanClearFinished)];
    // Keyed by the job instance: a retry is a new instance with the same Id, so it starts a fresh history.
    private readonly ConditionalWeakTable<QueueJob, QueueStageTrace> _queueTraces = new();
    private IRelayCommand? _queueGoToDownloadCommand;
    private QueueCounts _queueCounts;
    private bool _queueTracing;

    public IRelayCommand QueueGoToDownloadCommand => _queueGoToDownloadCommand ??= new RelayCommand(() => SelectedTab = 0);

    public bool HasQueueItems => _queueCounts.Total > 0;
    /// <summary>Downloading plus waiting jobs, matching the sidebar badge.</summary>
    public int QueueActiveTotal => _queueCounts.Running + _queueCounts.Waiting;
    public bool HasQueueActive => QueueActiveTotal > 0;
    public int QueueFinishedCount => _queueCounts.Finished;
    public bool HasQueueFinished => _queueCounts.Finished > 0;
    public int QueueFailedCount => _queueCounts.Failed;
    public bool HasQueueFailures => _queueCounts.Failed > 0;
    /// <summary>Clear finished removes finished and cancelled items (never failed ones, never files).</summary>
    public bool CanClearFinished => _queueCounts.Finished + _queueCounts.Cancelled > 0;

    internal QueueStageTrace QueueTrace(QueueJob job) => _queueTraces.GetValue(job, static _ => new QueueStageTrace());

    partial void OnQueueSynchronised()
    {
        // Job events are coalesced into one UI refresh per dispatcher cycle, which can skip a short stage.
        // The trace also listens on the engine's thread so every reported stage reaches the stepper.
        if (!_queueTracing) { _queueTracing = true; _queue.JobChanged += TraceQueueJob; }
        int running = 0, waiting = 0, finished = 0, failed = 0, cancelled = 0, movable = 0;
        foreach (var row in Queue)
        {
            if (row.CanMove) movable++;
            switch (row.State)
            {
                case JobState.Running: running++; break;
                case JobState.Queued or JobState.Paused: waiting++; break;
                case JobState.Finished: finished++; break;
                case JobState.Failed: failed++; break;
                case JobState.Cancelled: cancelled++; break;
            }
        }
        // Positions live on each row's Slot, so only captions that really changed are refreshed.
        var position = 0;
        foreach (var row in Queue) row.SetQueuePosition(row.CanMove ? ++position : 0, movable);
        var counts = new QueueCounts(Queue.Count, running, waiting, finished, failed, cancelled);
        if (counts == _queueCounts) return;
        _queueCounts = counts;
        foreach (var name in QueueSummaryProperties) OnPropertyChanged(name);
    }

    private void TraceQueueJob(QueueJob job) => QueueTrace(job).Observe(job.Progress, job.Options.Mode);

    private readonly record struct QueueCounts(int Total, int Running, int Waiting, int Finished, int Failed, int Cancelled);
}
