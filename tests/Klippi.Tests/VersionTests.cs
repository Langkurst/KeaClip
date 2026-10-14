using Klippi.Core;
using Xunit;

namespace Klippi.Tests;

public sealed class VersionTests
{
    // The shown version, the archive record and the user agent use AppIdentity.Version; the
    // package folder and installer receipt use <Version> in Directory.Build.props.
    [Fact]
    public void App_version_matches_the_build_version()
        => Assert.Equal(AppIdentity.Version, typeof(AppIdentity).Assembly.GetName().Version!.ToString(3));

    // Builds are public unless KeaClipEdition=Personal is passed (build/release-local.ps1 does), so a public copy can
    // never upload to the owner's own watch-link storage.
    [Fact]
    public void Builds_are_public_by_default_and_leave_out_watch_links()
    {
        Assert.Equal("Public", AppIdentity.Edition);
        Assert.False(AppIdentity.WatchLinksIncluded);
    }
}
