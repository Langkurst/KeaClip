using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Klippi.Core;

namespace Klippi.Desktop;

// Shape: crop a clip to Vertical 9:16 (TikTok, Shorts, Reels) or Square 1:1. The native video preview can't have
// anything drawn over it, so the crop is shown on a still frame from the clip's start, with the kept window lit up
// and a slider to move it.
public sealed partial class MainViewModel
{
    private FrameShape _exportShape = FrameShape.Original;
    private double _exportShapePosition = 0.5;
    private Bitmap? _shapePreview;
    private CancellationTokenSource? _shapePreviewLoad;
    private string? _shapePreviewKey;

    public FrameShape ExportShape
    {
        get => _exportShape;
        set { if (SetProperty(ref _exportShape, value)) { RaiseShape(); UpdateEstimate(); LoadShapePreview(); } }
    }
    public double ExportShapePosition
    {
        get => _exportShapePosition;
        set { if (SetProperty(ref _exportShapePosition, Math.Clamp(double.IsFinite(value) ? value : 0.5, 0, 1))) RaiseShapeGeometry(); }
    }
    private CommunityToolkit.Mvvm.Input.IRelayCommand<string>? _setShapeCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand<string> SetShapeCommand => _setShapeCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand<string>(name =>
    {
        if (Enum.TryParse<FrameShape>(name, out var shape)) ExportShape = shape;
    });
    public bool IsShapeOriginal { get => _exportShape == FrameShape.Original; set { if (value) ExportShape = FrameShape.Original; } }
    public bool IsShapeVertical { get => _exportShape == FrameShape.Vertical; set { if (value) ExportShape = FrameShape.Vertical; } }
    public bool IsShapeSquare { get => _exportShape == FrameShape.Square; set { if (value) ExportShape = FrameShape.Square; } }

    /// <summary>Shapes apply to a video clip made by an exact cut (the picture is re-encoded).</summary>
    public bool ShowShapeChoice => EditorHasVideo && !AudioOnly;
    public bool ShowShapeEditor => ShowShapeChoice && _exportShape != FrameShape.Original;
    public Bitmap? ShapePreview { get => _shapePreview; private set { var old = _shapePreview; if (SetProperty(ref _shapePreview, value)) { old?.Dispose(); OnPropertyChanged(nameof(HasShapePreview)); } } }
    public bool HasShapePreview => _shapePreview is not null;

    private double TargetAspect => _exportShape == FrameShape.Vertical ? 9d / 16 : 1d;
    private double SourceAspect => PreviewAspect is > 0 and var aspect && double.IsFinite(aspect) ? aspect : 16d / 9;
    /// <summary>True when the sides are cut (a wide video made taller); false when the top and bottom are.</summary>
    public bool ShapeCutsWidth => SourceAspect > TargetAspect;
    private double KeptFraction => ShapeCutsWidth ? TargetAspect / SourceAspect : SourceAspect / TargetAspect;
    // The still frame is drawn at the video's true shape (wide videos 200 px tall, tall ones up to 260 px), so the
    // kept window looks exactly like the export will.
    public double ShapePreviewHeight => SourceAspect >= 1 ? Math.Min(200, 520 / SourceAspect) : 260;
    public double ShapePreviewWidth => ShapePreviewHeight * SourceAspect;
    public bool ShapeNeedsCrop => ShowShapeEditor && KeptFraction < 0.995;
    public GridLength ShapeBeforeLength => new(Math.Max((1 - KeptFraction) * _exportShapePosition, 0.0001), GridUnitType.Star);
    public GridLength ShapeKeptLength => new(KeptFraction, GridUnitType.Star);
    public GridLength ShapeAfterLength => new(Math.Max((1 - KeptFraction) * (1 - _exportShapePosition), 0.0001), GridUnitType.Star);
    public string ShapeStartLabel => ShapeCutsWidth ? "Left" : "Top";
    public string ShapeEndLabel => ShapeCutsWidth ? "Right" : "Bottom";
    public string ShapeSummary => !ShowShapeEditor ? ""
        : !ShapeNeedsCrop ? "This video is already that shape, so nothing is cut."
        : (_exportShape == FrameShape.Vertical ? "Vertical 9:16 for TikTok, Shorts and Reels. " : "Square 1:1. ")
          + (ShapeCutsWidth ? "The sides are cut; slide to choose what stays in frame." : "The top and bottom are cut; slide to choose what stays in frame.");

    private void RaiseShape()
    {
        foreach (var name in (string[])[nameof(IsShapeOriginal), nameof(IsShapeVertical), nameof(IsShapeSquare), nameof(ShowShapeChoice), nameof(ShowShapeEditor), nameof(ShapeSummary), nameof(ShapeCutsWidth), nameof(ShapeStartLabel), nameof(ShapeEndLabel), nameof(ShapeNeedsCrop), nameof(ShapePreviewWidth), nameof(ShapePreviewHeight)]) OnPropertyChanged(name);
        RaiseShapeGeometry();
    }
    private void RaiseShapeGeometry()
    {
        foreach (var name in (string[])[nameof(ShapeBeforeLength), nameof(ShapeKeptLength), nameof(ShapeAfterLength)]) OnPropertyChanged(name);
    }

    // Dragging the start handle changes StartTime many times a second; the preview frame follows once it settles.
    private Avalonia.Threading.DispatcherTimer? _shapePreviewTimer;
    private void ScheduleShapePreview()
    {
        if (!ShowShapeEditor) return;
        if (_shapePreviewTimer is null)
        {
            _shapePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
            _shapePreviewTimer.Tick += (_, _) => { _shapePreviewTimer.Stop(); LoadShapePreview(); };
        }
        _shapePreviewTimer.Stop(); _shapePreviewTimer.Start();
    }

    /// <summary>Loads the still frame for the crop preview (clip start) when a shape is chosen; once per file and time.</summary>
    internal void LoadShapePreview()
    {
        if (!ShowShapeEditor || _probe is not { HasVideo: true } || string.IsNullOrEmpty(EditorPath)) return;
        var at = MediaValidation.TryTimecode(StartTime, out var start) ? start : TimeSpan.Zero;
        var key = EditorPath + "|" + Math.Round(at.TotalSeconds, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (key == _shapePreviewKey && _shapePreview is not null) return;
        _shapePreviewLoad?.Cancel();
        var load = _shapePreviewLoad = new CancellationTokenSource();
        var path = EditorPath;
        _ = Task.Run(async () =>
        {
            try
            {
                var png = await _services.Media.FrameAsync(path, at, 480, load.Token);
                if (png is null || load.IsCancellationRequested) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (load.IsCancellationRequested || _disposed || path != EditorPath) return;
                    try { using var stream = new MemoryStream(png); ShapePreview = new Bitmap(stream); _shapePreviewKey = key; }
                    catch (Exception error) { CaptureDiagnostics(error); }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Avalonia.Threading.Dispatcher.UIThread.Post(() => CaptureDiagnostics(error)); }
        });
    }
}
