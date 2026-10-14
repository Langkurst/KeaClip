using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Klippi.Desktop.Controls;

/// <summary>What the mascot is doing.</summary>
public enum KeaPose
{
    /// <summary>Perched, the app-icon artwork. Use it for empty and idle states.</summary>
    Idle,
    /// <summary>Holding a download card in its beak. Reserve it for work that is running.</summary>
    Carrying,
}

/// <summary>
/// The KeaClip kea as resolution-independent vector art. The shapes, palettes and
/// small-size hinting are read once from the embedded <c>Assets/kea.svg</c>, the same file
/// <c>assets/GenerateIcons.ps1</c> turns into the app icons, so the control and the icons
/// cannot drift apart (BrandTests also compares their pixels). Geometry and brushes are
/// shared by every instance, so rendering never allocates. Uses the dark or light palette
/// from the inherited theme variant; <see cref="Background"/> optionally draws the rounded
/// app-icon tile behind it. The carried card uses the app's <c>AppAccent</c> /
/// <c>AccentText</c> brushes, so it follows the theme and any future accent change.
/// <para>
/// The carried card sits on its own layer between the wing and the beak. Switching
/// <see cref="Pose"/> pops it in or out from the beak tip by animating only that layer's
/// Opacity and RenderTransform, and only while the window has the shell's
/// <c>motion</c> class (<see cref="MotionClass"/>, set by MainWindow while reduced motion is
/// off, see docs/DESIGN_SYSTEM.md "Motion"); otherwise the change is instant. The check is
/// made once per pose change, so nothing runs or listens while the pose is unchanged.
/// </para>
/// <para>
/// Small-size details (eye shape, highlight, feet, card glyph) are chosen by the size in
/// physical pixels, so the control re-renders when the window moves to a monitor with a
/// different scaling.
/// </para>
/// <para>
/// Accessibility: decorative by default (hidden from the control and content views, like an
/// unlabelled image). Give it <c>AutomationProperties.Name</c> to expose it as an image; it
/// then reports the item status "Working" while carrying.
/// </para>
/// </summary>
public sealed class KeaMascot : Control
{
    public static readonly StyledProperty<KeaPose> PoseProperty = AvaloniaProperty.Register<KeaMascot, KeaPose>(nameof(Pose));
    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<KeaMascot, IBrush?>(nameof(Background));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<KeaMascot, double>(nameof(Size), 48);

    /// <summary>The automation item status reported while <see cref="KeaPose.Carrying"/>.</summary>
    public const string WorkingStatus = "Working";

    /// <summary>The window class that allows motion (the design system's motion contract).</summary>
    public const string MotionClass = "motion";

    private static readonly TransformOperations CardShown = TransformOperations.Identity;
    private static readonly TransformOperations CardHidden = TransformOperations.Parse("scale(0.4) rotate(-18deg)");
    private readonly KeaArtwork _art = KeaArtwork.Shared;
    private readonly Layer _card;
    private readonly Layer _front;
    // Theme brushes for parts with a data-token (the carried card), refreshed only when the
    // theme or resources change; null falls back to the SVG palette.
    private readonly IBrush?[] _tokenBrushes;
    private TopLevel? _topLevel;
    private double _renderScaling = 1;

    static KeaMascot()
    {
        AffectsRender<KeaMascot>(BackgroundProperty);
        AffectsMeasure<KeaMascot>(SizeProperty);
    }

    public KeaMascot()
    {
        _tokenBrushes = new IBrush?[_art.Parts.Count];
        _card = new Layer(this, _art.PoseStart, _art.PoseEnd);
        _front = new Layer(this, _art.PoseEnd, _art.Parts.Count);
        ApplyPose(animate: false);
        VisualChildren.Add(_card);
        VisualChildren.Add(_front);
        ActualThemeVariantChanged += (_, _) => RefreshTheme();
        ResourcesChanged += (_, _) => RefreshTheme();
    }

    /// <summary>Idle (the app icon) or Carrying (holding a download card while work runs).</summary>
    public KeaPose Pose { get => GetValue(PoseProperty); set => SetValue(PoseProperty, value); }

    /// <summary>Optional fill for the rounded app-icon tile behind the bird (use a theme token).</summary>
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Desired square size in DIPs when Width/Height are not set.</summary>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PoseProperty) ApplyPose(animate: true);
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        RefreshTheme();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        OnScalingChanged(null, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new KeaMascotAutomationPeer(this);

    // The size tier depends on physical pixels, which a DPI change alters without a new
    // layout pass, so the recorded drawing must be refreshed.
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        var scaling = _topLevel?.RenderScaling ?? 1;
        if (scaling == _renderScaling) return;
        _renderScaling = scaling;
        InvalidateArt();
    }

    private void RefreshTheme()
    {
        var parts = _art.Parts;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Token is not { } token) continue;
            _tokenBrushes[i] = this.TryFindResource(token, ActualThemeVariant, out var value) ? value switch
            {
                IBrush brush => brush,
                Color color => new ImmutableSolidColorBrush(color),
                _ => null,
            } : null;
        }
        InvalidateArt();
    }

    private void InvalidateArt()
    {
        InvalidateVisual();
        _card.InvalidateVisual();
        _front.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _card.Measure(availableSize);
        _front.Measure(availableSize);
        return new Size(Size, Size);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = new Rect(finalSize);
        _card.Arrange(bounds);
        _front.Arrange(bounds);
        // The card grows from the beak tip of the centred square artwork.
        var side = Math.Min(finalSize.Width, finalSize.Height);
        var scale = side / _art.Canvas;
        _card.RenderTransformOrigin = new RelativePoint((finalSize.Width - side) / 2 + _art.PoseOrigin.X * scale, (finalSize.Height - side) / 2 + _art.PoseOrigin.Y * scale, RelativeUnit.Absolute);
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (!TryGetArtTransform(out var transform, out var pixels)) return;
        using (context.PushTransform(transform))
        {
            if (Background is { } background) context.DrawRectangle(background, null, _art.Tile, _art.TileRadius, _art.TileRadius);
            DrawParts(context, 0, _art.PoseStart, pixels);
        }
    }

    private void ApplyPose(bool animate)
    {
        var carrying = Pose == KeaPose.Carrying;
        // Transitions are attached only while motion is allowed, before the values change.
        var motion = animate && TopLevel.GetTopLevel(this) is { } topLevel && topLevel.Classes.Contains(MotionClass);
        _card.Transitions = motion ? _card.Transitions ?? CreateCardTransitions() : null;
        _card.Opacity = carrying ? 1 : 0;
        _card.RenderTransform = carrying ? CardShown : CardHidden;
        // Reported only when the page names the mascot (see the class remarks).
        SetCurrentValue(AutomationProperties.ItemStatusProperty, carrying ? WorkingStatus : null);
    }

    private static Transitions CreateCardTransitions() =>
    [
        new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() },
        new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(320), Easing = new BackEaseOut() },
    ];

    private bool TryGetArtTransform(out Matrix transform, out double pixels)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        pixels = side * _renderScaling;
        if (side <= 0) { transform = default; return false; }
        var scale = side / _art.Canvas;
        transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        return true;
    }

    private void DrawParts(DrawingContext context, int start, int end, double pixels)
    {
        var light = IsLight(ActualThemeVariant);
        var parts = _art.Parts;
        var geometries = _art.Geometries;
        for (var i = start; i < end; i++)
        {
            var part = parts[i];
            if (!part.IsVisibleAt(pixels)) continue;
            context.DrawGeometry(_tokenBrushes[i] ?? (light ? part.LightBrush : part.DarkBrush), null, geometries[i]);
        }
    }

    private static bool IsLight(ThemeVariant? variant)
    {
        for (; variant is not null; variant = variant.InheritVariant as ThemeVariant)
            if (variant == ThemeVariant.Light) return true;
        return false;
    }

    /// <summary>Draws a slice of the artwork above the mascot's own back layer.</summary>
    private sealed class Layer : Control
    {
        private readonly KeaMascot _owner;
        private readonly int _start;
        private readonly int _end;

        public Layer(KeaMascot owner, int start, int end)
        {
            _owner = owner; _start = start; _end = end;
            IsHitTestVisible = false;
            AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        }

        public override void Render(DrawingContext context)
        {
            if (!_owner.TryGetArtTransform(out var transform, out var pixels)) return;
            using (context.PushTransform(transform)) _owner.DrawParts(context, _start, _end, pixels);
        }
    }

    private sealed class KeaMascotAutomationPeer(KeaMascot owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(KeaMascot);
        protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;
        // Decorative unless the page names it (the sidebar mark already sits beside the app name).
        protected override bool IsControlElementCore() => !string.IsNullOrEmpty(GetName());
        protected override bool IsContentElementCore() => !string.IsNullOrEmpty(GetName());
    }
}

/// <summary>One shape of the kea artwork: an element of <c>kea.svg</c>.</summary>
internal sealed record KeaArtPart(string Id, string? PathData, Point Center, double Radius, Color Dark, Color Light, double MinPixels, double BelowPixels, KeaPose? Pose, string? Token)
{
    public IBrush DarkBrush { get; } = new ImmutableSolidColorBrush(Dark);
    public IBrush LightBrush { get; } = new ImmutableSolidColorBrush(Light);

    /// <summary>The same size rule as GenerateIcons.ps1: min-size &lt;= pixels &lt; below-size.</summary>
    public bool IsVisibleAt(double pixels) => pixels >= MinPixels && pixels < BelowPixels;

    public Geometry CreateGeometry() => PathData is { } data
        ? StreamGeometry.Parse("F1 " + data)
        : new EllipseGeometry(new Rect(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2));
}

/// <summary>
/// The kea artwork in paint order, parsed from <c>kea.svg</c> with the same structural rules
/// as <c>assets/GenerateIcons.ps1</c> (anything the generator would reject is rejected here).
/// </summary>
internal sealed class KeaArtwork
{
    public static readonly Uri Source = new("avares://KeaClip/Assets/kea.svg");
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly string[] ShapeAttributes = ["id", "d", "cx", "cy", "r", "x", "y", "width", "height", "rx", "fill", "display"];
    private static KeaArtwork? _shared;
    private Geometry[]? _geometries;

    private KeaArtwork(double canvas, double tileRadius, Point poseOrigin, IReadOnlyList<KeaArtPart> parts)
    {
        Canvas = canvas; TileRadius = tileRadius; PoseOrigin = poseOrigin; Parts = parts;
        Tile = new Rect(0, 0, canvas, canvas);
        PoseStart = parts.Count; PoseEnd = parts.Count;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Pose is null) continue;
            PoseStart = Math.Min(PoseStart, i); PoseEnd = i + 1;
        }
    }

    /// <summary>The app's artwork, read once from the embedded SVG.</summary>
    public static KeaArtwork Shared
    {
        get
        {
            if (_shared is { } shared) return shared;
            using var stream = AssetLoader.Open(Source);
            return _shared = Parse(XDocument.Load(stream).Root!);
        }
    }

    public double Canvas { get; }
    public double TileRadius { get; }
    public Rect Tile { get; }
    /// <summary>Where the hooked beak grips the carried card; the card pops from here.</summary>
    public Point PoseOrigin { get; }
    public IReadOnlyList<KeaArtPart> Parts { get; }
    /// <summary>The carried (pose) parts are [PoseStart, PoseEnd); later parts are drawn in front.</summary>
    public int PoseStart { get; }
    public int PoseEnd { get; }

    /// <summary>Parsed on first render (needs the platform), then shared.</summary>
    public Geometry[] Geometries => _geometries ??= Parts.Select(part => part.CreateGeometry()).ToArray();

    public static KeaArtwork Parse(XElement svg)
    {
        if (svg.Name != Svg + "svg") throw Invalid("the root element is not <svg>");
        var parts = new List<KeaArtPart>();
        XElement? tile = null;
        Point? poseOrigin = null;
        foreach (var element in svg.Elements())
        {
            if (element.Name.Namespace != Svg || element.Name.LocalName is "title" or "desc" or "metadata") continue;
            if (element.Name.LocalName == "g")
            {
                var pose = (string?)element.Attribute("data-pose");
                if (pose is null || (string?)element.Attribute("display") != "none") throw Invalid($"group #{(string?)element.Attribute("id")} is not a pose layer (<g data-pose=\"...\" display=\"none\">)");
                if (poseOrigin is not null) throw Invalid("only one pose layer is supported");
                poseOrigin = ParsePoint((string?)element.Attribute("data-origin") ?? throw Invalid($"pose layer #{(string?)element.Attribute("id")} needs data-origin=\"x y\""));
                var poseValue = Enum.Parse<KeaPose>(pose, ignoreCase: true);
                foreach (var child in element.Elements()) parts.Add(ParseShape(child, poseValue));
                continue;
            }
            if (tile is null)
            {
                if (element.Name.LocalName != "rect") throw Invalid("it must start with the tile <rect>");
                tile = element;
                continue;
            }
            var part = ParseShape(element, null);
            if ((string?)element.Attribute("display") == "none" && double.IsPositiveInfinity(part.BelowPixels)) throw Invalid($"#{part.Id} is hidden but is not a small-size variant (data-below-size)");
            parts.Add(part);
        }
        if (tile is null || parts.Count == 0) throw Invalid("it has no tile or no artwork");
        var canvas = Number(tile, "width");
        if (Number(tile, "height") != canvas) throw Invalid("the tile is not square");
        return new KeaArtwork(canvas, Number(tile, "rx", 0), poseOrigin ?? new Point(canvas / 2, canvas / 2), parts);
    }

    private static KeaArtPart ParseShape(XElement element, KeaPose? pose)
    {
        var id = (string?)element.Attribute("id") ?? throw Invalid("every shape needs an id");
        foreach (var attribute in element.Attributes())
            if (attribute.Name.Namespace == XNamespace.None && !attribute.Name.LocalName.StartsWith("data-", StringComparison.Ordinal) && !ShapeAttributes.Contains(attribute.Name.LocalName))
                throw Invalid($"#{id} has the unsupported attribute '{attribute.Name.LocalName}'");
        var fill = (string?)element.Attribute("fill") ?? throw Invalid($"#{id} has no fill");
        var dark = Color.Parse(fill);
        var light = Color.Parse((string?)element.Attribute("data-light-fill") ?? fill);
        var min = Number(element, "data-min-size", 0);
        var below = Number(element, "data-below-size", double.PositiveInfinity);
        var token = (string?)element.Attribute("data-token");
        return element.Name.LocalName switch
        {
            "path" => new KeaArtPart(id, (string?)element.Attribute("d") ?? throw Invalid($"#{id} has no path data"), default, 0, dark, light, min, below, pose, token),
            "circle" => new KeaArtPart(id, null, new Point(Number(element, "cx"), Number(element, "cy")), Number(element, "r"), dark, light, min, below, pose, token),
            _ => throw Invalid($"<{element.Name.LocalName}> #{id} is not a path or circle"),
        };
    }

    private static Point ParsePoint(string value)
    {
        var numbers = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (numbers.Length != 2) throw Invalid($"'{value}' is not a point");
        return new Point(double.Parse(numbers[0], CultureInfo.InvariantCulture), double.Parse(numbers[1], CultureInfo.InvariantCulture));
    }

    private static double Number(XElement element, string name, double? fallback = null)
    {
        if (element.Attribute(name) is { } attribute) return double.Parse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return fallback ?? throw Invalid($"<{element.Name.LocalName}> #{(string?)element.Attribute("id")} has no {name}");
    }

    private static FormatException Invalid(string reason) => new($"kea.svg cannot be drawn: {reason}.");
}
