using Avalonia;
using Avalonia.Controls;

namespace Klippi.Desktop.Controls;

/// <summary>
/// A media frame that always spans the available width and takes the height of the given
/// aspect ratio (width / height), capped by the available height (for example <c>MaxHeight</c>).
/// When the cap applies the frame stays full width, so its edges line up with the content around
/// it; the video surface inside letterboxes the picture. Used for the editor's preview frame.
/// </summary>
public sealed class AspectFrame : Decorator
{
    public static readonly StyledProperty<double> RatioProperty = AvaloniaProperty.Register<AspectFrame, double>(nameof(Ratio), 16d / 9d, validate: value => double.IsFinite(value) && value > 0);

    static AspectFrame() => AffectsMeasure<AspectFrame>(RatioProperty);

    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    private Size Fit(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 640;
        var height = width / Ratio;
        if (double.IsFinite(available.Height)) height = Math.Min(height, available.Height);
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Fit(availableSize);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = Fit(finalSize);
        Child?.Arrange(new Rect(0, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
        return finalSize;
    }
}
