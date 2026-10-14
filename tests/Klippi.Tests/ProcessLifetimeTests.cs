using System.Diagnostics;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

public sealed class WindowsProcessFactAttribute : FactAttribute
{
    public WindowsProcessFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows Job Object process lifetime check."; }
}

public sealed class ProcessLifetimeTests
{
    // Upper bounds that catch a hang; they are not speed assertions.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    [WindowsProcessFact]
    public async Task Cancellation_returns_only_after_child_and_grandchild_writers_exit()
    {
        using var fixture = new ProcessFixture(); using var cancellation = new CancellationTokenSource();
        var running = new ProcessRunner().RunAsync(fixture.Request(), cancellationToken: cancellation.Token);
        await fixture.ObserveWritersAsync(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(HangGuard));
        fixture.AssertWritersStopped();
    }

    [WindowsProcessFact]
    public async Task Timeout_returns_only_after_the_whole_job_exits()
    {
        // The writers must be seen running before the timeout fires. On a heavily loaded
        // machine three nested PowerShell start-ups can outlast a short timeout; that run
        // proves nothing either way, so it is repeated with more time instead of failing.
        foreach (var seconds in new[] { 6, 15, 40 })
        {
            using var fixture = new ProcessFixture(); using var stopObserving = new CancellationTokenSource();
            var running = new ProcessRunner().RunAsync(fixture.Request(timeout: TimeSpan.FromSeconds(seconds)));
            var observed = fixture.ObserveWritersAsync(stopObserving.Token);
            if (await Task.WhenAny(observed, running) != observed || !observed.IsCompletedSuccessfully)
            {
                stopObserving.Cancel();
                try { await observed; } catch (Exception) { }
                try { await running.WaitAsync(HangGuard); } catch (MediaOperationException) { }
                continue;
            }
            var error = await Assert.ThrowsAsync<MediaOperationException>(() => running.WaitAsync(HangGuard));
            Assert.Contains("too long", error.Message); fixture.AssertWritersStopped();
            return;
        }
        Assert.Fail("The machine was too busy for the synthetic writers to start within a 40 s timeout.");
    }

    [WindowsProcessFact]
    public async Task Parent_success_also_stops_leftover_descendants_before_returning()
    {
        using var fixture = new ProcessFixture();
        var running = new ProcessRunner().RunAsync(fixture.Request("child-exit"));
        await fixture.ObserveWritersAsync(); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "exit-parent"), "exit");
        Assert.Equal(0, (await running.WaitAsync(HangGuard)).ExitCode); fixture.AssertWritersStopped();
    }

    [WindowsProcessFact]
    public async Task Output_callback_failure_stops_children_before_reporting_failure()
    {
        using var fixture = new ProcessFixture();
        var running = new ProcessRunner().RunAsync(fixture.Request("child-output"), stdout: _ => throw new InvalidDataException("Injected output consumer failure."));
        await fixture.ObserveWritersAsync(); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "emit-output"), "emit");
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => running.WaitAsync(HangGuard));
        Assert.Contains("unreadable output", error.Message); fixture.AssertWritersStopped();
    }

    [WindowsProcessFact]
    public async Task Killing_only_runner_host_kills_its_private_child_job()
    {
        using var fixture = new ProcessFixture();
        var launcher = fixture.StartHost(); await fixture.ObserveWritersAsync();
        // A PowerShell .NET-tool shim runs the real host as its own child, so kill
        // the process that actually owns the job handle, never its whole tree.
        var host = await fixture.TrackFromFileAsync("host.pid");
        host.Kill(); // Deliberately NOT entireProcessTree: the private job must stop descendants.
        await host.WaitForExitAsync().WaitAsync(HangGuard);
        await launcher.WaitForExitAsync().WaitAsync(HangGuard);
        await fixture.WaitForWritersToExitAsync(); fixture.AssertWritersStopped();
        Assert.False(Process.GetCurrentProcess().HasExited); // The test/coordinating process was never assigned to the job.
    }

    [WindowsProcessFact]
    public async Task Named_job_query_stays_active_until_all_writers_stop_and_handles_uncertainty_safely()
    {
        using var fixture = new ProcessFixture();
        using var child = WindowsChildLifetime.Start(fixture.StartInfo("child"), interactive: true);
        await fixture.ObserveWritersAsync();
        Assert.True(WindowsChildLifetime.IsJobActive(child.JobName));
        await child.StopAndWaitAsync().WaitAsync(HangGuard);
        fixture.AssertWritersStopped(); Assert.False(WindowsChildLifetime.IsJobActive(child.JobName));
        child.Dispose(); Assert.False(WindowsChildLifetime.IsJobActive(child.JobName));
        Assert.True(WindowsChildLifetime.IsJobActive("invalid\\nested\\job"));
    }

    [WindowsProcessFact]
    public void Startup_failure_before_resume_waits_for_the_suspended_child_and_never_runs_it()
    {
        using var fixture = new ProcessFixture(); Process? suspended = null;
        var info = fixture.StartInfo("grandchild");
        Assert.Throws<InvalidOperationException>(() => WindowsChildLifetime.Start(info, id =>
        {
            suspended = fixture.Track(id);
            throw new InvalidOperationException("Injected initialization failure before ResumeThread.");
        }));
        Assert.NotNull(suspended); Assert.True(suspended.HasExited);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "grandchild.pid")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "writer.txt")));
    }

    [WindowsProcessFact]
    public async Task Missing_executable_and_invalid_directory_report_start_failure()
    {
        using var fixture = new ProcessFixture(); var runner = new ProcessRunner();
        await Assert.ThrowsAsync<MediaOperationException>(() => runner.RunAsync(new(Path.Combine(fixture.Root, "missing.exe"), [])));
        await Assert.ThrowsAsync<MediaOperationException>(() => runner.RunAsync(new(fixture.Powershell, ["-NoProfile"], Path.Combine(fixture.Root, "missing-directory"))));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "writer.txt")));
    }

    [WindowsProcessFact]
    public async Task Native_argument_list_round_trips_empty_quotes_unicode_and_trailing_backslashes()
    {
        using var fixture = new ProcessFixture();
        var script = Path.Combine(fixture.Root, "echo.ps1");
        await File.WriteAllTextAsync(script, "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Values)\n[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n[Console]::WriteLine((ConvertTo-Json -InputObject @($Values) -Compress))\n[Console]::Error.WriteLine('diagnostic')");
        string[] values = ["", "one two", "quote\"inside", "backslash\\\"quoted", "trailing slash \\", "kiwi 日本語", "; Start-Process imaginary-program"];
        var result = await new ProcessRunner().RunAsync(new(fixture.Powershell, ["-NoProfile", "-NonInteractive", "-File", script, .. values], fixture.Root));
        Assert.Equal(0, result.ExitCode); Assert.Equal(values, JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim()));
        Assert.Equal("diagnostic", result.StandardError.Trim());
    }

    private sealed class ProcessFixture : IDisposable
    {
        internal static readonly TimeSpan ReadinessBudget = TimeSpan.FromSeconds(90);
        private readonly List<Process> _owned = [];
        private readonly string _workspace;
        internal string Root { get; }
        internal string Powershell { get; }
        private string Script => Path.Combine(Root, "children.ps1");

        internal ProcessFixture()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Klippi.sln"))) directory = directory.Parent;
            _workspace = directory?.FullName ?? throw new InvalidOperationException("Process checks must run from this workspace.");
            Root = Path.Combine(_workspace, "artifacts", "process-lifetime-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            Powershell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")).FirstOrDefault(File.Exists)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            if (!File.Exists(Powershell)) throw new FileNotFoundException("The hidden process fixture requires PowerShell 7.", Powershell);
            File.WriteAllText(Script, ChildScript);
        }

        internal ProcessRequest Request(string mode = "child", TimeSpan? timeout = null)
            => new(Powershell, Arguments(mode), Root, timeout ?? TimeSpan.FromSeconds(30), Interactive: true); // Lifetime, not priority, is under test (see ProcessPriorityTests); normal priority keeps the helpers from starving on a busy PC.
        private string[] Arguments(string mode) => ["-NoProfile", "-NonInteractive", "-File", Script, "-Mode", mode, "-Root", Root, "-Executable", Powershell, "-AssemblyFolder", AppContext.BaseDirectory];
        internal ProcessStartInfo StartInfo(string mode)
        {
            var info = new ProcessStartInfo(Powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Root };
            foreach (var argument in Arguments(mode)) info.ArgumentList.Add(argument);
            return info;
        }
        internal Process StartHost()
        {
            var process = Process.Start(StartInfo("host")) ?? throw new InvalidOperationException("Synthetic runner host failed to start.");
            _ = process.Handle; _owned.Add(process); return process;
        }
        internal Process Track(int id)
        {
            var process = Process.GetProcessById(id); _ = process.Handle; _owned.Add(process); return process;
        }
        internal async Task<Process> TrackFromFileAsync(string name, CancellationToken cancellationToken = default)
        {
            // Readiness budgets are upper bounds only: under heavy CPU load (parallel builds,
            // FFmpeg encodes) nested PowerShell start-up was measured taking over 15 s.
            var path = Path.Combine(Root, name); var watch = Stopwatch.StartNew();
            while (!File.Exists(path) && watch.Elapsed < ReadinessBudget) await Task.Delay(20, cancellationToken);
            Assert.True(File.Exists(path), "Synthetic process did not report readiness: " + name);
            var text = await File.ReadAllTextAsync(path);
            while (string.IsNullOrWhiteSpace(text) && watch.Elapsed < ReadinessBudget) { await Task.Delay(20); text = await File.ReadAllTextAsync(path); }
            return Track(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        }
        internal async Task ObserveWritersAsync(CancellationToken cancellationToken = default)
        {
            foreach (var name in new[] { "child.pid", "grandchild.pid" }) await TrackFromFileAsync(name, cancellationToken);
            var writer = Path.Combine(Root, "writer.txt"); var elapsed = Stopwatch.StartNew();
            while ((!File.Exists(writer) || new FileInfo(writer).Length == 0) && elapsed.Elapsed < ReadinessBudget) await Task.Delay(20, cancellationToken);
            Assert.True(File.Exists(writer) && new FileInfo(writer).Length > 0); Assert.All(_owned, p => Assert.False(p.HasExited));
        }
        internal async Task WaitForWritersToExitAsync() => await Task.WhenAll(_owned.Select(p => p.WaitForExitAsync())).WaitAsync(HangGuard);
        internal void AssertWritersStopped()
        {
            Assert.All(_owned, p => Assert.True(p.HasExited));
            using var exclusive = new FileStream(Path.Combine(Root, "writer.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        public void Dispose()
        {
            foreach (var process in _owned)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(15_000); } }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            if (!Path.GetFullPath(Root).StartsWith(_workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe process-fixture cleanup.");
            // Terminated PowerShell teardown can briefly retain the working-directory
            // handle after its writer processes have signalled exit. Retry only that
            // specific sharing condition; writer-exit assertions above are immediate.
            var cleanup = Stopwatch.StartNew();
            while (Directory.Exists(Root))
            {
                try { Directory.Delete(Root, true); }
                catch (IOException error) when ((error.HResult & 0xFFFF) == 32 && cleanup.Elapsed < TimeSpan.FromSeconds(20)) { Thread.Sleep(20); }
            }
        }
        private const string ChildScript = """
            param([string]$Mode, [string]$Root, [string]$Executable, [string]$AssemblyFolder)
            $ErrorActionPreference = 'Stop'
            if ($Mode -eq 'host') {
                [IO.File]::WriteAllText((Join-Path $Root 'host.pid'), [string]$PID)
                [void][Reflection.Assembly]::LoadFrom((Join-Path $AssemblyFolder 'Klippi.Core.dll'))
                [void][Reflection.Assembly]::LoadFrom((Join-Path $AssemblyFolder 'Klippi.Infrastructure.dll'))
                $arguments = [string[]]@('-NoProfile','-NonInteractive','-File',$PSCommandPath,'-Mode','child','-Root',$Root,'-Executable',$Executable,'-AssemblyFolder',$AssemblyFolder)
                $request = [Klippi.Core.ProcessRequest]::new($Executable, $arguments, $Root, [TimeSpan]::FromSeconds(60), 65536, $true)
                $runner = [Klippi.Infrastructure.ProcessRunner]::new()
                [void]$runner.RunAsync($request, $null, $null, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
                exit 0
            }
            if ($Mode -eq 'grandchild') {
                [IO.File]::WriteAllText((Join-Path $Root 'grandchild.pid'), [string]$PID)
                for ($index = 0; $index -lt 3000; $index++) { [IO.File]::AppendAllText((Join-Path $Root 'writer.txt'), 'writer active' + [Environment]::NewLine); Start-Sleep -Milliseconds 20 }
                exit 0
            }
            [IO.File]::WriteAllText((Join-Path $Root 'child.pid'), [string]$PID)
            $info = [Diagnostics.ProcessStartInfo]::new($Executable)
            $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            foreach ($argument in @('-NoProfile','-NonInteractive','-File',$PSCommandPath,'-Mode','grandchild','-Root',$Root,'-Executable',$Executable,'-AssemblyFolder',$AssemblyFolder)) { $info.ArgumentList.Add($argument) }
            $grandchild = [Diagnostics.Process]::Start($info); $grandchild.Dispose()
            for ($index = 0; $index -lt 1200; $index++) {
                if ($Mode -eq 'child-exit' -and [IO.File]::Exists((Join-Path $Root 'exit-parent'))) { exit 0 }
                if ($Mode -eq 'child-output' -and [IO.File]::Exists((Join-Path $Root 'emit-output'))) { [Console]::WriteLine('test output'); $Mode = 'child' }
                Start-Sleep -Milliseconds 50
            }
            """;
    }
}
