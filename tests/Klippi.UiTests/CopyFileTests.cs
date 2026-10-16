using Avalonia.Headless.XUnit;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Copy file puts the file itself on the clipboard, so Ctrl+V in Discord, a chat or an email attaches it.
public sealed class CopyFileTests
{
    [AvaloniaFact]
    public async Task A_library_item_copies_its_file_not_its_path()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services);
        await model.CopyFileAsync(@"C:\Videos\KeaClip\clip.mp4");
        Assert.Equal(@"C:\Videos\KeaClip\clip.mp4", fake.Platform.CopiedFile);
        Assert.Null(fake.Platform.Clipboard); // no text copied
        Assert.Contains("Ctrl+V", model.Status);
    }

    [AvaloniaFact]
    public async Task A_missing_file_or_busy_clipboard_is_explained_and_never_throws()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services);
        fake.Platform.FilesMissing = true;
        await model.CopyFileAsync(@"C:\Videos\KeaClip\gone.mp4");
        Assert.Contains("no longer available", model.Status);

        fake.Platform.FilesMissing = false; fake.Platform.CopyFailure = new InvalidOperationException("Clipboard locked");
        await model.CopyFileAsync(@"C:\Videos\KeaClip\clip.mp4");
        Assert.Contains("clipboard is busy", model.Status);
        Assert.Null(fake.Platform.CopiedFile);
    }
}
