using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Klippi.Core;
using Klippi.Infrastructure;

namespace Klippi.Desktop;

// Presentation state for a queue card: state chip, stage stepper, speed/ETA and the
// finished/failed summaries. QueueRow.Update raises PropertyChanged("") after engine
// reports for this job only, so the stage tracking below runs once per refresh, before
// bindings re-read the computed properties. The stage history itself is recorded per
// download attempt by QueueStageTrace on the engine's thread, so stages shorter than one
// UI refresh are not lost.
public sealed partial class QueueRow
{
    private const string GenericDownloadMessage = "Downloading media";
    private readonly QueueStep[] _steps = [new("Analyse", isFirst: true), new("Download"), new(QueueStageTrace.PendingWorkLabel), new("Done")];
    private int _downloadPart = 1;
    private string? _meta, _folder;

    /// <summary>The four pipeline steps shown under a running job. The same instances are reused for the row's lifetime.</summary>
    public IReadOnlyList<QueueStep> Steps => _steps;

    /// <summary>Place in line for a waiting job. A separate object, so a job starting elsewhere in the queue
    /// updates only the captions and move buttons that changed instead of refreshing this whole card.</summary>
    public QueueSlot Slot { get; } = new();

    public JobState State => _job?.State ?? JobState.Queued;
    public bool IsQueued => State is JobState.Queued or JobState.Paused;
    public bool IsRunning => State == JobState.Running;
    public bool IsFinished => State == JobState.Finished;
    public bool IsFailed => State == JobState.Failed;
    public bool IsCancelled => State == JobState.Cancelled;
    /// <summary>The running dot breathes only while the job is really working (not while it is being removed).</summary>
    public bool IsLive => IsRunning && !IsRemoving;
    public bool IsChipAccent => (IsRunning || IsFinished) && !IsRemoving;
    public bool IsChipQuiet => IsCancelled || IsRemoving;

    // Actions: queued jobs are removed (that cancels them); running jobs can be cancelled, or cancelled and removed.
    public bool ShowCancel => CanCancel && IsRunning;
    public bool CanRemove => !IsRemoving && _job is not null;
    public bool ShowRetry => CanRetry && IsCancelled;
    public string RemoveHint => State switch
    {
        JobState.Running => "Cancel and remove from the list",
        JobState.Queued or JobState.Paused => "Remove from the queue",
        JobState.Finished => "Remove from the list · the saved file is kept",
        _ => "Remove from the list",
    };

    public string DisplayTitle => _job?.Result?.Media?.Title is { Length: > 0 } title ? title : Title;
    public DownloadMode Mode => _job?.Options.Mode ?? DownloadMode.VideoAudio;
    public bool HasThumbnail => Thumbnail is not null;

    public string StateLabel => IsRemoving ? "Removing…" : State switch
    {
        JobState.Running => _job!.Progress.Stage switch
        {
            JobStage.Queued or JobStage.Analysing => "Starting",
            JobStage.Downloading => "Downloading",
            JobStage.Merging => "Merging",
            JobStage.Converting => "Converting",
            JobStage.Compressing => "Compressing",
            JobStage.WritingMetadata => "Writing metadata",
            JobStage.Verifying => "Verifying",
            JobStage.Uploading => "Uploading",
            JobStage.Finished => "Finishing",
            _ => "Working",
        },
        JobState.Finished => "Finished",
        JobState.Failed => "Failed",
        JobState.Cancelled => "Cancelled",
        JobState.Paused => "Paused",
        _ => "Queued",
    };

    /// <summary>Source host, mode and quality. Job options never change (a retry keeps them), so the text is built once.</summary>
    public string Meta => _meta ?? (_job is { } job ? _meta = BuildMeta(job.Options) : "");

    private static string BuildMeta(DownloadOptions options)
    {
        var host = options.Url.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        var mode = options.Mode switch { DownloadMode.Audio => "Audio only", DownloadMode.VideoOnly => "Video only", DownloadMode.Archive => "Archive", _ => "Video + audio" };
        var quality = options.Mode == DownloadMode.Audio
            ? options.AudioFormat == AudioFormat.Original ? null : options.AudioFormat.ToString().ToUpperInvariant()
            : options.MaxHeight is { } height ? "up to " + height.ToString(CultureInfo.InvariantCulture) + "p" : options.SmallestFile ? "smallest file" : null;
        return quality is null ? $"{host} · {mode}" : $"{host} · {mode} · {quality}";
    }

    // Running: percent, stage caption, speed and ETA.
    public bool HasPercent => IsRunning && _job!.Progress.Percent is not null;
    /// <summary>The counter shows whole percents rounded down, so it never reads 100% while a stream is still downloading.</summary>
    public double DisplayPercent => Math.Floor(Percent);
    public bool IsPercentUnknown => IsRunning && _job!.Progress.Percent is null;
    /// <summary>Static stand-in for the spinning ring while reduced motion is on.</summary>
    public bool ShowWaitIcon => IsPercentUnknown && _owner.EffectiveReducedMotion;

    public string StageCaption
    {
        get
        {
            if (_job is not { State: JobState.Running } job) return "";
            if (IsRemoving) return "Stopping, then removing it from the list";
            var progress = job.Progress;
            var message = MediaValidation.Redact(progress.Message).Trim();
            if (progress.Stage == JobStage.Downloading)
            {
                // The chip and stepper already say "Downloading": the caption adds where it goes and which stream this is.
                if (message.Length > 0 && message != GenericDownloadMessage && message.StartsWith("Downloading", StringComparison.Ordinal)) return message;
                var folder = _folder ??= FolderName(job.Options.OutputDirectory);
                return _downloadPart > 1 ? "Stream " + _downloadPart.ToString(CultureInfo.InvariantCulture) + " · saving to " + folder : "Saving to " + folder;
            }
            if (message.Length > 0) return message;
            return progress.Stage switch
            {
                JobStage.Queued => "Starting",
                JobStage.Analysing => "Checking the selected media",
                JobStage.Merging => "Merging audio and video",
                JobStage.Converting => "Converting",
                JobStage.Compressing => "Compressing",
                JobStage.WritingMetadata => "Writing metadata",
                JobStage.Verifying => "Checking the result",
                JobStage.Uploading => "Uploading",
                JobStage.Finished => "Adding it to your library",
                _ => "Working",
            };
        }
    }

    // A job being removed is stopping: its speed and time left no longer mean anything.
    public bool HasSpeed => IsRunning && !IsRemoving && _job!.Progress.BytesPerSecond is > 0;
    public string SpeedText => _job?.Progress.BytesPerSecond is { } speed && speed > 0 ? MainViewModel.Size((long)speed) + "/s" : "";
    public bool HasEta => IsRunning && !IsRemoving && _job!.Progress.Eta is { } eta && eta > TimeSpan.Zero;
    public string EtaText => _job?.Progress.Eta is { } eta && eta > TimeSpan.Zero ? FormatEta(eta) + " left" : "";

    // Finished, failed and cancelled summaries.
    public string ResultSize => _job?.Result is { } result ? MainViewModel.Size(Math.Max(0, result.SizeBytes)) : "";
    public string ResultFileName => _job?.Result is { } result ? Path.GetFileName(result.FilePath) : "";
    public bool HasFinishWarning => IsFinished && !string.IsNullOrWhiteSpace(_job!.Error);
    public string FinishWarning => HasFinishWarning ? MediaValidation.Redact(_job!.Error) : "";
    public string FailureReason => !IsFailed ? "" : MediaValidation.Redact(_job!.Error) is { Length: > 0 } reason ? reason : "The download stopped unexpectedly. Retry, or check Settings › Components if it keeps happening.";

    internal void SetQueuePosition(int position, int waiting) => Slot.Update(position, waiting);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        // "" (or null) refreshes every binding: bring the derived stage state up to date first.
        if (string.IsNullOrEmpty(e.PropertyName)) TrackProgress();
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsIndeterminate)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowWaitIcon)));
        else if (e.PropertyName == nameof(Thumbnail)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasThumbnail)));
    }

    private void TrackProgress()
    {
        if (_job is not { } job) return;
        // A retry reuses this row with a new job instance, which starts a fresh trace.
        var trace = _owner.QueueTrace(job);
        trace.Observe(job.Progress, job.Options.Mode);
        var (step, part, work) = trace.Read();
        _downloadPart = part;
        var current = job.State switch { JobState.Running => step, JobState.Finished => _steps.Length, _ => -1 };
        for (var i = 0; i < _steps.Length; i++)
            _steps[i].Update(i == QueueStageTrace.WorkStep ? work : null, isDone: i < current, isCurrent: i == current);
    }

    private static string FolderName(string directory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(directory);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    /// <summary>Short, unambiguous remaining time: "45 s", "1 min 12 s", "34 min", "1 h 2 min".</summary>
    internal static string FormatEta(TimeSpan eta)
    {
        var seconds = (long)Math.Ceiling(eta.TotalSeconds);
        var invariant = CultureInfo.InvariantCulture;
        if (seconds < 60) return seconds.ToString(invariant) + " s";
        if (seconds < 600) return seconds % 60 == 0 ? string.Create(invariant, $"{seconds / 60} min") : string.Create(invariant, $"{seconds / 60} min {seconds % 60} s");
        var minutes = (seconds + 59) / 60;
        if (minutes < 60) return minutes.ToString(invariant) + " min";
        return minutes % 60 == 0 ? string.Create(invariant, $"{minutes / 60} h") : string.Create(invariant, $"{minutes / 60} h {minutes % 60} min");
    }

    internal static string Ordinal(int value)
    {
        var suffix = (value % 100) is 11 or 12 or 13 ? "th" : (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}

/// <summary>
/// The stage history of one download attempt, fed with every engine report (on the engine's thread) and
/// read by its queue card on the UI thread. The stepper only ever moves forward: the engine checks the
/// downloaded file before converting, remuxing or archiving it and checks again afterwards, so all work
/// after the download shares one step whose label names what is actually happening.
/// </summary>
public sealed class QueueStageTrace
{
    internal const string PendingWorkLabel = "Process";
    internal const int WorkStep = 2;
    private readonly object _sync = new();
    private JobProgress? _last;
    private double _lastDownloadPercent = -1;
    private int _part = 1, _step;
    private bool _checked;
    private string _work = PendingWorkLabel;

    internal void Observe(JobProgress progress, DownloadMode mode)
    {
        lock (_sync)
        {
            if (ReferenceEquals(progress, _last)) return;
            _last = progress;
            var stage = progress.Stage;
            // yt-dlp reports each stream (video, audio, subtitles) from 0 to 100 in turn; a large
            // drop after a near-complete stream means the next stream started, not a regression.
            if (stage == JobStage.Downloading && progress.Percent is { } percent)
            {
                if (_lastDownloadPercent >= 90 && percent <= _lastDownloadPercent - 50) _part++;
                _lastDownloadPercent = percent;
            }
            _step = Math.Max(_step, StepOf(stage));
            _work = stage switch
            {
                // Before the file check this is yt-dlp joining streams; afterwards it is a remux into the chosen container.
                JobStage.Merging => _checked ? "Repack" : "Merge",
                // A remux copies streams through the exporter, which reports Converting: it is still not a conversion.
                JobStage.Converting => _work == "Repack" ? _work : "Convert",
                JobStage.Compressing => "Compress",
                JobStage.WritingMetadata => mode == DownloadMode.Archive && _checked ? "Archive" : "Metadata",
                JobStage.Uploading => "Upload",
                // The first check is the downloaded file; later checks belong to the conversion, remux or archive they verify.
                JobStage.Verifying => _checked && _work is "Repack" or "Convert" or "Compress" or "Archive" ? _work : "Check",
                _ => _work,
            };
            if (stage == JobStage.Verifying) _checked = true;
        }
    }

    internal (int Step, int Part, string Work) Read()
    {
        lock (_sync) return (_step, _part, _work);
    }

    private static int StepOf(JobStage stage) => stage switch
    {
        JobStage.Queued or JobStage.Analysing => 0,
        JobStage.Downloading => 1,
        JobStage.Merging or JobStage.Converting or JobStage.Compressing or JobStage.WritingMetadata or JobStage.Verifying or JobStage.Uploading => WorkStep,
        JobStage.Finished => 3,
        _ => 0,
    };
}

/// <summary>One step of a queue card's stage stepper. Raises change notifications only when its state actually changes.</summary>
public sealed class QueueStep(string label, bool isFirst = false) : ObservableObject
{
    private readonly string _defaultLabel = label;
    private string _label = label;
    private bool _isDone, _isCurrent;

    public string Label { get => _label; private set { if (SetProperty(ref _label, value)) OnPropertyChanged(nameof(AutomationName)); } }
    public bool IsFirst { get; } = isFirst;
    public bool IsDone { get => _isDone; private set { if (SetProperty(ref _isDone, value)) OnStateChanged(); } }
    public bool IsCurrent { get => _isCurrent; private set { if (SetProperty(ref _isCurrent, value)) OnStateChanged(); } }
    public bool IsPending => !_isDone && !_isCurrent;
    /// <summary>The connector leading into this step lights up once the step is reached.</summary>
    public bool IsReached => _isDone || _isCurrent;
    /// <summary>What a screen reader announces for the step: its name and whether it is done, current or still to come.</summary>
    public string AutomationName => _label + (_isCurrent ? ", current step" : _isDone ? ", done" : ", not started");

    internal void Update(string? label, bool isDone, bool isCurrent)
    {
        Label = label ?? _defaultLabel;
        IsDone = isDone && !isCurrent;
        IsCurrent = isCurrent;
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsReached));
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// <summary>A waiting job's place in line and whether it can move up or down.</summary>
public sealed class QueueSlot : ObservableObject
{
    private int _position, _waiting;

    public string Caption => _position <= 1 ? "Waiting · next to start" : "Waiting · " + QueueRow.Ordinal(_position) + " in line";
    public bool CanMoveUp => _position > 1;
    public bool CanMoveDown => _position > 0 && _position < _waiting;

    internal void Update(int position, int waiting)
    {
        if (_position == position && _waiting == waiting) return;
        _position = position; _waiting = waiting;
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }
}
