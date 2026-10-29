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

    [AvaloniaFact]
    public void The_update_card_shows_its_highlights_and_a_link_while_download_notices_have_none()
    {
        var fake = new UiFakes();
        using var model = new MainViewModel(fake.Services) { ReducedMotion = true };
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 780 }; window.Show();
        try
        {
            model.Theme = "Dark";
            model.ShowWhatsNew("KeaClip was updated to 0.4.6. Your settings and media were kept.", "New: What's new after an update · Ready to share, from the start");
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindNamed<Border>("NoticeCard")!.IsEffectivelyVisible);
            Assert.True(window.FindNamed<Button>("NoticeLinkButton")!.IsEffectivelyVisible);
            if (Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY") is { Length: > 0 } output)
            {
                using var frame = window.CaptureRenderedFrame(); Directory.CreateDirectory(output);
                frame!.Save(Path.Combine(output, "whats-new-card-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            model.Notice = new CompletionNote("Download finished", "A moment worth keeping"); Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindNamed<Button>("NoticeLinkButton")!.IsEffectivelyVisible);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }
}
