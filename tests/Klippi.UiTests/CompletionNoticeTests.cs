using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// A finished download or upload shows KeaClip's notice card in the window (and, in the background, flashes the taskbar).
// Avalonia's own notification manager never drew in this window, which is why download notices used to be invisible.
public sealed class CompletionNoticeTests
{
    [AvaloniaFact]
    public async Task A_finished_download_shows_the_notice_card_until_dismissed()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video", ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 780 }; window.Show();
        try
        {
            await model.InitializeAsync(); model.NotifyCompleted = true;
            var card = window.FindNamed<Border>("NoticeCard")!;
            Assert.False(card.IsEffectivelyVisible);
            await model.QueueDownloadAsync(false);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!model.HasNotice && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
            Dispatcher.UIThread.RunJobs();
            Assert.True(card.IsEffectivelyVisible);
            Assert.True(card.Bounds.Width > 0 && card.Bounds.Height > 0, $"card bounds {card.Bounds}");
            Assert.Equal("Download finished", window.FindNamed<TextBlock>("NoticeHeadingText")!.Text);
            Assert.Equal("A moment worth keeping", window.FindNamed<TextBlock>("NoticeDetailText")!.Text);
            if (Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY") is { Length: > 0 } output)
            {
                await Task.Delay(400); Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame(); Directory.CreateDirectory(output);
                frame!.Save(Path.Combine(output, "completion-notice-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            model.DismissNoticeCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.False(card.IsEffectivelyVisible);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }
}
