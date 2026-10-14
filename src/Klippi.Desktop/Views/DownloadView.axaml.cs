using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klippi.Core;

namespace Klippi.Desktop.Views;

public sealed partial class DownloadView : UserControl
{
    // Content widths at which the preview thumbnail grows (.wide) and the four mode tiles
    // fit side by side (.roomy), and the action-bar width below which Download & edit shows
    // only its icon and the folder action its short label (.compact), so the path keeps room.
    private const double WideLayoutWidth = 760, RoomyLayoutWidth = 840, CompactBarWidth = 860;
    private MainViewModel? _model;

    public DownloadView()
    {
        InitializeComponent();
        // Tunnel so Enter is seen before the TextBox; Enter never inserts text in this single-line field.
        UrlInput.AddHandler(KeyDownEvent, OnUrlKeyDown, RoutingStrategies.Tunnel);
        UrlInput.LostFocus += (_, _) => (DataContext as MainViewModel)?.CompleteLinkOnLeave();
        PageRoot.SizeChanged += (_, e) =>
        {
            PageRoot.Classes.Set("wide", e.NewSize.Width >= WideLayoutWidth);
            PageRoot.Classes.Set("roomy", e.NewSize.Width >= RoomyLayoutWidth);
        };
        ActionBar.SizeChanged += (_, e) => ActionBar.Classes.Set("compact", e.NewSize.Width < CompactBarWidth);
        // Text never runs under the inline Paste/Analyse buttons, whichever of them are showing.
        LinkActions.SizeChanged += (_, e) => UrlInput.Padding = new Thickness(UrlInput.Padding.Left, 0, e.NewSize.Width + 14, 0);
        // The mode tiles are one radio group: one Tab stop, arrow keys move the choice.
        ModeTiles.AddHandler(KeyDownEvent, OnModeTilesKeyDown, RoutingStrategies.Bubble);
    }

    private RadioButton[] ModeTileOrder => [VideoAudioTile, AudioTile, VideoOnlyTile, ArchiveTile];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        (DataContext as MainViewModel)?.AttachDownloadPresentation();
        Observe(VisualRoot is null ? null : DataContext as MainViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Observe(DataContext as MainViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Observe(null);
    }

    // The view only listens while it is on screen, so a closed window never keeps a handler alive.
    private void Observe(MainViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        _model = model;
        if (model is null) return;
        model.AttachDownloadPresentation();
        model.PropertyChanged += ModelChanged;
        UpdateTabStop();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Choices that need attention appear at the end of the page; bring them into view.
        if (e.PropertyName == nameof(MainViewModel.HasDuplicates) && _model is { HasDuplicates: true })
            Dispatcher.UIThread.Post(() => DuplicatesCard.BringIntoView(), DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(MainViewModel.DownloadMode))
            UpdateTabStop();
    }

    // Tab enters the tile group on the chosen mode, like any radio group.
    private void UpdateTabStop() => KeyboardNavigation.SetTabOnceActiveElement(ModeTiles, ModeTileOrder.FirstOrDefault(tile => tile.IsChecked == true) ?? VideoAudioTile);

    private void OnModeTilesKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (step == 0 || e.KeyModifiers != KeyModifiers.None || (e.Source as Visual)?.FindAncestorOfType<RadioButton>(includeSelf: true) is not { } current) return;
        var tiles = ModeTileOrder;
        var index = Array.IndexOf(tiles, current);
        if (index < 0) return;
        var next = tiles[(index + step + tiles.Length) % tiles.Length];
        next.IsChecked = true;
        next.Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || DataContext is not MainViewModel { CanCheckLink: true } model) return;
        e.Handled = true;
        if (model.PrepareLinkForAction() && model.AnalyseCommand.CanExecute(null)) model.AnalyseCommand.Execute(null);
    }

    // Analyse, Download and Download & edit first complete a link typed without https://;
    // text that cannot be a link is explained under the field instead of being sent anywhere.
    private void OnLinkAction(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && !model.PrepareLinkForAction()) e.Handled = true;
    }

    // The container, codec and audio-format choices are plain properties; their pickers write
    // them first, so the conflict and bitrate state never lag one selection behind.
    private void OnFormatPickerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (sender == ContainerPicker && ContainerPicker.SelectedItem is MediaContainer container) model.Container = container;
        else if (sender == VideoCodecPicker && VideoCodecPicker.SelectedItem is VideoCodec codec) model.VideoCodec = codec;
        else if (sender == AudioFormatPicker && AudioFormatPicker.SelectedItem is AudioFormat audio) model.AudioFormat = audio;
        model.RefreshFormatChoice();
    }

    // Sign-in problems point straight at the opt-in browser choice inside More options.
    private void OnChooseBrowser(object? sender, RoutedEventArgs e)
    {
        MoreOptions.IsExpanded = true;
        Dispatcher.UIThread.Post(() => { BrowserPicker.BringIntoView(); BrowserPicker.Focus(NavigationMethod.Tab); }, DispatcherPriority.Loaded);
    }
}

/// <summary>Small one-purpose converters for the download page.</summary>
public static class DownloadConverters
{
    private static readonly Dictionary<int, string> BitrateNames = new() { [320] = "Highest", [256] = "High", [192] = "Standard", [128] = "Compact", [96] = "Tiny" };

    public static readonly IValueConverter BitrateLabel = new FuncValueConverter<object?, string>(value =>
        value is int kbps ? BitrateNames.TryGetValue(kbps, out var name) ? $"{kbps} kbps · {name}" : $"{kbps} kbps" : "");

    public static readonly IValueConverter QualityLabel = new FuncValueConverter<object?, string>(value => value switch
    {
        "Smallest available" => "Smallest file",
        string text when text.EndsWith("p maximum", StringComparison.Ordinal) && int.TryParse(text.AsSpan(0, text.IndexOf('p')), NumberStyles.None, CultureInfo.InvariantCulture, out var height) => height switch
        {
            >= 4320 => $"8K · {height}p",
            >= 2160 => $"4K · {height}p",
            _ => height + "p",
        },
        string text => text,
        _ => "",
    });

    public static readonly IValueConverter FrameRateLabel = new FuncValueConverter<object?, string>(value => value is string text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var fps) ? $"Up to {fps} fps" : "Keep original");

    public static readonly IValueConverter EntryDuration = new FuncValueConverter<TimeSpan?, string>(value => value is { } time && time > TimeSpan.Zero ? MainViewModel.ClockText(time) : "");
}
