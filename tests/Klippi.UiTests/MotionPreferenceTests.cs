using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

public sealed class MotionPreferenceTests
{
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, null, true)]
    public async Task System_and_manual_preferences_combine_without_overwriting_saved_choice(bool manual, bool? system, bool effective)
    {
        var fake = new UiFakes();
        fake.Platform.SystemReducedMotion = system;
        fake.Store.Settings = fake.Store.Settings with { ReducedMotion = manual };
        using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        Assert.Equal(system, model.SystemReducedMotion);
        Assert.Equal(effective, model.EffectiveReducedMotion);
        Assert.Equal(manual, model.ReducedMotion);
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(manual, fake.Store.Settings.ReducedMotion);
        Assert.Empty(fake.Platform.StartupWrites);
    }

    [AvaloniaFact]
    public async Task Motion_refresh_updates_busy_and_queue_indicators_immediately()
    {
        var fake = new UiFakes();
        fake.Platform.SystemReducedMotion = false;
        fake.Downloads.WaitForCancellation = true;
        fake.Downloads.IndeterminateProgress = true;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new FakeTemporaryWorkspaces { Handler = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(); } };
        using var model = new MainViewModel(fake.Services with { TemporaryWorkspaces = cleanup }) { Url = UiFakes.Info.Url.AbsoluteUri };
        await model.InitializeAsync();
        await model.QueueDownloadAsync(false);
        await Until(() => fake.Downloads.Requests.Count == 1 && model.Queue.Count == 1 && model.Queue[0].IsIndeterminate);
        var operation = model.CleanTemporaryFilesCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.AnimateBusyIndicator);
        fake.Platform.SystemReducedMotion = true;
        model.RefreshPlatformPreferences();
        Assert.True(model.EffectiveReducedMotion);
        Assert.False(model.AnimateBusyIndicator);
        Assert.False(model.Queue[0].IsIndeterminate);
        Assert.Contains("animations turned off", model.MotionPreferenceStatus);
        model.ReducedMotion = true;
        fake.Platform.SystemReducedMotion = false;
        model.RefreshPlatformPreferences();
        Assert.True(model.EffectiveReducedMotion);
        Assert.False(model.AnimateBusyIndicator);
        Assert.False(model.Queue[0].IsIndeterminate);
        model.ReducedMotion = false;
        Assert.True(model.AnimateBusyIndicator);
        Assert.True(model.Queue[0].IsIndeterminate);
        model.CancelOperationCommand.Execute(null);
        model.Queue[0].CancelCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await model.ShutdownAsync();
    }

    [AvaloniaFact]
    public void Attaching_a_view_refreshes_motion_without_saving_or_registering_anything()
    {
        var fake = new UiFakes(); fake.Platform.SystemReducedMotion = true;
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model };
        Assert.True(fake.Platform.PreferenceReads > 0);
        Assert.True(model.EffectiveReducedMotion);
        Assert.False(model.ReducedMotion);
        Assert.False(fake.Store.Settings.ReducedMotion);
        Assert.Empty(fake.Platform.StartupWrites);
        window.Close();
    }

    [Fact]
    public void Native_motion_query_is_read_only_and_supported_on_windows()
    {
        var reducedMotion = WindowsMotionPreference.ReadReducedMotion();
        if (OperatingSystem.IsWindows()) Assert.NotNull(reducedMotion);
        else Assert.Null(reducedMotion);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
