using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

/// <summary>An in-memory watch link service: the setup is kept in fields, the secret only as "was one saved".</summary>
public sealed class FakeWatchLinks : IWatchLinkService
{
    public const string Link = "https://watch.example.com/v/#AbCdEfGhIjKlMnOpQrStUv.0123456789abcdefABCD_-";
    public WatchLinkSetup? Setup { get; private set; }
    public string? SavedSecret { get; private set; }
    public int Shares { get; private set; }
    public string? SharedPath { get; private set; }
    public Task<WatchLinkSetupState> LoadSetupAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WatchLinkSetupState(Setup, SavedSecret is not null));
    public Task SaveSetupAsync(WatchLinkSetup setup, string? secretAccessKey, CancellationToken cancellationToken = default)
    {
        Setup = setup;
        if (secretAccessKey is not null) SavedSecret = secretAccessKey;
        return Task.CompletedTask;
    }
    public Task ClearSetupAsync(CancellationToken cancellationToken = default) { Setup = null; SavedSecret = null; return Task.CompletedTask; }
    public Task<ShareResult> ShareAsync(string path, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Shares++; SharedPath = path;
        progress?.Report(new JobProgress(JobStage.Converting, 50, "Converting for browsers"));
        progress?.Report(new JobProgress(JobStage.Uploading, 100, "Encrypting and uploading…"));
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new ShareResult(Link, "AbCdEfGhIjKlMnOpQrStUv", "media", 168, now, now.AddDays(7), "00", "Anyone with the full link can watch until the bucket deletes it, about 7 days from now."));
    }
}

public sealed class WatchLinkUiTests
{
    private const string Secret = "R2secretValue0123456789abcdefABCDEF";

    private static void Fill(MainViewModel model)
    {
        model.WatchEndpoint = "https://0123abcd.r2.cloudflarestorage.com";
        model.WatchBucket = "keaclip-watch";
        model.WatchAccessKeyId = "AKIDEXAMPLE0123456789";
        model.WatchSecret = Secret;
        model.WatchPlayerPage = "https://watch.example.com/v/";
        model.WatchRetentionDays = "7";
    }

    private static string TempMedia()
    {
        var file = Path.Combine(Path.GetTempPath(), "keaclip-watch-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(file, new byte[2048]);
        return file;
    }

    [AvaloniaFact]
    public async Task Without_a_setup_a_watch_link_sends_people_to_Settings_and_uploads_nothing()
    {
        var fake = new UiFakes(); var watch = new FakeWatchLinks();
        using var model = new MainViewModel(fake.Services with { WatchLinks = watch }); await model.InitializeAsync();
        var file = TempMedia();
        try
        {
            await model.ShareWatchLinkAsync(file);
            Assert.Equal(0, watch.Shares); Assert.Equal(0, fake.Sharing.Calls);
            Assert.Equal(4, model.SelectedTab);
            Assert.StartsWith("Set up watch links first", model.Status);
            Assert.False(model.IsWatchLinkReady);
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task Saving_the_setup_validates_it_and_never_keeps_the_secret_in_the_form()
    {
        var fake = new UiFakes(); var watch = new FakeWatchLinks();
        using var model = new MainViewModel(fake.Services with { WatchLinks = watch }); await model.InitializeAsync();
        Fill(model); model.WatchBucket = "Not_A_Bucket";
        await model.SaveWatchSetupCommand.ExecuteAsync(null);
        Assert.Null(watch.Setup); Assert.Contains("bucket", model.WatchSetupStatus); Assert.Equal(Secret, model.WatchSecret);
        model.WatchBucket = "keaclip-watch"; model.WatchRetentionDays = "forever";
        await model.SaveWatchSetupCommand.ExecuteAsync(null);
        Assert.Null(watch.Setup); Assert.Contains("days", model.WatchSetupStatus);
        model.WatchRetentionDays = "14";
        await model.SaveWatchSetupCommand.ExecuteAsync(null);
        Assert.Equal(14, watch.Setup!.RetentionDays); Assert.Equal(Secret, watch.SavedSecret);
        Assert.Equal("", model.WatchSecret); Assert.True(model.HasWatchSecret); Assert.True(model.IsWatchLinkReady);
        Assert.Equal("Saved and protected. Leave blank to keep it.", model.WatchSecretPlaceholder);
        // A later save without retyping the secret keeps the stored one.
        model.WatchRetentionDays = "30"; await model.SaveWatchSetupCommand.ExecuteAsync(null);
        Assert.Equal(30, watch.Setup!.RetentionDays); Assert.Equal(Secret, watch.SavedSecret);
        await model.RemoveWatchSetupCommand.ExecuteAsync(null);
        Assert.Null(watch.Setup); Assert.False(model.IsWatchLinkReady); Assert.Equal("", model.WatchEndpoint);
    }

    [AvaloniaFact]
    public async Task A_watch_link_fills_the_share_card_and_opens_only_on_the_configured_player_page()
    {
        var fake = new UiFakes(); var watch = new FakeWatchLinks();
        using var model = new MainViewModel(fake.Services with { WatchLinks = watch }); await model.InitializeAsync();
        Fill(model); await model.SaveWatchSetupCommand.ExecuteAsync(null);
        var file = TempMedia();
        try
        {
            await model.ShareWatchLinkAsync(file);
            Assert.Equal(1, watch.Shares); Assert.Equal(file, watch.SharedPath); Assert.Equal(0, fake.Sharing.Calls);
            Assert.Equal(FakeWatchLinks.Link, model.ShareUrl); Assert.Equal(3, model.SelectedTab);
            Assert.StartsWith("Watch link ready.", model.Status);
            Assert.Contains("end-to-end encrypted", model.ShareInfo); Assert.Contains("7 days", model.ShareInfo);
            Assert.False(model.IsSharing);
            model.OpenShareCommand.Execute(null);
            Assert.Equal(FakeWatchLinks.Link, Assert.Single(fake.Platform.OpenedUris).AbsoluteUri);
            // The same fragment on another host is not the user's player page.
            Assert.False(model.IsWatchLinkUrl(new Uri("https://evil.example.net/v/#AbCdEfGhIjKlMnOpQrStUv.0123456789abcdefABCD_-")));
            Assert.False(model.IsWatchLinkUrl(new Uri("https://watch.example.com/v/#short")));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Watch_links_are_private_share_links_never_offered_as_downloads()
    {
        Assert.True(MediaValidation.IsPrivateShareLink(new Uri(FakeWatchLinks.Link)));
        Assert.True(MediaValidation.IsWatchLinkFragment("#AbCdEfGhIjKlMnOpQrStUv.0123456789abcdefABCD_-"));
        Assert.False(MediaValidation.IsWatchLinkFragment("#AbCdEfGhIjKlMnOpQrStUv_0123456789abcdefABCD_-"));
        Assert.False(MediaValidation.IsWatchLinkFragment("#AbCdEfGhIjKlMnOpQrStUv.0123456789abcdefABCD+-"));
        Assert.False(MediaValidation.IsPrivateShareLink(new Uri("https://example.com/watch#t=30")));
    }

    [AvaloniaFact]
    public async Task The_settings_card_and_the_editor_button_appear_only_with_the_service()
    {
        foreach (var withService in new[] { true, false })
        {
            var fake = new UiFakes();
            var model = new MainViewModel(withService ? fake.Services with { WatchLinks = new FakeWatchLinks() } : fake.Services);
            var window = new MainWindow { DataContext = model, Width = 1180, Height = 860 }; window.Show();
            try
            {
                await model.InitializeAsync(); model.SelectedTab = 4; model.ShowAdvancedSettings = true; window.UpdateLayout();
                var card = window.FindNamed<Border>("WatchLinksCard")!;
                Assert.Equal(withService, card.IsEffectivelyVisible);
                if (withService)
                {
                    Fill(model); window.UpdateLayout();
                    var secretBox = window.FindNamed<TextBox>("WatchSecretBox")!;
                    Assert.Equal('•', secretBox.PasswordChar);
                    card.BringIntoView(); window.UpdateLayout(); await Task.Delay(260); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    Save(window, "settings-watch-links");
                }
                Assert.Equal(withService, window.FindNamed<Button>("WatchLinkButton")!.IsVisible);
            }
            finally { window.Close(); model.Dispose(); }
        }
    }

    private static void Save(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
