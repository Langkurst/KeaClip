using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

// Feedback: a ready-made problem report or idea on GitHub (for people with an account), or an email to KeaClip.
// Nothing is sent by KeaClip itself: the person sees the report and sends it, so "no tracking" stays true.
public sealed partial class MainViewModel
{
    public const string IssuesUrl = "https://github.com/Langkurst/KeaClip/issues/new";
    // Browsers and mail apps cut long links; the details stay well under that.
    private const int MaxReportDetails = 1800;
    private IRelayCommand? _reportOnGitHub, _suggestIdea;
    private IAsyncRelayCommand? _reportByEmail;

    public IRelayCommand ReportOnGitHubCommand => _reportOnGitHub ??= new RelayCommand(() => TryAction(() => _services.Platform.OpenUri(BugReportGitHubUri())));
    // The address is always copied first: plenty of PCs have no email app, and then the link opens nothing.
    public IAsyncRelayCommand ReportByEmailCommand => _reportByEmail ??= new AsyncRelayCommand(async () =>
    {
        await CopyToClipboardAsync(AppIdentity.ContactEmail, AppIdentity.ContactEmail + " is copied. If no email app opened, paste it into your email (Gmail, Outlook and so on).");
        TryAction(() => _services.Platform.OpenUri(BugReportEmailUri()));
    });
    public IRelayCommand SuggestIdeaCommand => _suggestIdea ??= new RelayCommand(() => TryAction(() => _services.Platform.OpenUri(IdeaGitHubUri())));

    public static Uri IdeaGitHubUri() => new(IssuesUrl + "?labels=idea&title=" + Uri.EscapeDataString("Idea: ")
        + "&body=" + Uri.EscapeDataString("**What would you like KeaClip to do?**\n\n\n**Why would it help?**\n"));

    /// <summary>App, Windows and tool versions, screen scaling and the last error, with personal paths removed.</summary>
    public string BugReportDetails()
    {
        var details = new StringBuilder()
            .AppendLine(VersionDiagnostics)
            .AppendLine("Screen scaling: " + Math.Round(DisplayScaling * 100).ToString(CultureInfo.InvariantCulture) + "%" + (ScreenSize.Length > 0 ? ", " + ScreenSize : ""));
        if (HasRecordedError) details.AppendLine("Last error:").AppendLine(Diagnostics);
        var text = MediaValidation.RedactDiagnostics(details.ToString()).Trim();
        return text.Length <= MaxReportDetails ? text : text[..MaxReportDetails] + "…";
    }

    public Uri BugReportGitHubUri()
    {
        var body = "**What happened?**\n\n\n**What did you expect?**\n\n\n**Details (added by KeaClip; personal folder names removed)**\n```\n" + BugReportDetails() + "\n```\n";
        return new Uri(IssuesUrl + "?labels=bug&title=" + Uri.EscapeDataString("Bug: ") + "&body=" + Uri.EscapeDataString(body));
    }

    public Uri BugReportEmailUri()
    {
        var body = "Your problem, idea or question:\n\n\n\nDetails, in case it's a problem (added by KeaClip; personal folder names removed):\n" + BugReportDetails() + "\n";
        return new Uri("mailto:" + AppIdentity.ContactEmail + "?subject=" + Uri.EscapeDataString("KeaClip " + AppIdentity.Version + " feedback") + "&body=" + Uri.EscapeDataString(body));
    }
}
