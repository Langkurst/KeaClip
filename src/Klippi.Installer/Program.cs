using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Klippi.Installer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--self-test") { SelfTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--hold-open-test" && int.TryParse(args[1], out var holdSeconds) && holdSeconds is > 0 and <= 60) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "hold-test-ready.json"), JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId })); Thread.Sleep(TimeSpan.FromSeconds(holdSeconds)); return 0; }
            if (args.Length == 3 && args[0] == "--test-uninstall-parent") { LaunchUninstallWorker(args[1], args[2], true); return 0; }
            if (args.Length is 3 or 4 && args[0] == "--test-upgrade") return TestUpgrade(args[1], args[2], args.Length == 4 ? args[3] : null);
            if (args.Length == 1 && args[0] == "--silent") { SilentInstall(); return 0; }
            if (args.Length > 0 && args[0] == "--upgrade")
            {
                // Started by KeaClip after the user chose Update now; no further clicks are needed.
                var request = UpgradeMode.Parse(args);
                ApplicationConfiguration.Initialize(); Application.Run(new UpgradeForm(request)); return 0;
            }
            if (args.Length == 3 && args[0] is "--uninstall-worker" or "--test-uninstall-worker")
            {
                var target = InstallEngine.SafeTarget(args[1]);
                if (!int.TryParse(args[2], out var parentId) || parentId <= 0) throw new IOException("The uninstall worker request is invalid. Nothing was removed.");
                try { using var parent = Process.GetProcessById(parentId); if (!parent.WaitForExit(30_000)) throw new IOException("The uninstall window has not exited. Close it and try again; nothing was removed."); } catch (ArgumentException) { }
                var preserved = new InstallEngine(args[0] == "--test-uninstall-worker" ? new NoIntegration() : new WindowsIntegration()).Uninstall(target);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "worker-result.json"), JsonSerializer.Serialize(new { Success = true, PreservedChangedFiles = preserved, DataPreserved = true }));
                if (args[0] == "--uninstall-worker") { ApplicationConfiguration.Initialize(); MessageBox.Show("KeaClip application files were removed. Your settings and saved media were kept." + (preserved.Count > 0 ? $" {preserved.Count} changed application file(s) and their receipt were kept for you to review." : ""), "KeaClip removed", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                return 0;
            }
            ApplicationConfiguration.Initialize();
            var uninstall = args.Contains("--uninstall") || string.Equals(Path.GetFileName(Environment.ProcessPath), "KeaClip.Uninstall.exe", StringComparison.OrdinalIgnoreCase);
            Application.Run(new SetupForm(uninstall)); return 0;
        }
        catch (Exception error)
        {
            if (args.FirstOrDefault() == "--self-test") { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "installer-self-test-failure.txt"), error.ToString()); }
            // Test workers remain hidden; production uninstallation reports its actual result.
            else if (args.FirstOrDefault() is "--uninstall-worker" or "--test-uninstall-worker" or "--test-uninstall-parent" or "--hold-open-test")
            {
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "worker-error.txt"), error.ToString()); } catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
                if (args.FirstOrDefault() == "--uninstall-worker") { ApplicationConfiguration.Initialize(); MessageBox.Show("KeaClip could not be removed: " + error.Message + " Your settings and saved media were kept. Close any running KeaClip window and try again, or review the retained recovery files.", "KeaClip removal needs attention", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
            else if (args.FirstOrDefault() != "--silent") MessageBox.Show(error.Message, "KeaClip setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    // Hidden self-test of the real upgrade mode: no window, no registry or shortcuts, and the app is
    // not started; the result (and the path that would be relaunched) is written to the result folder.
    private static int TestUpgrade(string root, string resultFolder, string? waitPid)
    {
        var results = InstallEngine.SafeTarget(resultFolder); Directory.CreateDirectory(results);
        try
        {
            var arguments = new List<string> { "--upgrade", "--path", root, "--relaunch" };
            if (waitPid is not null) { arguments.Add("--wait-pid"); arguments.Add(waitPid); }
            var request = UpgradeMode.Parse(arguments);
            var result = UpgradeMode.Run(request, Payload, Environment.ProcessPath!, new NoIntegration(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
            File.WriteAllText(Path.Combine(results, "upgrade-result.json"), JsonSerializer.Serialize(new { Success = true, result.FromVersion, result.ToVersion, Relaunch = request.Relaunch ? Path.Combine(request.Root, "KeaClip.exe") : null, Backup = result.Install.Backup, PrunedBackups = result.PrunedBackups.Count }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(results, "upgrade-result.json"), JsonSerializer.Serialize(new { Success = false, Error = error.Message }));
            return 1;
        }
    }
    /// <summary>
    /// Unattended install or upgrade for package managers (winget): no window, the default per-user folder,
    /// and the same verified transaction as the setup window. A new installation gets the setup window's
    /// defaults (Start menu shortcut only, no keaclip:// handler); an existing one keeps its own choices.
    /// The exit code is the only result (1 when anything failed, with nothing half-installed).
    /// </summary>
    public static void SilentInstall()
    {
        using var payload = Payload();
        _ = SilentInstall(payload, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KeaClip"), Environment.ProcessPath!, new InstallEngine(new WindowsIntegration()));
    }
    public static InstallResult SilentInstall(Stream payload, string target, string installerExecutable, InstallEngine engine) => engine.Install(payload, target, installerExecutable, SilentChoices(target));
    public static InstallChoices SilentChoices(string target) =>
        File.Exists(Path.Combine(target, InstallEngine.ReceiptName)) ? UpgradeMode.ChoicesFrom(InstallEngine.ReadReceipt(target)) : new InstallChoices(false, true, false);
    public static Stream Payload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("KeaClip.Payload.zip") ?? throw new IOException("This setup build has no embedded app payload. Build it with the installer publish script.");
    public static string LaunchUninstallWorker(string target, string? testWorkerRoot = null, bool testMode = false)
    {
        var workerRoot = testWorkerRoot is null ? Path.Combine(Path.GetTempPath(), "KeaClip.Uninstall.Workers", Guid.NewGuid().ToString("N")) : InstallEngine.SafeTarget(testWorkerRoot); InstallEngine.CheckLinks(workerRoot);
        if (Directory.Exists(workerRoot) && Directory.EnumerateFileSystemEntries(workerRoot).Any()) throw new IOException("The worker requires a dedicated empty folder.");
        Directory.CreateDirectory(workerRoot);
        var source = Environment.ProcessPath ?? throw new IOException("The installer executable is unavailable."); var worker = Path.Combine(workerRoot, "KeaClip.Remove.exe"); File.Copy(source, worker);
        File.WriteAllText(Path.Combine(workerRoot, "worker-owned.json"), JsonSerializer.Serialize(new { ApplicationId = "KeaClip.Uninstall.Worker", ExecutableSha256 = InstallEngine.Hash(worker) }));
        var info = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(testMode ? "--test-uninstall-worker" : "--uninstall-worker"); info.ArgumentList.Add(InstallEngine.SafeTarget(target)); info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = Process.Start(info) ?? throw new IOException("The uninstall worker could not start.");
        return workerRoot;
    }
}
