using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Avalonia.Threading;
using Klippi.Core;

namespace Klippi.Desktop;

internal sealed class SingleInstanceRelay : IDisposable
{
    private readonly string _name=CreatePipeName();
    private readonly Mutex _mutex;
    private readonly bool _owner;
    private readonly CancellationTokenSource _cancellation=new();
    private Action<string>? _receiver;
    private Task? _server;
    private int _listening;
    private bool _disposed;
    public SingleInstanceRelay()
    {
        _mutex=new Mutex(false,"Local\\"+_name);
        try { _owner=_mutex.WaitOne(0); }
        catch(AbandonedMutexException) { _owner=true; }
    }
    public bool IsOwner=>_owner;
    internal string PipeName=>_name;
    public void Listen(Action<string> receiver)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        ArgumentNullException.ThrowIfNull(receiver);
        if(!_owner)return;
        if(Interlocked.Exchange(ref _listening,1)!=0)throw new InvalidOperationException("The app-link listener has already started.");
        _receiver=receiver;
        _server=Task.Run(async()=>
        {
            while(!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var pipe=new NamedPipeServerStream(_name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_cancellation.Token);
                    using var request=CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token); request.CancelAfter(TimeSpan.FromSeconds(3));
                    var bytes=new List<byte>(); var buffer=new byte[1024]; int count;
                    while((count=await pipe.ReadAsync(buffer,request.Token))>0)
                    {
                        if(bytes.Count+count>32_768)throw new InvalidDataException("App link too large.");
                        bytes.AddRange(buffer.AsSpan(0,count).ToArray());
                    }
                    var argument=Encoding.UTF8.GetString(bytes.ToArray());
                    if(TryLaunchArgument(argument,out var validated))Dispatcher.UIThread.Post(()=>{if(!_disposed)_receiver?.Invoke(validated);});
                }
                catch(Exception ex)when(ex is IOException or OperationCanceledException or InvalidDataException)
                {
                    if(_cancellation.IsCancellationRequested)break;
                    // A busy/invalid connection must not turn into a tight retry loop.
                    try { await Task.Delay(50,_cancellation.Token); }
                    catch(OperationCanceledException) { break; }
                }
            }
        });
    }
    public async Task ForwardAsync(string argument)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(!TryLaunchArgument(argument,out var validated))return;
        try
        {
            await using var pipe=new NamedPipeClientStream(".",_name,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token); await pipe.WriteAsync(Encoding.UTF8.GetBytes(validated),timeout.Token);
        }
        catch(Exception ex)when(ex is IOException or OperationCanceledException or TimeoutException){ }
    }
    private static bool TryLaunchArgument(string value,out string validated)
    {
        validated="";
        if(value=="--show"){validated=value;return true;}
        try
        {
            if(value.StartsWith(AppIdentity.Protocol+":",StringComparison.OrdinalIgnoreCase)){validated=MediaValidation.ParseProtocol(value).AbsoluteUri;return true;}
            if(MediaValidation.TryMediaUrl(value,out var url)){validated=url!.AbsoluteUri;return validated.Length<=8192;}
        }
        catch(MediaOperationException){ }
        return false;
    }
    private static string CreatePipeName()
    {
        var identity=Environment.UserName;
        if(OperatingSystem.IsWindows())
        {
            using var windowsIdentity=WindowsIdentity.GetCurrent();
            identity=windowsIdentity.User?.Value??identity;
        }
        return "KeaClip-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_cancellation.Cancel();
        if(_owner)_mutex.ReleaseMutex();_mutex.Dispose();
        // The listener exits through cancellation; observe it without blocking the UI thread.
        if(_server is {} server)_=server.ContinueWith(_=>_cancellation.Dispose(),TaskScheduler.Default);else _cancellation.Dispose();
    }
}
