using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klippi.Core;
using Klippi.Desktop;
using Klippi.Desktop.Controls;
using Xunit;

namespace Klippi.UiTests;

/// <summary>
/// Shared design-system behaviour (Theme/*.axaml and Controls/*.cs): every page gets these from
/// the theme, so the tests use plain controls where they can and the real window where the
/// shell itself is the subject.
/// </summary>
public sealed class DesignSystemTests
{
    // ----- Buttons -----

    [AvaloniaTheory]
    [InlineData("icon small", 30)]
    [InlineData("icon large", 46)]
    [InlineData("icon", 36)]
    public void Icon_buttons_keep_a_zero_inset_in_every_size(string classes, double side)
    {
        var glyph = new StrokeIcon { Data = Geometry("IconClose"), Size = 16 };
        var button = new Button { Content = glyph };
        button.Classes.AddRange(classes.Split(' '));
        var window = Host(button);
        try
        {
            Assert.Equal(default, button.Padding);
            Assert.Equal(side, button.Bounds.Width, 1); Assert.Equal(side, button.Bounds.Height, 1);
            Assert.Equal(16, glyph.GlyphRect.Width, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("", 6)]
    [InlineData("small", 6)]
    [InlineData("large", 8)]
    [InlineData("nav", 8)]
    [InlineData("icon round", 999)]
    [InlineData("icon small round", 999)]
    [InlineData("icon round large primary", 999)]
    public void Keyboard_focus_draws_a_rounded_ring_that_matches_the_button(string classes, double radius)
    {
        var button = new Button { Content = "Choose" };
        if (classes.Length > 0) button.Classes.AddRange(classes.Split(' '));
        var window = Host(button);
        try
        {
            button.Focus(NavigationMethod.Tab); Settle(window);
            var ring = AdornerLayer.GetAdornerLayer(button)!.Children.OfType<Border>().Single();
            Assert.Equal(radius, ring.CornerRadius.TopLeft);
            Assert.Equal(2, ring.BorderThickness.Left);
            Assert.Equal(Color("AppAccent", ThemeVariant.Light), ((ISolidColorBrush)ring.BorderBrush!).Color);
            Assert.IsType<Border>(ring.Child);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Disabled_primary_buttons_stay_readable_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var fill = Color("AccentButtonBackgroundDisabled", theme); var label = Color("AccentButtonForegroundDisabled", theme);
            Assert.True(Contrast(fill, label) >= 3, $"{theme}: disabled primary label contrast is {Contrast(fill, label):0.00}:1");
            // The glyph dims with the label instead of keeping the bright enabled colour.
            var glyph = new StrokeIcon { Data = Geometry("IconDownload") };
            var button = new Button { Classes = { "primary" }, IsEnabled = false, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, new TextBlock { Text = "Download" } } } };
            var window = Host(button, theme);
            try { Assert.Equal(label, ((ISolidColorBrush)glyph.Foreground!).Color); }
            finally { window.Close(); }
        }
    }

    // ----- Icons -----

    [AvaloniaFact]
    public void Stroke_icons_keep_their_size_and_stay_centred_when_a_parent_stretches_them()
    {
        // A filled square (6.5-17.5 on the 24px grid) drawn at Size 18 inside a 40px tile.
        var icon = new StrokeIcon { Data = Geometry("IconStop"), IsFilled = true, Size = 18, Foreground = Brushes.Black };
        var tile = new Border { Width = 40, Height = 40, Child = icon };
        tile.Measure(new Size(40, 40)); tile.Arrange(new Rect(0, 0, 40, 40));
        Assert.Equal(new Size(40, 40), icon.Bounds.Size);
        Assert.Equal(new Rect(11, 11, 18, 18), icon.GlyphRect);
        using var bitmap = new RenderTargetBitmap(new PixelSize(40, 40), new Vector(96, 96));
        bitmap.Render(tile);
        var (left, top, right, bottom) = PaintedBox(bitmap);
        Assert.InRange(right - left + 1, 7, 10); Assert.InRange(bottom - top + 1, 7, 10); // 11/24 of 18px, not of 40px
        Assert.InRange((left + right) / 2.0, 18.5, 20.5); Assert.InRange((top + bottom) / 2.0, 18.5, 20.5);

        // Squeezed below its Size, the glyph shrinks to fit instead of spilling out.
        tile.Width = tile.Height = 12; tile.Measure(new Size(12, 12)); tile.Arrange(new Rect(0, 0, 12, 12));
        Assert.Equal(new Rect(0, 0, 12, 12), icon.GlyphRect);
    }

    [AvaloniaFact]
    public void Shared_icons_cover_what_pages_used_to_define_locally()
    {
        foreach (var key in new[] { "IconMore", "IconMinus", "IconPower", "IconTray", "IconQuestion" })
            Assert.IsAssignableFrom<Geometry>(Application.Current!.FindResource(key));
    }

    [AvaloniaFact]
    public void Icon_wells_come_from_the_theme_in_every_tone_and_size()
    {
        var glyph = new StrokeIcon { Data = Geometry("IconInfo") };
        var nested = new StrokeIcon { Data = Geometry("IconFilm") };
        var neutralGlyph = new StrokeIcon { Data = Geometry("IconFolder") };
        var well = new Border { Classes = { "well" }, Child = glyph };
        var large = new Border { Classes = { "well", "large" }, Child = new Panel { Children = { nested } } };
        var neutral = new Border { Classes = { "well", "neutral" }, Child = neutralGlyph };
        var window = Host(new StackPanel { Spacing = 4, Children = { well, large, neutral } });
        try
        {
            Assert.Equal(new Size(36, 36), well.Bounds.Size);
            Assert.Equal(VerticalAlignment.Top, well.VerticalAlignment);
            Assert.Equal(8, well.CornerRadius.TopLeft);
            Assert.Equal(Color("AppAccentSubtle", ThemeVariant.Light), ((ISolidColorBrush)well.Background!).Color);
            Assert.Equal(Color("AppAccent", ThemeVariant.Light), ((ISolidColorBrush)glyph.Foreground!).Color);
            Assert.Equal(18, glyph.Size);
            // Card headings: 38px with a 20px glyph, also when the glyph sits in a two-state Panel.
            Assert.Equal(new Size(38, 38), large.Bounds.Size);
            Assert.Equal(20, nested.Size);
            Assert.Equal(Color("AppAccent", ThemeVariant.Light), ((ISolidColorBrush)nested.Foreground!).Color);
            Assert.Equal(Color("AppSurfaceRaised", ThemeVariant.Light), ((ISolidColorBrush)neutral.Background!).Color);
            Assert.Equal(Color("AppMuted", ThemeVariant.Light), ((ISolidColorBrush)neutralGlyph.Foreground!).Color);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Card_headings_on_every_page_use_the_shared_large_well()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 1180, 800);
        try
        {
            await model.InitializeAsync(); Settle(window);
            Border[] Wells(string page) => window.FindNamed<Control>(page)!.GetLogicalDescendants().OfType<Border>().Where(border => border.Classes.Contains("well")).ToArray();
            var settings = Wells("SettingsPage"); var editor = Wells("EditorPage"); var download = Wells("DownloadPage");
            // Nine Settings cards, including Watch links (shown only when the watch link service is present), and the
            // Advanced settings row, which wears the same heading well.
            Assert.Equal(10, settings.Count(border => border.Classes.Contains("large")));
            // Export and Share; the preview header is one slim line without a well, so the picture gets the height.
            Assert.Equal(2, editor.Count(border => border.Classes.Contains("large")));
            Assert.NotEmpty(download);
            Assert.All(settings.Concat(editor).Where(border => border.Classes.Contains("large")), border => Assert.Equal(38, border.Width));
            Assert.All(download, border => Assert.Equal(36, border.Width));
            // Settings centres its wells on the short heading beside them; elsewhere they sit on the first text line.
            Assert.All(settings, border => Assert.Equal(VerticalAlignment.Center, border.VerticalAlignment));
            Assert.Contains(editor, border => border.VerticalAlignment == VerticalAlignment.Top);
        }
        finally { Close(window, model); }
    }

    // ----- Inputs -----

    [AvaloniaFact]
    public void Toggle_switches_are_exactly_their_track_so_trailing_switches_reach_the_edge()
    {
        var toggle = new ToggleSwitch { HorizontalAlignment = HorizontalAlignment.Right };
        var row = new Border { Width = 300, Child = toggle };
        var window = Host(row);
        try
        {
            var track = toggle.GetVisualDescendants().OfType<Border>().First(border => border.Name == "OuterBorder");
            Assert.Equal(40, toggle.Bounds.Width, 1);
            Assert.Equal(300, track.TranslatePoint(new Point(track.Bounds.Width, 0), row)!.Value.X, 1);
            // The click and focus area stays on the track.
            var area = toggle.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "SwitchAreaGrid");
            Assert.Equal(40, area.Bounds.Width, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Choice_tiles_share_the_inset_face_and_rounded_focus_ring()
    {
        var first = new RadioButton { Classes = { "tile" }, GroupName = "design-tiles", Content = "One", IsChecked = true };
        var second = new RadioButton { Classes = { "tile" }, GroupName = "design-tiles", Content = "Two" };
        var window = Host(new StackPanel { Width = 300, Children = { first, second } });
        try
        {
            var face = second.GetVisualDescendants().OfType<Border>().First(border => border.Name == "TileBorder");
            Assert.Equal(new Thickness(4), face.Margin);
            second.Focus(NavigationMethod.Tab); Settle(window);
            var ring = AdornerLayer.GetAdornerLayer(second)!.Children.OfType<Border>().Single();
            Assert.Equal(16, ring.CornerRadius.TopLeft); Assert.Equal(2, ring.BorderThickness.Left);
            // Focus never paints an unchecked face like the checked accent border; a checked face keeps its accent.
            await WaitForTransitions(window);
            Assert.Equal(Color("AppBorderStrong", ThemeVariant.Light), ((ISolidColorBrush)face.BorderBrush!).Color);
            first.Focus(NavigationMethod.Tab); await WaitForTransitions(window);
            var checkedFace = first.GetVisualDescendants().OfType<Border>().First(border => border.Name == "TileBorder");
            Assert.Equal(Color("AppAccent", ThemeVariant.Light), ((ISolidColorBrush)checkedFace.BorderBrush!).Color);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Flyouts_and_menus_use_the_raised_surface_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.Equal(Color("AppSurfaceRaised", theme), Color("FlyoutPresenterBackground", theme));
            Assert.Equal(Color("AppSurfaceRaised", theme), Color("MenuFlyoutPresenterBackground", theme));
            Assert.Equal(Color("AppBorderStrong", theme), Color("FlyoutBorderThemeBrush", theme));
            Assert.Equal(Color("AppBorderStrong", theme), Color("MenuFlyoutPresenterBorderBrush", theme));
            Assert.Equal(Color("AppSurfaceHover", theme), Color("MenuFlyoutItemBackgroundPointerOver", theme));

            // A plain flyout with no page styles picks the colours up.
            var button = new Button { Content = "More", Flyout = new Flyout { Content = new TextBlock { Text = "Hello" } } };
            var window = Host(button, theme);
            try
            {
                button.Flyout!.ShowAt(button); Settle(window);
                var presenter = ((Control)((Flyout)button.Flyout).Content!).FindAncestorOfType<FlyoutPresenter>();
                Assert.NotNull(presenter);
                Assert.Equal(Color("AppSurfaceRaised", theme), ((ISolidColorBrush)presenter!.Background!).Color);
                button.Flyout.Hide();
            }
            finally { window.Close(); }
        }
    }

    // ----- Motion helpers -----

    [AvaloniaFact]
    public void Animated_numbers_replace_their_text_only_when_the_shown_number_changes()
    {
        var number = new AnimatedNumberText { Format = "{0:0}%" };
        number.Value = 41.2; var shown = number.Text;
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0}%", 41.2), shown);
        number.Value = 41.3; number.Value = 41.45;
        Assert.Same(shown, number.Text); // same rounded number: no new string, no relayout
        number.Value = 41.6;
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0}%", 41.6), number.Text);
        number.Value = double.NaN;
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0}%", 0d), number.Text);
        number.Format = "{0:0.0} MB";
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.0} MB", 0d), number.Text);
    }

    // ----- Shell -----

    // Page sections (enter e1..e4) rise in one after another, and however the pages are switched they always end fully
    // visible and in place: a held-back section must never stay hidden.
    [AvaloniaFact]
    public async Task Page_sections_rise_in_turn_and_always_finish_visible()
    {
        var fake = new UiFakes(); fake.Platform.SystemReducedMotion = false;
        using var model = new MainViewModel(fake.Services);
        var window = Show(model, 1180, 1400);
        string[] sections = ["AppearanceCard", "DownloadsCard", "AboutCard", "AdvancedSettingsToggle"];
        async Task Run(int ms) { for (var waited = 0; waited < ms; waited += 50) { await Task.Delay(50); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); } }
        try
        {
            await model.InitializeAsync(); model.ReducedMotion = false; Settle(window);
            Assert.Contains("motion", window.Classes);
            model.SelectedTab = 4; window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            // At the start a later section is held at its first frame (FillMode Backward), not shown early.
            Assert.True(window.FindNamed<Border>("AboutCard")!.Opacity < 1);
            await Run(700);
            Assert.All(sections, name => Assert.Equal(1, window.FindNamed<Control>(name)!.Opacity));
            // Quick switching away and back mid-entrance still settles fully visible.
            model.SelectedTab = 0; await Run(50); model.SelectedTab = 4; await Run(50); model.SelectedTab = 3; await Run(50); model.SelectedTab = 4;
            await Run(700);
            Assert.All(sections, name => Assert.Equal(1, window.FindNamed<Control>(name)!.Opacity));
            Assert.Equal(1, window.FindNamed<Border>("LinkCard")!.Opacity);
        }
        finally { Close(window, model); }
    }

    // Headings and the wordmark really render in the bundled Bricolage Grotesque (both weights), not a silent fallback.
    [AvaloniaTheory]
    [InlineData("title", 700)]
    [InlineData("section", 600)]
    [InlineData("wordmark", 700)]
    public void Headings_use_the_bundled_display_font(string style, int weight)
    {
        var heading = new TextBlock { Text = "Kea", Classes = { style } };
        var window = Host(heading);
        try
        {
            Assert.Equal((FontWeight)weight, heading.FontWeight);
            var typeface = new Typeface(heading.FontFamily, heading.FontStyle, heading.FontWeight);
            Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
            // The SemiBold file names its legacy family "Bricolage Grotesque SemiBold"; both are the bundled font.
            Assert.StartsWith("Bricolage Grotesque", glyphs!.FamilyName);
            Assert.Equal((FontWeight)weight, glyphs.Weight);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Cancel_operation_stays_in_layout_so_the_header_never_reflows()
    {
        var fake = new UiFakes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new FakeTemporaryWorkspaces { Handler = async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(); } };
        using var model = new MainViewModel(fake.Services with { TemporaryWorkspaces = cleanup });
        var window = Show(model, 880, 740);
        try
        {
            await model.InitializeAsync(); model.SelectedTab = 2; Settle(window);
            var cancel = window.FindNamed<Button>("CancelOperationButton")!;
            var title = window.FindNamed<TextBlock>("PageTitleText")!; var header = window.FindNamed<Grid>("PageHeader")!;
            Assert.Equal(model.PageTitle, title.Text);
            Assert.Null(window.FindNamed<TextBlock>("PageSubtitleText"));
            var idleBounds = title.Bounds; var idleCancel = cancel.Bounds; var idleHeader = header.Bounds;
            Assert.True(cancel.IsVisible); Assert.False(cancel.IsEnabled); Assert.False(cancel.IsHitTestVisible);
            // Idle, it only holds its place: a screen reader must not find a "Cancel operation" button.
            Assert.Empty(CancelPeers(window));
            // The title takes the row beside the cancel slot.
            Assert.Equal(header.Bounds.Width - idleCancel.Width - 16, title.Bounds.Width, 1);
            await Until(() => cancel.Opacity == 0);

            var operation = model.CleanTemporaryFilesCommand.ExecuteAsync(null);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Settle(window);
            Assert.True(model.IsBusy);
            Assert.True(cancel.IsEnabled); Assert.True(cancel.IsHitTestVisible);
            await Until(() => cancel.Opacity == 1);
            Assert.Equal(idleBounds, title.Bounds); Assert.Equal(idleCancel, cancel.Bounds); Assert.Equal(idleHeader, header.Bounds);
            Assert.Equal("Cancel operation", AutomationProperties.GetName(cancel));
            Assert.Contains(CancelPeers(window), peer => peer.Owner == cancel);
            Assert.True(ControlAutomationPeer.CreatePeerForElement(cancel).IsControlElement());
            await Evidence(window, "ds-header-busy-880");

            model.CancelOperationCommand.Execute(null);
            await operation.WaitAsync(TimeSpan.FromSeconds(5)); Settle(window);
            Assert.False(cancel.IsEnabled); Assert.False(cancel.IsHitTestVisible);
            await Until(() => cancel.Opacity == 0);
            Assert.Equal(idleBounds, title.Bounds);
            Assert.Empty(CancelPeers(window));
        }
        finally { Close(window, model); }
    }

    // Control-view automation peers (what a screen reader walks) named "Cancel operation".
    private static List<ControlAutomationPeer> CancelPeers(Window window)
    {
        var found = new List<ControlAutomationPeer>();
        void Walk(AutomationPeer peer)
        {
            if (peer is ControlAutomationPeer control && peer.IsControlElement() && peer.GetName() == "Cancel operation") found.Add(control);
            foreach (var child in peer.GetChildren()) Walk(child);
        }
        Walk(ControlAutomationPeer.CreatePeerForElement(window));
        return found;
    }

    [AvaloniaFact]
    public async Task Nav_items_line_up_and_keep_their_chip_and_dot_at_the_right()
    {
        await using var scene = await QueueScene.CreateAsync(concurrent: 1);
        var window = scene.Window;
        var names = new[] { "DownloadNav", "QueueNav", "LibraryNav", "EditorNav", "SettingsNav" };
        AssertNavColumnsLineUp();

        // A running job shows the queue chip; an edited setting shows the unsaved dot.
        await scene.EnqueueAsync("https://media.example.org/nav", "Nav");
        scene.Model.DownloadDirectory = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Media", "Elsewhere");
        scene.Flush();
        Assert.True(scene.Model.HasActiveQueue); Assert.True(scene.Model.HasUnsavedSettings);
        AssertNavColumnsLineUp();
        foreach (var (navName, marker) in new (string, Control)[]
        {
            ("QueueNav", window.FindNamed<Button>("QueueNav")!.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("chip"))),
            ("SettingsNav", window.FindNamed<Border>("SettingsUnsavedDot")!),
        })
        {
            var nav = window.FindNamed<Button>(navName)!;
            Assert.True(marker.IsEffectivelyVisible, navName);
            // The marker's slot (its own margin included) ends at the item's right padding.
            var right = marker.TranslatePoint(new Point(marker.Bounds.Width + marker.Margin.Right, 0), nav)!.Value.X;
            Assert.InRange(nav.Bounds.Width - right, nav.Padding.Right, nav.Padding.Right + nav.BorderThickness.Right + 0.5);
        }

        void AssertNavColumnsLineUp()
        {
            var icons = new List<double>(); var labels = new List<double>();
            foreach (var name in names)
            {
                var nav = window.FindNamed<Button>(name)!;
                var icon = nav.GetVisualDescendants().OfType<StrokeIcon>().First();
                var label = nav.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == AutomationProperties.GetName(nav));
                icons.Add(icon.TranslatePoint(default, window)!.Value.X); labels.Add(label.TranslatePoint(default, window)!.Value.X);
            }
            // Same left edge for every icon and every label (a fixed-width content grid used to push two of them 1px left).
            Assert.All(icons, x => Assert.Equal(icons[0], x, 0.01));
            Assert.All(labels, x => Assert.Equal(labels[0], x, 0.01));
        }
    }

    [AvaloniaFact]
    public async Task Settings_nav_shows_a_warm_dot_while_changes_are_unsaved()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 1180, 800);
        try
        {
            await model.InitializeAsync(); Settle(window);
            var dot = window.FindNamed<Border>("SettingsUnsavedDot")!;
            Assert.False(dot.IsEffectivelyVisible);
            model.DownloadDirectory = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Media", "Elsewhere"); Settle(window);
            Assert.True(model.HasUnsavedSettings); Assert.True(dot.IsEffectivelyVisible);
            Assert.Equal(Color("AppWarm", window.ActualThemeVariant), ((ISolidColorBrush)dot.Background!).Color);
            Assert.Equal("Settings", AutomationProperties.GetName(window.FindNamed<Button>("SettingsNav")!));
            await Evidence(window, "ds-settings-unsaved-dot");
            model.DiscardSettingsCommand.Execute(null); Settle(window);
            Assert.False(dot.IsEffectivelyVisible);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Fullscreen_overlay_is_dark_and_its_icon_buttons_are_named()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 1180, 800); window.RequestedThemeVariant = ThemeVariant.Light;
        try
        {
            Settle(window);
            var overlay = window.FindNamed<ThemeVariantScope>("FullscreenOverlay")!;
            Assert.Equal(ThemeVariant.Dark, overlay.ActualThemeVariant);
            var play = window.FindNamed<Button>("FullscreenPlayButton")!; var exit = window.FindNamed<Button>("ExitFullscreenButton")!;
            Assert.Equal(model.PlayLabel, AutomationProperties.GetName(play));
            Assert.Equal("Exit fullscreen", AutomationProperties.GetName(exit));
            Assert.NotNull(window.FindNamed<Border>("FullscreenPreviewHost"));

            // Shown over a light window, the player keeps light glyphs and text on the dark backdrop.
            model.PreviewFullscreen = true; Settle(window);
            Assert.True(overlay.IsEffectivelyVisible);
            var onScrim = Color("AppOnScrim", ThemeVariant.Dark);
            var glyphs = play.GetVisualDescendants().OfType<StrokeIcon>().Concat(exit.GetVisualDescendants().OfType<StrokeIcon>()).ToArray();
            Assert.Equal(3, glyphs.Length);
            Assert.All(glyphs, glyph => Assert.Equal(onScrim, ((ISolidColorBrush)glyph.Foreground!).Color));
            await Evidence(window, "ds-fullscreen-overlay");
            model.PreviewFullscreen = false;
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public void Text_on_media_uses_the_light_on_scrim_token_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var onScrim = Color("AppOnScrim", theme);
            Assert.True(Luminance(onScrim) > 0.8, $"{theme}: AppOnScrim must be light");
            // The badge scrim keeps that text readable on a mid-tone picture, and legible even on a white one.
            Assert.True(Contrast(Over(Color("AppScrim", theme), Avalonia.Media.Color.FromRgb(128, 128, 128)), onScrim) >= 4.5, $"{theme}: on a mid-tone picture");
            Assert.True(Contrast(Over(Color("AppScrim", theme), Colors.White), onScrim) >= 3, $"{theme}: on a white picture");
        }
    }

    // ----- Pages using shared pieces -----

    [AvaloniaFact]
    public async Task Cover_option_is_unavailable_with_a_reason_for_formats_that_cannot_hold_it()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 1180, 1700);
        try
        {
            await model.InitializeAsync(); Settle(window);
            window.FindNamed<Expander>("MoreOptions")!.IsExpanded = true; Settle(window);
            var row = window.FindNamed<Grid>("EmbedCoverRow")!; var note = window.FindNamed<TextBlock>("CoverArtNoteText")!;
            var container = window.FindNamed<ComboBox>("ContainerPicker")!;
            var label = window.FindNamed<TextBlock>("EmbedCoverLabel")!;
            Color LabelColour() => ((ISolidColorBrush)label.Foreground!).Color;
            Assert.True(row.IsEnabled); Assert.False(note.IsVisible);
            Assert.Equal(Color("AppText", window.ActualThemeVariant), LabelColour());
            Assert.True(ToolTip.GetShowOnDisabled(row));

            container.SelectedItem = MediaContainer.WebM; Settle(window);
            Assert.False(row.IsEnabled); Assert.True(note.IsEffectivelyVisible);
            Assert.Equal(Color("AppFaint", window.ActualThemeVariant), LabelColour()); // the label dims with its switch
            Assert.Contains("WebM", ToolTip.GetTip(row) as string); Assert.Contains("WebM", note.Text);
            container.SelectedItem = MediaContainer.Mp4; Settle(window);
            Assert.True(row.IsEnabled); Assert.False(note.IsVisible);

            model.DownloadMode = DownloadMode.Audio; Settle(window);
            window.FindNamed<ComboBox>("AudioFormatPicker")!.SelectedItem = AudioFormat.Wav; Settle(window);
            Assert.False(row.IsEnabled); Assert.Contains("WAV", note.Text); Assert.Contains("WAV", ToolTip.GetTip(row) as string);
            note.BringIntoView(); await Evidence(window, "ds-cover-unavailable-wav");
            window.FindNamed<ComboBox>("AudioFormatPicker")!.SelectedItem = AudioFormat.Mp3; Settle(window);
            Assert.True(row.IsEnabled);

            model.DownloadMode = DownloadMode.Archive; Settle(window);
            Assert.False(row.IsEnabled); Assert.False(note.IsVisible); Assert.Contains("Archives", ToolTip.GetTip(row) as string);
            // The choice itself is kept for formats that can hold a cover.
            Assert.Equal(model.EmbedThumbnail, model.BuildDownloadOptions(UiFakes.Info.Url).EmbedThumbnail);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task Empty_states_draw_the_theme_aware_kea_instead_of_a_dark_bitmap_tile()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 1180, 800);
        try
        {
            await model.InitializeAsync(); Settle(window);
            foreach (var name in new[] { "QueueEmptyState", "HistoryEmptyState" })
            {
                var state = window.FindNamed<Control>(name)!;
                Assert.Single(state.GetLogicalDescendants().OfType<KeaMascot>());
                Assert.Empty(state.GetLogicalDescendants().OfType<Image>());
            }
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public void Editor_says_trim_silence_only_touches_the_start_and_end()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = Show(model, 1180, 800);
        try
        {
            var check = window.FindNamed<CheckBox>("TrimSilenceCheck")!;
            Assert.Equal("Trim silence at start and end", ((TextBlock)check.Content!).Text);
            Assert.Equal("Trim silence at start and end", AutomationProperties.GetName(check));
        }
        finally { Close(window, model); }
    }

    // ----- Helpers -----

    private static Geometry Geometry(string key) => (Geometry)Application.Current!.FindResource(key)!;

    private static Color Color(string key, ThemeVariant theme)
    {
        Assert.True(Application.Current!.TryGetResource(key, theme, out var value), key);
        return value switch { ISolidColorBrush brush => brush.Color, Color color => color, _ => throw new InvalidOperationException(key) };
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value) { var c = value / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color Over(Color top, Color bottom)
    {
        var alpha = top.A / 255d;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * alpha + b * (1 - alpha));
        return Avalonia.Media.Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static (int Left, int Top, int Right, int Bottom) PaintedBox(Bitmap bitmap)
    {
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var buffer = new byte[width * height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, width * 4); }
        finally { handle.Free(); }
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (buffer[(y * width + x) * 4 + 3] < 128) continue;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        Assert.True(right >= 0, "nothing was painted");
        return (left, top, right, bottom);
    }

    private static Window Host(Control content, ThemeVariant? theme = null)
    {
        var window = new Window { Width = 400, Height = 300, Content = new Border { Padding = new Thickness(20), Child = content }, RequestedThemeVariant = theme ?? ThemeVariant.Light };
        window.Show(); Settle(window);
        return window;
    }

    // Colour transitions run on wall-clock time; wait them out so the final state is checked.
    private static async Task WaitForTransitions(Window window) { Settle(window); await Task.Delay(260); Settle(window); }

    // Renders the frame in both themes (so a broken render fails the test) and saves the PNGs only
    // when KEACLIP_UI_EVIDENCE_DIRECTORY is set; the window's own theme is restored afterwards.
    private static async Task Evidence(Window window, string name)
    {
        var requested = window.RequestedThemeVariant;
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; await WaitForTransitions(window);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                if (string.IsNullOrWhiteSpace(output)) continue;
                Directory.CreateDirectory(output);
                frame.Save(Path.Combine(output, name + "-" + theme.ToString().ToLowerInvariant() + ".png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.RequestedThemeVariant = requested; await WaitForTransitions(window); }
    }

    private static MainWindow Show(MainViewModel model, double width, double height)
    {
        model.ReducedMotion = true;
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show(); window.UpdateLayout();
        return window;
    }

    private static void Close(Window window, MainViewModel model) { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }

    private static void Settle(Window window) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, deadline.Token); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
    }
}
