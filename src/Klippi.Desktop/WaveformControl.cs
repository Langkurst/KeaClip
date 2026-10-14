using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Klippi.Desktop;

/// <summary>
/// The editor's trim timeline: a time ruler, a mirrored waveform, the selected range with
/// warm trim grips, a smoothly interpolated playhead, a hover time label and zoom.
/// </summary>
/// <remarks>
/// Ratios (<see cref="StartRatio"/>, <see cref="EndRatio"/>, <see cref="PlayheadRatio"/> and the
/// value passed to <see cref="SeekRequested"/>) always live in full-media space (0 = start of
/// the file, 1 = end). Zoom only changes which window of that space is visible.
/// Performance: theme brushes and pens are resolved once per theme change; waveform, tick and
/// label geometry is rebuilt only when size, zoom, samples or duration change, and the hover
/// label is formatted on pointer input only. A display-synchronised animation frame loop runs
/// only while something moves (playback, an eased zoom or edge auto-pan) and stops as soon as it
/// settles or the control is hidden, so an idle timeline never re-renders.
/// </remarks>
public sealed class WaveformControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> SamplesProperty = AvaloniaProperty.Register<WaveformControl, IReadOnlyList<float>?>(nameof(Samples));
    public static readonly StyledProperty<double> StartRatioProperty = AvaloniaProperty.Register<WaveformControl, double>(nameof(StartRatio));
    public static readonly StyledProperty<double> EndRatioProperty = AvaloniaProperty.Register<WaveformControl, double>(nameof(EndRatio), 1);
    public static readonly StyledProperty<double> PlayheadRatioProperty = AvaloniaProperty.Register<WaveformControl, double>(nameof(PlayheadRatio));
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<WaveformControl, bool>(nameof(IsPlaying));
    public static readonly StyledProperty<TimeSpan> DurationProperty = AvaloniaProperty.Register<WaveformControl, TimeSpan>(nameof(Duration));
    public static readonly StyledProperty<string?> PlaceholderTextProperty = AvaloniaProperty.Register<WaveformControl, string?>(nameof(PlaceholderText));
    public static readonly DirectProperty<WaveformControl, double> ZoomProperty = AvaloniaProperty.RegisterDirect<WaveformControl, double>(nameof(Zoom), o => o.Zoom);
    public static readonly DirectProperty<WaveformControl, bool> CanZoomInProperty = AvaloniaProperty.RegisterDirect<WaveformControl, bool>(nameof(CanZoomIn), o => o.CanZoomIn);
    public static readonly DirectProperty<WaveformControl, bool> CanZoomOutProperty = AvaloniaProperty.RegisterDirect<WaveformControl, bool>(nameof(CanZoomOut), o => o.CanZoomOut);

    public IReadOnlyList<float>? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public double StartRatio { get => GetValue(StartRatioProperty); set => SetValue(StartRatioProperty, value); }
    public double EndRatio { get => GetValue(EndRatioProperty); set => SetValue(EndRatioProperty, value); }
    /// <summary>Last reported playback position (0–1). The control never writes it back.</summary>
    public double PlayheadRatio { get => GetValue(PlayheadRatioProperty); set => SetValue(PlayheadRatioProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    /// <summary>Media duration; enables the ruler, hover times, keyboard steps in seconds and zoom limits.</summary>
    public TimeSpan Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    /// <summary>Shown in the track when there are no samples (for example a video without audio).</summary>
    public string? PlaceholderText { get => GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }
    public double Zoom { get => _zoom; private set => SetAndRaise(ZoomProperty, ref _zoom, value); }
    public bool CanZoomIn { get => _canZoomIn; private set => SetAndRaise(CanZoomInProperty, ref _canZoomIn, value); }
    public bool CanZoomOut { get => _canZoomOut; private set => SetAndRaise(CanZoomOutProperty, ref _canZoomOut, value); }

    /// <summary>Raised with a full-media ratio when the user scrubs or steps the playhead.</summary>
    public event Action<double>? SeekRequested;

    private const double RulerHeight = 22, TrackRadius = 10, GripWidth = 12, GripSlop = 10, BarPitch = 5, BarThickness = 3, TrackPadding = 12, MinGap = .0001;
    private const double FineStepSeconds = .1, CoarseStepSeconds = 1, ZoomStep = 1.6, MaxExtrapolationSeconds = 1.2, ZoomAnimationSeconds = .2;
    private const double MinDataZoom = 8, OverviewInset = 10, OverviewHitHeight = 12, EdgeZone = 24, AutoPanSpansPerSecond = 1.2;
    private static readonly double[] RulerSteps = [.1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400];

    private enum DragMode { None, Scrub, Start, End, Pan }
    private enum Handle { None, Start, End }

    private DragMode _dragMode;
    private Handle _activeHandle;
    private double _zoom = 1, _viewStart;
    private bool _canZoomIn = true, _canZoomOut;
    private double _hoverX = double.NaN, _pointerX, _scrubRatio, _grabOffset, _panOffset;
    private bool _hoverOnOverview;
    private float _peak = 1;

    // Playhead interpolation: the last report is the anchor; the display extrapolates at 1x
    // and blends small report-to-report corrections out over ~200 ms instead of jumping.
    private long _anchorTicks;
    private double _anchorRatio, _correction;
    private bool _frameRequested;
    private readonly Action<TimeSpan> _frameCallback;

    // Button/keyboard zoom eases between levels (only when the window allows motion). The anchor
    // point stays fixed under the zoom; repeated requests accumulate onto the pending target.
    private bool _zoomAnimating;
    private long _zoomAnimationTicks;
    private double _zoomFrom, _zoomTarget, _zoomAnchorRatio, _zoomAnchorX;

    // Dragging a grip into the edge zone of a zoomed view pans towards it (speed grows with depth).
    private double _autoPan;
    private long _autoPanTicks;

    // Cached render resources. Rebuilt on size/zoom/sample/duration/theme changes only.
    private StreamGeometry? _bars, _ticks;
    private readonly List<(double X, FormattedText Text)> _rulerLabels = [];
    private bool _geometryDirty = true, _paletteReady;
    private Size _geometrySize;
    private FormattedText? _placeholder, _hoverText;
    private string? _hoverString;
    private Rect _hoverBox;
    private IImmutableBrush _trackBrush = Brushes.Transparent, _selectionBrush = Brushes.Transparent, _dimBrush = Brushes.Transparent, _warmBrush = Brushes.Transparent;
    private IImmutableBrush _playheadBrush = Brushes.Transparent, _labelBrush = Brushes.Transparent, _tipBrush = Brushes.Transparent, _tipTextBrush = Brushes.Transparent;
    private static readonly ImmutablePen NoPen = new(Brushes.Transparent, 0);
    private ImmutablePen _idlePen = NoPen, _activePen = NoPen, _tickPen = NoPen, _framePen = NoPen, _notchPen = NoPen, _playheadPen = NoPen, _knobPen = NoPen;
    private ImmutablePen _hoverPen = NoPen, _focusPen = NoPen, _activeGripPen = NoPen, _centerPen = NoPen, _overviewTrackPen = NoPen, _overviewThumbPen = NoPen, _overviewRangePen = NoPen;
    private Cursor? _resizeCursor, _scrubCursor;
    private TimelineAutomationPeer? _peer;

    static WaveformControl()
    {
        AffectsRender<WaveformControl>(SamplesProperty, StartRatioProperty, EndRatioProperty, PlayheadRatioProperty, IsPlayingProperty, DurationProperty, PlaceholderTextProperty);
        FocusableProperty.OverrideDefaultValue<WaveformControl>(true);
    }

    public WaveformControl()
    {
        _frameCallback = OnAnimationFrame;
        ActualThemeVariantChanged += (_, _) => RefreshPalette();
    }

    // ===== Public API (zoom buttons, page-level shortcuts and the editor's set-at-playhead buttons) =====

    public void ZoomIn() => ZoomAroundFocus(ZoomStep);
    public void ZoomOut() => ZoomAroundFocus(1 / ZoomStep);
    public void ResetZoom() { _zoomAnimating = false; SetView(1, 0); }

    /// <summary>Back to the whole file with no keyboard target: used when a different file opens.</summary>
    public void ResetView()
    {
        _zoomAnimating = false; _autoPan = 0; _activeHandle = Handle.None; _hoverX = double.NaN; _hoverOnOverview = false;
        SetView(1, 0);
        UpdateHoverLabel(); InvalidateVisual();
    }

    /// <summary>Marks the start at the drawn playhead (an in-point after the out-point resets the out-point to the end).</summary>
    public void MarkStart() => SetStartAt(DisplayedPlayhead());

    /// <summary>Marks the end at the drawn playhead (an out-point before the in-point resets the in-point to the start).</summary>
    public void MarkEnd() => SetEndAt(DisplayedPlayhead());

    /// <summary>Moves the playhead by the timeline's keyboard step: 0.1 s, or 1 s when <paramref name="fine"/> is false.</summary>
    public void StepPlayhead(int direction, bool fine)
    {
        var target = Clamp01(DisplayedPlayhead() + StepRatio(fine) * Math.Sign(direction));
        RequestSeek(target);
        RevealRatio(target);
    }

    private bool MotionAllowed => TopLevel.GetTopLevel(this) is { } top && top.Classes.Contains("motion");

    private double MaxZoom
    {
        get
        {
            if (Seconds <= 0) return 16;
            var limit = Math.Min(240, Seconds / 1.5);
            // Each bar keeps at least one real waveform point, so zooming never invents detail.
            if (Samples is { Count: > 0 } samples && Bounds.Width > 0) limit = Math.Min(limit, Math.Max(MinDataZoom, samples.Count * BarPitch / Bounds.Width));
            return Math.Max(1, limit);
        }
    }
    private double ViewSpan => 1 / _zoom;
    private double Seconds => Math.Max(0, Duration.TotalSeconds);
    private double RatioToX(double ratio) => (ratio - _viewStart) * _zoom * Bounds.Width;
    private double XToRatio(double x) => Bounds.Width <= 0 ? 0 : _viewStart + x / Bounds.Width / _zoom;
    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private void ZoomAroundFocus(double factor)
    {
        var width = Bounds.Width;
        if (width <= 0) return;
        var playhead = DisplayedPlayhead();
        var inView = playhead >= _viewStart && playhead <= _viewStart + ViewSpan;
        var anchor = inView ? playhead : _viewStart + ViewSpan / 2;
        var target = Math.Clamp((_zoomAnimating ? _zoomTarget : _zoom) * factor, 1, MaxZoom);
        if (!MotionAllowed || !IsEffectivelyVisible) { _zoomAnimating = false; ZoomAround(anchor, RatioToX(anchor), target / _zoom); return; }
        _zoomFrom = _zoom; _zoomTarget = target; _zoomAnchorRatio = anchor; _zoomAnchorX = RatioToX(anchor);
        _zoomAnimationTicks = Stopwatch.GetTimestamp(); _zoomAnimating = true;
        CanZoomIn = target < MaxZoom - .0001; CanZoomOut = target > 1;
        EnsureFrameLoop();
    }

    private void StepZoomAnimation()
    {
        var t = Math.Clamp(Stopwatch.GetElapsedTime(_zoomAnimationTicks).TotalSeconds / ZoomAnimationSeconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        // Interpolate in log space so zooming in and out feel equally even.
        var zoom = _zoomFrom * Math.Pow(_zoomTarget / _zoomFrom, eased);
        if (t >= 1) { zoom = _zoomTarget; _zoomAnimating = false; }
        if (Bounds.Width > 0) SetView(zoom, _zoomAnchorRatio - _zoomAnchorX / Bounds.Width / zoom);
    }

    private void ZoomAround(double anchorRatio, double anchorX, double factor)
    {
        var width = Bounds.Width;
        if (width <= 0 || !double.IsFinite(factor) || factor <= 0) return;
        var zoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
        SetView(zoom, anchorRatio - anchorX / width / zoom);
    }

    private void SetView(double zoom, double start)
    {
        var max = MaxZoom;
        zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1, 1, max);
        start = Math.Clamp(double.IsFinite(start) ? start : 0, 0, Math.Max(0, 1 - 1 / zoom));
        if (zoom < 1.0001) { zoom = 1; start = 0; }
        // While easing, the buttons already reflect where the zoom is heading.
        var settled = _zoomAnimating ? _zoomTarget : zoom;
        CanZoomIn = settled < max - .0001; CanZoomOut = settled > 1;
        if (zoom == _zoom && start == _viewStart) return;
        _viewStart = start; Zoom = zoom;
        _geometryDirty = true; UpdateHoverLabel(); InvalidateVisual();
    }

    // ===== Property changes =====

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SamplesProperty)
        {
            var peak = 0f;
            if (Samples is { } samples) for (var i = 0; i < samples.Count; i++) { var value = Math.Abs(samples[i]); if (float.IsFinite(value) && value > peak) peak = value; }
            _peak = Math.Max(peak, .02f); _geometryDirty = true;
            // A detailed waveform for the same file keeps the view; the zoom limit may change.
            SetView(_zoom, _viewStart);
        }
        else if (change.Property == DurationProperty)
        {
            // A new file starts fully zoomed out; the ruler and zoom limits follow its length.
            _zoomAnimating = false; _autoPan = 0; _viewStart = 0; Zoom = 1; CanZoomOut = false; CanZoomIn = MaxZoom > 1.0001; _geometryDirty = true;
            UpdateHoverLabel();
        }
        else if (change.Property == PlaceholderTextProperty) _placeholder = null;
        else if (change.Property == PlayheadRatioProperty)
        {
            var shown = DisplayedPlayhead();
            var reported = Clamp01(change.GetNewValue<double>());
            var error = shown - reported;
            _anchorRatio = reported; _anchorTicks = Stopwatch.GetTimestamp();
            // Small differences are timing jitter between reports: blend them out. Large ones are seeks: snap.
            _correction = IsPlaying && Seconds > 0 && Math.Abs(error) * Seconds < .75 ? error : 0;
            EnsureFrameLoop();
            _peer?.RaiseValueChanged(shown * Seconds, reported * Seconds);
        }
        else if (change.Property == IsPlayingProperty)
        {
            _anchorRatio = Clamp01(PlayheadRatio); _anchorTicks = Stopwatch.GetTimestamp(); _correction = 0;
            EnsureFrameLoop();
        }
        else if (change.Property == StartRatioProperty || change.Property == EndRatioProperty)
        {
            if (_dragMode is DragMode.Start or DragMode.End) UpdateHoverLabel();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // The zoom limit depends on the width (bars per waveform point); keep the view inside it.
        SetView(_zoom, _viewStart);
        UpdateHoverLabel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshPalette();
        EnsureFrameLoop();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new TimelineAutomationPeer(this);

    // ===== Playhead animation =====

    private double DisplayedPlayhead()
    {
        if (_dragMode == DragMode.Scrub) return _scrubRatio;
        if (!IsPlaying || Seconds <= 0) return Clamp01(PlayheadRatio);
        // Cap extrapolation so a stalled player (buffering) never runs the line away.
        var elapsed = Math.Min(MaxExtrapolationSeconds, Stopwatch.GetElapsedTime(_anchorTicks).TotalSeconds);
        return Clamp01(_anchorRatio + elapsed / Seconds + _correction * Math.Exp(-elapsed / .2));
    }

    private void EnsureFrameLoop()
    {
        if (_frameRequested || !IsEffectivelyVisible || !(_zoomAnimating || _autoPan != 0 || IsPlaying && Seconds > 0)) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(_frameCallback);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        _frameRequested = false;
        if (!IsEffectivelyVisible || VisualRoot is null)
        {
            // Hidden mid-zoom: land on the target so the view is right when shown again.
            if (_zoomAnimating) { _zoomAnimating = false; if (Bounds.Width > 0) SetView(_zoomTarget, _zoomAnchorRatio - _zoomAnchorX / Bounds.Width / _zoomTarget); }
            _autoPan = 0;
            return;
        }
        var more = false;
        if (_zoomAnimating) { StepZoomAnimation(); more = _zoomAnimating; }
        if (_autoPan != 0) more |= StepAutoPan();
        if (IsPlaying && Seconds > 0)
        {
            FollowPlayhead();
            InvalidateVisual();
            // Without fresh position reports (buffering, or a player that stopped without saying so)
            // the extrapolated line is already frozen: stop rendering until the next report restarts us.
            more |= Stopwatch.GetElapsedTime(_anchorTicks).TotalSeconds <= MaxExtrapolationSeconds;
        }
        if (more) EnsureFrameLoop();
    }

    // When zoomed in, page the view forward as the playhead crosses the right edge.
    private void FollowPlayhead()
    {
        if (_zoom <= 1 || _dragMode != DragMode.None || _zoomAnimating) return;
        var playhead = DisplayedPlayhead();
        var end = _viewStart + ViewSpan;
        if (playhead > end && playhead - end < ViewSpan * .5) SetView(_zoom, playhead - ViewSpan * .08);
    }

    private void UpdateAutoPan(double x)
    {
        var width = Bounds.Width;
        var speed = 0d;
        if (_zoom > 1 && _dragMode is DragMode.Start or DragMode.End && width > EdgeZone * 3)
        {
            if (x < EdgeZone && _viewStart > 0) speed = -Math.Clamp((EdgeZone - x) / EdgeZone, .15, 1);
            else if (x > width - EdgeZone && _viewStart + ViewSpan < 1) speed = Math.Clamp((x - (width - EdgeZone)) / EdgeZone, .15, 1);
        }
        if (speed != 0 && _autoPan == 0) _autoPanTicks = Stopwatch.GetTimestamp();
        _autoPan = speed;
        EnsureFrameLoop();
    }

    private bool StepAutoPan()
    {
        if (_dragMode is not (DragMode.Start or DragMode.End) || _zoom <= 1) { _autoPan = 0; return false; }
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Min(.05, Stopwatch.GetElapsedTime(_autoPanTicks, now).TotalSeconds);
        _autoPanTicks = now;
        var before = _viewStart;
        SetView(_zoom, _viewStart + _autoPan * ViewSpan * AutoPanSpansPerSecond * seconds);
        ApplyPointer(_pointerX);
        // Stop at either end of the media; moving the pointer again restarts it.
        if (_viewStart == before && seconds > 0) { _autoPan = 0; return false; }
        return true;
    }

    // ===== Pointer =====

    private Handle HitHandle(double x)
    {
        if (Bounds.Width <= 0) return Handle.None;
        var start = RatioToX(Clamp01(StartRatio));
        var end = RatioToX(Clamp01(EndRatio));
        var grip = GripSize(start, end);
        // Grips sit just inside the selection; the hit zone adds generous slop outside it.
        var onStart = x >= start - GripSlop && x <= start + grip + 4;
        var onEnd = x >= end - grip - 4 && x <= end + GripSlop;
        if (onStart && onEnd)
        {
            var toStart = Math.Abs(x - start); var toEnd = Math.Abs(x - end);
            if (Math.Abs(toStart - toEnd) < .5) return x < start ? Handle.Start : Handle.End;
            return toStart < toEnd ? Handle.Start : Handle.End;
        }
        return onStart ? Handle.Start : onEnd ? Handle.End : Handle.None;
    }

    private bool IsOverOverview(Point point) => _zoom > 1 && point.Y >= Bounds.Height - OverviewHitHeight && point.Y <= Bounds.Height;
    private double OverviewRatio(double x) => Bounds.Width <= OverviewInset * 2 ? 0 : (x - OverviewInset) / (Bounds.Width - OverviewInset * 2);
    private static double GripSize(double start, double end) => Math.Clamp((end - start) / 2, 4, GripWidth);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Bounds.Width <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(NavigationMethod.Pointer);
        var point = e.GetPosition(this);
        var x = point.X;
        _hoverX = x; _pointerX = x; _hoverOnOverview = false;
        if (IsOverOverview(point))
        {
            // The overview strip pans: grab the thumb where it was pressed, or centre the view on a click beside it.
            var ratio = OverviewRatio(x);
            _dragMode = DragMode.Pan;
            _panOffset = ratio >= _viewStart && ratio <= _viewStart + ViewSpan ? ratio - _viewStart : ViewSpan / 2;
            _hoverOnOverview = true;
            ApplyPointer(x);
        }
        else
        {
            var handle = HitHandle(x);
            _activeHandle = handle;
            switch (handle)
            {
                // Pressing a grip only picks it up; the trim point moves with the pointer from there.
                case Handle.Start: _dragMode = DragMode.Start; _grabOffset = x - RatioToX(Clamp01(StartRatio)); break;
                case Handle.End: _dragMode = DragMode.End; _grabOffset = x - RatioToX(Clamp01(EndRatio)); break;
                default: _dragMode = DragMode.Scrub; _grabOffset = 0; ApplyPointer(x); break;
            }
        }
        e.Pointer.Capture(this);
        UpdateHoverLabel(); InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var x = point.X;
        _hoverX = x; _pointerX = x;
        if (_dragMode != DragMode.None)
        {
            ApplyPointer(x);
            UpdateAutoPan(x);
            e.Handled = true;
        }
        else
        {
            _hoverOnOverview = IsOverOverview(point);
            UpdateCursor(!_hoverOnOverview && HitHandle(x) != Handle.None);
        }
        UpdateHoverLabel();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_dragMode != DragMode.None) return;
        _hoverX = double.NaN; _hoverOnOverview = false;
        UpdateHoverLabel(); InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragMode == DragMode.Scrub) { _anchorRatio = _scrubRatio; _anchorTicks = Stopwatch.GetTimestamp(); _correction = 0; }
        _dragMode = DragMode.None; _autoPan = 0;
        UpdateHoverLabel(); InvalidateVisual();
    }

    private void ApplyPointer(double x)
    {
        switch (_dragMode)
        {
            case DragMode.Start: SetCurrentValue(StartRatioProperty, Math.Clamp(Clamp01(XToRatio(x - _grabOffset)), 0, Math.Max(0, EndRatio - MinGap))); break;
            case DragMode.End: SetCurrentValue(EndRatioProperty, Math.Clamp(Clamp01(XToRatio(x - _grabOffset)), Math.Min(1, StartRatio + MinGap), 1)); break;
            case DragMode.Scrub: _scrubRatio = Clamp01(XToRatio(x)); SeekRequested?.Invoke(_scrubRatio); InvalidateVisual(); break;
            case DragMode.Pan: _zoomAnimating = false; SetView(_zoom, OverviewRatio(x) - _panOffset); break;
        }
    }

    private void UpdateCursor(bool overHandle)
    {
        var cursor = overHandle ? _resizeCursor ??= new Cursor(StandardCursorType.SizeWestEast) : _scrubCursor ??= new Cursor(StandardCursorType.Hand);
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Bounds.Width <= 0) return;
        var x = e.GetPosition(this).X;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            var notches = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (notches == 0) return;
            // Wheel notches are already incremental, so they apply immediately.
            _zoomAnimating = false;
            ZoomAround(XToRatio(x), x, Math.Pow(1.25, Math.Clamp(notches, -4, 4)));
            e.Handled = true;
            return;
        }
        // Not zoomed: let the page scroll normally.
        if (_zoom <= 1) return;
        _zoomAnimating = false;
        var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (delta == 0) return;
        SetView(_zoom, _viewStart - delta * ViewSpan * .12);
        e.Handled = true;
    }

    // ===== Keyboard =====

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var modifiers = e.KeyModifiers;
        var plain = modifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.I when plain: MarkStart(); break;
            case Key.O when plain: MarkEnd(); break;
            case Key.Left or Key.Right when plain || modifiers == KeyModifiers.Shift: Nudge(e.Key == Key.Left ? -1 : 1, plain); break;
            case Key.Home when plain: RequestSeek(Clamp01(StartRatio)); break;
            case Key.End when plain: RequestSeek(Clamp01(EndRatio)); break;
            case Key.OemPlus or Key.Add when plain || modifiers == KeyModifiers.Shift: ZoomIn(); break;
            case Key.OemMinus or Key.Subtract when plain: ZoomOut(); break;
            case Key.Escape:
                // Clear the keyboard target but let the window still use Escape (for example to leave fullscreen).
                if (_activeHandle != Handle.None) { _activeHandle = Handle.None; InvalidateVisual(); }
                return;
            default: return;
        }
        e.Handled = true;
    }

    private double StepRatio(bool fine) => Seconds > 0 ? (fine ? FineStepSeconds : CoarseStepSeconds) / Seconds : fine ? .002 : .02;

    private void Nudge(int direction, bool fine)
    {
        var step = StepRatio(fine) * direction;
        switch (_activeHandle)
        {
            case Handle.Start: SetCurrentValue(StartRatioProperty, Math.Clamp(StartRatio + step, 0, Math.Max(0, EndRatio - MinGap))); break;
            case Handle.End: SetCurrentValue(EndRatioProperty, Math.Clamp(EndRatio + step, Math.Min(1, StartRatio + MinGap), 1)); break;
            default: RequestSeek(Clamp01(DisplayedPlayhead() + step)); break;
        }
        RevealRatio(_activeHandle switch { Handle.Start => StartRatio, Handle.End => EndRatio, _ => DisplayedPlayhead() });
        InvalidateVisual();
    }

    private void SetStartAt(double ratio)
    {
        ratio = Clamp01(ratio);
        // Marking an in-point after the out-point resets the out-point to the end of the media.
        if (ratio >= EndRatio) SetCurrentValue(EndRatioProperty, 1d);
        SetCurrentValue(StartRatioProperty, Math.Min(ratio, Math.Max(0, EndRatio - MinGap)));
        _activeHandle = Handle.Start; RevealRatio(StartRatio); InvalidateVisual();
    }

    private void SetEndAt(double ratio)
    {
        ratio = Clamp01(ratio);
        if (ratio <= StartRatio) SetCurrentValue(StartRatioProperty, 0d);
        SetCurrentValue(EndRatioProperty, Math.Max(ratio, Math.Min(1, StartRatio + MinGap)));
        _activeHandle = Handle.End; RevealRatio(EndRatio); InvalidateVisual();
    }

    private void RequestSeek(double ratio)
    {
        _anchorRatio = ratio; _anchorTicks = Stopwatch.GetTimestamp(); _correction = 0;
        SeekRequested?.Invoke(ratio);
        InvalidateVisual();
    }

    private void RevealRatio(double ratio)
    {
        if (_zoom <= 1) return;
        if (ratio < _viewStart) SetView(_zoom, ratio - ViewSpan * .1);
        else if (ratio > _viewStart + ViewSpan) SetView(_zoom, ratio - ViewSpan * .9);
    }

    // ===== Theme =====

    private Color ResolveColor(string key, Color fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var value) ? value switch { ISolidColorBrush brush => brush.Color, Color color => color, _ => fallback } : fallback;

    private void RefreshPalette()
    {
        var track = ResolveColor("WaveformBackground", Color.FromRgb(0x12, 0x1A, 0x17));
        var idle = ResolveColor("WaveformIdle", Color.FromRgb(0x3B, 0x50, 0x47));
        var active = ResolveColor("WaveformActive", Color.FromRgb(0x6F, 0xCF, 0x97));
        var selection = ResolveColor("WaveformSelection", Color.FromRgb(0x1E, 0x3A, 0x2C));
        var playhead = ResolveColor("Playhead", Color.FromRgb(0xF4, 0xF7, 0xF5));
        var warm = ResolveColor("AppWarm", Color.FromRgb(0xF0, 0xA3, 0x5E));
        var faint = ResolveColor("AppFaint", Color.FromRgb(0x73, 0x85, 0x7B));
        var border = ResolveColor("AppBorderStrong", Color.FromRgb(0x33, 0x46, 0x3F));
        var accent = ResolveColor("AppAccent", Color.FromRgb(0x6F, 0xCF, 0x97));
        var tip = ResolveColor("ToolTipBackground", Color.FromRgb(0x1F, 0x2B, 0x27));
        var tipText = ResolveColor("ToolTipForeground", Color.FromRgb(0xEE, 0xF3, 0xEF));
        var surface = ResolveColor("AppSurface", Color.FromRgb(0x16, 0x1F, 0x1C));

        _trackBrush = new ImmutableSolidColorBrush(track);
        _selectionBrush = new ImmutableSolidColorBrush(selection);
        _dimBrush = new ImmutableSolidColorBrush(track, .55);
        _warmBrush = new ImmutableSolidColorBrush(warm);
        _playheadBrush = new ImmutableSolidColorBrush(playhead);
        _labelBrush = new ImmutableSolidColorBrush(faint);
        _tipBrush = new ImmutableSolidColorBrush(tip);
        _tipTextBrush = new ImmutableSolidColorBrush(tipText);
        _idlePen = new ImmutablePen(new ImmutableSolidColorBrush(idle), BarThickness, null, PenLineCap.Round);
        _activePen = new ImmutablePen(new ImmutableSolidColorBrush(active), BarThickness, null, PenLineCap.Round);
        _tickPen = new ImmutablePen(new ImmutableSolidColorBrush(border), 1);
        _centerPen = new ImmutablePen(new ImmutableSolidColorBrush(border), 1, new ImmutableDashStyle([3, 3], 0));
        _framePen = new ImmutablePen(_warmBrush, 2);
        _notchPen = new ImmutablePen(new ImmutableSolidColorBrush(track, .85), 2, null, PenLineCap.Round);
        _playheadPen = new ImmutablePen(_playheadBrush, 2, null, PenLineCap.Round);
        _knobPen = new ImmutablePen(new ImmutableSolidColorBrush(surface), 2);
        _hoverPen = new ImmutablePen(new ImmutableSolidColorBrush(playhead, .35), 1);
        _focusPen = new ImmutablePen(new ImmutableSolidColorBrush(accent), 2);
        _activeGripPen = new ImmutablePen(_playheadBrush, 2);
        _overviewTrackPen = new ImmutablePen(new ImmutableSolidColorBrush(border, .9), 4, null, PenLineCap.Round);
        _overviewRangePen = new ImmutablePen(new ImmutableSolidColorBrush(warm, .8), 4, null, PenLineCap.Round);
        _overviewThumbPen = new ImmutablePen(new ImmutableSolidColorBrush(playhead, .55), 4, null, PenLineCap.Round);
        _paletteReady = true;
        _placeholder = null; _hoverText = null; _hoverString = null; _geometryDirty = true;
        UpdateHoverLabel();
        InvalidateVisual();
    }

    private Typeface LabelTypeface(FontWeight weight = FontWeight.Normal) => new(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, weight);

    // ===== Geometry =====

    private void RebuildGeometry(double width, double height)
    {
        _geometryDirty = false; _geometrySize = Bounds.Size;
        _bars = null; _ticks = null; _rulerLabels.Clear();
        var trackHeight = height - RulerHeight;
        if (width <= 0 || trackHeight <= 0) return;

        var samples = Samples;
        if (samples is { Count: > 0 })
        {
            var mid = RulerHeight + trackHeight / 2;
            var maxHalf = Math.Max(1, trackHeight / 2 - TrackPadding - BarThickness / 2);
            var bars = new StreamGeometry();
            using (var context = bars.Open())
            {
                var count = (int)Math.Floor(width / BarPitch);
                var offset = (width - count * BarPitch) / 2;
                for (var i = 0; i < count; i++)
                {
                    var x0 = offset + i * BarPitch;
                    var amplitude = Peak(samples, XToRatio(x0), XToRatio(x0 + BarPitch)) / _peak;
                    // A gentle curve keeps quiet passages readable without flattening the dynamics.
                    var half = Math.Max(1, Math.Pow(Math.Clamp(amplitude, 0, 1), .8) * maxHalf);
                    var x = x0 + BarPitch / 2;
                    context.BeginFigure(new Point(x, mid - half), false);
                    context.LineTo(new Point(x, mid + half));
                    context.EndFigure(false);
                }
            }
            _bars = bars;
        }
        BuildRuler(width);
    }

    private static double Peak(IReadOnlyList<float> samples, double from, double to)
    {
        var count = samples.Count;
        var a = Math.Clamp(from, 0, 1) * count;
        var b = Math.Clamp(to, 0, 1) * count;
        if (b - a < 1)
        {
            // Between two points (only at the zoom limit or on very short files): interpolate neighbours.
            var center = Math.Clamp((a + b) / 2 - .5, 0, count - 1);
            var index = (int)Math.Floor(center);
            var next = Math.Min(index + 1, count - 1);
            var t = center - index;
            return Value(samples[index]) * (1 - t) + Value(samples[next]) * t;
        }
        var start = Math.Clamp((int)Math.Floor(a), 0, count - 1);
        var end = Math.Clamp((int)Math.Ceiling(b), start + 1, count);
        var peak = 0d;
        for (var i = start; i < end; i++) peak = Math.Max(peak, Value(samples[i]));
        return peak;
        static double Value(float sample) => float.IsFinite(sample) ? Math.Abs(sample) : 0;
    }

    private void BuildRuler(double width)
    {
        var seconds = Seconds;
        if (seconds <= 0) return;
        var visible = seconds / _zoom;
        var pixelsPerSecond = width / visible;
        var major = RulerSteps[^1];
        foreach (var step in RulerSteps) if (step * pixelsPerSecond >= 76) { major = step; break; }
        var divisions = major * pixelsPerSecond / 5 >= 9 ? 5 : 2;
        var minor = major / divisions;
        var startSeconds = _viewStart * seconds;
        var endSeconds = startSeconds + visible;
        var typeface = LabelTypeface();
        var ticks = new StreamGeometry();
        using (var context = ticks.Open())
        {
            for (var i = (long)Math.Floor(startSeconds / minor); ; i++)
            {
                var t = i * minor;
                if (t > endSeconds + minor) break;
                var x = Math.Round((t - startSeconds) * pixelsPerSecond) + .5;
                if (x < -1 || x > width + 1) continue;
                var isMajor = i % divisions == 0;
                context.BeginFigure(new Point(x, RulerHeight - (isMajor ? 8 : 4)), false);
                context.LineTo(new Point(x, RulerHeight - 2));
                context.EndFigure(false);
                if (!isMajor) continue;
                var label = new FormattedText(RulerLabel(t, major), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, _labelBrush);
                if (x + 4 + label.Width <= width - 2) _rulerLabels.Add((x + 4, label));
            }
        }
        _ticks = ticks;
    }

    private static string RulerLabel(double seconds, double step)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds, 3)));
        if (time.TotalHours >= 1) return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}");
        if (step < 1) return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }

    private static string PreciseTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 10:00}");
    }

    // The hover/drag time pill is formatted and measured on input only (pointer, drag, zoom), never
    // per animation frame, so hovering during playback does not allocate while rendering.
    private void UpdateHoverLabel()
    {
        var width = Bounds.Width;
        if (double.IsNaN(_hoverX) || Seconds <= 0 || width <= 0 || _dragMode == DragMode.Pan || _hoverOnOverview && _dragMode == DragMode.None)
        {
            _hoverBox = default;
            return;
        }
        var ratio = _dragMode switch { DragMode.Start => StartRatio, DragMode.End => EndRatio, DragMode.Scrub => _scrubRatio, _ => Clamp01(XToRatio(_hoverX)) };
        var text = PreciseTime(Clamp01(ratio) * Seconds);
        if (_hoverText is null || text != _hoverString)
        {
            _hoverString = text;
            _hoverText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface(FontWeight.SemiBold), 11, _tipTextBrush);
        }
        var anchor = _dragMode switch { DragMode.Start => RatioToX(Clamp01(StartRatio)), DragMode.End => RatioToX(Clamp01(EndRatio)), _ => _hoverX };
        var boxWidth = _hoverText.Width + 14; var boxHeight = Math.Max(18, _hoverText.Height + 4);
        _hoverBox = new Rect(Math.Clamp(anchor - boxWidth / 2, 0, Math.Max(0, width - boxWidth)), 0, boxWidth, boxHeight);
    }

    // ===== Render =====

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width; var height = Bounds.Height;
        if (width <= 0 || height <= RulerHeight) return;
        if (!_paletteReady) RefreshPalette();
        if (_geometryDirty || _geometrySize != Bounds.Size) RebuildGeometry(width, height);

        // Transparent fill keeps the ruler strip hit-testable for scrubbing.
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, width, height));
        var track = new Rect(0, RulerHeight, width, height - RulerHeight);
        var trackShape = new RoundedRect(track, TrackRadius);
        context.DrawRectangle(_trackBrush, null, trackShape);

        if (_ticks is not null) context.DrawGeometry(null, _tickPen, _ticks);
        // Labels under the hover time pill are skipped so the two never overlap.
        var pill = _hoverBox;
        foreach (var (x, text) in _rulerLabels)
            if (pill.Width <= 0 || x + text.Width < pill.X - 2 || x > pill.Right + 2) context.DrawText(text, new Point(x, 3));

        var start = RatioToX(Clamp01(StartRatio));
        var end = RatioToX(Clamp01(EndRatio));
        var selection = new Rect(start, track.Top, Math.Max(0, end - start), track.Height);
        using (context.PushClip(trackShape))
        {
            if (end > start) context.DrawRectangle(_selectionBrush, null, new RoundedRect(selection, 8));
            if (_bars is not null)
            {
                context.DrawGeometry(null, _idlePen, _bars);
                if (end > start) using (context.PushClip(selection)) context.DrawGeometry(null, _activePen, _bars);
            }
            else DrawPlaceholder(context, track);
            if (start > 0) context.FillRectangle(_dimBrush, new Rect(0, track.Top, start, track.Height));
            if (end < width) context.FillRectangle(_dimBrush, new Rect(end, track.Top, width - end, track.Height));
            if (pill.Width > 0 && _dragMode == DragMode.None) context.DrawLine(_hoverPen, new Point(Math.Round(_hoverX) + .5, track.Top), new Point(Math.Round(_hoverX) + .5, track.Bottom));
            if (_zoom > 1) DrawOverview(context, track);
        }

        DrawTrim(context, start, end, track);
        DrawPlayhead(context, track);
        if (pill.Width > 0 && _hoverText is { } label)
        {
            context.DrawRectangle(_tipBrush, null, new RoundedRect(pill, pill.Height / 2));
            context.DrawText(label, new Point(pill.X + 7, (pill.Height - label.Height) / 2));
        }
        if (IsFocused && Classes.Contains(":focus-visible")) context.DrawRectangle(null, _focusPen, new RoundedRect(track.Deflate(1), TrackRadius - 1));
    }

    private void DrawPlaceholder(DrawingContext context, Rect track)
    {
        var mid = Math.Round(track.Center.Y) + .5;
        context.DrawLine(_centerPen, new Point(TrackPadding, mid), new Point(track.Width - TrackPadding, mid));
        var text = PlaceholderText;
        if (string.IsNullOrWhiteSpace(text)) return;
        _placeholder ??= new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface(FontWeight.Medium), 12, _labelBrush);
        var box = new Rect(track.Center.X - _placeholder.Width / 2 - 10, mid - _placeholder.Height / 2 - 4, _placeholder.Width + 20, _placeholder.Height + 8);
        context.DrawRectangle(_trackBrush, null, new RoundedRect(box, box.Height / 2));
        context.DrawText(_placeholder, new Point(box.X + 10, box.Y + 4));
    }

    // While zoomed: the whole file in miniature with the kept range (warm) and the visible window.
    private void DrawOverview(DrawingContext context, Rect track)
    {
        var y = track.Bottom - 5;
        var left = OverviewInset; var right = track.Width - OverviewInset;
        if (right <= left) return;
        var span = right - left;
        context.DrawLine(_overviewTrackPen, new Point(left, y), new Point(right, y));
        var rangeStart = left + Clamp01(StartRatio) * span; var rangeEnd = left + Clamp01(EndRatio) * span;
        if (rangeEnd > rangeStart) context.DrawLine(_overviewRangePen, new Point(rangeStart, y), new Point(Math.Max(rangeEnd, rangeStart + 1), y));
        context.DrawLine(_overviewThumbPen, new Point(left + _viewStart * span, y), new Point(left + Math.Min(1, _viewStart + ViewSpan) * span, y));
    }

    private void DrawTrim(DrawingContext context, double start, double end, Rect track)
    {
        var width = track.Width;
        if (end < -GripWidth || start > width + GripWidth) return;
        var grip = GripSize(start, end);
        // Thin top/bottom rails join the grips into a frame around the kept range.
        if (end - start > grip * 2)
        {
            var left = Math.Max(start + grip - 1, -2); var right = Math.Min(end - grip + 1, width + 2);
            if (right > left)
            {
                context.DrawLine(_framePen, new Point(left, track.Top + 1), new Point(right, track.Top + 1));
                context.DrawLine(_framePen, new Point(left, track.Bottom - 1), new Point(right, track.Bottom - 1));
            }
        }
        DrawGrip(context, new Rect(start, track.Top, grip, track.Height), true, _activeHandle == Handle.Start);
        DrawGrip(context, new Rect(end - grip, track.Top, grip, track.Height), false, _activeHandle == Handle.End);
    }

    private void DrawGrip(DrawingContext context, Rect rect, bool leading, bool active)
    {
        if (rect.Right < -1 || rect.X > Bounds.Width + 1) return;
        var outer = 8d; var inner = 2d;
        var shape = leading ? new RoundedRect(rect, outer, inner, inner, outer) : new RoundedRect(rect, inner, outer, outer, inner);
        context.DrawRectangle(_warmBrush, null, shape);
        var center = rect.Center;
        var notch = Math.Min(16, rect.Height * .3);
        if (rect.Width >= 6) context.DrawLine(_notchPen, new Point(center.X, center.Y - notch / 2), new Point(center.X, center.Y + notch / 2));
        if (active && IsFocused) context.DrawRectangle(null, _activeGripPen, new RoundedRect(rect.Inflate(2), leading ? outer + 2 : inner + 2, leading ? inner + 2 : outer + 2, leading ? inner + 2 : outer + 2, leading ? outer + 2 : inner + 2));
    }

    private void DrawPlayhead(DrawingContext context, Rect track)
    {
        var x = RatioToX(DisplayedPlayhead());
        if (x < -6 || x > track.Width + 6) return;
        x = Math.Clamp(x, 1, track.Width - 1);
        var knobY = track.Top - 4;
        context.DrawLine(_playheadPen, new Point(x, knobY), new Point(x, track.Bottom - 1));
        context.DrawEllipse(_playheadBrush, _knobPen, new Point(x, knobY), 5.5, 5.5);
    }

    // ===== Accessibility =====

    /// <summary>
    /// Exposes the timeline to UI Automation as a slider: its value is the playhead in seconds and
    /// setting it seeks. The trim range is reported in the help text; the Start/End fields and the
    /// set-at-playhead buttons beside the timeline are the accessible way to change it.
    /// </summary>
    private sealed class TimelineAutomationPeer(WaveformControl owner) : ControlAutomationPeer(owner), IRangeValueProvider
    {
        private WaveformControl Timeline => (WaveformControl)Owner;
        private bool HasDuration => Timeline.Seconds > 0;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(WaveformControl);
        protected override string? GetHelpTextCore()
        {
            var timeline = Timeline;
            var help = base.GetHelpTextCore();
            if (!HasDuration) return help;
            var range = $"Selected from {PreciseTime(Clamp01(timeline.StartRatio) * timeline.Seconds)} to {PreciseTime(Clamp01(timeline.EndRatio) * timeline.Seconds)}.";
            return string.IsNullOrWhiteSpace(help) ? range : range + " " + help;
        }

        public bool IsReadOnly => false;
        public double Minimum => 0;
        public double Maximum => HasDuration ? Timeline.Seconds : 1;
        public double Value => Timeline.DisplayedPlayhead() * (HasDuration ? Timeline.Seconds : 1);
        public double SmallChange => HasDuration ? FineStepSeconds : .002;
        public double LargeChange => HasDuration ? CoarseStepSeconds : .02;

        public void SetValue(double value)
        {
            if (!double.IsFinite(value)) return;
            var ratio = Clamp01(value / Maximum);
            Timeline.RequestSeek(ratio);
            Timeline.RevealRatio(ratio);
        }

        public void RaiseValueChanged(double oldValue, double newValue)
        {
            if (oldValue != newValue) RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);
        }
    }
}
