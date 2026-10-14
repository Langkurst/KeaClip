using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klippi.Installer;

/// <summary>What the app asks for when the user chooses Update now: upgrade this receipt-backed installation.</summary>
public sealed record UpgradeRequest(string Root, int? WaitProcessId, bool Relaunch);
public sealed record UpgradeResult(InstallResult Install, string FromVersion, string ToVersion, IReadOnlyList<string> PrunedBackups);

/// <summary>
/// The no-click upgrade the app starts after the user chose Update now:
/// <c>KeaClip.Setup.exe --upgrade --path &lt;installed root&gt; [--wait-pid &lt;app process&gt;] [--relaunch]</c>.
/// It waits for the app to exit, refuses a downgrade or the same version, and then runs the same
/// verified <see cref="InstallEngine.Install"/> transaction as a manual upgrade (staged, hash-checked,
/// rolled back on failure; settings, Data and unrelated files preserved), keeping the installation's
/// existing shortcut and link choices.
/// </summary>
public static partial class UpgradeMode
{
    public static string IncomingVersion => typeof(InstallEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Parses the exact upgrade arguments; anything unexpected is refused rather than guessed.</summary>
    public static UpgradeRequest Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "--upgrade") throw new IOException("Not an upgrade request.");
        string? root = null; int? pid = null; var relaunch = false;
        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--path" when index + 1 < args.Count && root is null: root = args[++index]; break;
                case "--wait-pid" when index + 1 < args.Count && pid is null:
                    if (!int.TryParse(args[++index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0) throw new IOException("The update request is invalid. Nothing was changed.");
                    pid = id; break;
                case "--relaunch" when !relaunch: relaunch = true; break;
                default: throw new IOException("The update request is invalid. Nothing was changed.");
            }
        }
        if (root is null) throw new IOException("The update request names no installation. Nothing was changed.");
        return new(InstallEngine.SafeTarget(root), pid, relaunch);
    }

    /// <summary>Only a newer version may replace an installation; the same or an older one is refused.</summary>
    public static void RequireNewer(string installed, string incoming)
    {
        if (!Version.TryParse(installed, out var current) || !Version.TryParse(incoming, out var next)) throw new IOException("The installed or incoming KeaClip version could not be read. Nothing was changed.");
        if (next <= current) throw new IOException($"KeaClip {installed} is already installed, which is the same as or newer than this installer ({incoming}). Nothing was changed.");
    }

    /// <summary>Keeps what the installation already has: shortcuts that still exist and the link handler.</summary>
    public static InstallChoices ChoicesFrom(InstallReceipt receipt)
    {
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KeaClip.lnk");
        var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "KeaClip", "KeaClip.lnk");
        bool Has(string path) => receipt.Shortcuts.Keys.Any(key => string.Equals(Path.GetFullPath(key), path, StringComparison.OrdinalIgnoreCase)) && File.Exists(path);
        return new(Has(desktop), Has(menu), receipt.Protocol);
    }

    /// <summary>Waits for the app that asked for the update to exit; an app that does not exit in time stops the upgrade before any change.</summary>
    public static void WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(timeout)) throw new IOException("KeaClip did not close, so the update was not installed. Nothing was changed.");
        }
        catch (ArgumentException) { /* Already exited. */ }
    }

    public static UpgradeResult Run(UpgradeRequest request, Func<Stream> payload, string installerExecutable, IInstallIntegration integration, TimeSpan exitTimeout, TimeSpan closeTimeout, string? incomingVersion = null)
    {
        var target = InstallEngine.SafeTarget(request.Root); InstallEngine.CheckLinks(target);
        var receipt = InstallEngine.ReadReceipt(target);
        var incoming = incomingVersion ?? IncomingVersion;
        RequireNewer(receipt.Version, incoming);
        if (request.WaitProcessId is { } pid) WaitForExit(pid, exitTimeout);
        // Media tools started by the app end with it (kill-on-close jobs); give them a moment.
        var waited = Stopwatch.StartNew();
        while (InstallEngine.HasRunningProcess(target) && waited.Elapsed < closeTimeout) Thread.Sleep(250);
        InstallResult result;
        using (var stream = payload()) result = new InstallEngine(integration).Install(stream, target, installerExecutable, ChoicesFrom(receipt));
        var pruned = PruneOlderBackups(target, result.Backup);
        return new(result, receipt.Version, InstallEngine.ReadReceipt(target).Version, pruned);
    }

    /// <summary>
    /// Each upgrade keeps the previous complete tree as a sibling rollback backup. Automatic updates
    /// would let those pile up, so after a successful upgrade the older backups of this installation
    /// are thinned out: a file is removed only when it is an unchanged application file listed in that
    /// backup's receipt, or an exact copy (same path and SHA-256) of a file that is in the live
    /// installation. Anything else (a settings file that has changed since, a file found nowhere else)
    /// is kept, together with the backup's receipt, so nothing unique is ever lost. The newest backup
    /// (<paramref name="keep"/>, this upgrade's rollback copy) is never touched. Best effort.
    /// </summary>
    public static IReadOnlyList<string> PruneOlderBackups(string target, string? keep)
    {
        target = InstallEngine.SafeTarget(target);
        var parent = Path.GetDirectoryName(target)!; var removed = new List<string>();
        if (!Directory.Exists(parent)) return removed;
        foreach (var backup in Directory.EnumerateDirectories(parent, ".keaclip-backup-*", SearchOption.TopDirectoryOnly).ToArray())
        {
            if (!BackupName().IsMatch(Path.GetFileName(backup)) || keep is not null && string.Equals(Path.GetFullPath(backup), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                InstallEngine.CheckLinks(backup);
                var receiptPath = Path.Combine(backup, InstallEngine.ReceiptName);
                if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 5_000_000) continue;
                var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(receiptPath));
                if (receipt is null || receipt.ApplicationId != "KeaClip" || receipt.Schema != 1 || !string.Equals(Path.GetFullPath(receipt.Root).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase)) continue;
                var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in receipt.Files) owned[file.RelativePath] = file.Sha256;
                var kept = 0;
                foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).ToArray())
                {
                    var relative = Path.GetRelativePath(backup, file).Replace('\\', '/');
                    if (relative.Equals(InstallEngine.ReceiptName, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        InstallEngine.ValidateRelative(relative);
                        var digest = InstallEngine.Hash(file);
                        var live = InstallEngine.Under(target, relative);
                        var duplicate = owned.TryGetValue(relative, out var expected) ? expected == digest : File.Exists(live) && InstallEngine.Hash(live) == digest;
                        if (duplicate) File.Delete(file); else kept++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { kept++; }
                }
                if (kept == 0) File.Delete(receiptPath);
                RemoveEmptyDirectories(backup);
                if (!Directory.Exists(backup)) removed.Add(backup);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException) { /* Kept for review; the next upgrade tries again. */ }
        }
        return removed;
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root)) RemoveEmptyDirectories(directory);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root, false);
    }

    /// <summary>Starts the upgraded (or, after a failure, the unchanged) app as the signed-in user.</summary>
    public static void Relaunch(string root)
    {
        var executable = Path.Combine(InstallEngine.SafeTarget(root), "KeaClip.exe");
        if (!File.Exists(executable)) return;
        using var _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! });
    }

    [GeneratedRegex("^\\.keaclip-backup-[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}

/// <summary>A small window with no buttons while the upgrade runs; it closes itself and starts KeaClip again.</summary>
public sealed class UpgradeForm : Form
{
    private readonly UpgradeRequest _request;
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new(400, 0), Margin = new(0, 0, 0, 14) };
    private bool _busy = true;

    public UpgradeForm(UpgradeRequest request)
    {
        _request = request;
        Text = "Updating KeaClip"; Width = 470; Height = 210; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new("Segoe UI", 10); BackColor = Color.FromArgb(246, 248, 243); ShowInTaskbar = true;
        if (Environment.ProcessPath is { } self) Icon = Icon.ExtractAssociatedIcon(self);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(new Label { Text = $"Updating KeaClip to {UpgradeMode.IncomingVersion}", AutoSize = true, Font = new("Segoe UI", 13, FontStyle.Bold), Margin = new(0, 0, 0, 8) });
        _status.Text = "Waiting for KeaClip to close, then installing the verified update. Your settings and saved media are kept.";
        panel.Controls.Add(_status);
        panel.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 400, Height = 8, MarqueeAnimationSpeed = 40 });
        Controls.Add(panel);
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var completion = new TaskCompletionSource<UpgradeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(UpgradeMode.Run(_request, Program.Payload, Environment.ProcessPath!, new WindowsIntegration(), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30))); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = false; thread.Start();
        try
        {
            await completion.Task;
            _busy = false;
            if (_request.Relaunch) TryRelaunch();
        }
        catch (Exception error)
        {
            _busy = false;
            // The transaction rolled back, so the previous version is still installed and is opened again.
            MessageBox.Show(this, "KeaClip could not be updated: " + error.Message + " The previous version is still installed and your settings and saved media were kept.", "KeaClip update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (_request.Relaunch) TryRelaunch();
        }
        Close();
    }

    private void TryRelaunch()
    {
        try { UpgradeMode.Relaunch(_request.Root); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { MessageBox.Show(this, "KeaClip was updated but could not be started: " + error.Message + " Open it from the Start menu.", "KeaClip update", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
