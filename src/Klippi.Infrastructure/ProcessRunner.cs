using System.Diagnostics;
using System.Text;
using Klippi.Core;
namespace Klippi.Infrastructure;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Executable) || request.Executable.Contains('\0') || request.Arguments.Any(a => a.Contains('\0')))
            throw new MediaOperationException("The media component could not be started.");
        using var deadline = new CancellationTokenSource(request.Timeout ?? TimeSpan.FromHours(6));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var startInfo = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? AppContext.BaseDirectory
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        Process? process = null;
        WindowsChildLifetime? lifetime = null;
        var output = new BoundedText(request.OutputLimit); var error = new BoundedText(request.OutputLimit);
        Exception? readFailure = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows()) { lifetime = WindowsChildLifetime.Start(startInfo, interactive: request.Interactive); process = lifetime.Process; }
            else { process = new Process { StartInfo = startInfo }; process.Start(); }
            async Task ReadAsync(StreamReader reader, BoundedText target, Action<string>? receive)
            {
                try
                {
                    var buffer=new char[4096]; var lineBuffer=new StringBuilder();
                    var maxLine=Math.Clamp(request.OutputLimit,8192,16_777_216);
                    void Receive()
                    {
                        var line=lineBuffer.ToString();lineBuffer.Clear();
                        receive?.Invoke(line);
                        target.Append(line); target.Append("\n");
                    }
                    int count;
                    while((count=await reader.ReadAsync(buffer.AsMemory(),linked.Token).ConfigureAwait(false))>0)
                        for(var i=0;i<count;i++)
                        {
                            if(buffer[i] is '\r' or '\n'){if(lineBuffer.Length>0)Receive();}
                            else
                            {
                                if(lineBuffer.Length>=maxLine)throw new InvalidDataException("Component output line exceeds the safe limit.");
                                lineBuffer.Append(buffer[i]);
                            }
                        }
                    if(lineBuffer.Length>0)Receive();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.CompareExchange(ref readFailure, ex, null); await linked.CancelAsync().ConfigureAwait(false); }
            }
            var readOutput = ReadAsync(lifetime?.StandardOutput ?? process.StandardOutput, output, stdout);
            var readError = ReadAsync(lifetime?.StandardError ?? process.StandardError, error, stderr);
            try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (readFailure is not null) { throw new MediaOperationException("The media component returned unreadable output.", MediaValidation.Redact(readFailure.Message), readFailure); }
            finally
            {
                if (lifetime is not null) await lifetime.StopAndWaitAsync().ConfigureAwait(false);
                else
                {
                    if (!process.HasExited)
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                try { await Task.WhenAll(readOutput, readError).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (readFailure is not null) throw new MediaOperationException("The media component returned unreadable output.", MediaValidation.Redact(readFailure.Message), readFailure);
            return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested) { throw new MediaOperationException("This operation took too long. You can try again."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new MediaOperationException("A media component is missing or could not start. Check Components in Settings.", MediaValidation.Redact(ex.Message), ex); }
        finally { if (lifetime is not null) lifetime.Dispose(); else process?.Dispose(); }
    }

    // Keeps at most `limit` characters of a stream: the opening part and the
    // most recent part. Engines print their decisive error last, so a long run
    // of warnings must not push it out of what failure classification sees.
    // Output that fits the limit is returned exactly as received.
    private sealed class BoundedText(int limit)
    {
        private const string Gap = "\n…\n";
        private readonly int _limit = Math.Max(0, limit);
        private readonly int _tailLimit = Math.Max(0, Math.Min(65_536, limit / 2));
        private readonly StringBuilder _head = new();
        private char[]? _tail;
        private int _tailStart, _tailCount;
        private bool _dropped;
        public void Append(ReadOnlySpan<char> text)
        {
            var room = _limit - _tailLimit - _head.Length;
            if (room > 0) { var take = Math.Min(room, text.Length); _head.Append(text[..take]); text = text[take..]; }
            if (text.IsEmpty) return;
            if (_tailLimit == 0) { _dropped = true; return; }
            _tail ??= new char[_tailLimit];
            if (text.Length >= _tailLimit)
            {
                _dropped |= _tailCount > 0 || text.Length > _tailLimit;
                text[^_tailLimit..].CopyTo(_tail); _tailStart = 0; _tailCount = _tailLimit;
                return;
            }
            foreach (var character in text)
            {
                if (_tailCount == _tailLimit) { _tailStart = (_tailStart + 1) % _tailLimit; _tailCount--; _dropped = true; }
                _tail[(_tailStart + _tailCount++) % _tailLimit] = character;
            }
        }
        public override string ToString()
        {
            var tail = new StringBuilder(_tailCount);
            for (var i = 0; i < _tailCount; i++) tail.Append(_tail![(_tailStart + i) % _tailLimit]);
            if (!_dropped) return new StringBuilder(_head.Length + tail.Length).Append(_head).Append(tail).ToString();
            var room = _limit - _head.Length;
            var gap = room > Gap.Length ? Gap : "";
            var keep = Math.Min(tail.Length, room - gap.Length);
            return new StringBuilder(_limit).Append(_head).Append(gap).Append(tail.ToString(tail.Length - keep, keep)).ToString();
        }
    }
}
