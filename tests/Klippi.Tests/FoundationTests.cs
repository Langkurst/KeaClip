using System.IO.Compression;
using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

public sealed class FoundationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Klippi-tests-" + Guid.NewGuid().ToString("N"));
    public FoundationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData("javascript:alert(1)")][InlineData("file:///C:/secret")][InlineData("https://user:password@example.com/video")][InlineData("--exec calc.exe")][InlineData("https://example.com\n--exec")]
    public void RejectUnsafeMediaLinks(string url) => Assert.False(MediaValidation.TryMediaUrl(url, out _));
    [Fact] public void ProtocolReturnsOnlyValidatedUrl()
    {
        var url = MediaValidation.ParseProtocol("keaclip://download?url=" + Uri.EscapeDataString("https://youtube.com/watch?v=123&list=abc"));
        Assert.Equal("https://youtube.com/watch?v=123&list=abc", url.AbsoluteUri);
        Assert.Throws<MediaOperationException>(() => MediaValidation.ParseProtocol("keaclip://download?url=javascript:calc&url=https://example.com"));
    }
    [Theory][InlineData("05:48",348)][InlineData("00:05:48",348)][InlineData("00:05:48.250",348.25)][InlineData("1:02:03.001",3723.001)][InlineData("00:05:48,250",348.25)][InlineData("05:48,5",348.5)]
    public void PreciseTimecodes(string value, double seconds) { Assert.True(MediaValidation.TryTimecode(value, out var time)); Assert.Equal(seconds, time.TotalSeconds, 3); }
    [Theory][InlineData("1:60")][InlineData("-1")][InlineData("NaN")][InlineData("1:60:00")][InlineData("1:2:3:4")][InlineData("999999999999:01")][InlineData("1,000")][InlineData("05:48,2.5")][InlineData("05:48,2,5")][InlineData("05:60,5")]
    public void RejectInvalidTimecodes(string value) => Assert.False(MediaValidation.TryTimecode(value, out _));
    [Fact] public void NamingCannotCreateReservedOrTraversingPaths()
    {
        Assert.Equal("_CON", MediaValidation.FriendlyFileName("CON"));
        Assert.DoesNotContain('/', MediaValidation.FriendlyFileName("../../Videos/evil:*"));
        Assert.Equal("Media", MediaValidation.FriendlyFileName("..."));
    }
    [Fact] public void DiagnosticsRemoveSecretsWhileSourceKeepsMediaId()
    {
        var diagnostic = MediaValidation.Redact("failed https://example.com/video?token=secret#key uploadAuth=abc");
        Assert.DoesNotContain("secret", diagnostic); Assert.DoesNotContain("abc", diagnostic); Assert.DoesNotContain("#key", diagnostic);
        Assert.Equal("https://youtube.com/watch?v=123", MediaValidation.SafeSourceUrl(new("https://youtube.com/watch?v=123&token=private#key")));
        Assert.DoesNotContain("password",MediaValidation.Redact("failed https://user:password@example.com/video?auth=secret"));
        Assert.DoesNotContain("secret",MediaValidation.Redact("Authorization: Bearer secret"));
        Assert.DoesNotContain("second",MediaValidation.Redact("Cookie: first=one; second=two"));
    }
    [Fact] public async Task AtomicSettingsSurviveRestartAndRecoverBackup()
    {
        var first = new JsonLocalStore(_root);
        await first.SaveSettingsAsync(new() { Theme = "Dark" });
        await first.SaveSettingsAsync(new() { Theme = "Light" });
        var restarted = new JsonLocalStore(_root);
        Assert.Equal("Light", (await restarted.LoadSettingsAsync()).Theme);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{corrupt");
        Assert.Equal("Dark", (await restarted.LoadSettingsAsync()).Theme);
        Assert.Contains("recovered", restarted.LastWarning!);
        await restarted.SaveSettingsAsync(new(){Theme="System"});
        await File.WriteAllTextAsync(Path.Combine(_root,"settings.json"),"broken-again");
        Assert.Equal("Dark",(await new JsonLocalStore(_root).LoadSettingsAsync()).Theme);
        Assert.Single(Directory.GetFiles(_root,"settings.json.corrupt-*"));
    }
    [Fact] public async Task UnrecoverableDataIsPreservedAndNotCalledSuccess()
    {
        var path = Path.Combine(_root, "settings.json"); await File.WriteAllTextAsync(path,"broken");
        await Assert.ThrowsAsync<MediaOperationException>(() => new JsonLocalStore(_root).LoadSettingsAsync());
        Assert.Equal("broken", await File.ReadAllTextAsync(path));
    }
    [Fact] public async Task ConcurrentHistoryUpdatesDontLoseRecordsOrDeleteFiles()
    {
        var store = new JsonLocalStore(_root); var media = Path.Combine(_root,"precious.mp4"); await File.WriteAllTextAsync(media,"keep");
        await Task.WhenAll(Enumerable.Range(0,20).Select(i => store.AddHistoryAsync(new(i.ToString(),"Title",null,null,DateTimeOffset.UtcNow,media,"Video","Best",4,"Finished"))));
        Assert.Equal(20,(await store.LoadHistoryAsync()).Count);
        await store.ClearHistoryAsync(); Assert.Empty(await store.LoadHistoryAsync()); Assert.Equal("keep",await File.ReadAllTextAsync(media));
    }
    [Fact] public async Task CancelledSaveLeavesExistingSettingsIntact()
    {
        var store = new JsonLocalStore(_root); await store.SaveSettingsAsync(new(){Theme="Dark"});
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveSettingsAsync(new(){Theme="Light"},token.Token));
        Assert.Equal("Dark",(await store.LoadSettingsAsync()).Theme);
    }
    [Fact] public void ComponentZipCannotWriteOutsideOwnedDirectory()
    {
        var archive = Path.Combine(_root,"bad.zip");
        using(var zip = ZipFile.Open(archive,ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.exe").Open()); writer.Write("malicious"); }
        Assert.Throws<MediaOperationException>(() => ManagedComponentService.ExtractVerifiedZip(archive,Path.Combine(_root,"extracted")));
        Assert.False(File.Exists(Path.Combine(_root,"escape.exe")));
    }
    [Fact] public void ManagedComponentManifestRejectsOutsidePaths()
    {
        File.WriteAllText(Path.Combine(_root,"current.json"),"{\"yt-dlp\":{\"Name\":\"yt-dlp\",\"RelativePath\":\"../outside.exe\",\"Sha256\":\"anything\",\"Version\":\"1\",\"SourceUrl\":\"https://example.com\",\"Licence\":\"Unknown\"}}");
        Assert.Null(new ManagedComponentService(new ProcessRunner(),_root).Find("yt-dlp"));
    }
    [Fact] public async Task ProcessOutputIsBoundedAndCancelledWorkDoesNotStart()
    {
        var runner = new ProcessRunner();
        var version = await runner.RunAsync(new("dotnet",["--info"],OutputLimit:64));
        Assert.Equal(0,version.ExitCode); Assert.True(version.StandardOutput.Length<=64);
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new("nonexistent-executable",[]),cancellationToken:token.Token));
    }
}
