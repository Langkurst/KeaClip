using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace Klippi.Desktop.Controls;

/// <summary>
/// A TextBlock whose number counts smoothly to each new value. The theme gives
/// <see cref="Value"/> a DoubleTransition, so the value changes on every animation frame;
/// with reduced motion there is no transition and the text simply updates.
/// <para>
/// Each frame formats into a stack buffer and the <see cref="TextBlock.Text"/> string is
/// replaced only when the displayed text actually changes (for the default
/// <c>{0:0}%</c>, when the rounded percent moves), so a running card allocates nothing
/// per frame and the text never relayouts for an unchanged number.
/// </para>
/// </summary>
public sealed class AnimatedNumberText : TextBlock
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<AnimatedNumberText, double>(nameof(Value));
    public static readonly StyledProperty<string> FormatProperty = AvaloniaProperty.Register<AnimatedNumberText, string>(nameof(Format), "{0:0}%");

    private const int StackBufferLength = 64;
    private CompositeFormat? _format;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FormatProperty) { _format = null; UpdateText(); }
        else if (change.Property == ValueProperty) UpdateText();
    }

    private void UpdateText()
    {
        var value = double.IsFinite(Value) ? Value : 0;
        var format = _format ??= CompositeFormat.Parse(Format ?? "");
        Span<char> buffer = stackalloc char[StackBufferLength];
        if (buffer.TryWrite(CultureInfo.CurrentCulture, format, out var written, value))
        {
            var shown = buffer[..written];
            if (Text is { } current && shown.SequenceEqual(current)) return;
            Text = shown.ToString();
        }
        else
        {
            // Longer than the stack buffer (an unusual format): format normally.
            var text = string.Format(CultureInfo.CurrentCulture, format, value);
            if (!string.Equals(text, Text, StringComparison.Ordinal)) Text = text;
        }
    }
}
