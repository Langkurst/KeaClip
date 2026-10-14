using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Klippi.Installer;

internal static class ReparseTest
{
    // A junction usually needs no symlink privilege. This only creates the owned test path.
    public static bool TryCreateJunction(string link, string target)
    {
        InstallEngine.CheckLinks(Path.GetDirectoryName(link)!); Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target) + "\0"); var printed = Encoding.Unicode.GetBytes(Path.GetFullPath(target) + "\0");
        var bytes = new byte[16 + substitute.Length + printed.Length]; BitConverter.GetBytes(0xA0000003u).CopyTo(bytes, 0); BitConverter.GetBytes(checked((ushort)(8 + substitute.Length + printed.Length))).CopyTo(bytes, 4);
        BitConverter.GetBytes(checked((ushort)(substitute.Length - 2))).CopyTo(bytes, 10); BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(bytes, 12); BitConverter.GetBytes(checked((ushort)(printed.Length - 2))).CopyTo(bytes, 14); substitute.CopyTo(bytes, 16); printed.CopyTo(bytes, 16 + substitute.Length);
        using var handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        var success = !handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
        if (!success) Directory.Delete(link, false); return success;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
