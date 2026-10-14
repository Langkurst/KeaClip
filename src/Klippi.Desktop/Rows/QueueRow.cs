using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Infrastructure;

namespace Klippi.Desktop;

public sealed partial class QueueRow : ObservableObject
{
    private QueueJob? _job;
    private readonly MainViewModel _owner;
    public QueueRow(Guid id, MainViewModel owner)
    {
        Id = id; _owner = owner;
        CancelCommand = new RelayCommand(() => owner.CancelJob(Id)); RetryCommand = new RelayCommand(() => owner.RetryJob(Id)); RemoveCommand = new RelayCommand(() => owner.RemoveJob(Id));
        MoveUpCommand = new RelayCommand(() => owner.MoveJob(Id, -1)); MoveDownCommand = new RelayCommand(() => owner.MoveJob(Id, 1));
        OpenCommand = new RelayCommand(() => { if (FilePath is { } path) owner.OpenFile(path); }); RevealCommand = new RelayCommand(() => { if (FilePath is { } path) owner.RevealFile(path); });
        EditCommand = new AsyncRelayCommand(async () => { if (FilePath is { } path) await owner.OpenEditorAsync(path); }); ShareCommand = new AsyncRelayCommand(async () => { if (FilePath is { } path) await owner.ShareFileAsync(path); });
    }
    public Guid Id { get; }
    public string Title => _job?.Title ?? "Queued media";
    public Bitmap? Thumbnail => _owner.QueueThumbnail(Id, _job);
    public void ThumbnailChanged() => OnPropertyChanged(nameof(Thumbnail));
    public double Percent => _job?.Progress.Percent ?? 0;
    public bool IsIndeterminate => !_owner.EffectiveReducedMotion && _job?.State == JobState.Running && _job.Progress.Percent is null;
    public void MotionChanged() => OnPropertyChanged(nameof(IsIndeterminate));
    public bool IsRemoving => _job?.RemovalPending == true;
    public bool CanCancel => !IsRemoving && _job?.State is JobState.Running or JobState.Queued;
    public bool IsActive => _job?.State is JobState.Running or JobState.Queued;
    public bool CanRetry => !IsRemoving && _job?.State is JobState.Cancelled or JobState.Failed;
    public bool CanMove => !IsRemoving && _job?.State == JobState.Queued;
    public bool HasResult => _job?.Result is not null;
    public string? FilePath => _job?.Result?.FilePath;
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand RetryCommand { get; }
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand RevealCommand { get; }
    public IAsyncRelayCommand EditCommand { get; }
    public IAsyncRelayCommand ShareCommand { get; }
    public void Update(QueueJob job) { _job = job; OnPropertyChanged(""); }
}
