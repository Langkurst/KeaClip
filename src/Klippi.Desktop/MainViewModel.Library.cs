using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

// Library page presentation and its quick local actions.
// - State (loading, empty, no results, unreadable) is derived here, and notifications are
//   coalesced into one batch posted ahead of layout instead of one per row change.
// - Search: the box keeps the text as typed; the filter uses it trimmed with whitespace
//   collapsed, is debounced for long histories, and reuses rows by record id, so typing
//   keeps each row's file check and container instead of rebuilding the list every keystroke.
// - Remove, Clear and Try again are local-store operations: they do not take the app-wide
//   busy state, so they never show the shell's Cancel button (no header reflow) and work
//   while a download or analysis is running. They are serialised among themselves, and the
//   store serialises them with everything else that writes history.
public sealed partial class MainViewModel
{
    private const int DebouncedSearchThreshold = 300;
    private const int MaxInPlaceRowChanges = 64;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(120);

    private readonly SemaphoreSlim _libraryStoreGate = new(1, 1);
    private bool _libraryHooked, _libraryNotifyPending, _historyLoaded, _historyLoadAttempted, _historyLoadFailed, _historyReloading, _syncingSearch;
    private string _librarySearchText = "", _historyLoadError = "";
    private Dictionary<string, HistoryRow>? _libraryRows;
    private DispatcherTimer? _searchTimer;
    private IRelayCommand? _clearHistorySearchCommand, _browseDownloadsCommand;
    private IAsyncRelayCommand? _retryHistoryCommand, _clearHistoryRecordsCommand;

    /// <summary>Records saved in the library, before the search filter.</summary>
    public int HistoryTotalCount { get { HookLibrary(); return _history.Count; } }
    /// <summary>Records currently shown after the search filter.</summary>
    public int HistoryShownCount { get { HookLibrary(); return History.Count; } }
    public bool HasHistoryRecords { get { HookLibrary(); return _history.Count > 0; } }
    public bool HasHistoryResults { get { HookLibrary(); return History.Count > 0; } }
    public bool IsHistoryFiltered => !string.IsNullOrWhiteSpace(HistorySearch);
    /// <summary>
    /// True only while the history has never been read and a read is running: the first app operation (startup),
    /// or Try again. Once a read has failed, an unrelated operation (an analysis, say) keeps the error panel up.
    /// </summary>
    public bool IsHistoryLoading { get { HookLibrary(); return !_historyLoaded && (_historyReloading || (IsMaintaining && !_historyLoadFailed)); } }
    /// <summary>True when the history file could not be read; offers a retry instead of an empty-looking library.</summary>
    public bool HistoryLoadFailed { get { HookLibrary(); return _historyLoadFailed && !_historyLoaded && !IsHistoryLoading; } }
    /// <summary>Why the last history read failed, kept on the error panel (the status bar moves on).</summary>
    public string HistoryLoadError => _historyLoadError;
    public bool HasHistoryLoadError => _historyLoadError.Length > 0;
    /// <summary>Only after a successful read, so startup never flashes "Nothing saved yet".</summary>
    public bool ShowHistoryEmpty { get { HookLibrary(); return _historyLoaded && _history.Count == 0; } }
    public bool ShowHistoryNoResults => !IsHistoryLoading && _history.Count > 0 && History.Count == 0;
    /// <summary>The count line; hidden when nothing matches, because the no-results panel already says so.</summary>
    public bool ShowHistorySummary => !IsHistoryLoading && _history.Count > 0 && History.Count > 0;
    /// <summary>
    /// The count line keeps its height whenever records exist or are loading, so skeletons, results and
    /// no-results all start at the same offset and nothing jumps when one replaces another.
    /// </summary>
    public bool ShowHistoryCountRow => IsHistoryLoading || _history.Count > 0;

    /// <summary>
    /// The search box text exactly as typed. The filter (<see cref="HistorySearch"/>) is this text trimmed
    /// with runs of whitespace collapsed, so " kea " finds what "kea" finds and the summary shows the query used.
    /// </summary>
    public string LibrarySearchText
    {
        get { HookLibrary(); return _librarySearchText; }
        set
        {
            value ??= "";
            if (_librarySearchText == value) return;
            _librarySearchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLibrarySearchText));
            ScheduleLibrarySearch();
        }
    }
    /// <summary>Anything typed at all, including only spaces, so the clear button and Esc can remove it.</summary>
    public bool HasLibrarySearchText => _librarySearchText.Length > 0;

    public string HistoryResultSummary
    {
        get
        {
            HookLibrary();
            var total = _history.Count;
            if (total == 0) return "";
            if (!IsHistoryFiltered) return string.Format(CultureInfo.CurrentCulture, "{0:N0} {1} · newest first", total, total == 1 ? "item" : "items");
            return string.Format(CultureInfo.CurrentCulture, "{0:N0} of {1:N0} match “{2}”", History.Count, total, ShortQuery);
        }
    }

    public string HistoryNoResultsTitle => $"No matches for “{ShortQuery}”";

    public string ClearHistoryPrompt
    {
        get
        {
            var total = _history.Count;
            var records = total == 1 ? "the 1 record" : string.Format(CultureInfo.CurrentCulture, "all {0:N0} records", total);
            return $"This removes {records} from your library. Downloaded and exported files stay exactly where they are.";
        }
    }

    public IRelayCommand ClearHistorySearchCommand => _clearHistorySearchCommand ??= new RelayCommand(() => LibrarySearchText = "");
    public IRelayCommand BrowseDownloadsCommand => _browseDownloadsCommand ??= new RelayCommand(() => SelectedTab = 0);

    /// <summary>Reads the history again (the unreadable-library panel's Try again).</summary>
    public IAsyncRelayCommand RetryHistoryCommand => _retryHistoryCommand ??= new AsyncRelayCommand(() => RunLibraryStoreAsync(async () =>
    {
        _historyReloading = true; QueueLibraryNotification();
        try
        {
            await ReloadLibraryAsync();
            _historyLoadFailed = false; _historyLoadError = "";
            Status = "Library loaded.";
        }
        catch (Exception error)
        {
            CaptureDiagnostics(error);
            _historyLoadFailed = !_historyLoaded; _historyLoadError = FriendlyError(error); Status = _historyLoadError;
        }
        finally { _historyReloading = false; QueueLibraryNotification(); }
    }));

    /// <summary>
    /// Removes every record after the page's confirmation; media files are never touched. Same effect as
    /// <see cref="ClearHistoryCommand"/>, without the app-wide busy state.
    /// </summary>
    public IAsyncRelayCommand ClearHistoryRecordsCommand => _clearHistoryRecordsCommand ??= new AsyncRelayCommand(() => RunLibraryStoreAsync(async () =>
    {
        await _services.Store.ClearHistoryAsync();
        await ReloadLibraryAsync();
        Status = "History cleared. Your media files were kept.";
    }, report: true));

    /// <summary>Removes one record from the library; its media file stays where it is.</summary>
    public Task RemoveHistoryRecordAsync(HistoryRow row) => RunLibraryStoreAsync(async () =>
    {
        await _services.Store.RemoveHistoryAsync(row.Entry.Id);
        await ReloadLibraryAsync();
        Status = "History record removed. The media file was kept.";
    }, report: true);

    /// <summary>
    /// Show file for a record whose file has gone: opens the folder it was saved in when that
    /// still exists, so a renamed or moved file can be found.
    /// </summary>
    public void ShowHistoryFolder(HistoryRow row) => TryAction(() =>
    {
        var folder = string.IsNullOrWhiteSpace(row.Entry.FilePath) ? null : Path.GetDirectoryName(Path.GetFullPath(row.Entry.FilePath));
        if (!row.CanReveal || string.IsNullOrEmpty(folder))
        {
            Status = "That file and its folder are no longer there. You can remove the record; nothing else is deleted.";
            return;
        }
        _services.Platform.OpenFolder(folder);
        Status = "That file is no longer where it was saved. Showing the folder it was saved in.";
    });

    /// <summary>Forgets cached file checks so rows re-check whether their media is still on disk.</summary>
    public void RefreshLibraryFileStates()
    {
        HistoryRow.ForgetFileChecks();
        foreach (var row in History) row.InvalidateFileState();
        if (_libraryRows is { } rows) foreach (var row in rows.Values) row.InvalidateFileState();
    }

    /// <summary>
    /// The library filter: the title the row shows (the file name for a record saved without a
    /// title) or the source contains the query, ignoring case.
    /// </summary>
    internal static bool MatchesHistorySearch(HistoryEntry entry, string query)
        => string.IsNullOrWhiteSpace(query)
        || (string.IsNullOrWhiteSpace(entry.Title) ? Path.GetFileName(entry.FilePath ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) : entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        || (entry.Source?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Trims the query and collapses runs of whitespace (including pasted newlines and tabs) to one space.</summary>
    internal static string NormalizeLibraryQuery(string? text)
    {
        var trimmed = (text ?? "").AsSpan().Trim();
        if (trimmed.IsEmpty) return "";
        var builder = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var character in trimmed)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (!isSpace) builder.Append(character);
            else if (!lastWasSpace) builder.Append(' ');
            lastWasSpace = isSpace;
        }
        return builder.ToString();
    }

    private string ShortQuery
    {
        get
        {
            var query = HistorySearch.Trim();
            return query.Length > 40 ? query[..39] + "…" : query;
        }
    }

    private async Task RunLibraryStoreAsync(Func<Task> action, bool report = false)
    {
        if (_disposed) return;
        await _libraryStoreGate.WaitAsync();
        try { if (!_disposed) await action(); }
        catch (Exception error) when (report)
        {
            CaptureDiagnostics(error);
            Status = FriendlyError(error);
        }
        finally { _libraryStoreGate.Release(); }
    }

    partial void OnHistoryReloaded()
    {
        _historyLoaded = true; _historyLoadFailed = false; _historyLoadError = "";
        if (_libraryHooked) QueueLibraryNotification();
    }

    partial void OnHistoryLoadFailed(Exception error)
    {
        // Keep the reason on the Library panel; the status bar moves on with later messages.
        if (!_historyLoaded) { _historyLoadFailed = true; _historyLoadError = FriendlyError(error); }
        if (_libraryHooked) QueueLibraryNotification();
    }

    private async Task ReloadLibraryAsync()
    {
        var entries = await _services.Store.LoadHistoryAsync();
        if (_disposed) return;
        _history = entries.OrderByDescending(entry => entry.Date).ToList();
        _historyLoaded = true;
        SyncHistoryRows();
        QueueLibraryNotification();
    }

    private void ScheduleLibrarySearch()
    {
        var query = NormalizeLibraryQuery(_librarySearchText);
        if (query == HistorySearch) { _searchTimer?.Stop(); return; }
        // Short histories filter on every keystroke; long ones wait for a pause in typing. Clearing is always immediate.
        if (query.Length == 0 || _history.Count < DebouncedSearchThreshold) { _searchTimer?.Stop(); ApplyLibrarySearch(query); return; }
        if (_searchTimer is null)
        {
            // One-shot: it stops itself on the first tick, so nothing runs while the user is idle.
            _searchTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = SearchDebounce };
            _searchTimer.Tick += (_, _) => { _searchTimer?.Stop(); ApplyLibrarySearch(NormalizeLibraryQuery(_librarySearchText)); };
        }
        _searchTimer.Stop(); _searchTimer.Start();
    }

    // Sets the filter and updates the visible rows in place.
    private void ApplyLibrarySearch(string query)
    {
        if (_disposed || query == _historySearch) return;
        OnPropertyChanging(nameof(HistorySearch));
        _historySearch = query;
        _syncingSearch = true;
        try { OnPropertyChanged(nameof(HistorySearch)); }
        finally { _syncingSearch = false; }
        SyncHistoryRows();
        QueueLibraryNotification();
    }

    /// <summary>
    /// Brings <see cref="MainViewModel.History"/> in line with the records and the current query. Rows are
    /// reused by record id (also across searches, so a row that leaves and re-enters the results keeps its
    /// file check). Small changes are applied in place, so one removal is one Remove and the other rows keep
    /// their containers, scroll position and focus; a large change is one Reset plus the new rows.
    /// Every history refresh (reloads after downloads, exports and settings saves, search) goes through here.
    /// </summary>
    private void SyncHistoryRows()
    {
        var known = _libraryRows ?? new Dictionary<string, HistoryRow>(StringComparer.Ordinal);
        // Rows on screen win over cached ones.
        foreach (var row in History) known[row.Entry.Id] = row;

        var next = new Dictionary<string, HistoryRow>(_history.Count, StringComparer.Ordinal);
        var target = new List<HistoryRow>();
        foreach (var entry in _history)
        {
            var matches = MatchesHistorySearch(entry, _historySearch);
            var row = known.TryGetValue(entry.Id, out var existing) && existing.Entry == entry && !next.ContainsKey(entry.Id) ? existing : null;
            if (row is null && !matches) continue;
            row ??= new HistoryRow(entry, this);
            next.TryAdd(entry.Id, row);
            if (matches) target.Add(row);
        }
        _libraryRows = next;

        var keep = new HashSet<HistoryRow>(target, ReferenceEqualityComparer.Instance);
        var removals = 0;
        foreach (var row in History) if (!keep.Contains(row)) removals++;
        var insertions = target.Count - (History.Count - removals);
        if (removals + insertions > Math.Min(MaxInPlaceRowChanges, target.Count + 1))
        {
            History.Clear();
            foreach (var row in target) History.Add(row);
            return;
        }
        for (var i = History.Count - 1; i >= 0; i--) if (!keep.Contains(History[i])) History.RemoveAt(i);
        for (var i = 0; i < target.Count; i++)
        {
            if (i < History.Count && ReferenceEquals(History[i], target[i])) continue;
            var at = History.IndexOf(target[i]);
            if (at >= 0) History.Move(at, i); else History.Insert(i, target[i]);
        }
    }

    // Bindings read the library properties when the view attaches, which is the first
    // moment notifications are needed; view-model-only callers never pay for them.
    private void HookLibrary()
    {
        if (_libraryHooked || _disposed) return;
        _libraryHooked = true;
        _historyLoaded = _initialized || History.Count > 0;
        _librarySearchText = _historySearch;
        foreach (var row in History) row.Library = this;
        History.CollectionChanged += LibraryHistoryChanged;
        PropertyChanged += LibraryPropertyChanged;
    }

    private void LibraryHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _historyLoaded = true;
        if (e.NewItems is { } added) foreach (HistoryRow row in added) row.Library = this;
        QueueLibraryNotification();
    }

    private void LibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsBusy):
                if (IsMaintaining) _historyLoadAttempted |= !_historyLoaded;
                // The failed operation has just put its reason in the status bar; keep it for the error panel.
                // Only the first failure is captured, so a later unrelated operation cannot replace the reason.
                else if (_historyLoadAttempted && !_historyLoaded && !_historyReloading && !_historyLoadFailed) { _historyLoadFailed = true; _historyLoadError = Status; }
                QueueLibraryNotification();
                break;
            case nameof(IsHistory) when IsHistory:
                RefreshLibraryFileStates();
                break;
            // Someone else set HistorySearch (FilterHistory has already run): mirror it in the box.
            case nameof(HistorySearch) when !_syncingSearch:
                _searchTimer?.Stop();
                if (NormalizeLibraryQuery(_librarySearchText) != HistorySearch)
                {
                    _librarySearchText = HistorySearch;
                    OnPropertyChanged(nameof(LibrarySearchText));
                    OnPropertyChanged(nameof(HasLibrarySearchText));
                }
                QueueLibraryNotification();
                break;
        }
    }

    private void QueueLibraryNotification()
    {
        if (_libraryNotifyPending) return;
        _libraryNotifyPending = true;
        // Normal priority runs before layout and render, so no frame shows a stale count.
        Dispatcher.UIThread.Post(NotifyLibraryState, DispatcherPriority.Normal);
    }

    private void NotifyLibraryState()
    {
        _libraryNotifyPending = false;
        if (_disposed) return;
        if (_historyLoaded) _historyLoadFailed = false;
        OnPropertyChanged(nameof(HistoryTotalCount));
        OnPropertyChanged(nameof(HistoryShownCount));
        OnPropertyChanged(nameof(HasHistoryRecords));
        OnPropertyChanged(nameof(HasHistoryResults));
        OnPropertyChanged(nameof(IsHistoryFiltered));
        OnPropertyChanged(nameof(IsHistoryLoading));
        OnPropertyChanged(nameof(HistoryLoadFailed));
        OnPropertyChanged(nameof(HistoryLoadError));
        OnPropertyChanged(nameof(HasHistoryLoadError));
        OnPropertyChanged(nameof(ShowHistoryEmpty));
        OnPropertyChanged(nameof(ShowHistoryNoResults));
        OnPropertyChanged(nameof(ShowHistorySummary));
        OnPropertyChanged(nameof(ShowHistoryCountRow));
        OnPropertyChanged(nameof(HistoryResultSummary));
        OnPropertyChanged(nameof(HistoryNoResultsTitle));
        OnPropertyChanged(nameof(ClearHistoryPrompt));
    }
}
