using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

public sealed class RelayTests
{
    [AvaloniaFact]
    public async Task Actual_current_user_pipe_prefills_on_dispatcher_without_starting_downloads()
    {
        using var owner = CreateOwner();
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Listen(argument =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            model.AcceptLaunchArgument(argument); received.TrySetResult(argument);
        });
        const string url = "https://example.com/media?v=one&list=two";
        await ForwardFromAnotherThread("keaclip://download?url=" + Uri.EscapeDataString(url));
        Assert.Equal(url, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(url, model.Url); Assert.Empty(fake.Downloads.Requests); Assert.Equal(0, fake.Downloads.Analyses);
    }

    [AvaloniaFact]
    public async Task Concurrent_secondaries_have_one_owner_and_each_forward_once()
    {
        using var owner = CreateOwner();
        var received = new List<string>();
        owner.Listen(received.Add);
        var expected = Enumerable.Range(0, 8).Select(i => "https://example.com/media/" + i).ToArray();
        await Task.WhenAll(expected.Select(ForwardFromAnotherThread));
        await Until(() => received.Count == expected.Length);
        Assert.Equal(expected.Order(), received.Order());
    }

    [AvaloniaFact]
    public async Task Restart_acquires_unlocked_mutex_while_secondary_handle_still_exists()
    {
        using var original = CreateOwner();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var retained = Task.Run(() =>
        {
            using var secondary = new SingleInstanceRelay();
            ready.TrySetResult(secondary.IsOwner);
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Retained relay handle was not released.");
        });
        try
        {
            Assert.False(await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            original.Dispose();
            using var restarted = new SingleInstanceRelay();
            Assert.True(restarted.IsOwner);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            restarted.Listen(value => received.TrySetResult(value));
            await ForwardFromAnotherThread("https://example.com/restarted");
            Assert.Equal("https://example.com/restarted", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); await retained; }
    }

    [AvaloniaFact]
    public async Task Abandoned_owner_mutex_is_recovered_after_owner_thread_exits()
    {
        using var initial = CreateOwner(); var name = initial.PipeName; initial.Dispose();
        var ready = new TaskCompletionSource<Mutex>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Mutex? handle = null;
            try
            {
                handle = new Mutex(false, "Local\\" + name);
                if (!handle.WaitOne(0)) throw new InvalidOperationException("Could not acquire the test owner mutex.");
                ready.TrySetResult(handle); // Deliberately terminate its owning thread, retaining only the handle.
            }
            catch (Exception error) { handle?.Dispose(); ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        using var abandonedHandle = await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(await Task.Run(() => thread.Join(TimeSpan.FromSeconds(3))));
        using var recovered = new SingleInstanceRelay(); Assert.True(recovered.IsOwner);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        recovered.Listen(value => received.TrySetResult(value));
        await ForwardFromAnotherThread("--show");
        Assert.Equal("--show", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task Disposal_cancels_idle_connection_and_restarted_owner_receives_fresh_links()
    {
        using var original = CreateOwner();
        var previous = new List<string>(); original.Listen(previous.Add);
        await using var stalled = new NamedPipeClientStream(".", original.PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await stalled.ConnectAsync(deadline.Token);
        original.Dispose(); original.Dispose();
        await stalled.DisposeAsync();
        using var restarted = new SingleInstanceRelay(); Assert.True(restarted.IsOwner);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        restarted.Listen(value => received.TrySetResult(value));
        await ForwardFromAnotherThread("https://example.com/after-cancel");
        Assert.Equal("https://example.com/after-cancel", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(previous);
    }

    [AvaloniaFact]
    public async Task Hostile_wire_requests_are_rejected_and_listener_remains_usable()
    {
        using var owner = CreateOwner(); var received = new List<string>(); owner.Listen(received.Add);
        foreach (var argument in new[]
        {
            new string('x', 32_769), "cmd.exe /c calc.exe", "--cookies private.txt", "file:///C:/private.txt",
            "https://name:password@example.com/media", "https://example.com/media\n--exec=calc.exe",
            "keaclip://download?url=https%3A%2F%2Fexample.com%2Fbad&cookies=private.txt",
            "keaclip://download?url=https%3A%2F%2Fexample.com%2Fbad&command=calc.exe"
        }) await SendRaw(owner.PipeName, argument);
        await ForwardFromAnotherThread("https://example.com/still-working");
        await Until(() => received.Contains("https://example.com/still-working"));
        Assert.Equal(["https://example.com/still-working"], received);
    }

    [AvaloniaFact]
    public void Listener_is_started_once_and_disposed_relay_cannot_restart()
    {
        using var owner = CreateOwner(); owner.Listen(_ => { });
        Assert.Throws<InvalidOperationException>(() => owner.Listen(_ => { }));
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.Listen(_ => { }));
    }

    [Theory]
    [InlineData("keaclip://download?url=https%3A%2F%2Fexample.com&cookies=private.txt")]
    [InlineData("keaclip://download?url=https%3A%2F%2Fexample.com&command=calc.exe")]
    [InlineData("keaclip://download?url=https%3A%2F%2Fexample.com&url=https%3A%2F%2Fother.example")]
    [InlineData("keaclip://name:password@download?url=https%3A%2F%2Fexample.com")]
    [InlineData("keaclip://download:80?url=https%3A%2F%2Fexample.com")]
    [InlineData("keaclip://download/run?url=https%3A%2F%2Fexample.com")]
    [InlineData("keaclip://download?url=https%3A%2F%2Fexample.com#command=calc.exe")]
    [InlineData("keaclip://download?url=https%3A%2F%2Fname%3Apassword%40example.com")]
    [InlineData("keaclip://download?url=javascript%3Acalc.exe")]
    [InlineData("keaclip://download?url=https%3A%2F%2Fexample.com%2F%0A--cookies%3Dx")]
    public void Protocol_rejects_unsupported_fields_credentials_and_commands(string argument)
        => Assert.Throws<MediaOperationException>(() => MediaValidation.ParseProtocol(argument));

    [Fact]
    public void Protocol_and_media_size_limits_are_enforced()
    {
        Assert.False(MediaValidation.TryMediaUrl("https://example.com/" + new string('x', 8192), out _));
        Assert.False(MediaValidation.TryMediaUrl("https://example.com/" + new string('\u4e00', 1000), out _));
        Assert.Throws<MediaOperationException>(() => MediaValidation.ParseProtocol("keaclip://download?url=" + new string('x', 16384)));
        var valid = MediaValidation.ParseProtocol("keaclip://download?url=https%3A%2F%2Fexample.com%2Fmedia%3Fcookie%3Dordinary-url-value");
        Assert.Equal("https://example.com/media?cookie=ordinary-url-value", valid.AbsoluteUri);
    }

    [Fact]
    public void Playback_prepare_holds_the_windows_system_module_under_concurrent_calls()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows module lifetime mitigation.");
        Parallel.For(0, 20, _ => PlaybackRuntime.Prepare());
        var handle = GetModuleHandle("d3d11.dll"); Assert.NotEqual(0, handle);
        var path = new StringBuilder(1024); Assert.NotEqual(0u, GetModuleFileName(handle, path, path.Capacity));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3d11.dll"), path.ToString(), ignoreCase: true);
        // No VLC instance, graphics device, media file or window is created by this check.
    }

    [Fact]
    public async Task Extension_event_mocks_check_both_browser_handoffs_without_a_browser()
    {
        var root = FindRepository();
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-");
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Node did not start."); }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("Node.js is required for static extension event mocks."); throw; }
        using (process)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await process.StandardInput.WriteAsync(ExtensionMock); process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            Assert.Equal(0, process.ExitCode); Assert.Equal("Firefox and Chromium mock checks passed.", (await stdout).Trim()); Assert.Equal("", await stderr);
        }
    }

    private static SingleInstanceRelay CreateOwner()
    {
        var relay = new SingleInstanceRelay();
        if (!relay.IsOwner) { relay.Dispose(); Assert.Skip("A real KeaClip instance already owns the current-user relay; leave it untouched."); }
        return relay;
    }

    private static Task ForwardFromAnotherThread(string argument) => Task.Run(() =>
    {
        using var secondary = new SingleInstanceRelay(); Assert.False(secondary.IsOwner);
        secondary.ForwardAsync(argument).GetAwaiter().GetResult();
    });

    private static async Task SendRaw(string name, string argument)
    {
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(deadline.Token);
        try { await client.WriteAsync(Encoding.UTF8.GetBytes(argument), deadline.Token); }
        catch (IOException) when (argument.Length > 32_768) { /* Server may close an oversized request during its write. */ }
    }

    private static async Task Until(Func<bool> condition)
    {
        var limit = Stopwatch.StartNew();
        while (!condition() && limit.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(20);
        Assert.True(condition(), "The current-user relay did not deliver within its bounded deadline.");
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "extensions", "firefox", "background.js"))) return directory.FullName;
        throw new DirectoryNotFoundException("Extension sources were not found beside the test checkout.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(nint module, StringBuilder path, int capacity);

    private const string ExtensionMock = """
        import fs from 'node:fs';
        import vm from 'node:vm';
        import assert from 'node:assert/strict';
        for (const name of ['firefox', 'chromium']) {
          const calls = [], events = {};
          const api = {
            tabs: { create: value => calls.push(value) },
            contextMenus: { create: value => events.menu = value, onClicked: { addListener: cb => events.context = cb } },
            runtime: { onInstalled: { addListener: cb => events.install = cb } },
            action: { onClicked: { addListener: cb => events.action = cb } },
            browserAction: { onClicked: { addListener: cb => events.action = cb } }
          };
          const context = vm.createContext({ URL, encodeURIComponent, chrome: api, browser: api });
          vm.runInContext(fs.readFileSync(`extensions/${name}/background.js`, 'utf8'), context);
          events.install?.();
          assert.equal(events.menu.id, 'keaclip-send');
          assert.deepEqual(Array.from(events.menu.contexts), ['page', 'link', 'video', 'audio']);
          const manifest = JSON.parse(fs.readFileSync(`extensions/${name}/manifest.json`, 'utf8'));
          assert.deepEqual(manifest.permissions, ['contextMenus', 'activeTab']);
          assert.equal(manifest.host_permissions, undefined);
          assert.equal(manifest.manifest_version, name === 'firefox' ? 2 : 3);
          const url = 'https://example.com/media?one=1&two=2#fragment';
          events.action({ url });
          assert.equal(calls[0].url, 'keaclip://download?url=' + encodeURIComponent(url));
          events.context({ menuItemId: 'keaclip-send', srcUrl: 'https://example.com/src', linkUrl: 'https://example.com/link', pageUrl: 'https://example.com/page' });
          events.context({ menuItemId: 'keaclip-send', linkUrl: 'https://example.com/link', pageUrl: 'https://example.com/page' });
          events.context({ menuItemId: 'keaclip-send', pageUrl: 'https://example.com/page' });
          assert.deepEqual(calls.slice(1).map(c => decodeURIComponent(c.url.split('url=')[1])), ['https://example.com/src', 'https://example.com/link', 'https://example.com/page']);
          events.context({ menuItemId: 'unknown', pageUrl: url });
          for (const value of ['javascript:alert(1)', 'file:///C:/private.txt', 'data:text/plain,secret', 'https://name:password@example.com/', 'cmd.exe /c calc.exe', 'https://example.com/' + 'x'.repeat(8192)]) events.action({ url: value });
          assert.equal(calls.length, 4);
        }
        console.log('Firefox and Chromium mock checks passed.');
        """;
}
