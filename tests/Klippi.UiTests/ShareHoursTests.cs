using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Klippi.Desktop;
using Klippi.Desktop.Views;
using Xunit;

namespace Klippi.UiTests;

// File.Kiwi asked that users are told how long API uploads stay downloadable (it differs from the website), with a
// link to their table. Both Share spots link to it, and the hours follow the published table.
public sealed class ShareHoursTests
{
    [Theory]
    [InlineData(0L, "24 hours")]
    [InlineData(9_999_999_999L, "24 hours")]
    [InlineData(10_000_000_000L, "12 hours")]
    [InlineData(19_999_999_999L, "12 hours")]
    [InlineData(20_000_000_000L, "6 hours")]
    public void Free_download_hours_follow_the_published_table(long bytes, string expected) =>
        Assert.Equal(expected, MainViewModel.FreeDownloadHoursText(bytes));

    [AvaloniaFact]
    public void Both_share_spots_link_to_the_free_download_hours_table()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services);
        var editor = new EditorView { DataContext = model };
        var download = new DownloadView { DataContext = model };
        var editorLink = editor.FindControl<Button>("ShareHoursLink")!;
        var downloadLink = download.FindControl<Button>("ShareWhenFinishedHoursLink")!;
        Assert.Contains("24 hours", editor.FindControl<TextBlock>("ShareHoursText")!.Text);
        editorLink.Command!.Execute(null);
        downloadLink.Command!.Execute(null);
        Assert.Equal(2, fake.Platform.OpenedUris.Count);
        Assert.All(fake.Platform.OpenedUris, uri => Assert.Equal("https://file.kiwi/api#free-download-hours-by-file-size", uri.AbsoluteUri));
    }
}
