using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Klippi.Core;
using Xunit;

namespace Klippi.Tests;

/// <summary>build/release-manifest.ps1 must write a feed the app itself accepts, for the right version only.</summary>
public sealed class ReleaseManifestScriptTests : IDisposable
{
    private static readonly string Workspace = FindWorkspace();
    private readonly string _output = Path.Combine(Workspace, "artifacts", "test-release", Guid.NewGuid().ToString("N"));
    private readonly string _inputs = Path.Combine(Path.GetTempPath(), "KeaClipReleaseScript-" + Guid.NewGuid().ToString("N"));

    public ReleaseManifestScriptTests() => Directory.CreateDirectory(_inputs);
    public void Dispose()
    {
        foreach (var folder in new[] { _output, _inputs }) try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
    }

    [Fact]
    public void The_script_writes_a_feed_the_app_parses_with_exact_hashes_and_sizes()
    {
        // Any PE file whose version resource carries the app version stands in for the installer.
        var installer = Path.Combine(_inputs, "KeaClip.Setup.exe"); File.Copy(typeof(AppIdentity).Assembly.Location, installer);
        var portable = PortableZip(AppIdentity.Version);
        var notes = Path.Combine(_inputs, "notes.md"); File.WriteAllText(notes, "Faster clips.\r\nUpdates from Settings.");
        var result = Run("-Installer", installer, "-Portable", portable, "-NotesFile", notes, "-OutputDirectory", _output);
        if (result is null) return; // PowerShell 7 is not installed here.
        Assert.True(result.Value.ExitCode == 0, result.Value.Output);

        var offer = UpdateManifest.Parse(File.ReadAllText(Path.Combine(_output, UpdateManifest.FileName)));
        Assert.Equal(ReleaseVersion.Parse(AppIdentity.Version), offer.Version);
        Assert.Equal("Faster clips.\nUpdates from Settings.", offer.Notes);
        Assert.Equal($"https://github.com/Langkurst/KeaClip/releases/download/v{AppIdentity.Version}/KeaClip-{AppIdentity.Version}-Setup.exe", offer.Installer!.Url.AbsoluteUri);
        Assert.Equal(Sha256(installer), offer.Installer.Sha256);
        Assert.Equal(new FileInfo(installer).Length, offer.Installer.Size);
        Assert.Equal($"https://github.com/Langkurst/KeaClip/releases/download/v{AppIdentity.Version}/KeaClip-{AppIdentity.Version}-win-x64.zip", offer.Portable!.Url.AbsoluteUri);
        Assert.Equal(Sha256(portable), offer.Portable.Sha256);
        Assert.Equal(Sha256(installer), Sha256(Path.Combine(_output, $"KeaClip-{AppIdentity.Version}-Setup.exe")));
        var sums = File.ReadAllText(Path.Combine(_output, "SHA256SUMS.txt"));
        Assert.Contains($"{Sha256(portable)}  KeaClip-{AppIdentity.Version}-win-x64.zip", sums);
    }

    [Fact]
    public void Files_of_another_version_or_insecure_links_are_refused()
    {
        var other = Path.Combine(_inputs, "Other.exe"); File.Copy(typeof(FactAttribute).Assembly.Location, other); // xunit's own version
        var wrongInstaller = Run("-Installer", other, "-OutputDirectory", _output);
        if (wrongInstaller is null) return;
        Assert.NotEqual(0, wrongInstaller.Value.ExitCode);
        Assert.Contains("not " + AppIdentity.Version, wrongInstaller.Value.Output);

        var wrongZip = Run("-Portable", PortableZip("0.0.1"), "-OutputDirectory", _output);
        Assert.NotEqual(0, wrongZip!.Value.ExitCode);
        var insecure = Run("-Portable", PortableZip(AppIdentity.Version), "-BaseUrl", "http://example.com/", "-OutputDirectory", _output);
        Assert.NotEqual(0, insecure!.Value.ExitCode);
        Assert.False(File.Exists(Path.Combine(_output, UpdateManifest.FileName)));
    }

    private string PortableZip(string version)
    {
        var path = Path.Combine(_inputs, $"KeaClip-{version}-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry($"KeaClip-{version}-win-x64/{name}").Open(), new UTF8Encoding(false)); writer.Write(text); }
        Entry("BUILD_INFO.json", $"{{\"Version\":\"{version}\"}}"); Entry("portable.marker", "portable"); Entry("KeaClip.exe", "fixture");
        return path;
    }

    private static (int ExitCode, string Output)? Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Workspace };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(Workspace, "build", "release-manifest.ps1") }.Concat(arguments)) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000)) { process.Kill(); throw new TimeoutException("The release script did not finish."); }
            return (process.ExitCode, output.Result + error.Result);
        }
        catch (Win32Exception) { return null; }
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string FindWorkspace()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Directory.Build.props")) && File.Exists(Path.Combine(folder.FullName, "build", "release-manifest.ps1"))) return folder.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
