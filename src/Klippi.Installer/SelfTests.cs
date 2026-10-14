using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Klippi.Installer;

public static class SelfTests
{
    public static void Run(string directory)
    {
        var root = InstallEngine.SafeTarget(directory); InstallEngine.CheckLinks(root); Directory.CreateDirectory(root);
        var run = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run); var checks = new List<string>();
        var source = Environment.ProcessPath ?? throw new IOException("No installer executable.");
        var contents = new Dictionary<string, byte[]> { ["KeaClip.exe"] = Encoding.UTF8.GetBytes("synthetic executable fixture; never launched"), ["portable.marker"] = Encoding.UTF8.GetBytes("portable"), ["runtime/native.dll"] = [1, 2, 3] };
        StartupEntryChecks(run, checks);
        var payload = MakePayload(contents);
        // --silent (winget): a fresh folder gets the setup window's defaults; a second run upgrades and keeps Data.
        var silentTarget = Path.Combine(run, "Silent");
        Require(Program.SilentChoices(silentTarget) == new InstallChoices(false, true, false), "silent install defaults");
        using (var stream = new MemoryStream(payload)) Program.SilentInstall(stream, silentTarget, source, new InstallEngine());
        Directory.CreateDirectory(Path.Combine(silentTarget, "Data")); File.WriteAllText(Path.Combine(silentTarget, "Data", "settings.json"), "silent settings");
        using (var stream = new MemoryStream(payload)) Program.SilentInstall(stream, silentTarget, source, new InstallEngine());
        Require(File.Exists(Path.Combine(silentTarget, "KeaClip.exe")) && File.ReadAllText(Path.Combine(silentTarget, "Data", "settings.json")) == "silent settings", "silent install and upgrade");
        checks.Add("Silent install (winget) uses the default choices, and a repeat run upgrades and keeps Data"); var engine = new InstallEngine(); var choices = new InstallChoices(false, false, false); var target = Path.Combine(run, "App");
        using (var stream = new MemoryStream(payload)) engine.Install(stream, target, source, choices);
        Require(File.Exists(Path.Combine(target, "KeaClip.exe")) && !File.Exists(Path.Combine(target, "portable.marker")), "install files / installed data mode"); checks.Add("Verified install and portable marker exclusion");
        var receipt = InstallEngine.ReadReceipt(target); Require(receipt.Files.Count == 3 && !receipt.Protocol && !receipt.ProgramsEntry && receipt.Shortcuts.Count == 0, "receipt scope"); checks.Add("Receipt-owned files; no registry or shortcuts in test mode");
        Directory.CreateDirectory(Path.Combine(target, "Data")); File.WriteAllText(Path.Combine(target, "Data", "settings.json"), "user settings"); File.WriteAllText(Path.Combine(target, "unrelated.txt"), "user media");
        using (var stream = new MemoryStream(payload)) { var result = engine.Install(stream, target, source, choices); Require(result.Backup is not null && Directory.Exists(result.Backup), "rollback backup"); }
        Require(File.ReadAllText(Path.Combine(target, "Data", "settings.json")) == "user settings" && File.ReadAllText(Path.Combine(target, "unrelated.txt")) == "user media", "upgrade preservation"); checks.Add("Upgrade preserves data/unrelated files and old complete backup");
        foreach (var point in new[] { "before-promotion", "after-promotion", "after-integration" })
        {
            var prior = File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName));
            ExpectFailure(() => { using var stream = new MemoryStream(payload); new InstallEngine { FailurePoint = p => { if (p == point) throw new IOException("Injected transaction failure"); } }.Install(stream, target, source, choices); });
            Require(prior.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))) && File.ReadAllText(Path.Combine(target, "unrelated.txt")) == "user media", "rollback correctness"); checks.Add("Rollback at " + point);
        }
        var beforeIntegration = File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName));
        var failingIntegration = new FailingIntegration();
        ExpectFailure(() => { using var stream = new MemoryStream(payload); new InstallEngine(failingIntegration).Install(stream, target, source, choices); });
        Require(failingIntegration.RolledBack && beforeIntegration.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))), "integration failure rollback"); checks.Add("Integration failure rolls back file receipt and integration state");
        var changedIntegration = new RecordingIntegration();
        ExpectFailure(() => { using var stream = new MemoryStream(payload); new InstallEngine(changedIntegration) { FailurePoint = p => { if (p == "after-integration") throw new IOException("Injected post-integration failure"); } }.Install(stream, target, source, choices); });
        Require(changedIntegration.RolledBack && beforeIntegration.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))), "post-integration changed receipt rollback"); checks.Add("Changed integration receipt is cleaned and original receipt restored");
        var guardedIntegration = new RemovalIntegration();
        using (var locked = new FileStream(Path.Combine(target, "KeaClip.exe"), FileMode.Open, FileAccess.Read, FileShare.Read)) ExpectFailure(() => new InstallEngine(guardedIntegration).Uninstall(target));
        Require(guardedIntegration.RemoveCalls == 0 && File.Exists(Path.Combine(target, "KeaClip.exe")) && beforeIntegration.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))), "locked-file preflight leaves receipt and integration intact"); checks.Add("Locked application file refuses uninstall before any changes");
        var ready = Path.Combine(target, "hold-test-ready.json"); var holdInfo = new ProcessStartInfo(Path.Combine(target, "KeaClip.Uninstall.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }; holdInfo.ArgumentList.Add("--hold-open-test"); holdInfo.ArgumentList.Add("60");
        using (var hold = Process.Start(holdInfo) ?? throw new IOException("The hidden running-app test process could not start."))
        {
            try
            {
                var timer = Stopwatch.StartNew(); while (!File.Exists(ready) && !hold.HasExited && timer.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
                Require(File.Exists(ready) && !hold.HasExited, "owned running process starts"); ExpectFailure(() => new InstallEngine(guardedIntegration).Uninstall(target));
                Require(guardedIntegration.RemoveCalls == 0 && File.Exists(Path.Combine(target, "KeaClip.exe")) && beforeIntegration.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))), "running process uninstall preflight");
                ExpectFailure(() => { using var stream = new MemoryStream(payload); engine.Install(stream, target, source, choices); }); checks.Add("Owned running process refuses uninstall/upgrade without changes");
            }
            finally { if (!hold.HasExited) { hold.Kill(); hold.WaitForExit(10_000); } if (File.Exists(ready)) File.Delete(ready); }
        }
        ExpectFailure(() => new InstallEngine { FailurePoint = p => { if (p == "during-removal") throw new IOException("Injected removal failure"); } }.Uninstall(target));
        Require(InstallEngine.ReadReceipt(target).Files.All(f => File.Exists(InstallEngine.Under(target, f.RelativePath)) && InstallEngine.Hash(InstallEngine.Under(target, f.RelativePath)) == f.Sha256) && beforeIntegration.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))), "deleted application file restored"); checks.Add("Mid-removal failure restores deleted app files and receipt");
        var failingRemoval = new RemovalIntegration { Fail = true }; ExpectFailure(() => new InstallEngine(failingRemoval).Uninstall(target));
        Require(failingRemoval.RolledBack && InstallEngine.ReadReceipt(target).Files.All(f => InstallEngine.Hash(InstallEngine.Under(target, f.RelativePath)) == f.Sha256), "removal integration failure restores all files"); checks.Add("Failed uninstall integration restores owned files and integration state");
        File.WriteAllText(Path.Combine(target, "runtime", "native.dll"), "changed by user");
        ExpectFailure(() => { using var stream = new MemoryStream(payload); engine.Install(stream, target, source, choices); }); checks.Add("Modified app files refuse overwrite during upgrade");
        var preserved = engine.Uninstall(target); Require(preserved.Contains("runtime/native.dll") && File.Exists(Path.Combine(target, "Data", "settings.json")) && File.Exists(Path.Combine(target, "unrelated.txt")) && !File.Exists(Path.Combine(target, "KeaClip.exe")), "safe removal"); checks.Add("Uninstall preserves modified, unknown and Data files");
        var foreign = Path.Combine(run, "Foreign"); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "keep.txt"), "keep");
        ExpectFailure(() => { using var stream = new MemoryStream(payload); engine.Install(stream, foreign, source, choices); }); Require(File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "keep", "unrelated refusal"); checks.Add("Unrelated nonempty target refusal");
        foreach (var relative in new[] { "../escape.txt", "C:/escape.txt", "bad:stream", "CON.exe", "dir/../escape", "file. ", "dir//file" }) ExpectFailure(() => InstallEngine.ValidateRelative(relative)); checks.Add("Traversal, drive/ADS, reserved and ambiguous filename rejection");
        var tampered = MakePayload(contents, corruptDigest: true);
        ExpectFailure(() => { using var stream = new MemoryStream(tampered); engine.Install(stream, Path.Combine(run, "Tampered"), source, choices); }); Require(!File.Exists(Path.Combine(run, "Tampered", "KeaClip.exe")), "tamper not promoted"); checks.Add("Checksum tamper refusal before promotion");
        var extra = MakePayload(contents, unlisted: true); ExpectFailure(() => { using var stream = new MemoryStream(extra); engine.Install(stream, Path.Combine(run, "Unlisted"), source, choices); }); checks.Add("Exact manifest coverage rejects unlisted files");
        var unsafeZip = MakePayload(new Dictionary<string, byte[]> { ["../escape.txt"] = [4] }); ExpectFailure(() => { using var stream = new MemoryStream(unsafeZip); engine.Install(stream, Path.Combine(run, "Traversal"), source, choices); }); Require(!File.Exists(Path.Combine(run, "escape.txt")), "zip escape"); checks.Add("ZIP path escape blocked");
        var special = MakePayload(contents, specialFile: true); ExpectFailure(() => { using var stream = new MemoryStream(special); engine.Install(stream, Path.Combine(run, "SpecialFile"), source, choices); }); checks.Add("ZIP link/special-file attributes rejected");
        var forgedSize = MakePayload(contents); ForgeEntryLength(forgedSize);
        ExpectFailure(() => { using var stream = new MemoryStream(forgedSize); engine.Install(stream, Path.Combine(run, "WrongLength"), source, choices); }); checks.Add("Streamed extraction rejects false ZIP declared size");
        var metadataTarget = Path.Combine(run, "MetadataUpgrade");
        using (var stream = new MemoryStream(payload)) engine.Install(stream, metadataTarget, source, choices);
        var metadata = InstallEngine.ReadReceipt(metadataTarget) with { Shortcuts = new Dictionary<string, string> { [Path.Combine(run, "not-created.lnk")] = new string('a', 64) }, Protocol = true, ProgramsEntry = true }; InstallEngine.WriteReceipt(metadataTarget, metadata);
        using (var stream = new MemoryStream(payload)) engine.Install(stream, metadataTarget, source, choices);
        var afterMetadata = InstallEngine.ReadReceipt(metadataTarget); Require(afterMetadata.Shortcuts.SequenceEqual(metadata.Shortcuts) && afterMetadata.Protocol && afterMetadata.ProgramsEntry, "existing integration receipt ownership"); checks.Add("Upgrade retains existing shortcut/integration receipt ownership");
        UpgradeChecks(run, source, payload, checks);
        var linked = Path.Combine(run, "Linked"); var links = "Not run: Windows did not permit a symbolic link";
        try { Directory.CreateSymbolicLink(linked, foreign); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException && !Directory.Exists(linked)) { ReparseTest.TryCreateJunction(linked, foreign); }
        if (Directory.Exists(linked)) { ExpectFailure(() => { using var stream = new MemoryStream(payload); engine.Install(stream, linked, source, choices); }); Directory.Delete(linked, false); Require(File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "keep", "junction target preserved"); links = "Passed: real reparse-linked target rejected, original target preserved"; checks.Add("Reparse/link target refusal"); }
        var embedded = false; var workerTest = "Not run: no embedded single-file payload";
        try
        {
            using var real = Program.Payload(); var actualTarget = Path.Combine(run, "ActualPayload"); engine.Install(real, actualTarget, source, choices); var actualReceipt = InstallEngine.ReadReceipt(actualTarget);
            Require(actualReceipt.Files.All(f => InstallEngine.Hash(InstallEngine.Under(actualTarget, f.RelativePath)) == f.Sha256), "actual payload hashes");
            using (var upgrade = Program.Payload()) engine.Install(upgrade, actualTarget, source, choices);
            Require(actualReceipt.Files.All(f => InstallEngine.Hash(InstallEngine.Under(actualTarget, f.RelativePath)) == f.Sha256), "actual payload upgrade hashes"); checks.Add("Actual embedded portable payload install/hash/upgrade");
            Directory.CreateDirectory(Path.Combine(actualTarget, "Data")); File.WriteAllText(Path.Combine(actualTarget, "Data", "keep.json"), "preserved user data"); File.WriteAllText(Path.Combine(actualTarget, "keep-media.txt"), "preserved media");
            ActualUpgradeProcessCheck(actualTarget, Path.Combine(run, "ActualUpgradeResult"), checks);
            var workerRoot = Path.Combine(run, "CopyWorker"); var childInfo = new ProcessStartInfo(Path.Combine(actualTarget, "KeaClip.Uninstall.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            childInfo.ArgumentList.Add("--test-uninstall-parent"); childInfo.ArgumentList.Add(actualTarget); childInfo.ArgumentList.Add(workerRoot);
            using var child = Process.Start(childInfo) ?? throw new IOException("The real copied-uninstaller parent could not start.");
            Require(child.WaitForExit(60_000) && child.ExitCode == 0, "uninstaller parent exits");
            var workerResult = Path.Combine(workerRoot, "worker-result.json"); var timer = Stopwatch.StartNew();
            while (!File.Exists(workerResult) && !File.Exists(Path.Combine(workerRoot, "worker-error.txt")) && timer.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(250);
            Require(File.Exists(workerResult) && JsonDocument.Parse(File.ReadAllText(workerResult)).RootElement.GetProperty("Success").GetBoolean(), "copy-worker process result");
            Require(!File.Exists(Path.Combine(actualTarget, "KeaClip.exe")) && !File.Exists(Path.Combine(actualTarget, "KeaClip.Uninstall.exe")) && !File.Exists(Path.Combine(actualTarget, InstallEngine.ReceiptName)) && File.ReadAllText(Path.Combine(actualTarget, "Data", "keep.json")) == "preserved user data" && File.ReadAllText(Path.Combine(actualTarget, "keep-media.txt")) == "preserved media", "actual copy-worker uninstall/data");
            embedded = true; workerTest = "Passed: installed copied uninstaller spawned hidden worker, exited, and worker removed receipt-owned files while preserving user data/media"; checks.Add("Real single-file uninstaller parent-exit/copy-worker/uninstall/data preservation");
        }
        catch (IOException error) when (error.Message.Contains("no embedded app payload", StringComparison.Ordinal)) { }
        Require(!Directory.EnumerateDirectories(run, ".keaclip-stage-*", SearchOption.TopDirectoryOnly).Any() && !Directory.EnumerateDirectories(run, ".keaclip-removal-*", SearchOption.TopDirectoryOnly).Any(), "failed stages cleaned"); checks.Add("All failed install/removal transaction stage files cleaned");
        File.WriteAllText(Path.Combine(run, "self-test-result.json"), JsonSerializer.Serialize(new { Success = true, Checks = checks, Count = checks.Count, LinkCheck = links, EmbeddedPayloadTested = embedded, CopyWorkerTest = workerTest, RegistryAndShortcutsTouched = false, VisibleWindowsOpened = false, Limitations = "Native GUI, real shortcuts/registry and normal installed app launch remain separate checks. Test backups/worker evidence intentionally preserved." }, new JsonSerializerOptions { WriteIndented = true }));
    }
    // The no-click upgrade mode the app starts after Update now (UpgradeMode). Synthetic payloads,
    // no registry or shortcuts, nothing launched; the "running app" is the hidden hold-open process.
    private static void UpgradeChecks(string run, string source, byte[] payload, List<string> checks)
    {
        var target = Path.Combine(run, "UpgradeMode"); var engine = new InstallEngine(); var choices = new InstallChoices(false, false, false);
        using (var stream = new MemoryStream(payload)) engine.Install(stream, target, source, choices);
        Directory.CreateDirectory(Path.Combine(target, "Data")); File.WriteAllText(Path.Combine(target, "Data", "settings.json"), "user settings"); File.WriteAllText(Path.Combine(target, "my clip.mp4"), "user media");
        var request = new UpgradeRequest(target, null, true);
        Stream Payload() => new MemoryStream(payload);
        void MarkInstalledAs(string version) => InstallEngine.WriteReceipt(target, InstallEngine.ReadReceipt(target) with { Version = version });
        UpgradeResult Upgrade(UpgradeRequest value, string? incoming = null, TimeSpan? exitTimeout = null) => UpgradeMode.Run(value, Payload, source, new NoIntegration(), exitTimeout ?? TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), incoming);

        // Arguments: exact form only.
        var parsed = UpgradeMode.Parse(["--upgrade", "--path", target, "--wait-pid", "1234", "--relaunch"]);
        Require(parsed.Root == target && parsed.WaitProcessId == 1234 && parsed.Relaunch, "upgrade arguments");
        foreach (var bad in new string[][] { ["--upgrade"], ["--upgrade", "--path", target, "--wait-pid", "0"], ["--upgrade", "--path", target, "--other"], ["--upgrade", "--path", "relative\\folder"], ["--upgrade", "--path", target, "--path", target] })
            ExpectFailure(() => UpgradeMode.Parse(bad));
        checks.Add("Upgrade mode accepts only its exact arguments");

        // Same version and downgrades are refused before anything changes.
        var receiptBefore = File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName));
        ExpectFailure(() => Upgrade(request));
        MarkInstalledAs("9.0.0"); var newer = File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName));
        ExpectFailure(() => Upgrade(request));
        Require(newer.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))) && !Directory.EnumerateDirectories(run, ".keaclip-stage-*").Any(), "refused upgrade changes nothing");
        Require(!receiptBefore.SequenceEqual(newer), "receipt fixture changed");
        checks.Add("Upgrade mode refuses the same version and a downgrade without changes");

        // A folder without a KeaClip receipt is never upgraded.
        var foreign = Path.Combine(run, "UpgradeForeign"); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "KeaClip.exe"), "not ours");
        ExpectFailure(() => Upgrade(new UpgradeRequest(foreign, null, false)));
        Require(File.ReadAllText(Path.Combine(foreign, "KeaClip.exe")) == "not ours", "foreign folder untouched"); checks.Add("Upgrade mode refuses a folder without a KeaClip receipt");

        // A newer version upgrades, keeping settings, media and the existing integration choices.
        MarkInstalledAs("0.0.1");
        var first = Upgrade(request);
        Require(first.FromVersion == "0.0.1" && first.ToVersion == UpgradeMode.IncomingVersion && InstallEngine.ReadReceipt(target).Version == UpgradeMode.IncomingVersion, "upgrade version recorded");
        Require(File.ReadAllText(Path.Combine(target, "Data", "settings.json")) == "user settings" && File.ReadAllText(Path.Combine(target, "my clip.mp4")) == "user media", "upgrade keeps settings and media");
        Require(InstallEngine.ReadReceipt(target).Files.All(f => InstallEngine.Hash(InstallEngine.Under(target, f.RelativePath)) == f.Sha256), "upgraded files verified");
        checks.Add("Upgrade mode installs a newer version and preserves settings and media");

        // It waits for the running app to exit (a hidden process started from inside the installation).
        MarkInstalledAs("0.0.1");
        var ready = Path.Combine(target, "hold-test-ready.json");
        using (var hold = StartHold(target, 3))
        {
            try
            {
                Require(WaitForFile(ready) && !hold.HasExited, "running app fixture starts");
                var timer = Stopwatch.StartNew();
                Upgrade(request with { WaitProcessId = hold.Id });
                Require(hold.HasExited && timer.Elapsed > TimeSpan.FromSeconds(1) && InstallEngine.ReadReceipt(target).Version == UpgradeMode.IncomingVersion, "upgrade waited for the app to exit");
            }
            finally { if (!hold.HasExited) { hold.Kill(); hold.WaitForExit(10_000); } if (File.Exists(ready)) File.Delete(ready); }
        }
        checks.Add("Upgrade mode waits for the app to exit before changing files");

        // An app that does not exit in time stops the upgrade before any change.
        MarkInstalledAs("0.0.1"); var waiting = File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName));
        using (var hold = StartHold(target, 30))
        {
            try
            {
                Require(WaitForFile(ready), "stuck app fixture starts");
                ExpectFailure(() => Upgrade(request with { WaitProcessId = hold.Id }, exitTimeout: TimeSpan.FromSeconds(1)));
                Require(waiting.SequenceEqual(File.ReadAllBytes(Path.Combine(target, InstallEngine.ReceiptName))) && File.ReadAllText(Path.Combine(target, "Data", "settings.json")) == "user settings", "timed-out upgrade changes nothing");
            }
            finally { if (!hold.HasExited) { hold.Kill(); hold.WaitForExit(10_000); } if (File.Exists(ready)) File.Delete(ready); }
        }
        checks.Add("Upgrade mode stops without changes when the app does not exit");

        // Older rollback backups are thinned out: duplicates go, anything unique stays.
        string? BackupRoot(string backup) { try { return JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(backup, InstallEngine.ReceiptName)))?.Root; } catch (Exception error) when (error is IOException or JsonException) { return null; } }
        IEnumerable<string> Backups() => Directory.EnumerateDirectories(run, ".keaclip-backup-*").Where(b => BackupRoot(b) is { } root && string.Equals(Path.GetFullPath(root), target, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(target, "Data", "settings.json"), "settings changed later");
        MarkInstalledAs("0.0.1"); var latest = Upgrade(request);
        var older = Backups().Where(b => !string.Equals(b, latest.Install.Backup, StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(latest.Install.Backup is not null && Directory.Exists(latest.Install.Backup) && File.Exists(Path.Combine(latest.Install.Backup, "KeaClip.exe")), "newest rollback backup kept complete");
        Require(older.Length > 0 && older.All(b => !File.Exists(Path.Combine(b, "KeaClip.exe")) && !File.Exists(Path.Combine(b, "my clip.mp4")) && File.ReadAllText(Path.Combine(b, "Data", "settings.json")) == "user settings"), "older backups keep only unique files");
        checks.Add("Older rollback backups lose only exact duplicates; unique files and the newest backup are kept");
    }
    // The real single-file installer running --test-upgrade as its own process against the installed embedded payload.
    private static void ActualUpgradeProcessCheck(string actualTarget, string resultFolder, List<string> checks)
    {
        InstallEngine.WriteReceipt(actualTarget, InstallEngine.ReadReceipt(actualTarget) with { Version = "0.0.1" });
        var ready = Path.Combine(actualTarget, "hold-test-ready.json");
        using var hold = StartHold(actualTarget, 3);
        try
        {
            Require(WaitForFile(ready), "installed app fixture starts");
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "--test-upgrade", actualTarget, resultFolder, hold.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
            using var child = Process.Start(info) ?? throw new IOException("The upgrade-mode process could not start.");
            Require(child.WaitForExit(120_000), "upgrade-mode process exits");
            var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(resultFolder, "upgrade-result.json"))).RootElement;
            Require(child.ExitCode == 0 && result.GetProperty("Success").GetBoolean() && result.GetProperty("FromVersion").GetString() == "0.0.1" && result.GetProperty("ToVersion").GetString() == UpgradeMode.IncomingVersion, "upgrade-mode process result");
            Require(string.Equals(result.GetProperty("Relaunch").GetString(), Path.Combine(actualTarget, "KeaClip.exe"), StringComparison.OrdinalIgnoreCase), "upgrade-mode relaunch target");
            Require(hold.HasExited && File.ReadAllText(Path.Combine(actualTarget, "Data", "keep.json")) == "preserved user data" && File.ReadAllText(Path.Combine(actualTarget, "keep-media.txt")) == "preserved media", "upgrade-mode process preserved data");
            Require(InstallEngine.ReadReceipt(actualTarget).Files.All(f => InstallEngine.Hash(InstallEngine.Under(actualTarget, f.RelativePath)) == f.Sha256), "upgrade-mode process files verified");
        }
        finally { if (!hold.HasExited) { hold.Kill(); hold.WaitForExit(10_000); } if (File.Exists(ready)) File.Delete(ready); }
        checks.Add("Real --upgrade process waited for the installed app, upgraded the embedded payload, kept data and named the app to relaunch");
    }
    private static Process StartHold(string target, int seconds)
    {
        var info = new ProcessStartInfo(Path.Combine(target, "KeaClip.Uninstall.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--hold-open-test"); info.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(info) ?? throw new IOException("The hidden running-app test process could not start.");
    }
    private static bool WaitForFile(string path)
    {
        var timer = Stopwatch.StartNew(); while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
        return File.Exists(path);
    }
    private static byte[] MakePayload(Dictionary<string, byte[]> files, bool corruptDigest = false, bool unlisted = false, bool specialFile = false)
    {
        using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var manifest = new StringBuilder();
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry("Package/" + name); if (specialFile && name == "KeaClip.exe") entry.ExternalAttributes = unchecked((int)0xA0000000);
                using (var stream = entry.Open()) stream.Write(bytes);
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); manifest.Append(corruptDigest ? new string('0', 64) : digest).Append("  ").Append(name).Append('\n');
            }
            if (unlisted) { using var extra = zip.CreateEntry("Package/unlisted.txt").Open(); extra.WriteByte(2); }
            using var writer = new StreamWriter(zip.CreateEntry("Package/checksums.sha256").Open(), new UTF8Encoding(false)); writer.Write(manifest.ToString());
        }
        return memory.ToArray();
    }
    private static void ForgeEntryLength(byte[] bytes)
    {
        // Central-directory uncompressed length for first entry: decompression must not trust it.
        for (var index = 0; index <= bytes.Length - 46; index++) if (BitConverter.ToUInt32(bytes, index) == 0x02014B50) { bytes[index + 24] = 1; bytes[index + 25] = 0; bytes[index + 26] = 0; bytes[index + 27] = 0; return; }
        throw new IOException("No ZIP directory fixture found.");
    }
    private static void Require(bool condition, string description) { if (!condition) throw new IOException("Installer self-test failed: " + description); }
    private static void ExpectFailure(Action action) { try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return; } throw new IOException("An unsafe installer action was accepted."); }
    // "Start with Windows": uninstall removes only this installation's own Run value, and puts it
    // back if the uninstall rolls back. An in-memory store stands in for the registry value, so the
    // real Run key is never read or written here.
    private static void StartupEntryChecks(string run, List<string> checks)
    {
        var root = Path.Combine(run, "StartupOwner"); var executable = Path.Combine(root, "KeaClip.exe");
        var receipt = new InstallReceipt("KeaClip", 1, root, "0.0.0-test", DateTimeOffset.UtcNow, [], new Dictionary<string, string>(), false, false);
        foreach (var command in new[] { "\"" + executable + "\"", "\"" + executable + "\" --minimized" })
        {
            var store = new MemoryStartupEntry(command); new WindowsIntegration(store).Remove(receipt);
            Require(store.Value is null && store.Deletes == 1, "uninstall removes the app's own startup entry: " + (command.EndsWith("--minimized", StringComparison.Ordinal) ? "minimized" : "normal"));
        }
        checks.Add("Uninstall removes this installation's Start with Windows entry, with or without --minimized");
        var menu = Path.Combine(run, "StartMenu", "KeaClip"); Directory.CreateDirectory(menu); File.WriteAllText(Path.Combine(menu, "user note.txt"), "kept");
        Require(!WindowsIntegration.RemoveEmptyFolder(menu) && Directory.Exists(menu), "a Start menu folder that still holds anything is kept");
        File.Delete(Path.Combine(menu, "user note.txt"));
        Require(WindowsIntegration.RemoveEmptyFolder(menu) && !Directory.Exists(menu), "the app's empty Start menu folder is removed");
        checks.Add("Uninstall removes the app's own Start menu folder only when it is empty");
        foreach (var foreign in new[] { "\"" + Path.Combine(run, "Elsewhere", "KeaClip.exe") + "\"", "\"" + executable + "\" --other", executable + " --minimized", "\"" + Path.Combine(root, "Other.exe") + "\"" })
        {
            var store = new MemoryStartupEntry(foreign); new WindowsIntegration(store).Remove(receipt);
            Require(store.Value == foreign && store.Deletes == 0 && store.Writes == 0, "a startup entry that starts something else is kept");
        }
        var empty = new MemoryStartupEntry(null); new WindowsIntegration(empty).Remove(receipt); Require(empty.Value is null && empty.Deletes == 0 && empty.Writes == 0, "no startup entry, nothing written");
        checks.Add("Startup entries for another folder, other arguments or another program are never removed");
        var rolled = new MemoryStartupEntry("\"" + executable + "\" --minimized") { Kind = Microsoft.Win32.RegistryValueKind.ExpandString };
        var restore = WindowsIntegration.RemoveOwnedStartupEntry(rolled, root); Require(rolled.Value is null && restore is not null, "owned entry removed before rollback");
        restore!(); Require(rolled.Value == "\"" + executable + "\" --minimized" && rolled.Kind == Microsoft.Win32.RegistryValueKind.ExpandString, "rollback restores the startup entry exactly");
        checks.Add("A rolled-back uninstall restores the startup entry with its value and kind");
    }
    private sealed class MemoryStartupEntry(string? value) : IStartupEntryStore
    {
        public string? Value { get; private set; } = value;
        public Microsoft.Win32.RegistryValueKind Kind { get; set; } = Microsoft.Win32.RegistryValueKind.String;
        public int Deletes { get; private set; }
        public int Writes { get; private set; }
        public (object Value, Microsoft.Win32.RegistryValueKind Kind)? Read() => Value is null ? null : (Value, Kind);
        public void Delete() { Deletes++; Value = null; }
        public void Write(object value, Microsoft.Win32.RegistryValueKind kind) { Writes++; Value = (string)value; Kind = kind; }
    }
    private sealed class FailingIntegration : IInstallIntegration
    {
        public bool RolledBack { get; private set; }
        public InstallReceipt Apply(InstallReceipt receipt, InstallChoices choices) => throw new IOException("Injected integration failure");
        public void Remove(InstallReceipt receipt) { }
        public void Rollback() => RolledBack = true;
    }
    private sealed class RecordingIntegration : IInstallIntegration
    {
        public bool RolledBack { get; private set; }
        public InstallReceipt Apply(InstallReceipt receipt, InstallChoices choices) => receipt with { ProgramsEntry = true, Protocol = true };
        public void Remove(InstallReceipt receipt) { }
        public void Rollback() => RolledBack = true;
    }
    private sealed class RemovalIntegration : IInstallIntegration
    {
        public bool Fail { get; init; }
        public bool RolledBack { get; private set; }
        public int RemoveCalls { get; private set; }
        public InstallReceipt Apply(InstallReceipt receipt, InstallChoices choices) => receipt;
        public void Remove(InstallReceipt receipt) { RemoveCalls++; if (Fail) throw new IOException("Injected uninstall integration failure"); }
        public void Rollback() => RolledBack = true;
    }
}
