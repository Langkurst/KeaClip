using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Core;

namespace Klippi.Infrastructure.Updates;

public sealed record UpdateServiceOptions
{
    /// <summary>
    /// Default feed: the manifest asset of the newest GitHub release. GitHub serves this URL only when
    /// the release is publicly downloadable (see docs/RELEASING.md).
    /// </summary>
    public const string DefaultFeed = "https://github.com/Langkurst/KeaClip/releases/latest/download/" + UpdateManifest.FileName;

    /// <summary>
    /// The local feed build/release-local.ps1 writes. When it exists it is used instead of the default,
    /// so builds made on this PC reach the installed app without any public hosting.
    /// </summary>
    public static string LocalFeedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeaClip-Releases", UpdateManifest.FileName);

    public static Uri PreferredFeed(string localFeedPath) => File.Exists(localFeedPath) && UpdateManifest.IsLocalFeed(new Uri(localFeedPath)) ? new Uri(localFeedPath) : new Uri(DefaultFeed);

    /// <summary>The app's data folder: update-state.json and the Updates download folder live here.</summary>
    public required string DataRoot { get; init; }
    public UpdateInstallKind InstallKind { get; init; } = UpdateInstallKind.Development;
    /// <summary>The installation folder (installed mode): handed to the installer's upgrade mode.</summary>
    public string? InstallRoot { get; init; }
    public string CurrentVersion { get; init; } = AppIdentity.Version;
    public Uri FeedUrl { get; init; } = new(DefaultFeed);
    /// <summary>Tests only: accepts http://127.0.0.1 feeds and downloads. Never set by the app.</summary>
    public bool AllowLoopbackHttp { get; init; }
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan ManifestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>A download that receives nothing for this long is stopped.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public INetworkState Network { get; init; } = new WindowsNetworkState();
    public Func<string, SignatureState> CheckSignature { get; init; } = Authenticode.Check;
    /// <summary>Starts the verified installer; returns false when it could not start.</summary>
    public Func<ProcessStartInfo, bool> StartProcess { get; init; } = info => { using var process = Process.Start(info); return process is not null; };
    public int ProcessId { get; init; } = Environment.ProcessId;
}

/// <summary>
/// Checks the release feed, downloads the right asset over HTTPS into an app-owned folder and
/// verifies its exact size and SHA-256 before anything runs. Nothing is ever installed without the
/// user's Update now; the installer itself refuses a downgrade or the same version again.
/// </summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    public const string StateFileName = "update-state.json";
    public const string FolderName = "Updates";
    public const string InstallerFileName = "KeaClip.Setup.exe";
    private const string PartialSuffix = ".partial";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly UpdateServiceOptions _options;
    private readonly HttpClient _http;
    private readonly ReleaseVersion _current;
    private readonly object _stateSync = new();
    private Uri _feed;

    public UpdateService(UpdateServiceOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _current = ReleaseVersion.Parse(options.CurrentVersion);
        if (!IsAllowedFeed(options.FeedUrl, options.AllowLoopbackHttp)) throw new ArgumentException("The update feed must be an https link or a local keaclip-update.json file.", nameof(options));
        _feed = options.FeedUrl;
        // Redirects are followed (GitHub sends release downloads to its file host), but .NET never
        // follows an https link to plain http, and every final address is checked again below.
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 6, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(15) };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KeaClip", _current.ToString()));
    }

    public UpdateInstallKind InstallKind => _options.InstallKind;
    public ReleaseVersion CurrentVersion => _current;
    public Uri FeedUrl => _feed;
    public string UpdatesFolder => Path.Combine(Path.GetFullPath(_options.DataRoot), FolderName);
    private string StatePath => Path.Combine(Path.GetFullPath(_options.DataRoot), StateFileName);

    public DateTimeOffset? LastChecked => ReadState().LastChecked;
    /// <summary>A keaclip-update.json on this PC (see build/release-local.ps1): read directly, no network involved.</summary>
    public bool IsLocalFeed => UpdateManifest.IsLocalFeed(_feed);
    private static bool IsAllowedFeed(Uri url, bool allowLoopbackHttp) => UpdateManifest.IsAllowedUrl(url, allowLoopbackHttp) || UpdateManifest.IsLocalFeed(url);

    // A local feed costs nothing to read, so a new local build is noticed at the next start.
    public bool IsBackgroundCheckDue => IsLocalFeed || ReadState().LastChecked is not { } last || _options.Clock() - last >= _options.CheckInterval || last > _options.Clock() + TimeSpan.FromMinutes(5);

    public bool TrySetFeed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { _feed = _options.FeedUrl; return true; }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !IsAllowedFeed(uri, _options.AllowLoopbackHttp)) return false;
        _feed = uri; return true;
    }

    public async Task<UpdateCheckResult> CheckAsync(bool background, CancellationToken cancellationToken = default)
    {
        var now = _options.Clock();
        if (background)
        {
            if (!IsBackgroundCheckDue) return new(UpdateCheckOutcome.Skipped, null, "Checked recently.", LastChecked, Quiet: true);
            if (!IsLocalFeed && !_options.Network.IsOnline()) return new(UpdateCheckOutcome.Offline, null, "Offline, so the daily update check will run later.", LastChecked, Quiet: true);
            if (!IsLocalFeed && _options.Network.IsMetered() == true) return new(UpdateCheckOutcome.Skipped, null, "On a metered connection, so the daily update check waits. Choose Check for updates to check now.", LastChecked, Quiet: true);
        }
        else if (!IsLocalFeed && !_options.Network.IsOnline()) return new(UpdateCheckOutcome.Offline, null, "You appear to be offline. Connect to the internet and try again.", LastChecked);
        // The attempt is recorded first, so a feed that keeps failing is still asked at most once a day.
        UpdateState(state => state with { LastChecked = now });
        try
        {
            var offer = await FetchOfferAsync(cancellationToken).ConfigureAwait(false);
            if (offer.Version <= _current) return new(UpdateCheckOutcome.UpToDate, null, $"KeaClip {_current} is up to date.", now, Quiet: true);
            return new(UpdateCheckOutcome.Available, offer, $"KeaClip {offer.Version} is available.", now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UpdateValidationException error) { return new(UpdateCheckOutcome.Failed, null, error.Message, now, Quiet: background); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException || error is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
        {
            return new(UpdateCheckOutcome.Failed, null, "No update information was published at the release feed yet.", now, Quiet: background);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or TimeoutException)
        {
            return new(UpdateCheckOutcome.Offline, null, "Couldn't reach the update feed. KeaClip will try again later.", now, Quiet: background);
        }
    }

    private async Task<UpdateOffer> FetchOfferAsync(CancellationToken cancellationToken)
    {
        if (IsLocalFeed)
        {
            var path = _feed.LocalPath;
            RequireNotLink(path);
            byte[] bytes;
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous))
            {
                if (file.Length > UpdateManifest.MaxManifestBytes) throw new UpdateValidationException("The update information is too large, so it was ignored.");
                bytes = new byte[file.Length];
                await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            return UpdateManifest.Parse(DecodeManifest(bytes, bytes.Length).TrimStart('﻿'), _options.AllowLoopbackHttp, Path.GetDirectoryName(path));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ManifestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _feed);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RequireAllowedFinalUrl(response);
        if (response.Content.Headers.ContentLength is > UpdateManifest.MaxManifestBytes) throw new UpdateValidationException("The update information is too large, so it was ignored.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024]; int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > UpdateManifest.MaxManifestBytes) throw new UpdateValidationException("The update information is too large, so it was ignored.");
            buffer.Write(chunk, 0, read);
        }
        return UpdateManifest.Parse(DecodeManifest(buffer.GetBuffer(), (int)buffer.Length).TrimStart('﻿'), _options.AllowLoopbackHttp);
    }

    private static string DecodeManifest(byte[] bytes, int length)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, length); }
        catch (DecoderFallbackException) { throw new UpdateValidationException("The update information is not valid text, so it was ignored."); }
    }

    private static void RequireNotLink(string path)
    {
        if (IsLink(path) || Path.GetDirectoryName(path) is { } folder && IsLink(folder)) throw new UpdateValidationException("The local update folder is a link to somewhere else, so it was not used.");
    }

    public async Task<PreparedUpdate> DownloadAsync(UpdateOffer offer, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (offer.Version <= _current) throw new UpdateValidationException($"KeaClip {offer.Version} is not newer than this version ({_current}), so it was not downloaded.");
        var (asset, fileName) = _options.InstallKind switch
        {
            UpdateInstallKind.Installed => (offer.Installer ?? throw new UpdateValidationException("This release has no installer for installed copies of KeaClip."), InstallerFileName),
            UpdateInstallKind.Portable => (offer.Portable ?? throw new UpdateValidationException("This release has no portable ZIP."), $"KeaClip-{offer.Version}-win-x64.zip"),
            _ => throw new UpdateValidationException("This copy of KeaClip runs from a development build, so it can't update itself. Use the installer or the portable ZIP."),
        };
        var local = IsLocalFeed && asset.Url.IsFile && string.Equals(Path.GetDirectoryName(asset.Url.LocalPath), Path.GetDirectoryName(_feed.LocalPath), StringComparison.OrdinalIgnoreCase);
        if (!local && !UpdateManifest.IsAllowedUrl(asset.Url, _options.AllowLoopbackHttp)) throw new UpdateValidationException("The download link is not a secure https link, so nothing was downloaded.");
        var folder = OwnedFolder(offer.Version);
        var final = Path.Combine(folder, fileName); var partial = final + PartialSuffix;
        // A verified copy from an earlier attempt is reused, never trusted without checking again.
        if (File.Exists(final))
        {
            progress?.Report(new(JobStage.Verifying, null, "Checking the update downloaded earlier…"));
            if (new FileInfo(final).Length == asset.Size && await HashFileAsync(final, cancellationToken).ConfigureAwait(false) == asset.Sha256) return Prepared(offer, final, asset);
            File.Delete(final);
        }
        RequireFreeSpace(folder, asset.Size);
        if (File.Exists(partial)) File.Delete(partial);
        try
        {
            // A local feed's file is copied exactly like a download: size-capped, hashed, then checked again from disk.
            using var request = local ? null : new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request?.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = request is null ? null : await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response is not null) { response.EnsureSuccessStatusCode(); RequireAllowedFinalUrl(response); }
            else RequireNotLink(asset.Url.LocalPath);
            var length = response is null ? new FileInfo(asset.Url.LocalPath).Length : response.Content.Headers.ContentLength;
            if (length is { } known && known != asset.Size) throw new UpdateValidationException("The download is not the size the release lists, so it was stopped. Nothing was installed.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = response is null ? new FileStream(asset.Url.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan) : await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; long total = 0; var lastPercent = -1;
                while (true)
                {
                    int read;
                    using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        stall.CancelAfter(_options.StallTimeout);
                        try { read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("The download stopped receiving data."); }
                    }
                    if (read == 0) break;
                    total += read;
                    // Size cap: never write more than the release says the file is.
                    if (total > asset.Size) throw new UpdateValidationException("The download is larger than the release lists, so it was stopped. Nothing was installed.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    var percent = (int)(total * 100 / asset.Size);
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(new(JobStage.Downloading, percent, $"{Size(total)} of {Size(asset.Size)}")); }
                }
                if (total != asset.Size) throw new UpdateValidationException("The download ended early, so it was deleted. Nothing was installed.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            progress?.Report(new(JobStage.Verifying, 100, "Checking the download…"));
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != asset.Sha256) throw new UpdateValidationException("The download didn't match its published SHA-256, so it was deleted. Nothing was installed.");
            File.Move(partial, final, overwrite: false);
            // Checked again from disk: what runs is exactly what was verified.
            if (await HashFileAsync(final, cancellationToken).ConfigureAwait(false) != asset.Sha256) { File.Delete(final); throw new UpdateValidationException("The saved update changed after it was checked, so it was deleted. Nothing was installed."); }
            return Prepared(offer, final, asset);
        }
        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Removed by the next cleanup. */ } }
    }

    private PreparedUpdate Prepared(UpdateOffer offer, string path, UpdateAsset asset)
    {
        var note = "Verified against its published SHA-256.";
        if (_options.InstallKind == UpdateInstallKind.Installed)
        {
            switch (_options.CheckSignature(path))
            {
                case SignatureState.Invalid:
                    TryDelete(path);
                    throw new UpdateValidationException("The installer carries a digital signature that Windows could not verify, so it was deleted. Nothing was installed.");
                case SignatureState.Valid: note = "Digitally signed, and verified against its published SHA-256."; break;
                default: note = "Not digitally signed yet; verified against its published SHA-256."; break;
            }
        }
        return new(offer, path, _options.InstallKind, asset.Sha256, note);
    }

    public void StartInstall(PreparedUpdate update)
    {
        if (_options.InstallKind != UpdateInstallKind.Installed || update.Kind != UpdateInstallKind.Installed || string.IsNullOrWhiteSpace(_options.InstallRoot))
            throw new UpdateValidationException("Only an installed copy of KeaClip can run the installer to update itself.");
        if (update.Offer.Version <= _current) throw new UpdateValidationException($"KeaClip {update.Offer.Version} is not newer than this version, so it was not installed.");
        var asset = update.Offer.Installer ?? throw new UpdateValidationException("This release has no installer.");
        var path = Path.GetFullPath(update.FilePath);
        if (!IsUnder(path, UpdatesFolder) || !string.Equals(Path.GetFileName(path), InstallerFileName, StringComparison.OrdinalIgnoreCase)) throw new UpdateValidationException("The update file is not in KeaClip's own update folder, so it was not run.");
        RequireNoLinks(path);
        // The file stays open (others may read it, nobody may change or delete it) from the final
        // check until the installer has started, so what runs is exactly what was verified.
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (hold.Length != asset.Size || Convert.ToHexString(SHA256.HashData(hold)).ToLowerInvariant() != asset.Sha256 || asset.Sha256 != update.Sha256)
        {
            hold.Dispose(); TryDelete(path);
            throw new UpdateValidationException("The downloaded installer changed after it was checked, so it was deleted. Nothing was installed.");
        }
        if (_options.CheckSignature(path) == SignatureState.Invalid) throw new UpdateValidationException("The installer's digital signature could not be verified, so it was not run.");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var argument in new[] { "--upgrade", "--path", Path.GetFullPath(_options.InstallRoot), "--wait-pid", _options.ProcessId.ToString(CultureInfo.InvariantCulture), "--relaunch" }) info.ArgumentList.Add(argument);
        UpdateState(state => state with { PendingFrom = _current.ToString(), PendingTo = update.Offer.Version.ToString() });
        bool started;
        try { started = _options.StartProcess(info); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { started = false; }
        if (!started)
        {
            UpdateState(state => state with { PendingFrom = null, PendingTo = null });
            throw new UpdateValidationException("The installer could not start. Nothing was changed.");
        }
    }

    public string? TakeCompletedUpdateNotice()
    {
        var state = ReadState();
        if (state.PendingTo is null) return null;
        UpdateState(current => current with { PendingFrom = null, PendingTo = null });
        if (ReleaseVersion.TryParse(state.PendingTo, out var target) && _current >= target) return $"KeaClip was updated to {_current}. Your settings and media were kept.";
        return $"The update to KeaClip {state.PendingTo} didn't finish, so this version ({_current}) is still installed. Your settings and media were kept.";
    }

    public void CleanupDownloads()
    {
        try
        {
            var root = UpdatesFolder;
            if (!Directory.Exists(root) || IsLink(root)) return;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                if (IsLink(folder) || !ReleaseVersion.TryParse(Path.GetFileName(folder), out var version)) continue;
                var stale = version <= _current;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    var owned = name.Equals(InstallerFileName, StringComparison.OrdinalIgnoreCase) || name.Equals($"KeaClip-{version}-win-x64.zip", StringComparison.OrdinalIgnoreCase);
                    var partial = name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase) && (name.StartsWith(InstallerFileName, StringComparison.OrdinalIgnoreCase) || name.StartsWith("KeaClip-", StringComparison.OrdinalIgnoreCase));
                    // Only this app's own files: a finished download of an older or the current version, or any leftover partial download.
                    if (IsLink(file) || !(partial || stale && owned)) continue;
                    TryDelete(file);
                }
                try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Cleanup is best effort; the next start tries again. */ }
    }

    private string OwnedFolder(ReleaseVersion version)
    {
        var root = UpdatesFolder;
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, version.ToString());
        Directory.CreateDirectory(folder);
        RequireNoLinks(folder);
        return folder;
    }

    private void RequireAllowedFinalUrl(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is not { } final || !UpdateManifest.IsAllowedUrl(final, _options.AllowLoopbackHttp))
            throw new UpdateValidationException("The update server redirected to a link that is not secure, so it was not used.");
    }

    private static void RequireFreeSpace(string folder, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < bytes + 64L * 1024 * 1024) throw new UpdateValidationException($"There isn't enough free space for the update ({Size(bytes)} plus room to install). Free some space and try again.");
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { /* Unknown space: the download reports a full disk itself. */ }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private void RequireNoLinks(string path)
    {
        // Every level from the file up to the data folder itself must be a real folder, not a link elsewhere.
        var data = Path.GetFullPath(_options.DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        while (true)
        {
            if (IsLink(current)) throw new UpdateValidationException("KeaClip's update folder is a link to somewhere else, so nothing was downloaded or run.");
            if (string.Equals(current, data, StringComparison.OrdinalIgnoreCase) || !IsUnder(current, data) || Path.GetDirectoryName(current) is not { } parent) break;
            current = parent;
        }
    }
    private static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsUnder(string path, string root)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    private static string Size(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.##} GB" : bytes >= 1_000_000 ? $"{bytes / 1_000_000d:0.#} MB" : $"{bytes / 1000d:0.#} KB";

    // ----- Saved state (last check, pending upgrade) -----
    private sealed record State(DateTimeOffset? LastChecked = null, string? PendingFrom = null, string? PendingTo = null);

    private State ReadState()
    {
        lock (_stateSync)
        {
            try
            {
                if (!File.Exists(StatePath) || new FileInfo(StatePath).Length > 64 * 1024) return new();
                return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? new();
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return new(); }
        }
    }

    private void UpdateState(Func<State, State> change)
    {
        lock (_stateSync)
        {
            var next = change(ReadState());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var temporary = StatePath + ".write";
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json), new UTF8Encoding(false));
                File.Move(temporary, StatePath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Not saving the time only means the next start may check again. */ }
        }
    }

    public void Dispose() => _http.Dispose();
}
