using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

public sealed class ShellBehaviourTests
{
    [AvaloniaFact]
    public async Task Player_shortcuts_still_work_after_a_button_has_focus_and_do_not_click_it()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        var window = new MainWindow { DataContext = model }; window.Show(); window.UpdateLayout();
        try
        {
            var stop = window.GetLogicalDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, model.StopPlaybackCommand));
            stop.Focus();
            Assert.True(stop.IsFocused, "focus"); Assert.True(model.IsEditor, "editor tab");
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(fake.Playback.IsPlaying, "after key down");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(fake.Playback.IsPlaying); // Space reached the player, not the focused Stop button.
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Startup_entry_carries_minimized_only_when_start_minimized_is_on()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.StartWithWindows = true; model.StartMinimized = false; await model.SaveSettingsAsync();
        Assert.Equal((true, false), fake.Platform.StartupEntries.Last());
        model.StartMinimized = true; await model.SaveSettingsAsync();
        Assert.Equal((true, true), fake.Platform.StartupEntries.Last());
        var writes = fake.Platform.StartupEntries.Count; await model.SaveSettingsAsync();
        Assert.Equal(writes, fake.Platform.StartupEntries.Count); // Unchanged settings do not rewrite the entry.
    }
}
