using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

public sealed class TemporaryCleanupUiTests
{
    [AvaloniaFact]
    public async Task Cleanup_is_manual_and_the_settings_action_reports_kept_folders()
    {
        var fake = new UiFakes();
        var cleanup = new FakeTemporaryWorkspaces
        {
            Result = new() { DeletedFolders = 2, DeletedBytes = 1_500_000, ActiveFolders = 1, RecoveryFolders = 3, SkippedFolders = 4 }
        };
        using var model = new MainViewModel(fake.Services with { TemporaryWorkspaces = cleanup });
        await model.InitializeAsync();
        model.Url = UiFakes.Info.Url.AbsoluteUri;
        await model.AnalyseAsync();
        await model.OpenEditorAsync("fixture.mp4");
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(0, cleanup.Calls);
        model.SelectedTab = 4;
        var window = new MainWindow { DataContext = model }; window.Show();
        try
        {
            // Reduced motion: the Advanced panel would otherwise still be sliding in when the button is clicked.
            model.ReducedMotion = true; model.ShowAdvancedSettings = true; window.UpdateLayout();
            var button = window.FindNamed<Button>("CleanTemporaryFilesButton")!;
            Assert.True(button.IsEnabled);
            window.UpdateLayout(); button.BringIntoView(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var position = button.TranslatePoint(new(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            Assert.InRange(position.Y, 0, window.Bounds.Height);
            window.MouseDown(position, MouseButton.Left); window.MouseUp(position, MouseButton.Left);
            if (model.CleanTemporaryFilesCommand.ExecutionTask is { } operation) await operation;
            Assert.Equal(1, cleanup.Calls);
            Assert.Contains("Removed 2 folders (1.5 MB)", model.TemporaryCleanupStatus);
            Assert.Contains("1 active, 3 recovery and 4 unverified", model.TemporaryCleanupStatus);
            Assert.Equal(model.TemporaryCleanupStatus, window.FindNamed<TextBlock>("TemporaryCleanupStatusText")!.Text);
            Assert.False(model.IsBusy);
            await model.ShutdownAsync();
            Assert.Equal(1, cleanup.Calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_action_reaches_cleanup_and_never_reports_completion(bool partialResult)
    {
        var fake = new UiFakes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new FakeTemporaryWorkspaces
        {
            Handler = async cancellationToken =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) when (partialResult)
                {
                    return new() { DeletedFolders = 1, DeletedBytes = 250_000, ActiveFolders = 2, RecoveryFolders = 3, SkippedFolders = 4, Cancelled = true };
                }
                throw new InvalidOperationException("Cancellation must interrupt the operation.");
            }
        };
        using var model = new MainViewModel(fake.Services with { TemporaryWorkspaces = cleanup });
        var operation = model.CleanTemporaryFilesCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.IsBusy);
        Assert.False(model.CanCleanTemporaryFiles);
        Assert.False(model.CleanTemporaryFilesCommand.CanExecute(null));
        model.CancelOperationCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleanup.LastToken.IsCancellationRequested);
        Assert.Contains("cleanup cancelled", model.TemporaryCleanupStatus);
        Assert.DoesNotContain("cleanup finished", model.Status);
        if (partialResult) { Assert.Contains("Removed 1 folder", model.TemporaryCleanupStatus); Assert.Contains("2 active, 3 recovery and 4 unverified", model.TemporaryCleanupStatus); }
        else Assert.Contains("may already have been removed", model.TemporaryCleanupStatus);
        Assert.False(model.IsBusy);
        Assert.True(model.CanCleanTemporaryFiles);
    }

    [AvaloniaFact]
    public async Task Cleanup_failure_reports_incomplete_work_and_redacts_diagnostics()
    {
        var cleanup = new FakeTemporaryWorkspaces { Handler = _ => Task.FromException<TemporaryCleanupResult>(new IOException(@"Could not access C:\private\browser-profile\media; token=secret-value")) };
        using var model = new MainViewModel(new UiFakes().Services with { TemporaryWorkspaces = cleanup });
        await model.CleanTemporaryFilesCommand.ExecuteAsync(null);
        Assert.Contains("cleanup could not finish", model.TemporaryCleanupStatus);
        Assert.Contains("may already have been removed", model.Status);
        Assert.DoesNotContain("cleanup finished", model.Status);
        Assert.DoesNotContain("browser-profile", model.Diagnostics);
        Assert.DoesNotContain("secret-value", model.Diagnostics);
        Assert.False(model.IsBusy);
    }

    [AvaloniaFact]
    public async Task Cleanup_warnings_are_bounded_and_private_paths_and_tokens_are_removed()
    {
        var cleanup = new FakeTemporaryWorkspaces
        {
            Result = new() { SkippedFolders = 8, Warnings = Enumerable.Range(1, 8).Select(index => @"Kept C:\private\browser-profile\media; token=secret-value " + new string('x', 300) + index).ToArray() }
        };
        using var model = new MainViewModel(new UiFakes().Services with { TemporaryWorkspaces = cleanup });
        await model.CleanTemporaryFilesCommand.ExecuteAsync(null);
        Assert.Contains("8 unverified", model.TemporaryCleanupStatus);
        Assert.Contains("warning details are limited", model.TemporaryCleanupStatus);
        Assert.DoesNotContain("browser-profile", model.TemporaryCleanupStatus);
        Assert.DoesNotContain("secret-value", model.TemporaryCleanupStatus);
        Assert.True(model.TemporaryCleanupStatus.Length < 1600);
    }

    [AvaloniaFact]
    public async Task Optional_missing_cleanup_service_is_disabled_and_reports_unavailability()
    {
        using var model = new MainViewModel(new UiFakes().Services) { SelectedTab = 4 };
        Assert.False(model.CanCleanTemporaryFiles);
        Assert.False(model.CleanTemporaryFilesCommand.CanExecute(null));
        await model.CleanTemporaryFilesAsync();
        Assert.Contains("unavailable", model.TemporaryCleanupStatus);
    }
}

internal sealed class FakeTemporaryWorkspaces : ITemporaryWorkspaceService
{
    public int Calls { get; private set; }
    public CancellationToken LastToken { get; private set; }
    public TemporaryCleanupResult Result { get; init; } = new();
    public Func<CancellationToken, Task<TemporaryCleanupResult>>? Handler { get; init; }
    public ITemporaryWorkspace Create(string parent) => throw new InvalidOperationException("The settings cleanup action must never create a workspace.");
    public Task<TemporaryCleanupResult> CleanAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        LastToken = cancellationToken;
        return Handler is null ? Task.FromResult(Result) : Handler(cancellationToken);
    }
}
