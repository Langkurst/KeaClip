using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Klippi.Desktop;
using Klippi.Desktop.Controls;
using Xunit;
using Path = System.IO.Path;

namespace Klippi.UiTests;

/// <summary>
/// The kea artwork has one source of truth (assets/kea.svg). These tests keep the generated
/// icons and the KeaMascot control identical to it, check the rendered pixels where it
/// matters (small-size hinting, centring, the carried card), and (with
/// KEACLIP_UI_EVIDENCE_DIRECTORY set) save before/after evidence sheets.
/// Set KEACLIP_BRAND_BASELINE_DIRECTORY to a folder of the previous PNGs to compare against
/// them; otherwise the original round-chick artwork is redrawn from its vector data.
/// </summary>
public sealed class BrandTests
{
    // Every variant (dark tile, light tile, monochrome) exists at every icon size.
    private static readonly int[] IconSizes = [16, 20, 24, 32, 48, 64, 128, 256];
    private static readonly int[] SheetSizes = [16, 20, 24, 32, 64, 256];
    // Canvas points (64x64 artwork) that sit well inside a single visible shape.
    private static readonly Point BodyPoint = new(39, 30);
    private static readonly Point FlashPoint = new(28.3, 42.2);
    private static readonly Point EyePoint = new(36, 18.6);
    private static readonly Point CardPoint = new(45.2, 39.5); // card fill, left of the arrow
    private static readonly Point SmallCardPoint = new(46, 40.4);
    private static readonly Point SmallArrowPoint = new(51.5, 36);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    // The artwork exactly as the control parsed it from the embedded kea.svg.
    private static KeaArtwork KeaArt => KeaArtwork.Shared;

    [AvaloniaFact]
    public void Mascot_control_draws_exactly_the_svg_artwork()
    {
        var source = Path.Combine(RepositoryRoot(), "assets", "kea.svg");
        var svg = XDocument.Load(source).Root!;
        var tile = Assert.Single(svg.Elements(Svg + "rect"));
        Assert.Equal("tile", (string?)tile.Attribute("id"));
        Assert.Equal(KeaArt.Canvas, Number(tile, "width"));
        Assert.Equal(KeaArt.Canvas, Number(tile, "height"));
        Assert.Equal(KeaArt.TileRadius, Number(tile, "rx"));
        // Only pose layers may group shapes, and they never show in the full-size preview.
        Assert.All(svg.Descendants(Svg + "g"), g =>
        {
            Assert.NotNull(g.Attribute("data-pose"));
            Assert.Equal("none", (string?)g.Attribute("display"));
        });

        var shapes = svg.Descendants().Where(e => e.Name == Svg + "path" || e.Name == Svg + "circle").ToList();
        Assert.Equal(shapes.Select(e => (string?)e.Attribute("id")), KeaArt.Parts.Select(p => p.Id));
        for (var i = 0; i < shapes.Count; i++)
        {
            var element = shapes[i]; var part = KeaArt.Parts[i];
            if (element.Name == Svg + "path") Assert.Equal((string?)element.Attribute("d"), part.PathData);
            else
            {
                Assert.Null(part.PathData);
                Assert.Equal(new Point(Number(element, "cx"), Number(element, "cy")), part.Center);
                Assert.Equal(Number(element, "r"), part.Radius);
            }
            var fill = (string)element.Attribute("fill")!;
            Assert.Equal(Color.Parse(fill), part.Dark);
            Assert.Equal(Color.Parse((string?)element.Attribute("data-light-fill") ?? fill), part.Light);
            Assert.Equal(Number(element, "data-min-size", 0), part.MinPixels);
            Assert.Equal(Number(element, "data-below-size", double.PositiveInfinity), part.BelowPixels);
            Assert.Equal((string?)element.Attribute("data-token"), part.Token);
            var pose = element.Ancestors(Svg + "g").Select(g => (string?)g.Attribute("data-pose")).FirstOrDefault(p => p is not null);
            Assert.Equal(pose is null ? (KeaPose?)null : Enum.Parse<KeaPose>(pose, ignoreCase: true), part.Pose);
            // A hidden top-level shape is a small-size variant, so a plain SVG viewer shows
            // exactly the full-size artwork.
            if ((string?)element.Attribute("display") == "none") Assert.True(part.BelowPixels < 256, $"{part.Id} is hidden but is not a small-size variant");
        }
        // The card pops from the beak tip, and the app ships the regenerated source.
        Assert.Contains(FormattableString.Invariant($"{KeaArt.PoseOrigin.X} {KeaArt.PoseOrigin.Y}"), KeaArt.Parts.Single(p => p.Id == "beak").PathData);
        Assert.Equal(File.ReadAllBytes(source), AssetBytes("kea.svg"));
        // Every token the artwork names exists as a brush in both themes.
        foreach (var token in KeaArt.Parts.Select(p => p.Token).OfType<string>().Distinct())
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            TokenColor(token, variant);
    }

    [AvaloniaFact]
    public void Generated_icons_keep_their_names_sizes_frames_and_colours()
    {
        // Reads the resources embedded in the app, i.e. exactly what ships.
        var expected = IconSizes.SelectMany(size => new[] { ($"kea-{size}.png", size), ($"kea-light-{size}.png", size), ($"kea-mono-{size}.png", size) });
        foreach (var (name, size) in expected)
        {
            using var bitmap = AssetBitmap(name);
            Assert.Equal(new PixelSize(size, size), bitmap.PixelSize);
        }

        var ico = AssetBytes("kea.ico");
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        Assert.Equal(IconSizes.Length, BitConverter.ToUInt16(ico, 4));
        for (var i = 0; i < IconSizes.Length; i++)
        {
            var entry = 6 + 16 * i; var size = IconSizes[i];
            Assert.Equal(size == 256 ? 0 : size, ico[entry]);
            Assert.Equal(32, BitConverter.ToUInt16(ico, entry + 6));
            var frame = ico.AsSpan(BitConverter.ToInt32(ico, entry + 12), BitConverter.ToInt32(ico, entry + 8)).ToArray();
            Assert.Equal(AssetBytes($"kea-{size}.png"), frame);
        }

        foreach (var (name, light) in new[] { ("kea-256.png", false), ("kea-light-256.png", true) })
        {
            using var icon = AssetBitmap(name);
            Assert.Equal(0, PixelAt(icon, 1, 1).A); // rounded tile corner
            AssertColor(Fill("body", light), PixelAt(icon, BodyPoint, 4));
            AssertColor(Fill("flash", light), PixelAt(icon, FlashPoint, 4));
            AssertColor(Fill("eye", light), PixelAt(icon, EyePoint, 4));
        }
        using var mono = AssetBitmap("kea-mono-256.png");
        Assert.Equal(Colors.White, PixelAt(mono, BodyPoint, 4));
        Assert.Equal(0, PixelAt(mono, EyePoint, 4).A); // the eye is a cut-out, not a dark fill
        Assert.Equal(0, PixelAt(mono, 1, 1).A);
    }

    [AvaloniaFact]
    public void Small_icons_keep_a_crisp_eye_and_a_solid_flash()
    {
        // Up to 32 px (the 100% taskbar size) the eye is pixel-snapped: a solid 1x1 (16, 20 px)
        // or 2x2 (24, 32 px) block of the eye colour, never a half-tone smear or a 2x1 dash.
        // (x, y) is the block's top-left pixel, so it is the first darkest pixel around it.
        foreach (var (size, x, y, side) in new[] { (16, 8, 4, 1), (20, 11, 5, 1), (24, 12, 6, 2), (32, 17, 8, 2) })
        {
            foreach (var light in new[] { false, true })
            {
                var name = light ? $"kea-light-{size}.png" : $"kea-{size}.png";
                using var icon = AssetBitmap(name);
                var around = Neighbourhood(icon, x, y);
                Assert.Equal(12, Array.IndexOf(around, around.MinBy(Luminance)));
                for (var dy = 0; dy < side; dy++)
                for (var dx = 0; dx < side; dx++)
                {
                    var pixel = PixelAt(icon, x + dx, y + dy);
                    Assert.True(Distance(Fill("eye", light), pixel) <= 12, $"{name}: eye pixel ({x + dx},{y + dy}) {pixel} is a smear of {Fill("eye", light)}");
                }
                // The block is the whole eye: the pixels just outside it are clearly lighter.
                foreach (var (ox, oy) in new[] { (x - 1, y), (x + side, y), (x, y - 1), (x, y + side) })
                    Assert.True(Luminance(PixelAt(icon, ox, oy)) - Luminance(Fill("eye", light)) > 40, $"{name}: the eye bleeds into ({ox},{oy})");
            }
            using var mono = AssetBitmap($"kea-mono-{size}.png");
            Assert.True(PixelAt(mono, x, y).A < 40, $"kea-mono-{size} eye alpha {PixelAt(mono, x, y).A} is not a cut-out");
        }
        // Below 24 px the wing is dropped and the underwing flash is one solid band (not an
        // orange/olive checkerboard): a run of at least 3 solid flash pixels in a row.
        foreach (var (name, light) in new[] { ("kea-16.png", false), ("kea-light-16.png", true), ("kea-20.png", false), ("kea-light-20.png", true) })
        {
            using var icon = AssetBitmap(name);
            var size = icon.PixelSize.Width;
            var longest = 0;
            for (var y = 0; y < size; y++)
            for (int x = 0, run = 0; x < size; x++)
            {
                run = Distance(Fill("flash", light), PixelAt(icon, x, y)) <= 24 ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
            Assert.True(longest >= 3, $"{name}: longest solid flash run is {longest} px");
        }
    }

    [AvaloniaFact]
    public void Artwork_is_optically_centred_on_the_tile()
    {
        foreach (var (name, scale) in new[] { ("kea-256.png", 4.0), ("kea-16.png", 0.25) })
        {
            using var icon = AssetBitmap(name);
            var size = icon.PixelSize.Width;
            var tile = PixelAt(icon, size / 2, size - 2);
            int left = size, right = -1;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (Distance(tile, PixelAt(icon, x, y)) > 40 && PixelAt(icon, x, y).A == 255) { left = Math.Min(left, x); right = Math.Max(right, x); }
            var centre = (left + right + 1) / 2.0 / scale;
            // The bird's mass leans left of the thin hooked beak, so its box sits a hair right.
            Assert.InRange(centre, 31, 34);
            Assert.True(left / scale >= 6 && (size - 1 - right) / scale >= 6, $"{name}: margins {left / scale:0.#} / {(size - 1 - right) / scale:0.#} canvas units");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mascot_control_matches_the_generated_icons(bool light)
    {
        // Renders the control on its tile and compares it with the generator's PNG: this
        // catches geometry or size-rule drift between the SVG parser in GenerateIcons.ps1 and
        // Avalonia's path parser that a text comparison cannot. Edge anti-aliasing differs
        // (GDI+ vs Skia), so only clearly different opaque pixels count.
        foreach (var size in IconSizes)
        {
            using var expected = AssetBitmap(light ? $"kea-light-{size}.png" : $"kea-{size}.png");
            using var actual = Rasterize(new KeaMascot { Background = new SolidColorBrush(KeaTile(light)) }, size, light);
            var a = Pixels(expected); var b = Pixels(actual);
            int compared = 0, different = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i].A != 255 || b[i].A != 255) continue;
                compared++;
                if (Distance(a[i], b[i]) > 48) different++;
            }
            var share = (double)different / compared;
            Assert.True(share <= (size <= 24 ? 0.06 : 0.02), $"{size} px ({(light ? "light" : "dark")}): {different} of {compared} pixels differ ({share:P1})");
        }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Mascot_renders_idle_and_carrying_in_both_themes(string theme)
    {
        var light = theme == "Light";
        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var mascot = new KeaMascot { Size = 256, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var small = new KeaMascot { Size = 42, Pose = KeaPose.Carrying, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(260, 0, 0, 0) };
        var window = new Window { Width = 320, Height = 320, Content = new Panel { Children = { mascot, small } }, RequestedThemeVariant = variant };
        window.Show();
        try
        {
            using (var idle = Capture(window))
            {
                AssertColor(Fill("body", light), PixelAt(idle, BodyPoint, 4));
                AssertColor(Fill("flash", light), PixelAt(idle, FlashPoint, 4));
                AssertColor(Fill("eye", light), PixelAt(idle, EyePoint, 4));
                Assert.False(IsClose(TokenColor("AppAccent", variant), PixelAt(idle, CardPoint, 4)));
                // At sidebar size (42 px) the card keeps its full size and a pixel-aligned arrow.
                AssertColor(TokenColor("AppAccent", variant), PixelAt(idle, Offset(SmallCardPoint, 42 / 64.0, 260)));
                AssertColor(TokenColor("AccentText", variant), PixelAt(idle, Offset(SmallArrowPoint, 42 / 64.0, 260)));
            }
            mascot.Pose = KeaPose.Carrying; // the window has no motion class: the card appears at once
            using (var carrying = Capture(window))
            {
                AssertColor(TokenColor("AppAccent", variant), PixelAt(carrying, CardPoint, 4));
                AssertColor(Fill("body", light), PixelAt(carrying, BodyPoint, 4));
            }
            mascot.Background = Brushes.Magenta;
            using (var tiled = Capture(window)) AssertColor(Colors.Magenta, PixelAt(tiled, 128, 2));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Carried_card_follows_the_theme_accent_tokens()
    {
        var mascot = new KeaMascot { Size = 128, Pose = KeaPose.Carrying, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var host = new Border { Child = mascot };
        var window = new Window { Width = 200, Height = 200, Content = host, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            using (var app = Capture(window)) AssertColor(TokenColor("AppAccent", ThemeVariant.Dark), PixelAt(app, CardPoint, 2));
            // A changed accent reaches the card without any change to the control.
            host.Resources["AppAccent"] = new SolidColorBrush(Colors.Magenta);
            using (var changed = Capture(window)) AssertColor(Colors.Magenta, PixelAt(changed, CardPoint, 2));
            window.RequestedThemeVariant = ThemeVariant.Light;
            host.Resources.Remove("AppAccent");
            using (var themed = Capture(window)) AssertColor(TokenColor("AppAccent", ThemeVariant.Light), PixelAt(themed, CardPoint, 2));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Carried_card_never_shrinks_as_the_mascot_grows(bool light)
    {
        // The card switches artwork at 32 and 36 px. It must never get smaller in physical
        // pixels as the mascot grows (the 42 DIP sidebar mark is 53, 63 and 84 px at
        // 125/150/200%), and it stays about 12 of the 64 canvas units wide, never a speck.
        // Width and height are the card's coverage (alpha) summed along a row and a column
        // that cross only the card, so anti-aliased edges count by how much they are covered.
        double previousWidth = 0, previousHeight = 0; var previousSize = 0;
        foreach (var size in new[] { 20, 24, 27, 28, 32, 35, 36, 40, 42, 47, 48, 52, 53, 63, 64, 84, 96 })
        {
            using var carrying = Rasterize(new KeaMascot { Pose = KeaPose.Carrying }, size, light);
            var pixels = Pixels(carrying);
            int Px(double canvas) => (int)(canvas / 64 * size);
            double width = 0, height = 0;
            for (var x = Px(43.5); x < size; x++) width += pixels[Px(36.5) * size + x].A / 255.0;
            for (var y = Px(27); y < size; y++) height += pixels[y * size + Px(46)].A / 255.0;
            Assert.True(width >= previousWidth - 0.05 && height >= previousHeight - 0.05, $"the card shrinks from {previousWidth:0.0}x{previousHeight:0.0} px at {previousSize} px to {width:0.0}x{height:0.0} px at {size} px");
            Assert.True(width >= 0.18 * size && width <= 0.2 * size, $"{size} px: the card is {width:0.00} px wide, not 12-12.2 of 64 canvas units"); // never a speck
            (previousWidth, previousHeight, previousSize) = (width, height, size);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Carried_card_arrow_points_down_and_never_reads_as_a_plus(bool light)
    {
        // Row by row, the glyph (pixels nearer the AccentText ink than the AppAccent card) must
        // be an arrowhead: below its widest row it narrows strictly to a point, any shaft above
        // is narrower than the head, and it is not symmetric top to bottom. A '+' fails.
        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var card = TokenColor("AppAccent", variant); var ink = TokenColor("AccentText", variant);
        foreach (var size in new[] { 32, 42, 53, 63, 64, 84, 96, 128 })
        {
            var (box, pixels) = CardPixels(size, light);
            var widths = new List<int>();
            for (var y = box.Y; y < box.Bottom; y++)
            {
                var width = 0;
                for (var x = box.X; x < box.Right; x++)
                {
                    var pixel = pixels[y * size + x];
                    if (pixel.A == 255 && Distance(ink, pixel) < Distance(card, pixel)) width++;
                }
                if (width > 0) widths.Add(width);
            }
            var rows = string.Join(",", widths);
            Assert.True(widths.Count >= 2, $"{size} px: no arrow glyph ({rows})");
            var widest = widths.IndexOf(widths.Max());
            Assert.True(widths.Count - widest >= 2, $"{size} px: the arrowhead does not taper ({rows})");
            for (var i = widest + 1; i < widths.Count; i++) Assert.True(widths[i] < widths[i - 1], $"{size} px: the glyph rows {rows} do not narrow to a point");
            Assert.All(widths.Take(widest), shaft => Assert.True(shaft < widths[widest], $"{size} px: the shaft is as wide as the head ({rows})"));
            // A '+' (or a diamond) is the same upside down; an arrow never is.
            Assert.False(widths.SequenceEqual(Enumerable.Reverse(widths)), $"{size} px: the glyph rows {rows} are symmetric, so it reads as a '+'");
        }
    }

    [AvaloniaFact]
    public void Mascot_redraws_its_size_details_when_the_scaling_changes()
    {
        // A 30 DIP mascot is 30 px at 100% (a plain card, too small for a glyph) and 60 px at
        // 200% (the card with its arrow). Moving to a monitor with another scaling must redraw
        // with the details for the new pixel size, and back again.
        var mascot = new KeaMascot { Size = 30, Pose = KeaPose.Carrying, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 100, Height = 100, Content = mascot, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            var card = TokenColor("AppAccent", ThemeVariant.Dark); var ink = TokenColor("AccentText", ThemeVariant.Dark);
            var shaft = new Point(50.29, 34); // the arrow shaft of the full-size card
            using (var at100 = Capture(window)) AssertColor(card, PixelAt(at100, shaft, 30 / 64.0));
            window.SetRenderScaling(2);
            using (var at200 = Capture(window)) AssertColor(ink, PixelAt(at200, shaft, 60 / 64.0));
            window.SetRenderScaling(1);
            using (var back = Capture(window)) AssertColor(card, PixelAt(back, shaft, 30 / 64.0));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Carried_card_pops_in_only_when_motion_is_allowed()
    {
        // No style is involved: the control reads the same "motion" window class that
        // MainWindow sets while reduced motion is off (Real_shell_sets_the_motion_class_...).
        var mascot = new KeaMascot { Size = 128, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 200, Height = 200, Content = mascot, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            Assert.Equal("motion", KeaMascot.MotionClass);
            window.Classes.Add(KeaMascot.MotionClass);
            Capture(window).Dispose();
            mascot.Pose = KeaPose.Carrying;
            var accent = TokenColor("AppAccent", ThemeVariant.Dark);
            using (var first = Capture(window)) Assert.False(IsClose(accent, PixelAt(first, CardPoint, 2)), "the card should start hidden and grow in");
            var settled = false;
            for (var attempt = 0; attempt < 60 && !settled; attempt++)
            {
                await Task.Delay(16);
                using var frame = Capture(window);
                settled = IsClose(accent, PixelAt(frame, CardPoint, 2));
            }
            Assert.True(settled, "the card transition should finish showing the card");

            window.Classes.Remove(KeaMascot.MotionClass);
            mascot.Pose = KeaPose.Idle;
            using (var hidden = Capture(window)) Assert.False(IsClose(accent, PixelAt(hidden, CardPoint, 2)), "reduced motion hides the card instantly");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_shell_sets_the_motion_class_the_mascot_reads(bool reducedMotion)
    {
        var fake = new UiFakes(); fake.Platform.SystemReducedMotion = reducedMotion;
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 900, Height = 600 };
        window.Show();
        try { Assert.Equal(!reducedMotion, window.Classes.Contains(KeaMascot.MotionClass)); }
        finally { model.CloseBehavior = Klippi.Core.CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Real_shell_sidebar_mascot_carries_while_downloads_run()
    {
        // The kea carries its download card only while downloads are queued or running;
        // other busy lanes (settings, components, export) leave it idle.
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 600 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var mascot = window.GetVisualDescendants().OfType<KeaMascot>().FirstOrDefault(m => m.Classes.Contains("mascot"));
            Assert.NotNull(mascot);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Image>(), image => image.Classes.Contains("mascot"));
            Assert.Equal(KeaPose.Idle, mascot!.Pose);
            window.Classes.Add("busy");
            Assert.Equal(KeaPose.Idle, mascot.Pose);
            window.Classes.Remove("busy");
            await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1 && model.HasActiveQueue);
            Assert.Equal(KeaPose.Carrying, mascot.Pose);
            // The job's state flips on the worker thread; the window follows on the next UI pass.
            model.Queue[0].CancelCommand.Execute(null); await WaitUntil(() => mascot.Pose == KeaPose.Idle);
            Assert.False(model.HasActiveQueue);
        }
        finally { model.CloseBehavior = Klippi.Core.CloseBehavior.Exit; window.Close(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [AvaloniaFact]
    public void Mascot_is_decorative_unless_named_and_reports_work_when_carrying()
    {
        var mascot = new KeaMascot();
        var window = new Window { Content = mascot };
        window.Show();
        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(mascot);
            Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
            Assert.False(peer.IsControlElement());
            Assert.False(peer.IsContentElement());
            Assert.Empty(peer.GetChildren());

            AutomationProperties.SetName(mascot, "KeaClip");
            Assert.True(peer.IsControlElement());
            Assert.True(peer.IsContentElement());
            Assert.Equal("KeaClip", peer.GetName());
            Assert.True(string.IsNullOrEmpty(peer.GetItemStatus()));
            mascot.Pose = KeaPose.Carrying;
            Assert.Equal(KeaMascot.WorkingStatus, peer.GetItemStatus());
            mascot.Pose = KeaPose.Idle;
            Assert.True(string.IsNullOrEmpty(peer.GetItemStatus()));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Browser_extension_icons_match_the_app_icons()
    {
        // GenerateIcons.ps1 refreshes every icons/kea-<size>.png a manifest references.
        var extensions = Path.Combine(RepositoryRoot(), "extensions");
        foreach (var manifest in Directory.EnumerateFiles(extensions, "manifest.json", SearchOption.AllDirectories))
        foreach (Match match in Regex.Matches(File.ReadAllText(manifest), "\"(icons/kea-(\\d+)\\.png)\""))
        {
            var icon = Path.Combine(Path.GetDirectoryName(manifest)!, match.Groups[1].Value);
            Assert.True(File.Exists(icon), $"{manifest} references {match.Groups[1].Value}; run pwsh -File assets/GenerateIcons.ps1");
            Assert.Equal(AssetBytes($"kea-{match.Groups[2].Value}.png"), File.ReadAllBytes(icon));
        }
    }

    [AvaloniaFact]
    public void Brand_evidence_sheets_render_in_both_themes()
    {
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        var baseline = Environment.GetEnvironmentVariable("KEACLIP_BRAND_BASELINE_DIRECTORY");
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var light = theme == "Light";
            foreach (var (name, content, width, height) in new[]
            {
                ("brand-compare", ComparisonSheet(baseline, light), 1120, 1000),
                ("brand-mascot", MascotSheet(light), 1120, 980),
            })
            {
                var window = new Window { Width = width, Height = height, Content = content, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
                window.Show();
                try
                {
                    using var frame = Capture(window);
                    Assert.Equal(width, frame.PixelSize.Width);
                    if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, $"{name}-{theme.ToLowerInvariant()}.png"), PngBitmapEncoderOptions.Default); }
                }
                finally { window.Close(); }
            }
        }
    }

    [AvaloniaFact]
    public async Task Shell_integration_preview_renders_in_both_themes()
    {
        // The real shell with the requested sidebar swap (BRAND.md, "Shell integration")
        // applied at run time, idle and busy, so the integration can be judged in place
        // before the shell changes. Nothing here changes the shell's files.
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 };
        window.Show();
        try
        {
            await model.InitializeAsync();
            window.UpdateLayout();
            // Once the shell has made the swap itself, its own mascot is captured instead.
            var mascot = window.GetVisualDescendants().OfType<KeaMascot>().FirstOrDefault(m => m.Classes.Contains("mascot"));
            if (mascot is null)
            {
                var image = Assert.Single(window.GetVisualDescendants().OfType<Image>(), i => i.Classes.Contains("mascot"));
                var panel = Assert.IsAssignableFrom<Panel>(image.Parent);
                mascot = new KeaMascot { Classes = { "mascot" }, Size = 42 };
                panel.Children[panel.Children.IndexOf(image)] = mascot;
            }
            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var pose in new[] { KeaPose.Idle, KeaPose.Carrying })
            {
                model.Theme = theme;
                mascot.Pose = pose;
                using var frame = Capture(window);
                Assert.Equal(new Size(42, 42), mascot.Bounds.Size);
                var name = $"brand-shell-{(pose == KeaPose.Idle ? "idle" : "busy")}-{theme.ToLowerInvariant()}.png";
                if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default); }
            }
        }
        finally { model.CloseBehavior = Klippi.Core.CloseBehavior.Exit; window.Close(); }
    }

    // Before/after at real pixel sizes: small sizes are magnified with nearest-neighbour
    // sampling beside a 1:1 copy so pixel-level readability can be judged.
    private static Control ComparisonSheet(string? baseline, bool light)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,*,*,*"), ColumnSpacing = 16, RowSpacing = 14, Margin = new Thickness(28, 24) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headings = [baseline is null ? "Before (original)" : "Before (previous)", "After · app icon", "After · monochrome", "After · KeaMascot control"];
        for (var c = 0; c < headings.Length; c++) grid.Children.Add(At(new TextBlock { Text = headings[c], Classes = { "section" } }, 0, c + 1));
        for (var r = 0; r < SheetSizes.Length; r++)
        {
            var size = SheetSizes[r];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(At(new TextBlock { Text = $"{size} px", Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center }, r + 1, 0));
            var file = light ? $"kea-light-{size}.png" : $"kea-{size}.png";
            var before = LoadBaseline(baseline, file) ?? Rasterize(LegacyKea(light), size, light);
            grid.Children.Add(At(Cell(before, size, null), r + 1, 1));
            grid.Children.Add(At(Cell(AssetBitmap(file), size, null), r + 1, 2));
            grid.Children.Add(At(Cell(AssetBitmap($"kea-mono-{size}.png"), size, Color.Parse("#1F2421")), r + 1, 3));
            var control = new KeaMascot { Background = new SolidColorBrush(KeaTile(light)) };
            grid.Children.Add(At(Cell(Rasterize(control, size, light), size, null), r + 1, 4));
        }
        return new ScrollViewer { Content = grid };
    }

    // The control in context: shell header options, both poses, the carried card at the
    // sizes it is used, and an empty state.
    private static Control MascotSheet(bool light)
    {
        var theme = light ? ThemeVariant.Light : ThemeVariant.Dark;
        IBrush Token(string key) => Application.Current!.TryGetResource(key, theme, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
        Control Header(string caption, Control mascot) => new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = caption, Classes = { "caption", "faint" } },
                new Border
                {
                    Background = Token("AppSidebar"), BorderBrush = Token("AppBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Width = 232, Padding = new Thickness(20, 20, 16, 20),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 12,
                        Children =
                        {
                            mascot,
                            new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { new TextBlock { Text = "KeaClip", FontSize = 18, FontWeight = FontWeight.SemiBold, LetterSpacing = -0.2 }, new TextBlock { Text = "Save. Shape. Share.", Classes = { "caption", "faint" } } } },
                        },
                    },
                },
            },
        };
        Control Labelled(string caption, Control content) => new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Children = { content, new TextBlock { Text = caption, Classes = { "caption", "muted" }, HorizontalAlignment = HorizontalAlignment.Center } } };

        var headers = new WrapPanel { ItemSpacing = 20, LineSpacing = 20 };
        headers.Children.Add(Header("Shell today: Image kea-128.png", new Image { Source = AssetBitmap("kea-128.png"), Width = 42, Height = 42 }));
        headers.Children.Add(Header("Proposed: KeaMascot, no tile", new KeaMascot { Size = 42 }));
        headers.Children.Add(Header("Proposed, while busy (Carrying)", new KeaMascot { Size = 42, Pose = KeaPose.Carrying }));
        headers.Children.Add(Header("Rejected: AppAccentSubtle tile", new KeaMascot { Size = 42, Background = Token("AppAccentSubtle") }));

        var poses = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 30 };
        poses.Children.Add(Labelled("Idle · 160", new KeaMascot { Size = 160 }));
        poses.Children.Add(Labelled("Carrying · 160", new KeaMascot { Size = 160, Pose = KeaPose.Carrying }));
        foreach (var size in new[] { 64, 48, 42, 32, 24, 20 })
            poses.Children.Add(Labelled($"Carrying · {size}", new KeaMascot { Size = size, Pose = KeaPose.Carrying }));

        // Pixel view of the carried card on the sidebar colour: the small tiers (below 28 px a
        // plain card, 28-35 px the stepped arrowhead) and the 42 DIP sidebar mark at
        // 100/125/150/200% scaling.
        Control Magnified(int size, int zoom)
        {
            var bitmap = Rasterize(new KeaMascot { Pose = KeaPose.Carrying, Background = Token("AppSidebar") }, size, light);
            var image = new Image { Source = bitmap, Width = size * zoom, Height = size * zoom };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
            return Labelled($"{size} px ×{zoom}", image);
        }
        var small = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        foreach (var size in new[] { 20, 24, 28, 32 }) small.Children.Add(Magnified(size, 4));
        var sidebar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        foreach (var size in new[] { 42, 53, 63, 84 }) sidebar.Children.Add(Magnified(size, 2));
        var magnified = new StackPanel { Spacing = 20, Children = { small, sidebar } };

        var empty = new Border
        {
            Classes = { "card" }, Width = 460, VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new KeaMascot { Size = 96, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Nothing downloading", Classes = { "section" }, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Paste a link on the Download page and it will appear here.", Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 40, Children = { magnified, empty } };
        return new StackPanel { Margin = new Thickness(28, 24), Spacing = 28, Children = { headers, poses, bottom } };
    }

    private static Control Cell(Bitmap bitmap, int size, Color? backdrop)
    {
        var display = size <= 32 ? size * 4 : size <= 64 ? size * 2 : 128;
        var magnified = new Image { Source = bitmap, Width = display, Height = display, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(magnified, size <= 64 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { magnified } };
        if (size <= 64) panel.Children.Add(new Image { Source = bitmap, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center });
        return backdrop is { } color ? new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, Child = panel } : panel;
    }

    private static Control At(Control control, int row, int column) { Grid.SetRow(control, row); Grid.SetColumn(control, column); return control; }

    private static Bitmap Rasterize(Control control, int pixels, bool light)
    {
        var scope = new ThemeVariantScope { RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark, Child = control, Width = pixels, Height = pixels };
        scope.Measure(new Size(pixels, pixels)); scope.Arrange(new Rect(0, 0, pixels, pixels));
        var target = new RenderTargetBitmap(new PixelSize(pixels, pixels), new Vector(96, 96));
        target.Render(scope);
        return target;
    }

    // The carried card in physical pixels: the box where Carrying differs from Idle, and the
    // Carrying pixels (rendered without a tile, so the pose change is the only difference).
    private static (PixelRect Box, Color[] Pixels) CardPixels(int size, bool light)
    {
        using var idle = Rasterize(new KeaMascot(), size, light);
        using var carrying = Rasterize(new KeaMascot { Pose = KeaPose.Carrying }, size, light);
        var a = Pixels(idle); var b = Pixels(carrying);
        int left = size, top = size, right = -1, bottom = -1;
        for (var i = 0; i < a.Length; i++)
        {
            if (Distance(a[i], b[i]) <= 48 && Math.Abs(a[i].A - b[i].A) <= 48) continue;
            var (x, y) = (i % size, i / size);
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        Assert.True(right >= 0, $"{size} px: the carried card is not drawn");
        return (new PixelRect(left, top, right - left + 1, bottom - top + 1), b);
    }

    private static Bitmap? LoadBaseline(string? directory, string name) =>
        !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, name)) ? new Bitmap(Path.Combine(directory, name)) : null;

    // The original artwork, redrawn from the vector data of the first generator.
    private static Control LegacyKea(bool light) => new Viewbox
    {
        Child = new Canvas
        {
            Width = 64, Height = 64,
            Children =
            {
                new Ellipse { Width = 64, Height = 64, Fill = Brush(light ? "#F6F8F3" : "#17231F") },
                new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M8 37C3 30 5 20 15 14C25 4 42 8 47 19C51 26 49 39 39 43L25 47L15 43Z"), Fill = Brush("#91AE71") },
                new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M22 25C31 18 41 24 39 34C35 41 23 41 18 34Z"), Fill = Brush("#D59356") },
                new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M45 17L57 22L46 28L49 21Z"), Fill = Brush("#D4BF9C") },
                new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M41 17m-2 0a2 2 0 1 0 4 0a2 2 0 1 0 -4 0"), Fill = Brush("#122419") },
                new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M17 41L13 53M31 43L31 53"), Stroke = Brush("#91AE71"), StrokeThickness = 3 },
            },
        },
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private static Color KeaTile(bool light)
    {
        using var stream = AssetLoader.Open(new Uri("avares://KeaClip/Assets/kea.svg"));
        var tile = XDocument.Load(stream).Root!.Element(Svg + "rect")!;
        return Color.Parse((string)(light ? tile.Attribute("data-light-fill")! : tile.Attribute("fill")!));
    }

    private static Color TokenColor(string key, ThemeVariant variant) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.TryGetResource(key, variant, out var value) ? value : null).Color;

    private static Color Fill(string id, bool light)
    {
        var part = KeaArt.Parts.Single(p => p.Id == id);
        return light ? part.Light : part.Dark;
    }

    private static Bitmap AssetBitmap(string name)
    {
        using var stream = AssetLoader.Open(new Uri("avares://KeaClip/Assets/" + name));
        return new Bitmap(stream);
    }

    private static byte[] AssetBytes(string name)
    {
        using var stream = AssetLoader.Open(new Uri("avares://KeaClip/Assets/" + name));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static WriteableBitmap Capture(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame(), exactMatch: false);
    }

    private static PixelPoint Offset(Point canvasPoint, double scale, double left) => new((int)(left + canvasPoint.X * scale), (int)(canvasPoint.Y * scale));

    private static Color PixelAt(Bitmap bitmap, Point canvasPoint, double scale) => PixelAt(bitmap, (int)(canvasPoint.X * scale), (int)(canvasPoint.Y * scale));

    private static Color PixelAt(Bitmap bitmap, PixelPoint point) => PixelAt(bitmap, point.X, point.Y);

    private static Color PixelAt(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), buffer.Length, buffer.Length); }
        finally { handle.Free(); }
        return ToColor(bitmap.Format, buffer, 0);
    }

    private static Color[] Pixels(Bitmap bitmap)
    {
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var buffer = new byte[width * height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, width * 4); }
        finally { handle.Free(); }
        return Enumerable.Range(0, width * height).Select(i => ToColor(bitmap.Format, buffer, i * 4)).ToArray();
    }

    private static Color ToColor(PixelFormat? format, byte[] buffer, int offset) => format == PixelFormats.Rgba8888
        ? Color.FromArgb(buffer[offset + 3], buffer[offset], buffer[offset + 1], buffer[offset + 2])
        : Color.FromArgb(buffer[offset + 3], buffer[offset + 2], buffer[offset + 1], buffer[offset]);

    // The 5x5 pixels around (x, y), row by row (clamped to the bitmap).
    private static Color[] Neighbourhood(Bitmap bitmap, int x, int y) =>
        Enumerable.Range(0, 25).Select(i => PixelAt(bitmap, Math.Clamp(x - 2 + i % 5, 0, bitmap.PixelSize.Width - 1), Math.Clamp(y - 2 + i / 5, 0, bitmap.PixelSize.Height - 1))).ToArray();

    private static double Luminance(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static int Distance(Color expected, Color actual) =>
        Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));

    private static bool IsClose(Color expected, Color actual, int tolerance = 4) => actual.A == 255 && Distance(expected, actual) <= tolerance;

    private static void AssertColor(Color expected, Color actual) => Assert.True(IsClose(expected, actual), $"Expected {expected} but found {actual}.");

    private static double Number(XElement element, string name, double fallback = double.NaN) =>
        element.Attribute(name) is { } attribute ? double.Parse(attribute.Value, CultureInfo.InvariantCulture) : fallback;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Klippi.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Klippi.sln was not found above the test output.");
    }
}
