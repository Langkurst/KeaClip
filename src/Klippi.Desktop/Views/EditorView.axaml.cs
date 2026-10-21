using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Klippi.Core;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Klippi.Desktop.Views;

public sealed partial class EditorView : UserControl
{
    // Content width (after page padding, capped at the page max width) where the export
    // panel moves beside the preview instead of below it.
    private const double WideContentWidth = 940, PagePadding = 72, OutputColumnWidth = 380, ColumnGap = 18;
    // The preview takes the room the window gives it (up to PreviewMaxHeight on big screens) and shrinks
    // (down to PreviewMinHeight) so the transport and the whole trim card stay on screen without scrolling.
    private const double PreviewMinHeight = 228, PreviewMaxHeight = 900, PageTopPadding = 4, BottomBreathingRoom = 10;
    private MainViewModel? _model;
    private (bool Wide, bool HasEditor, bool HasOutput, double Width)? _layout;
    private bool _playbackFrameRequested;
    private double PageMaxWidth => _model?.PageMaxWidth ?? 1040;

    public EditorView()
    {
        InitializeComponent();
        Waveform.SeekRequested += ratio => (DataContext as MainViewModel)?.Seek(ratio);
        DataContextChanged += (_, _) => AttachModel(DataContext as MainViewModel);
        AddHandler(KeyDownEvent, (_, e) => HandleShortcut(e));
        ResultDragHandle.PointerPressed += OnResultDragStart;
        DragDrop.AddDragEnterHandler(DropZone, (_, _) => DropZone.Classes.Set("dragover", true));
        DragDrop.AddDragOverHandler(DropZone, (_, _) => DropZone.Classes.Set("dragover", true));
        DragDrop.AddDragLeaveHandler(DropZone, OnDropZoneLeave);
        // The window performs the actual open; this only clears the highlight and never marks the event handled.
        DragDrop.AddDropHandler(DropZone, (_, _) => DropZone.Classes.Set("dragover", false));
        StartTimeBox.KeyDown += CommitTimecodeOnEnter; StartTimeBox.LostFocus += (_, _) => CommitTimecode(StartTimeBox);
        EndTimeBox.KeyDown += CommitTimecodeOnEnter; EndTimeBox.LostFocus += (_, _) => CommitTimecode(EndTimeBox);
        // Card heights change with wrapping and the selected file; the preview height follows them.
        PreviewCard.SizeChanged += (_, _) => FitPreview();
        TimelineCard.SizeChanged += (_, _) => FitPreview();
    }

    /// <summary>The inline host that owns the native video surface outside fullscreen.</summary>
    public Border PreviewHost => InlinePreviewHost;

    /// <summary>Marks the clip start at the playhead the timeline shows (the single path for buttons and keys).</summary>
    public void MarkStart() { if (_model?.HasEditor == true) Waveform.MarkStart(); }

    /// <summary>Marks the clip end at the playhead the timeline shows (the single path for buttons and keys).</summary>
    public void MarkEnd() { if (_model?.HasEditor == true) Waveform.MarkEnd(); }

    private void ToggleFullscreen(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.TogglePreviewFullscreen();
    private void ZoomTimelineIn(object? sender, RoutedEventArgs e) => Waveform.ZoomIn();
    private void ZoomTimelineOut(object? sender, RoutedEventArgs e) => Waveform.ZoomOut();
    private void ResetTimelineZoom(object? sender, RoutedEventArgs e) => Waveform.ResetZoom();
    private void MarkStartAtPlayhead(object? sender, RoutedEventArgs e) => MarkStart();
    private void MarkEndAtPlayhead(object? sender, RoutedEventArgs e) => MarkEnd();

    private void AttachModel(MainViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        _model = model;
        if (model is not null) model.PropertyChanged += ModelChanged;
        UpdateLayoutMode();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HasEditor) or nameof(MainViewModel.ShowShareCard) or nameof(MainViewModel.PageMaxWidth):
                UpdateLayoutMode();
                break;
            case nameof(MainViewModel.IsPlaying):
                EnsurePlaybackFrames();
                break;
            case nameof(MainViewModel.EditorSession):
                // A newly opened file (even one of the same length, or the same file again) starts unzoomed.
                Waveform.ResetView();
                break;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateLayoutMode();
        FitPreview();
    }

    // Leave room for the transport and the complete trim card below the preview, measured from
    // the cards themselves so text wrapping, scaling and the selected file are all accounted for.
    private void FitPreview()
    {
        var viewport = Bounds.Height;
        if (viewport <= 0 || PreviewCard.Bounds.Height <= 0 || TimelineCard.Bounds.Height <= 0) return;
        var chrome = PreviewCard.Bounds.Height - PreviewFrame.Bounds.Height;
        var available = viewport - PageTopPadding - chrome - ColumnGap - TimelineCard.Bounds.Height - BottomBreathingRoom;
        var height = Math.Clamp(Math.Floor(available), PreviewMinHeight, PreviewMaxHeight);
        if (Math.Abs(PreviewFrame.MaxHeight - height) >= 1) PreviewFrame.MaxHeight = height;
    }

    // Places the columns for the current state. Only runs layout changes when the state flips,
    // so resizing never rebuilds layout per pixel.
    //  - wide (export beside preview) when there is room and something to show on the right;
    //  - narrow with a file: workspace, then output below;
    //  - no file: output (a finished clip or share link) first, then the drop zone.
    private void UpdateLayoutMode()
    {
        var hasEditor = _model?.HasEditor == true;
        var hasOutput = _model?.ShowShareCard == true;
        var content = Math.Min(PageMaxWidth, Bounds.Width - PagePadding);
        var wide = content >= WideContentWidth && hasOutput;
        var layout = (wide, hasEditor, hasOutput, PageMaxWidth);
        if (_layout == layout) return;
        _layout = layout;
        EditorLayout.ColumnDefinitions[1].Width = wide ? new GridLength(OutputColumnWidth) : new GridLength(0);
        EditorLayout.ColumnSpacing = wide ? ColumnGap : 0;
        // Spacing only between two visible rows: a single row must not add an empty gap.
        EditorLayout.RowSpacing = !wide && hasOutput ? ColumnGap : 0;
        var mainRow = !wide && !hasEditor && hasOutput ? 1 : 0;
        Grid.SetRow(StartColumn, mainRow); Grid.SetRow(WorkspaceColumn, mainRow);
        Grid.SetColumn(OutputColumn, wide ? 1 : 0);
        Grid.SetRow(OutputColumn, wide ? 0 : mainRow == 1 ? 0 : 1);
        Classes.Set("wide", wide);
    }

    // While playing, nudge the player once per screen refresh (144 times a second on a 144 Hz screen) so the clock
    // and the fullscreen slider move as smoothly as the waveform's playhead. Stops by itself when playback stops.
    private void EnsurePlaybackFrames()
    {
        if (_playbackFrameRequested || _model is not { IsPlaying: true } || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top) return;
        _playbackFrameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _playbackFrameRequested = false;
            if (_model is not { IsPlaying: true } model) return;
            model.Playback.Tick();
            EnsurePlaybackFrames();
        });
    }

    // Drags the finished clip out as a real file, as Explorer does, so Discord, chats, mail and folders accept it.
    private async void OnResultDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ResultDragHandle).Properties.IsLeftButtonPressed || _model is not { } model || model.LastExportPath is not { } path || !File.Exists(path)) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            if (await storage.TryGetFileFromPathAsync(Path.GetFullPath(path)) is not { } file) return;
            var data = new DataTransfer(); data.Add(DataTransferItem.CreateFile(file));
            model.DraggingOutPath = path;
            try { await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy); }
            finally { model.DraggingOutPath = null; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.Status = "The clip could not be dragged. Use Copy clip instead.";
        }
    }

    private void OnDropZoneLeave(object? sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between children; keep the highlight while still inside.
        var point = e.GetPosition(DropZone);
        if (!new Rect(DropZone.Bounds.Size).Contains(point)) DropZone.Classes.Set("dragover", false);
    }

    /// <summary>
    /// Editor shortcuts (the timeline handles its own keys first): I / O mark at the playhead,
    /// + / − zoom the timeline and Left / Right step the playhead by the timeline's 0.1 s (Shift: 1 s);
    /// F11 is ignored for audio-only media. Text entry and controls that use these keys keep them.
    /// Runs for keys raised inside the page; the window can forward other keys while the editor is shown.
    /// </summary>
    public void HandleShortcut(KeyEventArgs e)
    {
        if (e.Handled || _model is not { HasEditor: true } model) return;
        var plain = e.KeyModifiers == KeyModifiers.None;
        if (e.Key == Key.F11 && plain && model.IsAudioOnlyMedia && !model.PreviewFullscreen)
        {
            // Audio has no picture: fullscreen would only show an empty black window.
            e.Handled = true;
            return;
        }
        var source = e.Source as Visual;
        if (source is not null && source.GetSelfAndVisualAncestors().Any(v => v is TextBox or ComboBox or NumericUpDown)) return;
        switch (e.Key)
        {
            case Key.I when plain: MarkStart(); break;
            case Key.O when plain: MarkEnd(); break;
            case Key.OemPlus or Key.Add when plain || e.KeyModifiers == KeyModifiers.Shift: Waveform.ZoomIn(); break;
            case Key.OemMinus or Key.Subtract when plain: Waveform.ZoomOut(); break;
            case Key.Left or Key.Right when (plain || e.KeyModifiers == KeyModifiers.Shift) && !UsesArrowKeys(source):
                Waveform.StepPlayhead(e.Key == Key.Left ? -1 : 1, fine: plain);
                break;
            default: return;
        }
        e.Handled = true;
    }

    // Sliders, switches, tiles and expander headers move or change with arrow keys; plain buttons do not.
    private static bool UsesArrowKeys(Visual? source)
        => source is not null && source.GetSelfAndVisualAncestors().Any(v => v is Slider or ToggleSwitch or ToggleButton or ScrollBar);

    // Timecodes commit on focus loss (so typing never makes the trim handles jump); Enter commits too.
    private void CommitTimecodeOnEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        CommitTimecode(box);
        box.SelectAll();
        e.Handled = true;
    }

    // The fields show tenths of a second; leaving one unchanged must not round the precise time behind it.
    private void CommitTimecode(TextBox box)
    {
        if (_model is null) return;
        var current = ReferenceEquals(box, StartTimeBox) ? _model.StartTime : _model.EndTime;
        if (string.Equals(box.Text, EditorTimeConverter.Shown(current), StringComparison.Ordinal)) return;
        BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
    }
}

/// <summary>
/// Trim fields show m:ss.t (h:mm:ss.t for long files): tenths are precise enough to read and type, while the value
/// behind them keeps full precision. Anything typed goes back as typed; the view model parses and checks it.
/// </summary>
public sealed class EditorTimeConverter : IValueConverter
{
    public static string Shown(string? timecode) => MainViewModel.TrimFieldText(timecode);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Shown(value as string);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value as string ?? "";
}

/// <summary>
/// Two-way equality converter for segmented tiles: checked when the bound value matches
/// <c>ConverterParameter</c> (compared as text so enum names and True/False work in XAML);
/// checking a tile writes the parameter back, unchecking leaves the source unchanged.
/// </summary>
public sealed class EditorChoiceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter?.ToString() is not { } text) return BindingOperations.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type.IsEnum) return Enum.TryParse(type, text, true, out var choice) ? choice : BindingOperations.DoNothing;
        if (type == typeof(bool)) return bool.TryParse(text, out var flag) ? flag : BindingOperations.DoNothing;
        return type == typeof(string) || type == typeof(object) ? text : BindingOperations.DoNothing;
    }
}

/// <summary>
/// Messaging presets: the "no preset" entry reads as such (a size typed by hand, or compression
/// without a size target), instead of suggesting a custom target is set.
/// </summary>
public sealed class EditorPresetLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value as string == "Custom target" ? "No preset" : value?.ToString() ?? "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
