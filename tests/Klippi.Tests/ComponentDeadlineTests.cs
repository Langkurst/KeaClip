using System.Net;
using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;
namespace Klippi.Tests;

public sealed class ComponentDeadlineTests
{
    [Fact]public async Task BodyStallTimesOutAfterHeadersWithoutChangingActiveManifest()
    {
        var root=Path.Combine(Path.GetTempPath(),"Klippi-body-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var manifest=Path.Combine(root,"current.json");await File.WriteAllTextAsync(manifest,"{}");
            using var client=new HttpClient(new StalledHandler()){Timeout=TimeSpan.FromMilliseconds(150)};
            var service=new ManagedComponentService(new ProcessRunner(),root,client);
            var error=await Assert.ThrowsAsync<MediaOperationException>(()=>service.DownloadVerifiedAsync("https://example.com/tool",new string('a',64),Path.Combine(root,"download.bin"),null,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("timed out",error.Message);Assert.Equal("{}",await File.ReadAllTextAsync(manifest));
        }
        finally{Directory.Delete(root,true);}
    }
    // The output limit is under test, not start-up time: normal priority and a 60 s hang guard keep a busy PC from failing it.
    [Fact]public async Task OversizeProcessLineIsRejectedBeforeItReachesCallback()
    {
        if(!OperatingSystem.IsWindows())return;
        var callbackChars=0;
        var error=await Assert.ThrowsAsync<MediaOperationException>(()=>new ProcessRunner().RunAsync(new("pwsh",["-NoProfile","-NonInteractive","-NoLogo","-Command","[Console]::Out.Write(('x' * 2097152))"],Timeout:TimeSpan.FromSeconds(60),OutputLimit:64,Interactive:true),line=>callbackChars+=line.Length));
        Assert.Contains("unreadable",error.Message);Assert.Equal(0,callbackChars);
    }
    private sealed class StalledHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new StalledStream())});
    }
    private sealed class StalledStream:MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default){await Task.Delay(Timeout.Infinite,cancellationToken);return 0;}
        public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken){await Task.Delay(Timeout.Infinite,cancellationToken);return 0;}
    }
}
