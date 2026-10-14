using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Klippi.Desktop.Controls;

/// <summary>
/// Draws a 24x24 line-icon geometry scaled to <see cref="Size"/> with round caps.
/// The glyph is drawn at <see cref="Size"/> (smaller only when squeezed) and centred in the
/// arranged bounds, so an icon that its parent stretches (a badge, tile or well) keeps its
/// size without page-local alignment styles.
/// The pen is cached and rebuilt only when the brush or thickness changes, so a
/// hover colour transition or re-render never allocates per frame.
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<StrokeIcon, double>(nameof(Size), 18);
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 1.8);
    public static readonly StyledProperty<bool> IsFilledProperty = AvaloniaProperty.Register<StrokeIcon, bool>(nameof(IsFilled));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    private static readonly Rect ViewBox = new(0, 0, 24, 24);
    private Pen? _pen;

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, SizeProperty, StrokeThicknessProperty, IsFilledProperty, ForegroundProperty);
        AffectsMeasure<StrokeIcon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool IsFilled { get => GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty || change.Property == StrokeThicknessProperty) _pen = null;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    /// <summary>
    /// The square the 24x24 glyph is drawn into, in local coordinates: <see cref="Size"/>,
    /// never larger than the arranged bounds, centred in them.
    /// </summary>
    internal Rect GlyphRect
    {
        get
        {
            var size = double.IsFinite(Size) ? Size : 0;
            var side = Math.Min(size, Math.Min(Bounds.Width, Bounds.Height));
            return side > 0 ? new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side) : default;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry || Foreground is not { } brush) return;
        var glyph = GlyphRect;
        if (glyph.Width <= 0) return;
        var scale = glyph.Width / ViewBox.Width;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(glyph.X, glyph.Y)))
        {
            if (IsFilled) context.DrawGeometry(brush, null, geometry);
            else context.DrawGeometry(null, _pen ??= new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
