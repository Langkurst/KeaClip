using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Klippi.Installer;

/// <summary>
/// The app's own "Start with Windows" value: HKCU\Software\Microsoft\Windows\CurrentVersion\Run,
/// value name KeaClip. Implementations never read or change any other value in that key.
/// </summary>
public interface IStartupEntryStore
{
    (object Value, RegistryValueKind Kind)? Read();
    void Delete();
    void Write(object value, RegistryValueKind kind);
}

/// <summary>The real Run-key value KeaClip.Desktop writes (DesktopPlatform.SetStartup).</summary>
public sealed class RunKeyStartupEntry : IStartupEntryStore
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "KeaClip";
    public (object Value, RegistryValueKind Kind)? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value ? (value, key.GetValueKind(ValueName)) : null;
    }
    public void Delete() { using var key = Registry.CurrentUser.OpenSubKey(RunKey, true); key?.DeleteValue(ValueName, false); }
    public void Write(object value, RegistryValueKind kind) { using var key = Registry.CurrentUser.CreateSubKey(RunKey); key.SetValue(ValueName, value, kind); }
}

public sealed class WindowsIntegration : IInstallIntegration
{
    private const string ProtocolKey = @"Software\Classes\keaclip";
    private const string ProgramsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KeaClip";
    private readonly IStartupEntryStore _startup;
    private Action? _rollback;
    public WindowsIntegration(IStartupEntryStore? startup = null) => _startup = startup ?? new RunKeyStartupEntry();

    /// <summary>
    /// True when a Run command starts this installation's KeaClip.exe exactly as the app writes it:
    /// the quoted executable path, optionally followed by --minimized. Anything else (another folder,
    /// a portable copy, other arguments) belongs to someone else and is never removed.
    /// </summary>
    public static bool IsOwnStartupCommand(string? command, string installRoot)
    {
        var text = command?.Trim() ?? "";
        if (text.Length < 3 || text[0] != '"') return false;
        var close = text.IndexOf('"', 1);
        if (close < 2) return false;
        var arguments = text[(close + 1)..].Trim();
        if (arguments.Length > 0 && !arguments.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) return false;
        try { return string.Equals(Path.GetFullPath(text[1..close]), Path.GetFullPath(Path.Combine(installRoot, "KeaClip.exe")), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// Removes the startup value when it starts this installation, and returns how to put it back
    /// exactly (value and kind) if the uninstall rolls back; null when nothing was removed.
    /// </summary>
    public static Action? RemoveOwnedStartupEntry(IStartupEntryStore startup, string installRoot)
    {
        if (startup.Read() is not { Value: string command } before || !IsOwnStartupCommand(command, installRoot)) return null;
        startup.Delete();
        return () => { if (startup.Read() is null) startup.Write(before.Value, before.Kind); };
    }
    public InstallReceipt Apply(InstallReceipt receipt, InstallChoices choices)
    {
        var executable = Path.Combine(receipt.Root, "KeaClip.exe"); var uninstaller = Path.Combine(receipt.Root, "KeaClip.Uninstall.exe");
        var command = "\"" + executable + "\" \"%1\"";
        using (var existing = Registry.CurrentUser.OpenSubKey(ProgramsKey))
            if (existing is not null && !string.Equals(existing.GetValue("InstallLocation") as string, receipt.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Another KeaClip installation is registered. Upgrade that folder instead.");
        using (var existingProtocol = Registry.CurrentUser.OpenSubKey(ProtocolKey))
        using (var existing = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command"))
            if (choices.RegisterProtocol && existingProtocol is not null && (existingProtocol.ValueCount > 0 || existingProtocol.SubKeyCount > 0) && !string.Equals(existing?.GetValue("") as string, command, StringComparison.OrdinalIgnoreCase)) throw new IOException("The KeaClip link handler belongs to another installation. Uncheck link registration or upgrade that installation.");
        var beforeProtocol = Snapshot(ProtocolKey); var beforePrograms = Snapshot(ProgramsKey); var previousLinks = new Dictionary<string, byte[]?>();
        _rollback = () => { Restore(ProtocolKey, beforeProtocol); Restore(ProgramsKey, beforePrograms); foreach (var (path, bytes) in previousLinks) { if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes); } };
        var links = new Dictionary<string, string>(receipt.Shortcuts, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (choices.StartMenuShortcut)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "KeaClip"); InstallEngine.CheckLinks(folder); Directory.CreateDirectory(folder);
                CreateShortcut(Path.Combine(folder, "KeaClip.lnk"), executable, "", receipt.Root, previousLinks, links);
                CreateShortcut(Path.Combine(folder, "Uninstall KeaClip.lnk"), uninstaller, "--uninstall", receipt.Root, previousLinks, links);
            }
            if (choices.DesktopShortcut) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KeaClip.lnk"), executable, "", receipt.Root, previousLinks, links);
            if (choices.RegisterProtocol)
            {
                using var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey); protocol.SetValue("", "URL:KeaClip Protocol"); protocol.SetValue("URL Protocol", "");
                using var icon = protocol.CreateSubKey("DefaultIcon"); icon.SetValue("", executable + ",0");
                using var launch = protocol.CreateSubKey(@"shell\open\command"); launch.SetValue("", command);
            }
            using var programs = Registry.CurrentUser.CreateSubKey(ProgramsKey);
            programs.SetValue("DisplayName", "KeaClip"); programs.SetValue("DisplayVersion", receipt.Version); programs.SetValue("Publisher", "Langkurst"); programs.SetValue("InstallLocation", receipt.Root);
            programs.SetValue("DisplayIcon", executable + ",0"); programs.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall"); programs.SetValue("NoModify", 1, RegistryValueKind.DWord); programs.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            return receipt with { Shortcuts = links, Protocol = choices.RegisterProtocol || receipt.Protocol, ProgramsEntry = true };
        }
        catch { Rollback(); throw; }
    }
    public void Commit() => _rollback = null;
    internal static bool RemoveEmptyFolder(string folder)
    {
        if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) return false;
        InstallEngine.CheckLinks(folder); Directory.Delete(folder); return true;
    }
    public void Rollback() { var action = _rollback; _rollback = null; action?.Invoke(); }
    public void Remove(InstallReceipt receipt)
    {
        var protocolBefore = Snapshot(ProtocolKey); var programsBefore = Snapshot(ProgramsKey); var linksBefore = new Dictionary<string, byte[]>(); Action? restoreStartup = null;
        _rollback = () => { Restore(ProtocolKey, protocolBefore); Restore(ProgramsKey, programsBefore); foreach (var pair in linksBefore) { Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!); File.WriteAllBytes(pair.Key, pair.Value); } restoreStartup?.Invoke(); };
        foreach (var (path, digest) in receipt.Shortcuts)
        {
            if (!AllowedShortcut(path)) continue; InstallEngine.CheckLinks(path);
            if (File.Exists(path) && InstallEngine.Hash(path) == digest) { linksBefore[path] = File.ReadAllBytes(path); File.Delete(path); }
        }
        // The Start menu folder is this app's own: once its shortcuts are gone, an empty folder is removed too
        // (seen in a real uninstall: an empty "KeaClip" folder stayed in the Start menu). Never a non-empty one.
        var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "KeaClip");
        if (receipt.Shortcuts.Keys.Any(path => string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), menu, StringComparison.OrdinalIgnoreCase))) RemoveEmptyFolder(menu);
        var expected = "\"" + Path.Combine(receipt.Root, "KeaClip.exe") + "\" \"%1\"";
        using (var command = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command"))
        {
            if (string.Equals(command?.GetValue("") as string, expected, StringComparison.OrdinalIgnoreCase))
            {
                RemoveValue(ProtocolKey + @"\shell\open\command", "", expected); RemoveEmptyKey(ProtocolKey + @"\shell\open\command"); RemoveEmptyKey(ProtocolKey + @"\shell\open"); RemoveEmptyKey(ProtocolKey + @"\shell");
                RemoveValue(ProtocolKey + @"\DefaultIcon", "", Path.Combine(receipt.Root, "KeaClip.exe") + ",0"); RemoveEmptyKey(ProtocolKey + @"\DefaultIcon");
                RemoveValue(ProtocolKey, "", "URL:KeaClip Protocol"); RemoveValue(ProtocolKey, "URL Protocol", ""); RemoveEmptyKey(ProtocolKey);
            }
        }
        using (var programs = Registry.CurrentUser.OpenSubKey(ProgramsKey))
        {
            if (string.Equals(programs?.GetValue("InstallLocation") as string, receipt.Root, StringComparison.OrdinalIgnoreCase))
            {
                var expectedValues = new Dictionary<string, object> { ["DisplayName"] = "KeaClip", ["DisplayVersion"] = receipt.Version, ["Publisher"] = "Langkurst", ["InstallLocation"] = receipt.Root, ["DisplayIcon"] = Path.Combine(receipt.Root, "KeaClip.exe") + ",0", ["UninstallString"] = "\"" + Path.Combine(receipt.Root, "KeaClip.Uninstall.exe") + "\" --uninstall", ["NoModify"] = 1, ["NoRepair"] = 1 };
                foreach (var pair in expectedValues) RemoveValue(ProgramsKey, pair.Key, pair.Value); RemoveEmptyKey(ProgramsKey);
            }
        }
        // "Start with Windows" must not keep pointing at the removed KeaClip.exe. Only this
        // installation's own value is removed; it comes back if the uninstall rolls back.
        restoreStartup = RemoveOwnedStartupEntry(_startup, receipt.Root);
    }
    private static bool AllowedShortcut(string path)
    {
        var full = Path.GetFullPath(path); var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KeaClip.lnk"); var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "KeaClip");
        return full.Equals(desktop, StringComparison.OrdinalIgnoreCase) || full.Equals(Path.Combine(folder, "KeaClip.lnk"), StringComparison.OrdinalIgnoreCase) || full.Equals(Path.Combine(folder, "Uninstall KeaClip.lnk"), StringComparison.OrdinalIgnoreCase);
    }
    private static void CreateShortcut(string path, string target, string arguments, string workingDirectory, Dictionary<string, byte[]?> originals, Dictionary<string, string> receipts)
    {
        if (!AllowedShortcut(path)) throw new IOException("Unexpected shortcut path."); InstallEngine.CheckLinks(path);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
        object shellObject = Activator.CreateInstance(type)!; object? shortcutObject = null;
        try
        {
            dynamic shell = shellObject; shortcutObject = shell.CreateShortcut(path); dynamic shortcut = shortcutObject;
            if (File.Exists(path) && !string.Equals((string)shortcut.TargetPath, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("An unrelated shortcut already has this name.");
            originals[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
            shortcut.TargetPath = target; shortcut.Arguments = arguments; shortcut.WorkingDirectory = workingDirectory; shortcut.IconLocation = target + ",0"; shortcut.Description = "KeaClip media app"; shortcut.Save(); receipts[path] = InstallEngine.Hash(path);
        }
        finally { if (shortcutObject is not null) Marshal.FinalReleaseComObject(shortcutObject); Marshal.FinalReleaseComObject(shellObject); }
    }
    private sealed record RegistryTree(Dictionary<string, (object Value, RegistryValueKind Kind)> Values, Dictionary<string, RegistryTree> Children);
    private static RegistryTree? Snapshot(string path) { using var key = Registry.CurrentUser.OpenSubKey(path); return key is null ? null : Snapshot(key); }
    private static RegistryTree Snapshot(RegistryKey key)
    {
        var values = key.GetValueNames().ToDictionary(n => n, n => (key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(n)));
        var children = new Dictionary<string, RegistryTree>(); foreach (var name in key.GetSubKeyNames()) { using var child = key.OpenSubKey(name)!; children[name] = Snapshot(child); } return new(values, children);
    }
    private static void Restore(string path, RegistryTree? tree) { Registry.CurrentUser.DeleteSubKeyTree(path, false); if (tree is null) return; using var key = Registry.CurrentUser.CreateSubKey(path); Restore(key, tree); }
    private static void Restore(RegistryKey key, RegistryTree tree) { foreach (var (name, data) in tree.Values) key.SetValue(name, data.Value, data.Kind); foreach (var (name, child) in tree.Children) { using var sub = key.CreateSubKey(name); Restore(sub, child); } }
    private static void RemoveValue(string path, string name, object value) { using var key = Registry.CurrentUser.OpenSubKey(path, true); if (Equals(key?.GetValue(name), value)) key?.DeleteValue(name, false); }
    private static void RemoveEmptyKey(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path)) { if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0) return; }
        Registry.CurrentUser.DeleteSubKey(path, false);
    }
}
