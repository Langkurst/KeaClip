using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;
using Klippi.Infrastructure.Sharing;
using Klippi.Infrastructure.Updates;

namespace Klippi.Desktop;

public sealed record DesktopServices(IDownloadService Downloads, IMediaService Media, ISharingService Sharing, ILocalStore Store, IComponentService Components, IDesktopPlatform Platform, IPlaybackService Playback, string DataRoot, ITemporaryWorkspaceService? TemporaryWorkspaces = null, IUpdateService? Updates = null, IWatchLinkService? WatchLinks = null, string? RecordingsRoot = null, bool BetaTest = false, string? ReportsRoot = null, bool AutoSetup = false)
{
    public static DesktopServices CreateDefault()
    {
        var portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));
        var root = portable ? Path.Combine(AppContext.BaseDirectory, "Data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeaClip");
        var runner = new ProcessRunner();
        var components = new ManagedComponentService(runner, Path.Combine(root, "Components"));
        var temporaryWorkspaces = new OwnedTemporaryWorkspaceService(root);
        // Installed copies update through the verified installer; portable copies get the verified ZIP.
        var environment = UpdateEnvironment.Detect(AppContext.BaseDirectory);
        // Only the owner's personal build reads the local feed (release-local.ps1); public copies trust GitHub alone,
        // so nothing else on the PC can plant an update for them.
        var feed = AppIdentity.Edition == "Personal" ? UpdateServiceOptions.PreferredFeed(UpdateServiceOptions.LocalFeedPath) : new Uri(UpdateServiceOptions.DefaultFeed);
        var updates = new UpdateService(new UpdateServiceOptions { DataRoot = root, InstallKind = environment.Kind, InstallRoot = environment.InstallRoot, FeedUrl = feed });
        var media = new MediaService(runner, components, temporaryWorkspaces);
        return new(new DownloadService(runner, components, temporaryWorkspaces), media, new FileKiwiSharingService(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }), new JsonLocalStore(root), components, new DesktopPlatform(), new VlcPlaybackService(), root, temporaryWorkspaces, updates,
            AppIdentity.WatchLinksIncluded ? WatchLinkService.Create(media, root) : null, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            AppIdentity.IsBeta, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AutoSetup: true);
    }
}
