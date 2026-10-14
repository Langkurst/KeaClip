using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

// Watch links: a link that opens KeaClip's web player, where the recipient watches the video in the browser. The file is
// encrypted on this PC (key only in the link's #fragment) and stored in the user's own S3-compatible bucket (Cloudflare R2).
// The File.Kiwi "Share" option is unchanged. The storage secret is entered once in Settings, kept DPAPI-protected by the
// service and never read back into the UI.
public sealed partial class MainViewModel
{
    private string _watchEndpoint = "", _watchBucket = "", _watchAccessKeyId = "", _watchSecret = "", _watchPlayerPage = "", _watchRetentionDays = "7", _watchSetupStatus = "";
    private bool _hasWatchSecret, _isWatchLinkReady, _sharingWatchLink;
    private IAsyncRelayCommand? _saveWatchSetupCommand, _removeWatchSetupCommand, _shareWatchLinkCommand;

    /// <summary>True when this build has the watch link service (the Settings card and the editor button are shown).</summary>
    public bool WatchLinksAvailable => _services.WatchLinks is not null;
    public string WatchEndpoint { get => _watchEndpoint; set => SetProperty(ref _watchEndpoint, value ?? ""); }
    public string WatchBucket { get => _watchBucket; set => SetProperty(ref _watchBucket, value ?? ""); }
    public string WatchAccessKeyId { get => _watchAccessKeyId; set => SetProperty(ref _watchAccessKeyId, value ?? ""); }
    /// <summary>Only ever holds a newly typed secret until it is saved; the stored one is never loaded back.</summary>
    public string WatchSecret { get => _watchSecret; set => SetProperty(ref _watchSecret, value ?? ""); }
    public string WatchPlayerPage { get => _watchPlayerPage; set => SetProperty(ref _watchPlayerPage, value ?? ""); }
    public string WatchRetentionDays { get => _watchRetentionDays; set => SetProperty(ref _watchRetentionDays, value ?? ""); }
    public string WatchSetupStatus { get => _watchSetupStatus; private set => SetProperty(ref _watchSetupStatus, value); }
    public bool HasWatchSecret { get => _hasWatchSecret; private set { if (SetProperty(ref _hasWatchSecret, value)) OnPropertyChanged(nameof(WatchSecretPlaceholder)); } }
    public bool IsWatchLinkReady { get => _isWatchLinkReady; private set => SetProperty(ref _isWatchLinkReady, value); }
    public string WatchSecretPlaceholder => HasWatchSecret ? "Saved and protected. Leave blank to keep it." : "Secret access key";

    public IAsyncRelayCommand SaveWatchSetupCommand => _saveWatchSetupCommand ??= new AsyncRelayCommand(SaveWatchSetupAsync);
    public IAsyncRelayCommand RemoveWatchSetupCommand => _removeWatchSetupCommand ??= new AsyncRelayCommand(RemoveWatchSetupAsync);
    /// <summary>Makes a watch link for the latest export, or for the open file when nothing was exported.</summary>
    public IAsyncRelayCommand ShareWatchLinkCommand => _shareWatchLinkCommand ??= new AsyncRelayCommand(() => ShareWatchLinkAsync(_lastExport?.FilePath ?? EditorPath));

    private async Task LoadWatchSetupAsync(CancellationToken ct)
    {
        if (_services.WatchLinks is not { } watch) return;
        try { ApplyWatchSetup(await watch.LoadSetupAsync(ct)); }
        catch (Exception error) when (error is not OperationCanceledException) { CaptureDiagnostics(error); WatchSetupStatus = "The watch link setup could not be read. Enter it again to use watch links."; }
    }

    private void ApplyWatchSetup(WatchLinkSetupState state)
    {
        if (state.Setup is { } setup)
        {
            WatchEndpoint = setup.StorageEndpoint.GetLeftPart(UriPartial.Authority);
            WatchBucket = setup.Bucket;
            WatchAccessKeyId = setup.AccessKeyId;
            WatchPlayerPage = setup.PlayerPage.AbsoluteUri;
            WatchRetentionDays = setup.RetentionDays.ToString(CultureInfo.InvariantCulture);
        }
        HasWatchSecret = state.HasSecret;
        IsWatchLinkReady = state.IsReady;
        WatchSetupStatus = state.IsReady ? "Ready. Watch links upload to your bucket." : state.Setup is null ? "" : "Enter the secret access key to finish the setup.";
    }

    /// <summary>The setup typed into Settings, or the first problem with it.</summary>
    internal (WatchLinkSetup? Setup, string? Problem) ReadWatchSetup()
    {
        if (!Uri.TryCreate(WatchEndpoint.Trim(), UriKind.Absolute, out var endpoint)) return (null, "Enter the storage endpoint, for example https://<account id>.r2.cloudflarestorage.com.");
        if (!Uri.TryCreate(WatchPlayerPage.Trim(), UriKind.Absolute, out var page)) return (null, "Enter the player page, for example https://watch.example.com/v/.");
        if (!int.TryParse(WatchRetentionDays.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days)) return (null, $"Delete links after a whole number of days from 1 to {WatchLinkSetup.MaximumRetentionDays}.");
        var setup = new WatchLinkSetup(endpoint, WatchBucket.Trim(), WatchAccessKeyId.Trim(), page, days);
        if (setup.Problems() is { Count: > 0 } problems) return (null, problems[0]);
        string secret = WatchSecret.Trim();
        if (secret.Length == 0 && !HasWatchSecret) return (null, "Enter the secret access key.");
        if (secret.Length > 0 && !WatchLinkSetup.ValidSecret(secret)) return (null, "The secret access key must be 16 to 128 characters without spaces.");
        return (setup, null);
    }

    private Task SaveWatchSetupAsync() => RunAsync(OperationLane.Maintenance, async ct =>
    {
        if (_services.WatchLinks is not { } watch) return;
        var (setup, problem) = ReadWatchSetup();
        if (setup is null) { WatchSetupStatus = problem!; Status = "Watch links were not saved. " + problem; return; }
        string? secret = WatchSecret.Trim() is { Length: > 0 } typed ? typed : null;
        await watch.SaveSetupAsync(setup, secret, ct);
        WatchSecret = "";
        ApplyWatchSetup(await watch.LoadSetupAsync(ct));
        Status = "Watch link setup saved. The secret is protected for your Windows account.";
    });

    private Task RemoveWatchSetupAsync() => RunAsync(OperationLane.Maintenance, async ct =>
    {
        if (_services.WatchLinks is not { } watch) return;
        await watch.ClearSetupAsync(ct);
        WatchEndpoint = WatchBucket = WatchAccessKeyId = WatchSecret = WatchPlayerPage = "";
        WatchRetentionDays = "7";
        ApplyWatchSetup(new WatchLinkSetupState(null, false));
        Status = "Watch link setup removed from this PC. Links already shared keep working until your bucket deletes them.";
    });

    /// <summary>Creates a watch link for a local file. Needs the setup from Settings; nothing is uploaded without it.</summary>
    public Task ShareWatchLinkAsync(string path) => RunAsync(OperationLane.Share, async ct =>
    {
        if (_services.WatchLinks is not { } watch) throw new MediaOperationException("Watch links are not available in this build.");
        if (string.IsNullOrWhiteSpace(path)) throw new MediaOperationException("Open or choose a local file first.");
        var state = await watch.LoadSetupAsync(ct);
        ApplyWatchSetup(state);
        if (!state.IsReady)
        {
            SelectedTab = 4;
            Status = "Set up watch links first: enter your storage details under Settings, Watch links.";
            return;
        }
        if (IsExporting && DeleteOriginal && SamePath(path, EditorPath)) throw new MediaOperationException("The running export may delete this file. Wait for it to finish, then share.");
        ClearLargeShareRequest();
        Status = "Preparing a watch link…"; ShareUrl = ""; ShareFileName = Path.GetFileName(path); _shareLinkPath = path;
        ShareInfo = "Making a copy browsers can play, encrypting it on this device, then uploading it to your storage. Keep the app open until the link appears.";
        ShareResult result;
        IsSharing = true; _sharingPath = path; _sharingWatchLink = true; ShareProgressText = "";
        var progress = Progress(OperationLane.Share);
        try { result = await Task.Run(() => watch.ShareAsync(path, progress, ct), ct); }
        catch { ShareInfo = "The watch link was not created, so nothing was shared. You can try again."; throw; }
        finally { progress.Close(); IsSharing = false; _sharingPath = null; _sharingWatchLink = false; ShareProgressText = ""; }
        ShareUrl = result.ShareUrl;
        ShareInfo = "Watch link: opens in any browser with KeaClip's player and stays end-to-end encrypted. " + (result.Warning is { } warning ? MediaValidation.Redact(warning) : "");
        SelectedTab = 3; Status = "Watch link ready. Copy the full link to share; its key is not saved to history.";
    });

    /// <summary>A link to the configured player page with a watch-link fragment (opened in the browser, never downloaded).</summary>
    internal bool IsWatchLinkUrl(Uri uri)
        => Uri.TryCreate(WatchPlayerPage.Trim(), UriKind.Absolute, out var page) && uri.Scheme == page.Scheme && uri.IdnHost.Equals(page.IdnHost, StringComparison.OrdinalIgnoreCase)
            && uri.Port == page.Port && uri.AbsolutePath.Equals(page.AbsolutePath, StringComparison.Ordinal) && uri.UserInfo.Length == 0 && MediaValidation.IsWatchLinkFragment(uri.Fragment);
}
