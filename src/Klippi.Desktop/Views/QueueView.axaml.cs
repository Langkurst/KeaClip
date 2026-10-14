using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.VisualTree;
using Klippi.Core;
using Klippi.Desktop.Controls;

namespace Klippi.Desktop.Views;

public sealed partial class QueueView : UserControl
{
    public static readonly DirectProperty<QueueView, bool> IsOnScreenProperty =
        AvaloniaProperty.RegisterDirect<QueueView, bool>(nameof(IsOnScreen), view => view.IsOnScreen);

    private Window? _window;
    private bool _isOnScreen;

    public QueueView()
    {
        InitializeComponent();
        QueueToolbar.CollapsibleLabel = ClearFinishedLabel;
    }

    /// <summary>
    /// True while this page is shown in a visible, non-minimised window. The breathing dot and the
    /// activity ring only animate then, so a long download in the tray or a minimised window costs
    /// no animation frames.
    /// </summary>
    public bool IsOnScreen { get => _isOnScreen; private set => SetAndRaise(IsOnScreenProperty, ref _isOnScreen, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowPropertyChanged;
        UpdateOnScreen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        UpdateOnScreen();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateOnScreen();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) UpdateOnScreen();
    }

    private void UpdateOnScreen() => IsOnScreen = IsVisible && _window is { IsVisible: true } window && window.WindowState != WindowState.Minimized;
}

/// <summary>
/// Puts the queue summary (first child) and the toolbar buttons (second child) on one line. When both do not fit
/// (the narrowest window, double-digit counts or the longer "Resume new starts"), the <see cref="CollapsibleLabel"/>
/// folds away first and its button keeps only its icon, so the summary stays on one line. It wraps only if even
/// that is too narrow.
/// </summary>
public sealed class QueueToolbarPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<QueueToolbarPanel, double>(nameof(Spacing), 12);
    private double _labelExtent;

    static QueueToolbarPanel() => AffectsMeasure<QueueToolbarPanel>(SpacingProperty);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>The button label that gives way first.</summary>
    public Control? CollapsibleLabel { get; set; }

    /// <summary>True while the collapsible label is folded away.</summary>
    public bool IsCompact { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [var summary, var actions]) return base.MeasureOverride(availableSize);
        actions.Measure(Size.Infinity);
        summary.Measure(Size.Infinity);
        var gap = summary.IsVisible ? Spacing : 0;
        if (CollapsibleLabel is { } label && double.IsFinite(availableSize.Width))
        {
            // While the label shows, remember exactly how much room it takes (with its icon spacing), so the
            // full width is known while it is folded away and the decision cannot flip back and forth.
            if (!IsCompact && label.IsVisible && label.DesiredSize.Width > 0)
                _labelExtent = label.DesiredSize.Width + ((label.GetVisualParent() as StackPanel)?.Spacing ?? 0);
            var full = summary.DesiredSize.Width + gap + actions.DesiredSize.Width + (IsCompact ? _labelExtent : 0);
            var compact = IsCompact ? full > availableSize.Width - 1 : full > availableSize.Width + 0.5;
            if (compact != IsCompact)
            {
                IsCompact = compact;
                label.IsVisible = !compact;
                actions.Measure(Size.Infinity);
            }
        }
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : summary.DesiredSize.Width + gap + actions.DesiredSize.Width;
        summary.Measure(new Size(Math.Max(0, width - gap - actions.DesiredSize.Width), double.PositiveInfinity));
        return new Size(Math.Min(width, summary.DesiredSize.Width + gap + actions.DesiredSize.Width), Math.Max(summary.DesiredSize.Height, actions.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is not [var summary, var actions]) return base.ArrangeOverride(finalSize);
        var actionsWidth = Math.Min(actions.DesiredSize.Width, finalSize.Width);
        actions.Arrange(new Rect(finalSize.Width - actionsWidth, (finalSize.Height - actions.DesiredSize.Height) / 2, actionsWidth, actions.DesiredSize.Height));
        var room = Math.Max(0, finalSize.Width - actionsWidth - (summary.IsVisible ? Spacing : 0));
        summary.Arrange(new Rect(0, (finalSize.Height - summary.DesiredSize.Height) / 2, room, summary.DesiredSize.Height));
        return finalSize;
    }
}

/// <summary>
/// Drives a ProgressBar or AnimatedNumberText from a percent so it glides forward but snaps back. A new
/// stream or stage restarts at its own beginning; sweeping the bar (or counting the number) backwards
/// would read as lost progress. It also snaps when a recycled card shows a different job.
/// </summary>
public static class QueueProgress
{
    // NaN by default, so even a first value of 0 raises a change and records which row the control shows;
    // the row's next value then glides instead of being mistaken for a recycled card.
    public static readonly AttachedProperty<double> PercentProperty = AvaloniaProperty.RegisterAttached<Control, double>("Percent", typeof(QueueProgress), double.NaN);
    private static readonly AttachedProperty<object?> SourceProperty = AvaloniaProperty.RegisterAttached<Control, object?>("Source", typeof(QueueProgress));

    static QueueProgress() => PercentProperty.Changed.AddClassHandler<Control>(OnPercentChanged);

    public static double GetPercent(Control control) => control.GetValue(PercentProperty);
    public static void SetPercent(Control control, double value) => control.SetValue(PercentProperty, value);

    private static void OnPercentChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        AvaloniaProperty? target = control switch { RangeBase => RangeBase.ValueProperty, AnimatedNumberText => AnimatedNumberText.ValueProperty, _ => null };
        if (target is null) return;
        var value = e.GetNewValue<double>();
        if (!double.IsFinite(value)) value = 0;
        var source = control.DataContext;
        var snap = value < (double)control.GetValue(target)! - 0.5 || !ReferenceEquals(source, control.GetValue(SourceProperty));
        control.SetValue(SourceProperty, source);
        if (!snap) { control.SetValue(target, value); return; }
        // Removing the transitions disposes any running one; restoring them afterwards glides on from the new value.
        control.SetValue(Animatable.TransitionsProperty, (Transitions?)null);
        control.SetValue(target, value);
        control.ClearValue(Animatable.TransitionsProperty);
    }
}

/// <summary>Maps a job's download mode to the shared icon shown when it has no thumbnail.</summary>
public sealed class QueueModeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch { DownloadMode.Audio => "IconMusic", DownloadMode.VideoOnly => "IconScreen", DownloadMode.Archive => "IconArchive", _ => "IconFilm" };
        return Application.Current is { } app && app.TryGetResource(key, null, out var resource) ? resource as Geometry : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
