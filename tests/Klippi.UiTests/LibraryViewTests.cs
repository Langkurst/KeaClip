using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

public sealed class LibraryViewTests
{
    private static string? Evidence => Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");

    [AvaloniaFact]
    public async Task Library_renders_loading_empty_populated_and_wide_states_in_both_themes()
    {
        using var files = new FixtureFolder();
        var fake = new UiFakes(); var store = new GatedStore(fake.Store);
        using var model = new MainViewModel(fake.Services with { Store = store }) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            model.SelectedTab = 2; model.Theme = "Dark";
            var initialise = model.InitializeAsync(); await Settle(window);
            Assert.True(model.IsHistoryLoading); Assert.False(model.ShowHistoryEmpty); Assert.True(Named<StackPanel>(window, "HistoryLoading").IsVisible);
            // The count line keeps its height while loading, so skeletons sit exactly where rows will appear.
            Assert.True(Named<Panel>(window, "HistoryCountRow").IsVisible); Assert.False(Named<TextBlock>(window, "HistoryCount").IsVisible);
            // Measured against the page: the shell header above it is not this page's layout.
            var page = Named<Control>(window, "LibraryPage");
            var skeletonTop = Named<StackPanel>(window, "HistoryLoading").TranslatePoint(default, page)!.Value.Y;
            // Loading settings re-applies the saved "System" theme, so pick each theme after initialisation starts.
            foreach (var theme in new[] { "Dark", "Light" }) { model.Theme = theme; await Settle(window); Capture(window, "library-loading-" + theme.ToLowerInvariant()); }

            store.Gate.SetResult(); await initialise; await Settle(window);
            Assert.False(model.IsHistoryLoading); Assert.True(model.ShowHistoryEmpty); Assert.False(model.HasHistoryRecords);
            Assert.False(Named<Button>(window, "ClearHistoryButton").IsEffectivelyEnabled);
            foreach (var theme in new[] { "Dark", "Light" }) { model.Theme = theme; await Settle(window); Capture(window, "library-empty-" + theme.ToLowerInvariant()); }

            fake.Store.History.AddRange(files.Entries());
            await model.RetryHistoryCommand.ExecuteAsync(null); await Settle(window);
            Assert.Equal(6, model.HistoryTotalCount); Assert.Equal(6, model.History.Count); Assert.True(model.ShowHistorySummary); Assert.Equal("6 items · newest first", model.HistoryResultSummary);
            Assert.Equal("Kea at Arthur's Pass · morning flight", model.History[0].Title);
            var firstRowTop = Named<ListBox>(window, "HistoryList").ContainerFromIndex(0)!.TranslatePoint(default, page)!.Value.Y;
            Assert.Equal(skeletonTop, firstRowTop, 1);
            // Normal saved rows stay quiet: no status pill; only exceptions (a missing file) carry one.
            Assert.False(Row(model, "v1").ShowStatusPill); Assert.True(Row(model, "m1").ShowStatusPill);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                model.Theme = theme; window.Width = 900; await Settle(window); Capture(window, "library-populated-" + theme.ToLowerInvariant());
                AssertRowsFit(window);
                window.Width = 1400; await Settle(window); Capture(window, "library-wide-" + theme.ToLowerInvariant());
                AssertRowsFit(window);
                Assert.True(Named<ListBox>(window, "HistoryList").ItemsPanelRoot!.Bounds.Width <= 1040.5);
            }
            // Laptop width: the size survives trimming because the meta line is ordered by importance.
            foreach (var theme in new[] { "Dark", "Light" }) { model.Theme = theme; window.Width = 880; await Settle(window); AssertRowsFit(window); Capture(window, "library-populated-880-" + theme.ToLowerInvariant()); }
            window.Width = 900; model.Theme = "Dark";
            foreach (var scale in new[] { 1.25, 1.5, 2 }) { window.SetRenderScaling(scale); await Settle(window); AssertRowsFit(window); Capture(window, $"library-populated-dark-{scale * 100:0}"); }
            window.SetRenderScaling(1);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Search_shows_counts_no_results_and_supports_keyboard_flow()
    {
        using var files = new FixtureFolder();
        var fake = new UiFakes(); fake.Store.History.AddRange(files.Entries());
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 2; model.Theme = "Dark"; await Settle(window);
            var search = Named<TextBox>(window, "HistorySearch");

            search.Text = "KEA"; await Settle(window);
            Assert.Equal("KEA", model.HistorySearch); Assert.Equal(2, model.History.Count); Assert.Equal("2 of 6 match “KEA”", model.HistoryResultSummary);
            Assert.True(search.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ClearSearchButton").IsVisible);
            foreach (var theme in new[] { "Dark", "Light" }) { model.Theme = theme; await Settle(window); Capture(window, "library-search-" + theme.ToLowerInvariant()); }

            // The applied query is the typed text trimmed with whitespace collapsed, and the summary shows that query.
            search.Text = " kea	"; await Settle(window);
            Assert.Equal(" kea	", model.LibrarySearchText); Assert.Equal("kea", model.HistorySearch); Assert.Equal(2, model.History.Count); Assert.Equal("2 of 6 match “kea”", model.HistoryResultSummary);
            search.Text = "   "; await Settle(window);
            Assert.Equal("", model.HistorySearch); Assert.Equal(6, model.History.Count); Assert.True(model.HasLibrarySearchText);

            search.Text = "zebra crossing"; await Settle(window);
            Assert.True(model.ShowHistoryNoResults); Assert.False(model.ShowHistoryEmpty); Assert.False(Named<ListBox>(window, "HistoryList").IsVisible);
            Assert.Equal("No matches for “zebra crossing”", model.HistoryNoResultsTitle);
            // The no-results panel already says it: the count line is hidden but keeps its height.
            Assert.False(model.ShowHistorySummary); Assert.False(Named<TextBlock>(window, "HistoryCount").IsVisible); Assert.True(Named<Panel>(window, "HistoryCountRow").IsVisible);
            foreach (var theme in new[] { "Dark", "Light" }) { model.Theme = theme; await Settle(window); Capture(window, "library-no-results-" + theme.ToLowerInvariant()); }
            Invoke(Named<Button>(window, "NoResultsClearButton")); await Settle(window);
            Assert.Equal("", model.HistorySearch); Assert.Equal("", search.Text); Assert.Equal(6, model.History.Count); Assert.False(model.ShowHistoryNoResults);

            // Esc clears an active search; Ctrl+F returns focus to the field from anywhere on the page.
            search.Text = "dawn"; search.Focus(); await Settle(window);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Settle(window);
            Assert.Equal("", model.HistorySearch); Assert.Equal("", search.Text);
            Named<Button>(window, "ClearHistoryButton").Focus(); Assert.False(search.IsFocused);
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f"); window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
            Assert.True(search.IsFocused);

            // Down moves into the list; Enter opens the selected record.
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); await Settle(window);
            var list = Named<ListBox>(window, "HistoryList");
            Assert.Equal(0, list.SelectedIndex); Assert.True(list.ContainerFromIndex(0)!.IsFocused);
            await Task.Delay(200); await Settle(window); Capture(window, "library-keyboard-focus-light");
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.Equal("OpenItemButton", (window.FocusManager?.GetFocusedElement() as Control)?.Name);
            await Task.Delay(200); await Settle(window); Capture(window, "library-keyboard-button-focus-light");
            list.ContainerFromIndex(0)!.Focus(NavigationMethod.Directional);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await WaitFor(window, () => fake.Platform.OpenedFiles.Count > 0);
            Assert.Equal(model.History[0].Entry.FilePath, Assert.Single(fake.Platform.OpenedFiles));

            // Selection is only the keyboard cursor: once focus leaves the list, no row stays highlighted.
            search.Focus(); await Task.Delay(200); await Settle(window);
            Assert.Equal(((ListBoxItem)list.ContainerFromIndex(1)!).Background, ((ListBoxItem)list.ContainerFromIndex(0)!).Background); Assert.True(((ListBoxItem)list.ContainerFromIndex(0)!).IsSelected);

            // Hovering a row lifts it with a colour fade only.
            model.Theme = "Dark"; list.SelectedIndex = -1; await Settle(window);
            var hoverTarget = list.ContainerFromIndex(3)!; var hoverPoint = hoverTarget.TranslatePoint(new Point(hoverTarget.Bounds.Width / 2, hoverTarget.Bounds.Height / 2), window)!.Value;
            window.MouseMove(hoverPoint); await Task.Delay(200); await Settle(window);
            Assert.True(hoverTarget.IsPointerOver); Capture(window, "library-hover-dark");
            model.Theme = "Light"; await Task.Delay(200); await Settle(window); Capture(window, "library-hover-light");

            // Double-clicking a row's text opens it; double-clicking its buttons does not.
            fake.Platform.OpenedFiles.Clear();
            var meta = list.ContainerFromIndex(1)!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("row-meta"));
            var point = meta.TranslatePoint(new Point(10, meta.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await WaitFor(window, () => fake.Platform.OpenedFiles.Count > 0);
            Assert.Equal(model.History[1].Entry.FilePath, Assert.Single(fake.Platform.OpenedFiles));
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Long_history_is_virtualised_and_the_list_owns_its_scrolling()
    {
        var fake = new UiFakes(); var now = DateTimeOffset.Now;
        fake.Store.History.AddRange(Enumerable.Range(0, 600).Select(i => new HistoryEntry("bulk-" + i, $"Saved item {i:000}", "https://example.com/" + i, "Example", now.AddMinutes(-i), Path.Combine(Path.GetTempPath(), "keaclip-absent-" + i + ".mp4"), i % 3 == 0 ? "Audio" : "VideoAudio", "1080p", 1_000_000 + i, "Finished")));
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 2; model.Theme = "Dark"; await Settle(window);
            var list = Named<ListBox>(window, "HistoryList");
            Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            var realised = list.GetRealizedContainers().Count();
            Assert.InRange(realised, 1, 40);
            Assert.Equal("600 items · newest first", model.HistoryResultSummary);
            // The page itself no longer scrolls: the list's own viewport is bounded by the window.
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height * 10);
            Assert.True(list.Bounds.Height < 740);
            list.ScrollIntoView(599); await Settle(window);
            Assert.NotNull(list.ContainerFromIndex(599)); Assert.InRange(list.GetRealizedContainers().Count(), 1, 40);
            Capture(window, "library-long-scrolled-dark");
            model.Theme = "Light"; await Settle(window); Capture(window, "library-long-scrolled-light");

            // Long histories filter after a short pause in typing, and rows are reused by record id:
            // searching and clearing never rebuilds every row (each would carry its own commands and file check).
            var first = model.History[0]; var changes = 0;
            model.History.CollectionChanged += (_, _) => changes++;
            var search = Named<TextBox>(window, "HistorySearch");
            search.Text = "S"; search.Text = "Sa"; search.Text = "Saved item 00";
            Assert.Equal("", model.HistorySearch); Assert.Equal(600, model.History.Count);
            await WaitFor(window, () => model.HistorySearch == "Saved item 00");
            Assert.Equal(10, model.History.Count); Assert.Same(first, model.History[0]); Assert.Equal("10 of 600 match “Saved item 00”", model.HistoryResultSummary);
            changes = 0;
            search.Text = ""; await Settle(window);
            Assert.Equal(600, model.History.Count); Assert.Same(first, model.History[0]);
            Assert.True(changes <= 601, "Clearing the search raised " + changes + " collection changes.");
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Missing_files_are_flagged_and_offer_download_again_instead_of_a_silent_open()
    {
        using var files = new FixtureFolder();
        var fake = new UiFakes(); fake.Store.History.AddRange(files.Entries());
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 2; await Settle(window);
            var list = Named<ListBox>(window, "HistoryList");
            var present = Row(model, "v1"); var missingWithSource = Row(model, "m1"); var missingLocal = Row(model, "m2");
            Assert.False(present.IsMissing); Assert.Equal("Saved", present.StatusLabel); Assert.True(present.ShowOpenPrimary); Assert.False(present.ShowStatusPill);
            Assert.True(missingWithSource.IsMissing); Assert.Equal("File missing", missingWithSource.StatusLabel); Assert.True(missingWithSource.ShowRepeatPrimary); Assert.True(missingWithSource.ShowStatusPill);
            Assert.True(missingLocal.IsMissing); Assert.False(missingLocal.ShowRepeatPrimary); Assert.Equal("Exported", Row(model, "c1").StatusLabel);
            // Most important facts first, so trimming at laptop widths cuts the source and quality before the size.
            Assert.Equal("Audio clip", missingLocal.KindLabel); Assert.Equal(new[] { "Video", "120 MB", "720p", "Example" }, missingWithSource.MetaLine.Split(" · ")[1..]);
            Assert.Equal(new[] { "Audio", "6.4 MB", "Best quality", "Example" }, Row(model, "a1").MetaLine.Split(" · ")[1..]);
            Assert.Equal("saved.mp4", new HistoryRow(new("x", " ", null, null, DateTimeOffset.Now, Path.Combine("folder", "saved.mp4"), "Video", "Original", 0, "Finished"), model).DisplayTitle);
            Assert.Equal("", new HistoryRow(new("y", "Song", null, null, DateTimeOffset.Now, "song.m4a", "Audio", "1080p", 0, "Finished"), model).QualityLabel);
            // Screen readers hear the status after the title.
            Assert.StartsWith("File missing. ", missingWithSource.AccessibleSummary);

            var missingIndex = model.History.IndexOf(missingWithSource);
            list.ScrollIntoView(missingIndex); await Settle(window);
            Assert.True(RowButton(list, missingIndex, "RepeatItemButton").IsVisible); Assert.False(RowButton(list, missingIndex, "OpenItemButton").IsVisible);
            Assert.Same(missingWithSource.RepeatCommand, RowButton(list, missingIndex, "RepeatItemButton").Command);
            Assert.False(RowButton(list, missingIndex, "EditItemButton").IsEffectivelyEnabled); Assert.False(RowButton(list, missingIndex, "ShareItemButton").IsEffectivelyEnabled);
            // Disabled actions explain themselves.
            foreach (var name in new[] { "EditItemButton", "ShareItemButton" })
            {
                var button = RowButton(list, missingIndex, name);
                Assert.True(ToolTip.GetShowOnDisabled(button)); Assert.StartsWith("File missing · ", ToolTip.GetTip(button) as string);
            }
            var missingRow = list.ContainerFromIndex(missingIndex)!;
            Assert.Equal(missingWithSource.AccessibleSummary, AutomationProperties.GetHelpText(missingRow));

            // Its folder is still there, so Show file stays available and opens that folder instead.
            var reveal = RowButton(list, missingIndex, "RevealItemButton");
            Assert.True(reveal.IsEffectivelyEnabled); Assert.Equal("File missing · Open the folder it was saved in", ToolTip.GetTip(reveal));
            Invoke(reveal); await WaitFor(window, () => model.Status.Contains("Showing the folder", StringComparison.Ordinal));

            // A file deleted after the row was drawn is caught before Open, Edit or Share run.
            var presentIndex = model.History.IndexOf(present);
            list.ScrollIntoView(presentIndex); await Settle(window);
            File.Delete(present.Entry.FilePath);
            Invoke(RowButton(list, presentIndex, "OpenItemButton"));
            await WaitFor(window, () => present.IsMissing);
            Assert.Contains("no longer where it was saved", model.Status); Assert.Empty(fake.Platform.OpenedFiles);

            var shared = Row(model, "c1"); var sharedIndex = model.History.IndexOf(shared);
            list.ScrollIntoView(sharedIndex); await Settle(window);
            File.Delete(shared.Entry.FilePath);
            Invoke(RowButton(list, sharedIndex, "ShareItemButton"));
            await WaitFor(window, () => shared.IsMissing);
            Assert.Equal(0, fake.Sharing.Calls); Assert.Contains("no longer where it was saved", model.Status);

            var edited = Row(model, "a1"); var editedIndex = model.History.IndexOf(edited);
            list.ScrollIntoView(editedIndex); await Settle(window);
            File.Delete(edited.Entry.FilePath);
            Invoke(RowButton(list, editedIndex, "EditItemButton"));
            await WaitFor(window, () => edited.IsMissing);
            Assert.False(model.HasEditor); Assert.True(model.IsHistory);
            foreach (var theme in new[] { "Dark", "Light" }) { model.Theme = theme; list.ScrollIntoView(0); await Settle(window); Capture(window, "library-missing-" + theme.ToLowerInvariant()); }

            // An available file opens exactly once: the button's own command run is replaced by the checked run.
            var archive = Row(model, "ar1"); var archiveIndex = model.History.IndexOf(archive);
            list.ScrollIntoView(archiveIndex); await Settle(window);
            Invoke(RowButton(list, archiveIndex, "OpenItemButton"));
            await WaitFor(window, () => fake.Platform.OpenedFiles.Count > 0); await Task.Delay(50); await Settle(window);
            Assert.Equal(archive.Entry.FilePath, Assert.Single(fake.Platform.OpenedFiles));

            // Restoring the file and returning to the page clears the warning.
            await File.WriteAllTextAsync(present.Entry.FilePath, "restored fixture");
            model.SelectedTab = 0; model.SelectedTab = 2; await Settle(window);
            await WaitFor(window, () => !present.IsMissing);
            Assert.Equal("Saved", present.StatusLabel); Assert.False(present.ShowStatusPill);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Row_actions_and_more_menu_reach_every_existing_command_and_clear_needs_confirmation()
    {
        using var files = new FixtureFolder();
        var fake = new UiFakes(); fake.Store.History.AddRange(files.Entries());
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 2; model.Theme = "Dark"; await Settle(window);
            var list = Named<ListBox>(window, "HistoryList"); var row = model.History[0];
            Assert.Same(row.OpenCommand, RowButton(list, 0, "OpenItemButton").Command);
            Assert.Same(row.RevealCommand, RowButton(list, 0, "RevealItemButton").Command);
            Assert.Same(row.EditCommand, RowButton(list, 0, "EditItemButton").Command);
            Assert.Same(row.ShareCommand, RowButton(list, 0, "ShareItemButton").Command);
            foreach (var name in new[] { "OpenItemButton", "RevealItemButton", "EditItemButton", "ShareItemButton", "MoreItemButton" })
            {
                var button = RowButton(list, 0, name);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))); Assert.NotNull(ToolTip.GetTip(button));
            }

            var more = RowButton(list, 0, "MoreItemButton"); var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            menu.ShowAt(more); await Settle(window);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(4, items.Length);
            Assert.Same(row.CopyPathCommand, items[0].Command); Assert.Same(row.CopySourceCommand, items[1].Command); Assert.Same(row.RepeatCommand, items[2].Command); Assert.Same(row.RemoveRecordCommand, items[3].Command);
            Assert.True(items[1].IsVisible); Assert.True(items[2].IsVisible);
            Capture(TopLevel.GetTopLevel(items[0])!, "library-more-menu-dark");
            InvokeItem(items[0]); await Settle(window); Assert.Equal(row.Entry.FilePath, fake.Platform.Clipboard);
            InvokeItem(items[1]); await Settle(window); Assert.Equal(row.Entry.SourceUrl, fake.Platform.Clipboard);
            menu.Hide();

            // Local exports have no source: the source-only actions are hidden.
            var local = Row(model, "c1"); var localIndex = model.History.IndexOf(local); list.ScrollIntoView(localIndex); await Settle(window);
            var localMore = RowButton(list, localIndex, "MoreItemButton"); var localMenu = (MenuFlyout)localMore.Flyout!; localMenu.ShowAt(localMore); await Settle(window);
            var localItems = localMenu.Items.OfType<MenuItem>().ToArray(); Assert.False(localItems[1].IsVisible); Assert.False(localItems[2].IsVisible);
            localMenu.Hide();

            // Remove (from the menu, as a keyboard user would) keeps the media file, never takes the app-wide busy
            // state (no Cancel button, so the header never reflows), and leaves focus on the row that took its place.
            var busyChanges = 0; model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsBusy)) busyChanges++; };
            var removed = model.History[1]; var next = model.History[2];
            var removeMore = RowButton(list, 1, "MoreItemButton"); removeMore.Focus(NavigationMethod.Tab);
            var removeMenu = (MenuFlyout)removeMore.Flyout!; Invoke(removeMore); await Settle(window); Assert.True(removeMenu.IsOpen);
            var removeItem = removeMenu.Items.OfType<MenuItem>().Single(item => item.Name == "RemoveRecordItem");
            removeItem.Focus(NavigationMethod.Directional); InvokeItem(removeItem); removeMenu.Hide();
            await WaitFor(window, () => model.History.Count == 5);
            Assert.Equal(5, model.HistoryTotalCount); Assert.True(File.Exists(removed.Entry.FilePath)); Assert.DoesNotContain(fake.Store.History, h => h.Id == removed.Entry.Id);
            Assert.Equal(0, busyChanges); Assert.Equal("History record removed. The media file was kept.", model.Status);
            var focusedRow = (window.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            Assert.Same(next, focusedRow?.DataContext); Assert.Equal(1, list.SelectedIndex);
            await Task.Delay(200); await Settle(window); Capture(window, "library-after-remove-focus-dark");

            // Clear is guarded by a confirmation flyout that says files are kept.
            var clear = Named<Button>(window, "ClearHistoryButton"); var confirmFlyout = Assert.IsType<Flyout>(clear.Flyout);
            Assert.Contains("all 5 records", model.ClearHistoryPrompt); Assert.Contains("stay exactly where they are", model.ClearHistoryPrompt);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                model.Theme = theme; confirmFlyout.ShowAt(clear); await Settle(window);
                var content = (Control)confirmFlyout.Content!;
                // The safe choice holds focus, so Enter or Space never clears by accident.
                Assert.True(content.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "CancelClearHistoryButton").IsFocused);
                Capture(TopLevel.GetTopLevel(content)!, "library-clear-confirm-" + theme.ToLowerInvariant());
                Capture(window, "library-clear-window-" + theme.ToLowerInvariant());
                if (theme == "Dark")
                {
                    var keep = content.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "CancelClearHistoryButton");
                    Invoke(keep); await Settle(window); Assert.False(confirmFlyout.IsOpen); Assert.Equal(5, fake.Store.History.Count);
                }
            }
            var confirm = ((Control)confirmFlyout.Content!).GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConfirmClearHistoryButton");
            Assert.Same(model.ClearHistoryRecordsCommand, confirm.Command);
            confirmFlyout.ShowAt(clear); await Settle(window); confirm.Focus(NavigationMethod.Tab);
            Invoke(confirm); await WaitFor(window, () => model.History.Count == 0 && model.ShowHistoryEmpty);
            Assert.False(confirmFlyout.IsOpen); Assert.Empty(fake.Store.History); Assert.Equal(0, busyChanges);
            Assert.All(files.Paths, path => Assert.True(File.Exists(path)));
            // Nothing is left to focus in the list: focus moves to the empty state's action instead of falling out of the page.
            Assert.True(Named<Button>(window, "EmptyDownloadButton").IsFocused);

            // The empty state's primary action leads back to Download.
            Invoke(Named<Button>(window, "EmptyDownloadButton")); Assert.True(model.IsDownload);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Unreadable_history_offers_retry_instead_of_an_empty_library()
    {
        using var files = new FixtureFolder();
        var fake = new UiFakes(); fake.Store.HistoryLoadFailure = new IOException("History unavailable");
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            model.SelectedTab = 2; await model.InitializeAsync(); await Settle(window);
            Assert.True(model.HistoryLoadFailed); Assert.False(model.ShowHistoryEmpty); Assert.False(model.IsHistoryLoading);
            Assert.True(Named<StackPanel>(window, "HistoryLoadFailedState").IsVisible);
            // The reason stays on the panel itself; the status bar moves on with the next message.
            Assert.Contains("History unavailable", Named<TextBlock>(window, "HistoryLoadErrorText").Text);
            foreach (var theme in new[] { "Dark", "Light" }) { model.Theme = theme; await Settle(window); Capture(window, "library-error-" + theme.ToLowerInvariant()); }
            // An unrelated operation neither replaces the error panel with loading placeholders nor its reason.
            var loadingWhileBusy = false;
            model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsBusy) && model.IsBusy) loadingWhileBusy |= model.IsHistoryLoading; };
            var busySeen = false; model.PropertyChanged += (_, e) => busySeen |= e.PropertyName == nameof(MainViewModel.IsBusy) && model.IsBusy;
            await model.AnalyseAsync(); await Settle(window);
            Assert.True(busySeen); Assert.False(loadingWhileBusy);
            Assert.DoesNotContain("History unavailable", model.Status); Assert.Contains("History unavailable", model.HistoryLoadError); Assert.True(model.HistoryLoadFailed);

            fake.Store.HistoryLoadFailure = null; fake.Store.History.AddRange(files.Entries());
            Invoke(Named<Button>(window, "RetryHistoryButton")); await WaitFor(window, () => model.History.Count == 6);
            Assert.False(model.HistoryLoadFailed); Assert.False(Named<StackPanel>(window, "HistoryLoadFailedState").IsVisible); Assert.True(Named<ListBox>(window, "HistoryList").IsVisible);
            Assert.Equal("", model.HistoryLoadError);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Removing_a_record_works_while_another_operation_is_running()
    {
        using var files = new FixtureFolder();
        var fake = new UiFakes(); fake.Store.History.AddRange(files.Entries()); var store = new GatedStore(fake.Store);
        store.Gate.SetResult();
        using var model = new MainViewModel(fake.Services with { Store = store }) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 2; await Settle(window);
            // Hold a long app operation open (initialisation waiting on history maintenance).
            store.PruneGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = model.InitializeAsync(); await Settle(window);
            Assert.True(model.IsBusy);

            var row = Row(model, "a1");
            await row.RemoveRecordCommand.ExecuteAsync(null); await Settle(window);
            Assert.DoesNotContain(fake.Store.History, h => h.Id == "a1"); Assert.Equal(5, model.History.Count);
            Assert.Equal("History record removed. The media file was kept.", model.Status); Assert.True(File.Exists(row.Entry.FilePath));
            Assert.False(model.IsHistoryLoading); Assert.True(Named<ListBox>(window, "HistoryList").IsVisible);

            store.PruneGate.SetResult(); await operation; await Settle(window);
            Assert.False(model.IsBusy); Assert.Equal(5, model.History.Count);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Every_library_binding_resolves_in_every_state()
    {
        // Reflection bindings fail silently at runtime: a misspelt or removed property only shows up as a log
        // entry. Capture binding errors raised by anything on the library page (rows, flyouts, state panels).
        using var files = new FixtureFolder();
        var fake = new UiFakes(); var store = new GatedStore(fake.Store);
        var previous = Avalonia.Logging.Logger.Sink; var sink = new BindingErrorSink(previous); Avalonia.Logging.Logger.Sink = sink;
        using var model = new MainViewModel(fake.Services with { Store = store }) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            var page = Named<Control>(window, "LibraryPage"); sink.Page = page;
            model.SelectedTab = 2; await Settle(window);
            // The sink itself works: a deliberately broken binding on the page is reported.
            var countRow = Named<Panel>(window, "HistoryCountRow");
            var probe = new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding("NoSuchLibraryProperty") };
            countRow.Children.Add(probe); await Settle(window);
            Assert.Contains(sink.Collect(), error => error.Contains("NoSuchLibraryProperty", StringComparison.Ordinal));
            countRow.Children.Remove(probe); sink.Reset();

            // Loading, then unreadable, then populated.
            fake.Store.HistoryLoadFailure = new IOException("History unavailable");
            var initialise = model.InitializeAsync(); await Settle(window);
            Assert.True(model.IsHistoryLoading);
            store.Gate.SetResult(); await initialise; await Settle(window);
            Assert.True(model.HistoryLoadFailed);
            fake.Store.HistoryLoadFailure = null; fake.Store.History.AddRange(files.Entries());
            await model.RetryHistoryCommand.ExecuteAsync(null); await Settle(window);
            Assert.Equal(6, model.History.Count);
            var list = Named<ListBox>(window, "HistoryList");
            for (var i = 0; i < model.History.Count; i++)
            {
                list.ScrollIntoView(i); await Settle(window);
                var more = RowButton(list, i, "MoreItemButton"); var menu = (MenuFlyout)more.Flyout!; menu.ShowAt(more); await Settle(window); menu.Hide();
            }
            var clear = Named<Button>(window, "ClearHistoryButton"); clear.Flyout!.ShowAt(clear); await Settle(window); clear.Flyout.Hide();
            Named<TextBox>(window, "HistorySearch").Text = "zebra"; await Settle(window);
            Named<TextBox>(window, "HistorySearch").Text = ""; await Settle(window);
            await model.ClearHistoryRecordsCommand.ExecuteAsync(null); await Settle(window);
            Assert.True(model.ShowHistoryEmpty);
            var errors = sink.Collect();
            Assert.True(errors.Count == 0, "Library binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
        finally { Avalonia.Logging.Logger.Sink = previous; model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    private static HistoryRow Row(MainViewModel model, string id) => model.History.Single(row => row.Entry.Id == id);

    private static T Named<T>(Control root, string name) where T : Control => root.FindNamed<T>(name) ?? throw new InvalidOperationException(name + " was not found.");

    private static Button RowButton(ListBox list, int index, string name)
        => list.ContainerFromIndex(index)?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == name) ?? throw new InvalidOperationException($"{name} was not realised for row {index}.");

    // A real click: raises Click and then runs the bound command, exactly like a pointer press.
    private static void Invoke(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, (button.Name ?? "Button") + " should be enabled.");
        var peer = ControlAutomationPeer.CreatePeerForElement(button);
        Assert.IsAssignableFrom<IInvokeProvider>(peer).Invoke();
    }

    // Menu items run their command from the Click class handler, as a pointer click does.
    private static void InvokeItem(MenuItem item)
    {
        Assert.True(item.IsEffectivelyEnabled, item.Header + " should be enabled.");
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
    }

    // Every visible row keeps its actions inside the row at this width and scale.
    private static void AssertRowsFit(Window window)
    {
        var list = Named<ListBox>(window, "HistoryList");
        foreach (var container in list.GetRealizedContainers().OfType<ListBoxItem>().Where(c => c.IsVisible))
        {
            var more = container.GetVisualDescendants().OfType<Button>().First(b => b.Name == "MoreItemButton");
            var right = more.TranslatePoint(new Point(more.Bounds.Width, 0), container)!.Value.X;
            Assert.True(right <= container.Bounds.Width - 8, $"Row actions overflow: {right} > {container.Bounds.Width - 8}");
            var title = container.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("row-title"));
            Assert.True(title.Bounds.Width > 120, "Title column collapsed to " + title.Bounds.Width);
        }
    }

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); await Task.Delay(15); }
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static async Task WaitFor(Window window, Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Settle(window); deadline.Token.ThrowIfCancellationRequested(); }
        await Settle(window);
    }

    private static void Capture(TopLevel topLevel, string name)
    {
        topLevel.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = topLevel.CaptureRenderedFrame(); Assert.NotNull(frame);
        if (string.IsNullOrWhiteSpace(Evidence)) return;
        Directory.CreateDirectory(Evidence); frame!.Save(Path.Combine(Evidence, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>Owned temporary media fixtures: four files that exist and two records whose files are gone.</summary>
    private sealed class FixtureFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "KeaClip-Ui-Library-" + Guid.NewGuid().ToString("N"));
        public FixtureFolder() => Directory.CreateDirectory(_root);
        public List<string> Paths { get; } = [];
        private string File(string name) { var path = Path.Combine(_root, name); System.IO.File.WriteAllText(path, "owned test fixture"); Paths.Add(path); return path; }
        public IEnumerable<HistoryEntry> Entries()
        {
            var now = DateTimeOffset.Now;
            return
            [
                new("v1", "Kea at Arthur's Pass · morning flight", "https://example.com/kea", "Example", now.AddMinutes(-20), File("kea.mp4"), "VideoAudio", "1080p", 48_200_000, "Finished"),
                new("a1", "Dawn chorus field recording", "https://example.com/dawn", "Example", now.AddDays(-1).AddMinutes(-30), File("dawn.m4a"), "Audio", "Best available", 6_400_000, "Finished"),
                new("m2", "Soundboard bite - clip.mp3", null, "Local export", now.AddDays(-2), Path.Combine(_root, "deleted.mp3"), "Audio Clip", "Verified export", 210_000, "Finished"),
                new("c1", "kea-flight - clip.mp4", null, "Local export", now.AddDays(-3), File("clip.mp4"), "Video Clip", "Verified export", 9_100_000, "Finished"),
                new("ar1", "Conservation talk (preservation archive)", "https://example.com/talk", "Example", now.AddDays(-40), File("talk.mkv"), "Archive", "Best available", 812_000_000, "Finished"),
                new("m1", "A very long title that keeps going so trimming can be checked without pushing the row actions out at laptop width", "https://example.com/long", "Example", now.AddDays(-400), Path.Combine(_root, "moved-away.mp4"), "VideoAudio", "720p", 120_000_000, "Finished"),
            ];
        }
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Records binding warnings and errors whose target belongs to the library page (including its popups).</summary>
    private sealed class BindingErrorSink(Avalonia.Logging.ILogSink? inner) : Avalonia.Logging.ILogSink
    {
        public Control? Page { get; set; }
        // Errors raised while a control is being attached are classified again later, once it is in the tree.
        private readonly List<(WeakReference<StyledElement> Source, string Text)> _pending = [];
        public List<string> Errors { get; } = [];
        public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area)
            => (area == Avalonia.Logging.LogArea.Binding && level >= Avalonia.Logging.LogEventLevel.Warning) || inner?.IsEnabled(level, area) == true;
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (inner?.IsEnabled(level, area) == true) inner.Log(level, area, source, messageTemplate, propertyValues);
            if (area != Avalonia.Logging.LogArea.Binding || level < Avalonia.Logging.LogEventLevel.Warning) return;
            var text = $"{source?.GetType().Name} ({(source as StyledElement)?.Name}): {string.Join(" | ", propertyValues)}";
            if (BelongsToPage(source)) Errors.Add(text);
            else if (source is StyledElement element) _pending.Add((new(element), text));
        }
        public List<string> Collect()
        {
            foreach (var (source, text) in _pending) if (source.TryGetTarget(out var element) && BelongsToPage(element)) Errors.Add(text);
            _pending.Clear();
            return Errors;
        }
        public void Reset() { Errors.Clear(); _pending.Clear(); }
        private bool BelongsToPage(object? source)
        {
            if (Page is null || source is not StyledElement element) return false;
            if (element.DataContext is HistoryRow) return true;
            if (element is Visual visual && Page.IsVisualAncestorOf(visual)) return true;
            return element is ILogical logical && logical.GetLogicalAncestors().Contains(Page);
        }
    }

    /// <summary>Holds the first history read (Gate) or history maintenance (PruneGate) open so in-between states can be observed.</summary>
    private sealed class GatedStore(FakeStore inner) : ILocalStore
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => inner.LoadSettingsAsync(cancellationToken);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => inner.SaveSettingsAsync(settings, cancellationToken);
        public async Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken = default) { await Gate.Task.WaitAsync(cancellationToken); return await inner.LoadHistoryAsync(cancellationToken); }
        public Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default) => inner.AddHistoryAsync(entry, cancellationToken);
        public Task RemoveHistoryAsync(string id, CancellationToken cancellationToken = default) => inner.RemoveHistoryAsync(id, cancellationToken);
        public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => inner.ClearHistoryAsync(cancellationToken);
        public TaskCompletionSource PruneGate { get; set; } = Completed();
        public async Task PruneHistoryAsync(int? retentionDays, CancellationToken cancellationToken = default) { await PruneGate.Task.WaitAsync(cancellationToken); await inner.PruneHistoryAsync(retentionDays, cancellationToken); }
        private static TaskCompletionSource Completed() { var source = new TaskCompletionSource(); source.SetResult(); return source; }
    }
}
