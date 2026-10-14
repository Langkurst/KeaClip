using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

public sealed partial class HistoryRow
{
    private readonly MainViewModel _owner;
    // A long history keeps a row per record; commands are created only for rows that are
    // actually used, and each getter keeps returning its first instance (bindings and tests
    // compare them by identity).
    private IRelayCommand? _openCommand, _revealCommand;
    private IAsyncRelayCommand? _editCommand, _shareCommand, _removeCommand, _copyPathCommand, _copySourceCommand, _repeatCommand, _copyShareLinkCommand;

    public HistoryRow(HistoryEntry entry, MainViewModel owner)
    {
        Entry = entry; _owner = owner;
    }
    public HistoryEntry Entry { get; }
    public string Title => Entry.Title;
    public string Detail => $"{Entry.Date.ToLocalTime():g} · {Entry.Mode} · {MainViewModel.Size(Entry.SizeBytes)} · {Entry.Status}";
    public bool HasSource => MediaValidation.TryMediaUrl(Entry.SourceUrl, out _);
    public IRelayCommand OpenCommand => _openCommand ??= new RelayCommand(() => _owner.OpenFile(Entry.FilePath));
    public IRelayCommand RevealCommand => _revealCommand ??= new RelayCommand(() => _owner.RevealFile(Entry.FilePath));
    public IAsyncRelayCommand EditCommand => _editCommand ??= new AsyncRelayCommand(() => _owner.OpenEditorAsync(Entry.FilePath));
    public IAsyncRelayCommand ShareCommand => _shareCommand ??= new AsyncRelayCommand(() => _owner.ShareFileAsync(Entry.FilePath));
    public IAsyncRelayCommand RemoveCommand => _removeCommand ??= new AsyncRelayCommand(() => _owner.RemoveHistoryAsync(Entry.Id));
    public IAsyncRelayCommand CopyPathCommand => _copyPathCommand ??= new AsyncRelayCommand(() => _owner.CopyPathAsync(Entry.FilePath));
    public IAsyncRelayCommand CopySourceCommand => _copySourceCommand ??= new AsyncRelayCommand(() => _owner.CopySourceAsync(Entry));
    public IAsyncRelayCommand RepeatCommand => _repeatCommand ??= new AsyncRelayCommand(() => _owner.RepeatDownloadAsync(Entry));
    /// <summary>A share link was made for this file in this session; Copy link copies it (it is kept in memory only).</summary>
    public bool HasShareLink => _owner.ShareLinkFor(Entry.FilePath) is not null;
    public IAsyncRelayCommand CopyShareLinkCommand => _copyShareLinkCommand ??= new AsyncRelayCommand(() => _owner.CopyShareLinkAsync(Entry.FilePath));
    internal void ShareLinkChanged() => OnPropertyChanged(nameof(HasShareLink));
}
