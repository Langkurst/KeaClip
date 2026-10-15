using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Klippi.UiTests;

// Hover colours fade in and out. A fade from plain Transparent (transparent white) to a dark colour passes through a
// pale grey, which made the sidebar strobe when the pointer moved over it. Fades start from AppClear instead: the
// hover colour itself at zero opacity.
public sealed class HoverFadeTests
{
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Clear_is_the_hover_colour_at_zero_opacity(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var app = Application.Current!;
        Assert.True(app.TryGetResource("AppClear", variant, out var clear));
        Assert.True(app.TryGetResource("AppSurfaceHover", variant, out var hover));
        var c = ((ISolidColorBrush)clear!).Color; var h = ((ISolidColorBrush)hover!).Color;
        Assert.Equal(0, c.A);
        Assert.Equal((h.R, h.G, h.B), (c.R, c.G, c.B));
    }

    [Fact]
    public void Styles_never_fade_from_plain_transparent()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Klippi.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var desktop = Path.Combine(root!.FullName, "src", "Klippi.Desktop");
        var offenders = Directory.EnumerateFiles(desktop, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(item => Regex.IsMatch(item.line, "Setter Property=\"(Background|BorderBrush)\" Value=\"Transparent\"|>Transparent</SolidColorBrush>"))
            .Select(item => $"{Path.GetFileName(item.path)}:{item.index + 1}")
            .ToArray();
        Assert.Empty(offenders);
    }
}
