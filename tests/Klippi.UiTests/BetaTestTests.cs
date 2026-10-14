using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Beta builds: the first start opens a short checklist, answers and notes survive a restart, and Save report writes a
// plain-text file with personal paths removed. Public builds show none of it.
public sealed class BetaTestTests
{
    private static (DesktopServices Services, string Root) BetaServices(bool beta = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "keaclip-beta-" + Guid.NewGuid().ToString("N"));
        return (new UiFakes().Services with { DataRoot = Path.Combine(root, "data"), BetaTest = beta, ReportsRoot = Path.Combine(root, "desktop") }, root);
    }

    [AvaloniaFact]
    public async Task A_public_build_has_no_beta_test()
    {
        var (services, root) = BetaServices(beta: false);
        try
        {
            using var model = new MainViewModel(services);
            await model.InitializeAsync();
            Assert.False(model.IsBeta);
            Assert.Equal(0, model.SelectedTab);
            Assert.DoesNotContain("beta", model.AppVersion);
            Assert.False(File.Exists(Path.Combine(services.DataRoot, "beta-test.json")));
            var window = new MainWindow { DataContext = model, Width = 1100, Height = 760 };
            window.Show();
            try { Settle(window); Assert.False(window.FindControl<Button>("BetaTestButton")!.IsVisible); }
            finally { model.CloseBehavior = Klippi.Core.CloseBehavior.Exit; window.Close(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task The_first_beta_start_opens_the_checklist_and_answers_survive_a_restart()
    {
        var (services, root) = BetaServices();
        try
        {
            using (var first = new MainViewModel(services))
            {
                await first.InitializeAsync();
                Assert.True(first.IsBetaTest);
                Assert.Equal("Beta test", first.PageTitle);
                Assert.EndsWith(" · beta", first.AppVersion);
                Assert.Equal(6, first.BetaChecks.Count);
                first.BetaChecks[0].MarkWorksCommand.Execute(null);
                first.BetaChecks[0].MarkWorksCommand.Execute(null); // pressing the chosen answer again clears it
                first.BetaChecks[1].MarkWorksCommand.Execute(null);
                first.BetaChecks[2].MarkProblemCommand.Execute(null);
                first.BetaNotes = "Sound stutters";
                Assert.Equal("2 of 6 tried", first.BetaProgress);
                Assert.Equal("2/6", first.BetaProgressShort);
            }
            using var second = new MainViewModel(services);
            await second.InitializeAsync();
            Assert.Equal(0, second.SelectedTab); // only the first start opens the checklist
            Assert.Equal([BetaResult.NotTried, BetaResult.Works, BetaResult.Problem], second.BetaChecks.Take(3).Select(check => check.Result));
            Assert.Equal("Sound stutters", second.BetaNotes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Save_report_writes_readable_answers_without_personal_paths()
    {
        var (services, root) = BetaServices();
        try
        {
            using var model = new MainViewModel(services) { DisplayScaling = 1.5, ScreenSize = "2560x1440" };
            await model.InitializeAsync();
            model.BetaChecks[1].MarkWorksCommand.Execute(null);
            model.BetaChecks[2].MarkProblemCommand.Execute(null);
            model.BetaNotes = @"Froze after 3 s playing C:\Users\Sam\Videos\holiday.mp4";
            await model.SaveBetaReportCommand.ExecuteAsync(null);

            Assert.True(model.HasBetaReport);
            var report = Assert.Single(Directory.GetFiles(services.ReportsRoot!, "KeaClip beta report *.txt"));
            var text = await File.ReadAllTextAsync(report);
            Assert.Contains("1. Let it set up: Not tried", text);
            Assert.Contains("2. Save a video: Works", text);
            Assert.Contains("3. Play it: PROBLEM", text);
            Assert.Contains("Froze after 3 s playing", text); // a path in a note is removed with the rest of its line
            Assert.Contains("Display scaling: 150%, screen 2560x1440", text);
            Assert.Contains("KeaClip 0.", text);
            Assert.DoesNotContain(@"C:\Users\Sam", text);
            Assert.DoesNotContain("holiday.mp4", text);
            Assert.Contains("keaclip@proton.me", model.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task The_beta_page_and_sidebar_entry_render(string theme)
    {
        var (services, root) = BetaServices();
        try
        {
            using var model = new MainViewModel(services) { ReducedMotion = true };
            await model.InitializeAsync();
            model.ReducedMotion = true;
            model.Theme = theme;
            model.BetaChecks[0].MarkWorksCommand.Execute(null);
            model.BetaChecks[2].MarkProblemCommand.Execute(null);
            var window = new MainWindow { DataContext = model, Width = 1180, Height = 860 };
            window.Show();
            try
            {
                Settle(window);
                var button = window.FindControl<Button>("BetaTestButton")!;
                Assert.True(button.IsVisible);
                Assert.Contains("selected", button.Classes);
                Assert.False(window.FindControl<Border>("NavIndicator")!.IsVisible);
                Assert.True(window.FindControl<Control>("BetaTestPage")!.IsVisible);
                Assert.Equal("2/6", window.FindControl<TextBlock>("BetaProgressShortText")!.Text);
                await Capture(window, "beta-test-" + theme.ToLowerInvariant());
                model.SelectedTab = 0; Settle(window);
                Assert.DoesNotContain("selected", button.Classes);
                Assert.True(window.FindControl<Border>("NavIndicator")!.IsVisible);
            }
            finally { model.CloseBehavior = Klippi.Core.CloseBehavior.Exit; window.Close(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Settle(Window window) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static async Task Capture(Window window, string name)
    {
        window.UpdateLayout(); await Task.Delay(260); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
