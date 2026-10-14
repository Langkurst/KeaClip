using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Klippi.Desktop.Controls;

/// <summary>
/// A circular progress indicator. Determinate values glide between updates via the
/// Value transition declared in the theme; indeterminate mode draws a fixed arc
/// that the theme spins with a RenderTransform animation (no re-render per frame).
/// </summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));
    public static readonly StyledProperty<bool> IsIndeterminateProperty = AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsIndeterminate));
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 3);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(TrackBrush));

    private Pen? _arcPen, _trackPen;

    static ProgressRing() => AffectsRender<ProgressRing>(ValueProperty, IsIndeterminateProperty, ThicknessProperty, ForegroundProperty, TrackBrushProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty || change.Property == ThicknessProperty) _arcPen = null;
        if (change.Property == TrackBrushProperty || change.Property == ThicknessProperty) _trackPen = null;
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var radius = (side - Thickness) / 2;
        if (radius <= 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (TrackBrush is { } track)
            context.DrawEllipse(null, _trackPen ??= new Pen(track, Thickness), center, radius, radius);
        if (Foreground is not { } brush) return;
        var fraction = IsIndeterminate ? .28 : Math.Clamp(Value / 100, 0, 1);
        if (fraction <= 0) return;
        _arcPen ??= new Pen(brush, Thickness, lineCap: PenLineCap.Round);
        if (fraction >= .9999) { context.DrawEllipse(null, _arcPen, center, radius, radius); return; }
        var start = -Math.PI / 2;
        var end = start + fraction * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)), false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, _arcPen, geometry);
    }
}
