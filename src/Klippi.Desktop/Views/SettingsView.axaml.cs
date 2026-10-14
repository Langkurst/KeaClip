using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Klippi.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => (DataContext as MainViewModel)?.ObserveSettingsChanges();
        KeyDown += OnSettingsKeyDown;
    }

    // Ctrl+S saves while focus is anywhere on the settings page.
    private void OnSettingsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.S || e.KeyModifiers != KeyModifiers.Control || DataContext is not MainViewModel model) return;
        e.Handled = true;
        if (model.SaveSettingsChangesCommand.CanExecute(null)) model.SaveSettingsChangesCommand.Execute(null);
    }

    private void OnReviewSettingClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { FirstSettingNeedingAttention: { } name } model) RevealSetting(model, name);
    }

    /// <summary>Scrolls the given setting into view with some context around it and moves focus to it.</summary>
    public bool RevealSetting(MainViewModel model, string settingName)
    {
        var target = settingName switch
        {
            nameof(MainViewModel.DownloadDirectory) => "DownloadFolderBox",
            nameof(MainViewModel.AudioDirectory) => "AudioFolderBox",
            nameof(MainViewModel.ClipsDirectory) => "ClipsFolderBox",
            nameof(MainViewModel.ArchivesDirectory) => "ArchivesFolderBox",
            nameof(MainViewModel.StartWithWindows) => "StartWithWindowsSwitch",
            nameof(MainViewModel.RetentionDays) => model.IsCustomRetention ? "RetentionDaysBox" : "RetentionPicker",
            nameof(MainViewModel.SoundboardSeconds) => "SoundboardSecondsBox",
            nameof(MainViewModel.SoundboardBytes) => "SoundboardBytesBox",
            nameof(MainViewModel.Theme) => "ThemePicker",
            nameof(MainViewModel.ReducedMotion) => "ReducedMotionSwitch",
            nameof(MainViewModel.ConcurrentDownloads) => "ConcurrentDownloadsBox",
            nameof(MainViewModel.DetectClipboard) => "DetectClipboardSwitch",
            nameof(MainViewModel.CloseBehavior) => "CloseBehaviorPicker",
            nameof(MainViewModel.StartMinimized) => "StartMinimizedSwitch",
            nameof(MainViewModel.NotifyCompleted) => "NotifyCompletedSwitch",
            nameof(MainViewModel.CheckForUpdates) => "CheckForUpdatesSwitch",
            MainViewModel.UpdateSectionKey => model.CanUpdateNow ? "UpdateNowButton" : "CheckForUpdatesButton",
            _ => null,
        };
        if (target is null) return false;
        if (target is "AudioFolderBox" or "ClipsFolderBox" or "ArchivesFolderBox") model.SeparateFoldersExpanded = true;
        if (target is "StartWithWindowsSwitch" or "RetentionDaysBox" or "RetentionPicker" or "SoundboardSecondsBox" or "SoundboardBytesBox" or "CloseBehaviorPicker" or "StartMinimizedSwitch" or "NotifyCompletedSwitch") model.ShowAdvancedSettings = true;
        UpdateLayout();
        if (this.FindControl<Control>(target) is not { } control) return false;
        control.BringIntoView(new Rect(0, -72, control.Bounds.Width, control.Bounds.Height + 144));
        // A group of tiles focuses its chosen tile; a field or switch focuses itself.
        var candidates = control.GetVisualDescendants().OfType<InputElement>().Where(element => element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible).ToArray();
        InputElement? focusable = control.Focusable ? control : candidates.OfType<RadioButton>().FirstOrDefault(radio => radio.IsChecked == true) ?? candidates.FirstOrDefault();
        focusable?.Focus(NavigationMethod.Tab);
        return true;
    }
}

/// <summary>
/// Two-way equality for segmented RadioButton tiles: checked when the bound value matches
/// ConverterParameter; checking a tile writes the parameter back (parsed for enums), and
/// unchecking writes nothing so the newly checked tile wins.
/// </summary>
public sealed class SettingsEqualityConverter : IValueConverter
{
    public static readonly SettingsEqualityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter?.ToString() is not { Length: > 0 } text) return BindingOperations.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum ? Enum.Parse(type, text) : text;
    }
}

/// <summary>Looks up an application resource (for example an icon geometry) by the key a row exposes.</summary>
public sealed class SettingsResourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key && Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var resource) == true ? resource : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
