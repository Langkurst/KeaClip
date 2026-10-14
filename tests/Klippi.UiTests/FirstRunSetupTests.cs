using Avalonia.Headless.XUnit;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// A real copy's first start fetches the media tools by itself (the setup card shows progress); with the tools in
// place, or in builds that do not ask for it (tests, tools), nothing starts.
public sealed class FirstRunSetupTests
{
    [AvaloniaFact]
    public async Task Missing_tools_start_setup_by_itself_once()
    {
        var fake = new UiFakes(); fake.Components.Ready = false;
        using var model = new MainViewModel(fake.Services with { AutoSetup = true });
        await model.InitializeAsync();
        await Until(() => fake.Components.Provisions > 0 && !model.IsComponentTaskRunning);
        Assert.Equal(1, fake.Components.Provisions);
    }

    [AvaloniaTheory]
    [InlineData(true, true)]   // tools already there
    [InlineData(false, false)] // tests and tools never fetch by themselves
    public async Task Setup_waits_when_tools_are_ready_or_auto_setup_is_off(bool ready, bool autoSetup)
    {
        var fake = new UiFakes(); fake.Components.Ready = ready;
        using var model = new MainViewModel(fake.Services with { AutoSetup = autoSetup });
        await model.InitializeAsync();
        for (var i = 0; i < 20; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        Assert.Equal(0, fake.Components.Provisions);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(condition());
    }
}
