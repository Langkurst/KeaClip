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
        // Guards the release step: every version's CHANGELOG entry needs at least one bold title for the card.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "CHANGELOG.md"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        Assert.NotEmpty(WhatsNew.Highlights(File.ReadAllText(Path.Combine(root!, "CHANGELOG.md")), Klippi.Core.AppIdentity.Version));
    }
}
