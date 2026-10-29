using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// The "what's new" card reads its highlights from the CHANGELOG.md shipped beside the app.
public sealed class WhatsNewTests
{
    private const string Changelog = """
        # Changelog

        ## 0.4.6 (2026-10-24)

        - **What's new after an update**: a card says what changed.
        - **Fixed**: something small.

        ## 0.4.5 (2026-10-22)

        - **You'll notice when it's done**: the taskbar flashes.
        """;

    [Fact]
    public void Highlights_are_the_bold_titles_of_that_version_only_without_fixes()
    {
        Assert.Equal(["What's new after an update"], WhatsNew.Highlights(Changelog, "0.4.6"));
        Assert.Equal(["You'll notice when it's done"], WhatsNew.Highlights(Changelog, "0.4.5"));
        Assert.Empty(WhatsNew.Highlights(Changelog, "9.9.9"));
        Assert.Empty(WhatsNew.Highlights(Changelog, "0.4")); // a prefix of another version is not that version
    }

    [Fact]
    public void The_real_changelog_has_highlights_for_this_version()
    {
        // Guards the release step: every version's CHANGELOG entry needs a bold feature title or a **Fixed** line for the card.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "CHANGELOG.md"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var changelog = File.ReadAllText(Path.Combine(root!, "CHANGELOG.md"));
        Assert.True(WhatsNew.Highlights(changelog, Klippi.Core.AppIdentity.Version).Count > 0 || WhatsNew.HasFixes(changelog, Klippi.Core.AppIdentity.Version));
    }

    [Fact]
    public void A_fixes_only_version_is_recognised()
    {
        const string fixesOnly = "## 0.5.4 (2026-10-30)\n\n- **Fixed**: the drag panel could stay on screen.\n\n## 0.5.3 (2026-10-24)\n\n- **Make it a Short**: one click.\n";
        Assert.Empty(WhatsNew.Highlights(fixesOnly, "0.5.4"));
        Assert.True(WhatsNew.HasFixes(fixesOnly, "0.5.4"));
        Assert.False(WhatsNew.HasFixes(fixesOnly, "0.5.3"));
    }
}
