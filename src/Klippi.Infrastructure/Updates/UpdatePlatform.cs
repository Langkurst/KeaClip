using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Klippi.Core;

namespace Klippi.Infrastructure.Updates;

public interface INetworkState
{
    bool IsOnline();
    /// <summary>True on a metered (capped, roaming or over-limit) connection; null when Windows can't say.</summary>
    bool? IsMetered();
}

/// <summary>Windows network status: any usable network, and the connection cost from the Network List Manager.</summary>
public sealed class WindowsNetworkState : INetworkState
{
    private const uint Fixed = 0x2, Variable = 0x4, OverDataLimit = 0x10000, Roaming = 0x40000, ApproachingDataLimit = 0x80000;

    public bool IsOnline()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch (NetworkInformationException) { return true; }
    }

    public bool? IsMetered()
    {
        if (!OperatingSystem.IsWindows()) return null;
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: false);
            if (type is null) return null;
            manager = Activator.CreateInstance(type);
            if (manager is not INetworkCostManager cost) return null;
            cost.GetCost(out var flags, IntPtr.Zero);
            return (flags & (Fixed | Variable | OverDataLimit | Roaming | ApproachingDataLimit)) != 0;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or NotSupportedException or TypeLoadException or UnauthorizedAccessException) { return null; }
        finally { if (manager is not null && OperatingSystem.IsWindows() && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager); }
    }

    // netlistmgr.h: only GetCost is called; the other two methods keep the vtable order.
    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destination);
        void GetDataPlanStatus(IntPtr status, IntPtr destination);
        void SetDestinationAddresses(uint length, IntPtr addresses, short append);
    }
}

public enum SignatureState { Unsigned, Valid, Invalid }

/// <summary>
/// Authenticode check with WinVerifyTrust, offline (no revocation lookups). An unsigned installer is
/// allowed because the release hash is required anyway; a signature that is present but fails
/// verification (tampered, untrusted, or not allowed) is refused.
/// </summary>
public static class Authenticode
{
    private const int NoSignature = unchecked((int)0x800B0100), SubjectFormUnknown = unchecked((int)0x800B0003), ProviderUnknown = unchecked((int)0x800B0001);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureState Check(string path)
    {
        if (!OperatingSystem.IsWindows()) return SignatureState.Unsigned;
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The file to verify is missing.");
        var pathPointer = Marshal.StringToHGlobalUni(fullPath);
        var filePointer = IntPtr.Zero; var dataPointer = IntPtr.Zero;
        try
        {
            var file = new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), FilePath = pathPointer };
            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>()); Marshal.StructureToPtr(file, filePointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2 /* WTD_UI_NONE */, RevocationChecks = 0 /* WTD_REVOKE_NONE */,
                UnionChoice = 1 /* WTD_CHOICE_FILE */, File = filePointer, StateAction = 1 /* WTD_STATEACTION_VERIFY */,
                ProviderFlags = 0x10 | 0x1000 /* WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL: never goes online */,
            };
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustData>()); Marshal.StructureToPtr(data, dataPointer, false);
            var result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, dataPointer);
            // Release the verification state whatever the result was.
            var close = Marshal.PtrToStructure<TrustData>(dataPointer); close.StateAction = 2; /* WTD_STATEACTION_CLOSE */
            Marshal.StructureToPtr(close, dataPointer, true);
            _ = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, dataPointer);
            return result switch
            {
                0 => SignatureState.Valid,
                NoSignature or SubjectFormUnknown or ProviderUnknown => SignatureState.Unsigned,
                _ => SignatureState.Invalid,
            };
        }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            if (filePointer != IntPtr.Zero) Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoData { public uint Size; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks;
        public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference;
        public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }
}

/// <summary>How this copy of KeaClip was installed, read from the files beside KeaClip.exe.</summary>
public sealed record UpdateEnvironment(UpdateInstallKind Kind, string? InstallRoot)
{
    public const string ReceiptName = ".keaclip-install.json";

    public static UpdateEnvironment Detect(string appDirectory)
    {
        var root = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(root, "portable.marker"))) return new(UpdateInstallKind.Portable, root);
        var receipt = Path.Combine(root, ReceiptName);
        try
        {
            if (File.Exists(receipt) && new FileInfo(receipt).Length < 5_000_000 && File.Exists(Path.Combine(root, "KeaClip.Uninstall.exe")))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(receipt));
                var element = document.RootElement;
                if (element.TryGetProperty("ApplicationId", out var id) && id.GetString() == "KeaClip"
                    && element.TryGetProperty("Root", out var saved) && saved.GetString() is { } savedRoot
                    && string.Equals(Path.GetFullPath(savedRoot).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                    return new(UpdateInstallKind.Installed, root);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        return new(UpdateInstallKind.Development, null);
    }
}
