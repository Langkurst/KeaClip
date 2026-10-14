using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Klippi.Infrastructure;

// Each media process gets its own job. Windows assigns it atomically at creation,
// before its suspended thread can write anything; descendants cannot outlive the
// owning app. The job handle is deliberately absent from the inherited handle list.
// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
internal sealed class WindowsChildLifetime : IDisposable
{
    private readonly KernelHandle _job;
    internal string JobName { get; }
    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    private WindowsChildLifetime(KernelHandle job, string jobName, Process process, StreamReader output, StreamReader error)
        => (_job, JobName, Process, StandardOutput, StandardError) = (job, jobName, process, output, error);

    internal static WindowsChildLifetime Start(ProcessStartInfo info, Action<int>? beforeResume = null, bool interactive = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var jobName = "KeaClip.WorkspaceJob." + Guid.NewGuid().ToString("N");
        var job = CreateJobObjectW(IntPtr.Zero, jobName);
        var jobError = Marshal.GetLastWin32Error();
        if (job.IsInvalid || jobError == 183) { job.Dispose(); throw new Win32Exception(jobError); }
        Process? process = null;
        StreamReader? output = null, error = null;
        SafeFileHandle? outputRead = null, errorRead = null;
        KernelHandle? nativeProcess = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero;
        var initialized = false;
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway.
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));
            // The durable receipt closes the host-crash handle-close-order gap:
            // a named job exists until its last handle AND all its processes end.
            OwnedTemporaryWorkspaceService.RegisterWriterJob(jobName);
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Check(CreatePipe(out outputRead, out var outputWrite, ref security, 0));
            using (outputWrite)
            {
                Check(CreatePipe(out errorRead, out var errorWrite, ref security, 0));
                using (errorWrite)
                using (var input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero))
                {
                    if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    Check(SetHandleInformation(outputRead, 1, 0));
                    Check(SetHandleInformation(errorRead, 1, 0));
                    nuint bytes = 0;
                    _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
                    if (bytes == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    attributes = Marshal.AllocHGlobal(checked((nint)bytes));
                    Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes));
                    initialized = true;
                    handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                    Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size, outputWrite.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errorWrite.DangerousGetHandle());
                    Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero));
                    jobs = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                    Check(UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                    var startup = new StartupInfoEx
                    {
                        Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x101, Input = input.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(), Error = errorWrite.DangerousGetHandle() },
                        Attributes = attributes
                    };
                    var executable = ResolveExecutable(info.FileName);
                    var command = new StringBuilder(QuoteArgument(executable));
                    foreach (var argument in info.ArgumentList) command.Append(' ').Append(QuoteArgument(argument));
                    // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED, plus BELOW_NORMAL_PRIORITY_CLASS for
                    // long work or NORMAL_PRIORITY_CLASS for short interactive calls: below-normal children got no CPU
                    // for 18-38 s while a game ran, so quick probes could time out.
                    Check(CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, interactive ? 0x08080024u : 0x08084004u, IntPtr.Zero, info.WorkingDirectory, ref startup, out var created));
                    nativeProcess = new KernelHandle(created.Process);
                    using var thread = new KernelHandle(created.Thread);
                    process = Process.GetProcessById(checked((int)created.ProcessId));
                    _ = process.Handle; // Pin the exact process handle before it can exit or its PID can be reused.
                    output = new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, false), info.StandardOutputEncoding ?? Encoding.UTF8);
                    outputRead = null;
                    error = new StreamReader(new FileStream(errorRead, FileAccess.Read, 4096, false), info.StandardErrorEncoding ?? Encoding.UTF8);
                    errorRead = null;
                    var child = new WindowsChildLifetime(job, jobName, process, output, error);
                    beforeResume?.Invoke(process.Id); // A deterministic startup-failure seam; production does not supply it.
                    if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return child;
                }
            }
        }
        catch
        {
            // Creation already assigned the suspended child to this job. Nothing
            // can run during a failed start; wait for actual exit before returning.
            if (nativeProcess is not null)
            {
                _ = TerminateJobObject(job, 1);
                _ = TerminateProcess(nativeProcess, 1);
                _ = WaitForSingleObject(nativeProcess, uint.MaxValue);
            }
            output?.Dispose(); error?.Dispose(); process?.Dispose(); job.Dispose();
            throw;
        }
        finally
        {
            nativeProcess?.Dispose(); outputRead?.Dispose(); errorRead?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
        }
    }

    internal async Task StopAndWaitAsync()
    {
        Check(TerminateJobObject(_job, 1));
        // Wait for descendants as well as the direct process. Cancellation does
        // not interrupt this safety barrier: the engine's staging lease stays held.
        while (true)
        {
            Check(QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero));
            if (accounting.ActiveProcesses == 0) break;
            await Task.Delay(10).ConfigureAwait(false);
        }
        // HasExited/GetExitCodeProcess and job accounting can change before all
        // teardown I/O/handles have finished. Require the native process signal.
        uint state;
        while ((state = WaitForSingleObject(Process.SafeHandle, 0)) == 258) await Task.Delay(10).ConfigureAwait(false);
        if (state != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _job.Dispose();
        // Named jobs are destroyed only after their last handle AND every
        // associated process have ended, including a just-spawned grandchild.
        // Query briefly opens/closes a handle; it never retains or terminates it.
        while (IsJobActive(JobName)) await Task.Delay(10).ConfigureAwait(false);
    }

    internal static bool IsJobActive(string name)
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var job = OpenJobObjectW(4, false, name); // Query only, never assign/terminate someone else's job.
        // Keep even an empty-but-existing job: its last kernel/file teardown may
        // still be pending. Absence is the only positive cleanup evidence.
        return !job.IsInvalid || Marshal.GetLastWin32Error() != 2;
    }

    public void Dispose()
    {
        _job.Dispose();
        StandardOutput.Dispose(); StandardError.Dispose(); Process.Dispose();
    }

    // This is the Windows CRT escaping used by an argument list, never a shell command.
    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(executable);
        var path = new StringBuilder(32768);
        var count = SearchPathW(null, executable, ".exe", (uint)path.Capacity, path, IntPtr.Zero);
        if (count == 0 || count >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        internal KernelHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { internal long ProcessTime, JobTime; internal uint LimitFlags; internal nuint MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveLimit; internal nuint Affinity; internal uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting { internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { internal int Size; internal string? Reserved, Desktop, Title; internal uint X, Y, Width, Height, CharacterWidth, CharacterHeight, Fill, Flags; internal ushort Show, ReservedBytes; internal IntPtr ReservedPointer, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern KernelHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern KernelHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(KernelHandle job, int kind, ref ExtendedLimits information, uint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(KernelHandle job, int kind, out BasicAccounting information, uint bytes, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(KernelHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out KernelHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern KernelHandle CreateFileW(string name, uint access, uint sharing, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint bytes, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(KernelHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint SearchPathW(string? path, string file, string extension, uint capacity, StringBuilder output, IntPtr part);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
