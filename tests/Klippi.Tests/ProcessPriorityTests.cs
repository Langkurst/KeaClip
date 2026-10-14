using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// Children run below normal so downloads and exports never take the CPU from a game. Short
// calls the user waits for run at normal priority: below normal, they got no CPU for 18-38 s
// while a game ran, and a quick probe could fail with "took too long".
public sealed class ProcessPriorityTests
{
    [WindowsProcessFact]
    public async Task Interactive_calls_run_at_normal_priority_and_other_work_below_normal()
    {
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")).FirstOrDefault(File.Exists)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Assert.True(File.Exists(pwsh), "This check requires PowerShell 7.");
        string[] report = ["-NoProfile", "-NonInteractive", "-Command", "[Diagnostics.Process]::GetCurrentProcess().PriorityClass"];
        var background = await new ProcessRunner().RunAsync(new ProcessRequest(pwsh, report, Timeout: TimeSpan.FromMinutes(3)));
        var interactive = await new ProcessRunner().RunAsync(new ProcessRequest(pwsh, report, Timeout: TimeSpan.FromMinutes(3), Interactive: true));
        Assert.Equal("BelowNormal", background.StandardOutput.Trim());
        Assert.Equal("Normal", interactive.StandardOutput.Trim());
    }

    [Fact]
    public async Task Link_analysis_and_file_probes_are_interactive()
    {
        const string metadata = """{"id":"abcdefghijk","title":"Sample","webpage_url":"https://www.youtube.com/watch?v=abcdefghijk","duration":60,"uploader":"Artist","extractor_key":"Youtube","formats":[{"format_id":"137","ext":"mp4","height":1080,"vcodec":"avc1","acodec":"none","filesize_approx":12345}]}""";
        var process = new EngineFakeProcess((request, _, _) => request.Executable == "fake-ffprobe"
            ? new ProcessResult(0, EngineFakeProcess.ProbeJson(), "")
            : new ProcessResult(0, metadata, ""));
        await new DownloadService(process, new EngineFakeComponents()).AnalyseAsync(new Uri("https://www.youtube.com/watch?v=abcdefghijk"));
        Assert.True(Assert.Single(process.Calls, call => call.Arguments.Contains("--dump-single-json")).Interactive);

        using var files = new EngineTestFiles();
        await new MediaService(process, new EngineFakeComponents()).ProbeAsync(files.Input());
        Assert.True(Assert.Single(process.Calls, call => call.Executable == "fake-ffprobe").Interactive);
    }
}
