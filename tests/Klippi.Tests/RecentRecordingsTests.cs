using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

public sealed class RecentRecordingsTests : IDisposable
{
    private readonly string _videos = Path.Combine(Path.GetTempPath(), "KeaClipRecordings-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public RecentRecordingsTests() => Directory.CreateDirectory(_videos);
    public void Dispose() { try { Directory.Delete(_videos, true); } catch (IOException) { } }

    private string Make(string relative, double hoursAgo, int kilobytes = 200)
    {
        var path = Path.Combine(_videos, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[kilobytes * 1024]);
        File.SetLastWriteTimeUtc(path, _now.AddHours(-hoursAgo).UtcDateTime);
        return path;
    }

    [Fact]
    public void Newest_recordings_from_each_recorder_come_first_with_familiar_names()
    {
        var gameBar = Make(@"Captures\Rift 2026-10-14 11-30-00.mp4", 0.5);
        var relive = Make(@"Radeon ReLive\League of Legends\Replay_2026.10.14-10.00.mp4", 2);
        var nvidia = Make(@"Valorant\Valorant 2026.10.13 - 20.00.00.02.mp4", 16);
        var obs = Make("2026-10-12 21-00-00.mkv", 39);
        var found = RecentRecordings.Find(_videos, _now);
        Assert.Equal([gameBar, relive, nvidia, obs], found.Select(r => r.Path));
        Assert.Equal(["Xbox Game Bar", "AMD ReLive", "Valorant", "Videos"], found.Select(r => r.Source));
        Assert.Equal(200 * 1024, found[0].SizeBytes);
    }

    [Fact]
    public void Old_tiny_non_video_hidden_and_too_deep_files_are_left_out()
    {
        Make("old.mp4", 24 * 30);
        Make("still-writing.mp4", 0.1, kilobytes: 10);
        Make("notes.txt", 1);
        Make(@"a\b\c\too-deep.mp4", 1);
        var hidden = Make("hidden.mp4", 1); File.SetAttributes(hidden, FileAttributes.Hidden);
        var kept = Make("kept.webm", 1);
        Assert.Equal([kept], RecentRecordings.Find(_videos, _now).Select(r => r.Path));
    }

    [Fact]
    public void At_most_the_requested_number_are_returned_and_a_missing_folder_is_empty()
    {
        for (var i = 0; i < 8; i++) Make($"clip{i}.mp4", i + 1);
        var found = RecentRecordings.Find(_videos, _now, max: 3);
        Assert.Equal(["clip0.mp4", "clip1.mp4", "clip2.mp4"], found.Select(r => Path.GetFileName(r.Path)));
        Assert.Empty(RecentRecordings.Find(Path.Combine(_videos, "nope"), _now));
        Assert.Empty(RecentRecordings.Find("", _now));
    }
}
