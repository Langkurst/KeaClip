using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Core;

namespace Klippi.Infrastructure.Sharing;

/// <summary>
/// Keeps the watch link setup in <c>watch-links.json</c> in KeaClip's data folder. The secret access key is stored only as a
/// Windows DPAPI blob bound to the current user (with KeaClip-specific entropy), never as plain text; it is decrypted only
/// for an upload and never logged or returned to the UI.
/// </summary>
public sealed class WatchLinkSettingsStore
{
    internal const string FileName = "watch-links.json";
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("KeaClip watch link storage secret v1");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WatchLinkSettingsStore(string dataRoot) : this(dataRoot, new WindowsUserSecretProtector()) { }
    internal WatchLinkSettingsStore(string dataRoot, ISecretProtector protector)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("A data folder is required.", nameof(dataRoot));
        _path = Path.Combine(Path.GetFullPath(dataRoot), FileName);
        _protector = protector;
    }

    internal string FilePath => _path;

    public async Task<WatchLinkSetupState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return record is null ? new WatchLinkSetupState(null, false) : new WatchLinkSetupState(record.ToSetup(), !string.IsNullOrEmpty(record.ProtectedSecret));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Saves the setup; a null <paramref name="secret"/> keeps the protected secret already stored.</summary>
    public async Task SaveAsync(WatchLinkSetup setup, string? secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.Problems() is { Count: > 0 } problems) throw new MediaOperationException("The watch link setup was not saved.", problems[0]);
        if (secret is not null && !WatchLinkSetup.ValidSecret(secret)) throw new MediaOperationException("The watch link setup was not saved.", "The secret access key must be 16 to 128 printable characters without spaces.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? protectedSecret = secret is null ? (await ReadAsync(cancellationToken).ConfigureAwait(false))?.ProtectedSecret : Protect(secret);
            var record = Record.From(setup, protectedSecret);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Written beside the target, then moved over it, so a crash never leaves a half-written file.
            string temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, Json), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { File.Delete(_path); }
        finally { _gate.Release(); }
    }

    /// <summary>The decrypted secret for one upload. Callers keep it only for that upload.</summary>
    internal async Task<(WatchLinkSetup Setup, string Secret)> LoadForUploadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (record?.ToSetup() is not { } setup || string.IsNullOrEmpty(record.ProtectedSecret))
                throw new MediaOperationException("Watch links are not set up yet.", "Open Settings, Watch links, and enter your storage details first.");
            string secret;
            try { secret = _protector.Unprotect(record.ProtectedSecret, Entropy); }
            catch (Exception error) when (error is CryptographicException or FormatException or Win32Exception)
            {
                throw new MediaOperationException("The saved storage secret could not be unlocked.", "It was saved by another Windows user or on another PC. Enter the secret access key again in Settings.");
            }
            return (setup, secret);
        }
        finally { _gate.Release(); }
    }

    private string Protect(string secret) => _protector.Protect(secret, Entropy);

    private async Task<Record?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false));
            return record?.ToSetup() is null ? null : record;
        }
        catch (JsonException) { return null; }
    }

    internal sealed record Record(string StorageEndpoint, string Bucket, string AccessKeyId, string PlayerPage, int RetentionDays, string Region, string? ProtectedSecret)
    {
        public static Record From(WatchLinkSetup setup, string? protectedSecret)
            => new(setup.StorageEndpoint.AbsoluteUri, setup.Bucket, setup.AccessKeyId, setup.PlayerPage.AbsoluteUri, setup.RetentionDays, setup.Region, protectedSecret);

        public WatchLinkSetup? ToSetup()
        {
            if (!Uri.TryCreate(StorageEndpoint, UriKind.Absolute, out var endpoint) || !Uri.TryCreate(PlayerPage, UriKind.Absolute, out var page)) return null;
            var setup = new WatchLinkSetup(endpoint, Bucket ?? "", AccessKeyId ?? "", page, RetentionDays, Region ?? "auto");
            return setup.Problems().Count == 0 ? setup : null;
        }
    }
}

internal interface ISecretProtector
{
    string Protect(string secret, byte[] entropy);
    string Unprotect(string protectedSecret, byte[] entropy);
}

/// <summary>Windows DPAPI (CryptProtectData) for the current user, without UI. The blob is stored as base64.</summary>
internal sealed class WindowsUserSecretProtector : ISecretProtector
{
    private const int UiForbidden = 0x1;

    public string Protect(string secret, byte[] entropy)
    {
        byte[] plain = Encoding.UTF8.GetBytes(secret);
        try { return Convert.ToBase64String(Transform(plain, entropy, protect: true)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string Unprotect(string protectedSecret, byte[] entropy)
    {
        byte[] plain = Transform(Convert.FromBase64String(protectedSecret), entropy, protect: false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected secret storage needs Windows.");
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var data = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var extra = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref data, null, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // The unprotected secret must not linger in unmanaged memory.
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
