using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Klippi.Desktop.Views;

/// <summary>
/// Library page. View-only behaviour lives here: keyboard flow (Ctrl+F, Esc, Down into
/// the list, Enter to open), double-click to open, closing the clear-history flyout,
/// checking a file is still on disk before Open, Show file, Edit or Share run (so a
/// since-moved file is reported instead of failing silently or vaguely), and keeping
/// keyboard focus in the list when the focused row is removed or the list is rebuilt.
/// </summary>
public sealed partial class LibraryView : UserControl
{
    private TopLevel? _topLevel;
    private MainViewModel? _model;
    private int _lastFocusedIndex = -1;
    private bool _focusRestorePending;
    // The row whose More menu was opened last: Remove record runs from that menu after it has closed
    // and focus has already left the page, so focus follows this row rather than the focus state.
    private HistoryRow? _menuRow;

    public LibraryView()
    {
        InitializeComponent();
        HistorySearch.KeyDown += OnSearchKeyDown;
        // Tunnel: the ListBox consumes Enter for selection before a bubbling handler sees it.
        HistoryList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        HistoryList.DoubleTapped += OnListDoubleTapped;
        HistoryList.AddHandler(Button.ClickEvent, OnRowButtonClick);
        HistoryList.AddHandler(GotFocusEvent, OnListGotFocus);
        // The safe choice takes focus, so Enter or Space in the confirmation never clears by accident.
        if (ClearHistoryButton.Flyout is { } flyout) flyout.Opened += (_, _) => CancelClearHistoryButton.Focus();
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.History.CollectionChanged -= OnHistoryChanged;
        _model = Model;
        if (_model is not null) _model.History.CollectionChanged += OnHistoryChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null) return;
        _topLevel.AddHandler(KeyDownEvent, OnTopLevelKeyDown);
        if (_topLevel is Window window) window.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            if (_topLevel is Window window) window.Activated -= OnWindowActivated;
            _topLevel = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    // Files may have been moved or deleted in Explorer while the app was in the background.
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (IsEffectivelyVisible) Model?.RefreshLibraryFileStates();
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !IsEffectivelyVisible || e.Key != Key.F || e.KeyModifiers != KeyModifiers.Control) return;
        HistorySearch.Focus(NavigationMethod.Tab);
        HistorySearch.SelectAll();
        e.Handled = true;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Model is { HasLibrarySearchText: true } model)
        {
            model.ClearHistorySearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None && HistoryList.ItemCount > 0)
        {
            FocusRow(Math.Max(0, HistoryList.SelectedIndex));
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.Source is not ListBoxItem { DataContext: HistoryRow row }) return;
        _ = RunCheckedAsync(row, row.OpenCommand, revealFolder: false);
        e.Handled = true;
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double-clicking a row's own buttons must not also open the file.
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: HistoryRow row }) _ = RunCheckedAsync(row, row.OpenCommand, revealFolder: false);
    }

    // Click is raised before the button runs its command; marking it handled stops that run,
    // so file actions only run once the file is confirmed to still be on disk.
    private void OnRowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Name: "MoreItemButton", DataContext: HistoryRow menuRow }) { _menuRow = menuRow; return; }
        if (e.Source is not Button { DataContext: HistoryRow row } button || !button.Classes.Contains("checks-file") || button.Command is not { } command) return;
        e.Handled = true;
        _ = RunCheckedAsync(row, command, revealFolder: button.Name == "RevealItemButton");
    }

    private void OnListGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // While a restore is pending, the list may move focus on its own (it focuses a row when the focused
        // container is recycled); that is not the user's choice, so it must not replace the row to return to.
        if (_focusRestorePending || e.Source is not Visual source || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item) return;
        _lastFocusedIndex = HistoryList.IndexFromContainer(item);
        if (_menuRow is not null && !ReferenceEquals(item.DataContext, _menuRow)) _menuRow = null;
    }

    private void OnCancelClearHistory(object? sender, RoutedEventArgs e) => ClearHistoryButton.Flyout?.Hide();

    // Click is raised before the button runs its command. Closing the flyout right away
    // would detach the button and drop its command binding, so close once it has run.
    private void OnConfirmClearHistory(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => ClearHistoryButton.Flyout?.Hide());

    private async Task RunCheckedAsync(HistoryRow row, ICommand command, bool revealFolder)
    {
        if (!await row.RevalidateAsync())
        {
            // A missing file whose folder is still there: Show file opens that folder instead.
            if (revealFolder && row.CanReveal) Model?.ShowHistoryFolder(row);
            else if (Model is { } model) model.Status = MissingFileMessage(row);
            return;
        }
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void FocusRow(int index)
    {
        HistoryList.SelectedIndex = index;
        HistoryList.ScrollIntoView(index);
        HistoryList.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }

    // When the focused row is removed (Remove record) or the list is rebuilt, its container is
    // recycled and keyboard focus would fall out of the page (or onto whichever row the list picks).
    // Put it on the row that took the removed row's place, or on the empty state's action when
    // nothing is left. Focus the user has moved elsewhere (the search box, another page) is left alone.
    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset) || !IsEffectivelyVisible) return;
        var menuRowRemoved = _menuRow is not null && e.Action == NotifyCollectionChangedAction.Remove && e.OldItems?.Contains(_menuRow) == true;
        if (!menuRowRemoved && _menuRow is null && !FocusIsInLibrary()) return;
        if (e.Action == NotifyCollectionChangedAction.Remove && (menuRowRemoved || _menuRow is null)) _lastFocusedIndex = e.OldStartingIndex;
        if (_focusRestorePending) return;
        _focusRestorePending = true;
        // Background runs after the state notifications and layout, when the right panel is visible.
        Dispatcher.UIThread.Post(RestoreFocus, DispatcherPriority.Background);
    }

    private void RestoreFocus()
    {
        _focusRestorePending = false;
        if (Model is not { } model) return;
        var followRemovedRow = _menuRow is not null && !model.History.Contains(_menuRow);
        if (followRemovedRow) _menuRow = null;
        if (!IsEffectivelyVisible) return;
        if (FocusIsUsable() && !(followRemovedRow && FocusIsInList())) return;
        if (model.History.Count > 0) FocusRow(Math.Clamp(_lastFocusedIndex, 0, model.History.Count - 1));
        else if (EmptyDownloadButton.IsEffectivelyVisible) EmptyDownloadButton.Focus(NavigationMethod.Directional);
        else HistorySearch.Focus(NavigationMethod.Directional);
    }

    private IInputElement? FocusedElement => _topLevel?.FocusManager?.GetFocusedElement();

    // Focus on this page, or in one of its flyouts (a flyout's logical parent is the button that opened it).
    private bool FocusIsInLibrary() => FocusedElement is ILogical focused && (focused == this || focused.GetLogicalAncestors().Contains(this) || (focused is Visual visual && this.IsVisualAncestorOf(visual)));

    private bool FocusIsInList() => FocusedElement is Visual visual && HistoryList.IsVisualAncestorOf(visual);

    // Focus that still sits on something the user can see and use (not a recycled row or a disabled button).
    private bool FocusIsUsable()
    {
        if (FocusedElement is not Visual visual || !visual.IsAttachedToVisualTree() || !visual.IsEffectivelyVisible) return false;
        if (visual is InputElement { IsEffectivelyEnabled: false }) return false;
        var item = visual.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        return item is null || item.DataContext is HistoryRow row && Model?.History.Contains(row) == true;
    }

    private static string MissingFileMessage(HistoryRow row) => row.HasSource
        ? "That file is no longer where it was saved. Choose Download again, or remove the record."
        : "That file is no longer where it was saved. You can remove the record; nothing else is deleted.";
}
