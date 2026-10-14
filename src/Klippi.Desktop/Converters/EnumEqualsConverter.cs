using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Klippi.Desktop.Converters;

/// <summary>
/// Compares an enum value with <c>ConverterParameter</c>. Built for RadioButton groups
/// bound two-way to one enum property:
/// <code>IsChecked="{Binding DownloadMode, Mode=TwoWay, Converter={x:Static conv:EnumEqualsConverter.Instance}, ConverterParameter={x:Static core:DownloadMode.Audio}}"</code>
/// Convert returns true when the value equals the parameter. ConvertBack returns the
/// parameter when the radio becomes checked and <see cref="BindingOperations.DoNothing"/>
/// when it is unchecked, so the radio that loses the selection never overwrites the new
/// choice. The parameter may be an enum value (preferred, via x:Static) or a member name;
/// one-way checks may list alternatives separated by '|' (for example "H264|Hevc").
/// <see cref="NotEqual"/> inverts the one-way result and never writes back.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static EnumEqualsConverter Instance { get; } = new();
    public static EnumEqualsConverter NotEqual { get; } = new() { Invert = true };

    public bool Invert { get; init; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Matches(value, parameter) != Invert;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (Invert || value is not true) return BindingOperations.DoNothing;
        if (parameter is Enum) return parameter;
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return parameter is string name && enumType.IsEnum && !name.Contains('|') && Enum.TryParse(enumType, name.Trim(), false, out var parsed) && Enum.IsDefined(enumType, parsed!)
            ? parsed
            : BindingOperations.DoNothing;
    }

    private static bool Matches(object? value, object? parameter)
    {
        if (value is not Enum || parameter is null) return false;
        if (parameter is Enum) return value.Equals(parameter);
        if (parameter is not string names) return false;
        var enumType = value.GetType();
        foreach (var name in names.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse(enumType, name, false, out var candidate) && value.Equals(candidate)) return true;
        return false;
    }
}
