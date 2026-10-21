using System.IO.Compression;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Dropping one video opens it for editing; anything else waits on a "Ready to share" card and uploads only on Share.
public sealed class DropShareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KeaClip-drop-share-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task A_dropped_folder_waits_for_Share_then_goes_as_one_zip_that_is_deleted_afterwards()
    {
        var folder = Path.Combine(_root, "Holiday photos"); Directory.CreateDirectory(Path.Combine(folder, "Day 2"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "pack me");
        File.WriteAllBytes(Path.Combine(folder, "Day 2", "beach.jpg"), [1, 2, 3, 4]);
        var fake = new UiFakes(); fake.Sharing.Gate = new();
        using var model = new MainViewModel(fake.Services with { DataRoot = Path.Combine(_root, "data") });

        await model.AcceptDroppedAsync([folder]);
        Assert.True(model.HasDropShare); Assert.Equal(3, model.SelectedTab);
        Assert.Equal("Holiday photos", model.DropShareName);
        Assert.Contains("2 files", model.DropShareDetail); Assert.Contains(".zip", model.DropShareDetail);
        Assert.Equal(0, fake.Sharing.Calls); // nothing uploads from the drop alone

        var sharing = model.ShareDroppedCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (fake.Sharing.Calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var zip = Assert.Single(fake.Sharing.Paths);
        Assert.Equal("Holiday photos.zip", Path.GetFileName(zip));
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(["Holiday photos/Day 2/beach.jpg", "Holiday photos/notes.txt"], archive.Entries.Select(entry => entry.FullName).Order());
            using var reader = new StreamReader(archive.GetEntry("Holiday photos/notes.txt")!.Open());
            Assert.Equal("pack me", reader.ReadToEnd());
        }
        fake.Sharing.Gate.SetResult(); await sharing;
        Assert.True(model.HasShareUrl); Assert.False(model.HasDropShare);
        Assert.False(File.Exists(zip)); // the bundle is removed once the link exists
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt"))); // the originals are never touched
    }

    [AvaloniaFact]
    public async Task One_other_file_is_shared_as_it_is_and_cancel_shares_nothing()
    {
        Directory.CreateDirectory(_root);
        var pdf = Path.Combine(_root, "tickets.pdf"); File.WriteAllText(pdf, "pdf");
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services with { DataRoot = Path.Combine(_root, "data") });

        await model.AcceptDroppedAsync([pdf]);
        Assert.DoesNotContain(".zip", model.DropShareDetail);
        model.CancelDroppedCommand.Execute(null);
        Assert.False(model.HasDropShare); Assert.Equal(0, fake.Sharing.Calls);

        await model.AcceptDroppedAsync([pdf]);
        await model.ShareDroppedCommand.ExecuteAsync(null);
        Assert.Equal(pdf, Assert.Single(fake.Sharing.Paths));
    }

    [AvaloniaFact]
    public async Task One_video_still_opens_in_the_editor_and_a_failed_upload_keeps_the_card()
    {
        Directory.CreateDirectory(_root);
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services with { DataRoot = Path.Combine(_root, "data") });
        await model.InitializeAsync();
        var clip = Path.Combine(_root, "clip.mp4"); File.WriteAllText(clip, "video");
        await model.AcceptDroppedAsync([clip]);
        Assert.False(model.HasDropShare); Assert.True(model.HasEditor, model.Status);

        // A file KeaClip itself is dragging out, let go over KeaClip (or just clicked), does nothing.
        var a = Path.Combine(_root, "a.txt");
        File.WriteAllText(a, "a");
        model.DraggingOutPath = a;
        await model.AcceptDroppedAsync([a]);
        Assert.False(model.HasDropShare);
        model.DraggingOutPath = null;

        var b = Path.Combine(_root, "b.txt"); File.WriteAllText(b, "b");
        await model.AcceptDroppedAsync([a, b]);
        Assert.Equal("a.txt and 1 more", model.DropShareName);
        fake.Sharing.Failure = new InvalidOperationException("offline");
        await model.ShareDroppedCommand.ExecuteAsync(null);
        Assert.True(model.HasDropShare); // try again from the same card
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "data", "ShareBundles")));
    }

    [AvaloniaFact]
    public async Task The_ready_to_share_card_shows_above_the_editor_with_share_and_cancel()
    {
        var folder = Path.Combine(_root, "Holiday photos"); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "beach.jpg"), new byte[3 * 1024 * 1024]);
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services with { DataRoot = Path.Combine(_root, "data") }) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 780 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.Theme = "Dark";
            await model.AcceptDroppedAsync([folder]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await Task.Delay(300); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindNamed<Avalonia.Controls.Border>("DropShareCard")!.IsEffectivelyVisible);
            Assert.True(window.FindNamed<Avalonia.Controls.Button>("ShareDroppedButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindNamed<Avalonia.Controls.Button>("CancelDroppedButton")!.IsEffectivelyVisible);
            if (Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY") is { Length: > 0 } output)
            {
                using var frame = window.CaptureRenderedFrame(); Directory.CreateDirectory(output);
                frame!.Save(Path.Combine(output, "drop-share-card-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { model.CloseBehavior = Klippi.Core.CloseBehavior.Exit; window.Close(); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
