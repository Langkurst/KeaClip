using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Klippi.Infrastructure;
using LibVLCSharp.Shared;

if (args.Length != 3) throw new ArgumentException("Supply the portable folder, synthetic fixture folder and workspace report filename.");
var package = Path.GetFullPath(args[0]);
var fixtures = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args[2]);
var results = new List<object>();
using (var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "BUILD_INFO.json"))))
{
    var info = build.RootElement;
    var commit = info.GetProperty("SourceCommit").GetString();
    if (info.GetProperty("WorkingTreeChanged").GetBoolean() || info.GetProperty("Runtime").GetString() != "win-x64" || commit is null || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Package provenance does not identify a clean committed Windows x64 source.");
    results.Add(new { Check = "Clean committed package provenance", Passed = true, SourceCommit = commit });
}
var entries = File.ReadAllLines(Path.Combine(package, "checksums.sha256"));
var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var line in entries.Where(l => !string.IsNullOrWhiteSpace(l)))
{
    if (line.Length < 67 || line[64..66] != "  ") throw new InvalidDataException("Malformed package checksum entry.");
    var relative = line[66..];
    var path = Path.GetFullPath(Path.Combine(package, relative));
    if (!path.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !declared.Add(relative)) throw new InvalidDataException("Unsafe or repeated package checksum path.");
    using var stream = File.OpenRead(path);
    var actual = Convert.ToHexString(SHA256.HashData(stream));
    if (!actual.Equals(line[..64], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Packaged file checksum mismatch: " + relative);
}
var actualFiles = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(package, p).Replace('\\', '/')).Where(p => p != "checksums.sha256").ToHashSet(StringComparer.OrdinalIgnoreCase);
if (!actualFiles.SetEquals(declared)) throw new InvalidDataException("Package manifest does not cover the exact file inventory.");
results.Add(new { Check = "Complete package file inventory and SHA256", Passed = true, Files = declared.Count });
Console.WriteLine($"Verified {declared.Count} exact package file hashes.");
using (var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "KeaClip.runtimeconfig.json"))))
{
    var version = runtime.RootElement.GetProperty("runtimeOptions").GetProperty("includedFrameworks").EnumerateArray().First(f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App").GetProperty("version").GetString();
    if (version != "10.0.12") throw new InvalidDataException("The expected self-contained serviced runtime is missing.");
    results.Add(new { Check = "Self-contained runtime declaration", Passed = true, Version = version });
}
if (Directory.Exists(Path.Combine(package, "libvlc", "win-x86")) || Directory.Exists(Path.Combine(package, "libvlc", "win-arm64"))) throw new InvalidDataException("The Windows x64 package includes unnecessary native VLC architectures.");
var componentRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "component-check-state");
var bundledComponents = Path.Combine(package, "Components");
if (!Directory.Exists(bundledComponents))
{
    // Public download: KeaClip fetches FFmpeg, yt-dlp and Deno from their publishers on first run, so none may be
    // bundled (docs/SOURCE.md), and the personal-build marker must be absent.
    var stray = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Select(Path.GetFileName)
        .Where(name => name is "ffmpeg.exe" or "ffprobe.exe" or "yt-dlp.exe" or "deno.exe" or "PRIVATE_BUILD.txt").ToArray();
    if (stray.Length > 0) throw new InvalidDataException("The public package must not include " + string.Join(", ", stray) + ".");
    results.Add(new { Check = "Public package bundles no FFmpeg, yt-dlp or Deno", Passed = true });
    Console.WriteLine("Public package: no FFmpeg, yt-dlp or Deno bundled.");
}
else
{
    var components = new ManagedComponentService(new ProcessRunner(), componentRoot, bundledRoot: bundledComponents);
    var inventory = await components.InspectAsync();
    if (inventory.Count != 4 || inventory.Any(c => !c.Ready)) throw new InvalidDataException("Bundled media components were not all launch-ready.");
    results.Add(new { Check = "Bundled component discovery and real version launches", Passed = true, Versions = inventory.Select(c => new { c.Name, c.Version }) });
    Console.WriteLine("Four bundled engines launched with verified versions.");
}
var nativeRoot = Path.Combine(package, "libvlc", "win-x64");
// VLC's Windows plugin enumeration may release its D3D11 module while a
// driver callback still exists. Keep the Windows system module loaded for
// this console host's lifetime, as the desktop graphics host normally does.
var packagedDesktop = System.Reflection.Assembly.LoadFile(Path.Combine(package, "KeaClip.dll"));
var prepare = packagedDesktop.GetType("Klippi.Desktop.PlaybackRuntime")?.GetMethod("Prepare") ?? throw new InvalidDataException("The packaged playback module-lifetime fix is missing.");
prepare.Invoke(null, null);
Core.Initialize(nativeRoot);
using (var vlc = new LibVLC("--no-video-title-show", "--quiet", "--vout=dummy", "--aout=dummy", "--avcodec-hw=none", "--avcodec-threads=1"))
{
    foreach (var extension in new[] { "mp4", "mkv", "webm", "mp3", "wav", "m4a", "opus", "flac" })
    {
        var path = Path.Combine(fixtures, "format." + extension);
        if (!File.Exists(path)) throw new FileNotFoundException("Synthetic format fixture missing: " + extension);
        using var input = File.OpenRead(path); var before = SHA256.HashData(input); input.Close();
        using var media = new Media(vlc, path, FromType.FromPath);
        using var player = new MediaPlayer(vlc);
        var error = false; var ended = false; player.EncounteredError += (_, _) => error = true; player.EndReached += (_, _) => ended = true;
        if (!player.Play(media)) throw new InvalidDataException("Native playback did not start: " + extension);
        var watch = Stopwatch.StartNew();
        while (!error && !ended && player.Time < 300 && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        var stats = media.Statistics;
        var decoded = stats.DecodedVideo + stats.DecodedAudio;
        var videoExpected = extension is "mp4" or "mkv" or "webm";
        if (error || stats.DecodedAudio == 0 || videoExpected && stats.DecodedVideo == 0 || !videoExpected && stats.DecodedVideo != 0) throw new InvalidDataException("Native decoding did not match the expected tracks: " + extension);
        var seekChecked = !ended && player.Time >= 300;
        if (seekChecked)
        {
            player.SetPause(true); var paused = player.Time; player.Position = .55f; player.SetPause(false);
            watch.Restart();
            while (!error && !ended && player.Time <= paused + 100 && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            if (error || !ended && player.Time <= paused + 100) throw new InvalidDataException("Native seek/resume failed: " + extension);
        }
        player.Stop();
        using var final = File.OpenRead(path);
        if (!before.AsSpan().SequenceEqual(SHA256.HashData(final))) throw new InvalidDataException("Playback modified a fixture.");
        results.Add(new { Check = "Native silent decoding and source integrity", Format = extension, Passed = true, DecodedBlocks = decoded, SeekResumeChecked = seekChecked });
        Console.WriteLine("Native silent decoding/seek passed: " + extension);
    }
    results.Add(new { Check = "Exact package native playback version", Version = vlc.Version, Passed = true });
}
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { AtUtc = DateTimeOffset.UtcNow, Scope = "Hidden, dummy sound/video outputs; hardware decoding disabled. Does not verify visible VideoView, monitor scaling, focus or fullscreen.", Results = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Package inventory, bundled tools and eight native format decoding checks passed. Report: {reportPath}");
