using System.Text.Json;
using Klippi.Core;
using Xunit;

namespace Klippi.Tests;

internal delegate void EnginePromotion(IReadOnlyList<(string Staged, string Output)> outputs, bool replace, out bool retainRecovery);

internal sealed class EngineWindowsFactAttribute : FactAttribute
{
    public EngineWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows file-share locking to provoke a real rename failure.";
    }
}

internal sealed class EngineRaceOutputList(IReadOnlyList<(string Staged, string Output)> entries, Action mutate, int onEnumeration = 2) : IReadOnlyList<(string Staged, string Output)>
{
    private int enumerations;
    public int Count => entries.Count;
    public (string Staged, string Output) this[int index] => entries[index];
    public IEnumerator<(string Staged, string Output)> GetEnumerator()
    {
        if (++enumerations == onEnumeration) mutate();
        return entries.GetEnumerator();
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class EngineTestFiles : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "KeaClipEngineTests", Guid.NewGuid().ToString("N"));
    public EngineTestFiles() => Directory.CreateDirectory(Root);
    public string Input(string name = "source.mp4")
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)17, 256).ToArray());
        return path;
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

internal sealed class EngineFakeComponents : IComponentService
{
    public HashSet<string> Missing { get; } = [];
    public string? Find(string name) => Missing.Contains(name) ? null : "fake-" + name;
    public Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentInfo>>([new("yt-dlp", "fake-yt-dlp", "2026.08.19", true), new("ffmpeg", "fake-ffmpeg", "9.0.2", true)]);
    public Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class EngineFakeProcess(Func<ProcessRequest, Action<string>?, CancellationToken, ProcessResult> handler) : IProcessRunner
{
    public List<ProcessRequest> Calls { get; } = [];
    internal const string FullBuildFilters = "Filters:\n ------\n .S tonemap           V->V       Conversion to/from different dynamic ranges.\n .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.\n";
    public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(request);
        // MediaService checks for the HDR tone-mapping filters before an HDR conversion;
        // answer like the bundled full FFmpeg build so download fakes need not know about it.
        if (request.Arguments is ["-hide_banner", "-filters"]) return Task.FromResult(new ProcessResult(0, FullBuildFilters, ""));
        var result = handler(request, stdout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
    public static string ProbeJson(double seconds = 60, bool video = true, bool audio = true) => JsonSerializer.Serialize(new
    {
        format = new { duration = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        streams = new object?[] { video ? new { codec_type = "video", codec_name = "h264", width = 1920, height = 1080, avg_frame_rate = "30000/1001" } : null, audio ? new { codec_type = "audio", codec_name = "aac", sample_rate = "48000", channels = 2 } : null }.Where(stream => stream is not null)
    });
    public const string Encoders = " V....D libx264 H.264 CPU\n V....D h264_amf AMD AMF\n A....D libmp3lame MP3\n";
    public static string Argument(ProcessRequest request, string name) => request.Arguments[Array.IndexOf(request.Arguments.ToArray(), name) + 1];
}

internal sealed class EngineProgress : IProgress<JobProgress>
{
    public List<JobProgress> Values { get; } = [];
    public void Report(JobProgress value) => Values.Add(value);
}
