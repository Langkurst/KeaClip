using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Feedback opens a ready-made report or idea on GitHub, or an email; KeaClip itself sends nothing.
public sealed class FeedbackTests
{
    [AvaloniaFact]
    public async Task Problem_reports_carry_versions_and_never_personal_paths()
    {
        var fake = new UiFakes();
        fake.Downloads.AnalysisFailure = new InvalidOperationException(@"Access to C:\Users\Sam\Videos\holiday.mp4 was denied");
        using var model = new MainViewModel(fake.Services) { DisplayScaling = 1.25, ScreenSize = "1920x1080" };
        await model.InitializeAsync();
        model.Url = "https://example.com/video"; await model.AnalyseCommand.ExecuteAsync(null);

        var details = model.BugReportDetails();
        Assert.Contains("KeaClip " + AppIdentity.Version, details);
        Assert.Contains("Screen scaling: 125%, 1920x1080", details);
        Assert.DoesNotContain(@"C:\Users\Sam", details);

        var github = model.BugReportGitHubUri();
        Assert.StartsWith(MainViewModel.IssuesUrl + "?labels=bug", github.AbsoluteUri);
        Assert.DoesNotContain("Sam", Uri.UnescapeDataString(github.Query));
        Assert.True(github.AbsoluteUri.Length < 8000);
        var email = model.BugReportEmailUri();
        Assert.Equal("mailto", email.Scheme);
        Assert.StartsWith("mailto:keaclip@proton.me?subject=", email.OriginalString);
        Assert.Contains("labels=idea", MainViewModel.IdeaGitHubUri().AbsoluteUri);
    }

    [AvaloniaFact]
    public void The_feedback_menu_opens_each_choice()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 800 };
        window.Show();
        try
        {
            var button = window.FindControl<Button>("FeedbackButton")!;
            Assert.True(button.IsVisible);
            var menu = Assert.IsType<MenuFlyout>(button.Flyout);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(["Report a problem", "Suggest an idea", "Email keaclip@proton.me"], items.Select(item => item.Header as string));
            model.ReportOnGitHubCommand.Execute(null); model.SuggestIdeaCommand.Execute(null); model.ReportByEmailCommand.Execute(null);
            Assert.Equal(["https", "https", "mailto"], fake.Platform.OpenedUris.Select(uri => uri.Scheme));
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }
}
