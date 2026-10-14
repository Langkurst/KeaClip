using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Regression tests for the final review's editor findings: the soundboard preset (#5, #6), the
// editor's audio bitrates (#7), the label of a kept share link (#8), compression labels for audio
// clips (#10) and one waveform decode per open (#21).
public sealed class ReviewFixEditorTests
{
    // ===== #5: the soundboard preset never leaks into later exports =====

    [AvaloniaFact]
    public async Task Choosing_video_clip_ends_the_soundboard_preset_and_restores_the_audio_settings()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("sound.mp4");
        var (format, kbps, rate) = (model.ExportAudioFormat, model.ExportAudioKbps, model.SampleRate);
        model.SoundboardCommand.Execute(null);
        Assert.True(model.Soundboard); Assert.True(model.Mono); Assert.Equal(128, model.ExportAudioKbps);

        model.AudioOnly = false;

        Assert.False(model.Soundboard); Assert.False(model.Mono);
        Assert.Equal(format, model.ExportAudioFormat); Assert.Equal(kbps, model.ExportAudioKbps); Assert.Equal(rate, model.SampleRate);
        fake.Platform.SavePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4"); // never written: the fake export only records its options
        await model.ExportAsync();
        Assert.True(fake.Media.Exports.Count == 1, model.Status); var export = fake.Media.Exports[0];
        Assert.False(export.DiscordSoundboard); Assert.False(export.Mono); Assert.Equal(kbps, export.AudioKbps);
    }

    [AvaloniaFact]
    public async Task Opening_another_file_ends_the_soundboard_preset()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("sound.mp4");
        model.SoundboardCommand.Execute(null);

        await model.OpenEditorAsync("holiday.mp4");

        Assert.False(model.Soundboard); Assert.False(model.Mono); Assert.Equal(192, model.ExportAudioKbps);
        Assert.Contains("soundboard preset from the previous file is off", model.Status);
        fake.Platform.SavePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4"); // never written: the fake export only records its options
        await model.ExportAsync();
        Assert.True(fake.Media.Exports.Count == 1, model.Status); var export = fake.Media.Exports[0];
        Assert.False(export.DiscordSoundboard); Assert.False(export.Mono);
    }

    [AvaloniaFact]
    public async Task Choosing_a_format_a_soundboard_cannot_use_ends_the_preset()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("sound.mp4");
        model.SoundboardCommand.Execute(null);

        model.ExportAudioFormat = AudioFormat.Aac;

        Assert.False(model.Soundboard); Assert.False(model.Mono);
        Assert.Equal(AudioFormat.Aac, model.ExportAudioFormat);
    }

    [AvaloniaFact]
    public async Task A_soundboard_conflict_is_refused_before_the_save_dialog()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("clip.mp4");
        model.ClipMode = ClipMode.Exact; model.Soundboard = true; // a video clip with the switch on

        await model.ExportAsync();

        Assert.Equal(0, fake.Platform.SaveDialogs); Assert.Empty(fake.Media.Exports);
        Assert.Contains("Soundboard export needs an audio clip", model.Status);
    }

    // ===== #6: the soundboard preset only shortens =====

    [AvaloniaFact]
    public async Task Soundboard_preset_keeps_a_shorter_selection()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("sound.mp4");
        model.StartTime = "00:00:02.000"; model.EndTime = "00:00:03.500";

        model.SoundboardCommand.Execute(null);

        Assert.Equal("00:00:02.000", model.StartTime); Assert.Equal("00:00:03.500", model.EndTime);
    }

    [AvaloniaFact]
    public async Task Soundboard_preset_still_shortens_a_longer_selection()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("sound.mp4");

        model.SoundboardCommand.Execute(null);

        Assert.Equal("00:00:05.200", model.EndTime);
    }

    // ===== #7: only bitrates the encoder accepts =====

    [AvaloniaTheory]
    [InlineData(AudioFormat.Ogg, 48000, 192)]
    [InlineData(AudioFormat.Opus, 48000, 256)]
    [InlineData(AudioFormat.Ogg, 22050, 64)]
    public async Task Mono_audio_offers_only_bitrates_the_encoder_accepts(AudioFormat format, int sourceRate, int expectedMax)
    {
        var fake = new UiFakes(); var media = new CountingMedia(fake.Media) { SampleRate = sourceRate };
        using var model = new MainViewModel(Services(fake, media));
        await model.OpenEditorAsync("voice.mp4");
        model.ClipMode = ClipMode.Exact; model.AudioOnly = true; model.ExportAudioFormat = format; model.ExportAudioKbps = 320;

        model.Mono = true;

        Assert.Equal(expectedMax, model.ExportBitrates.Max());
        Assert.Equal(expectedMax, model.ExportAudioKbps);
        Assert.True(model.HasExportBitrateNote);
        Assert.Contains("kbps", model.ExportBitrateNote);
        // Picking another offered bitrate clears the note.
        if (model.ExportBitrates.Count > 1) { model.ExportAudioKbps = model.ExportBitrates.Min(); Assert.False(model.HasExportBitrateNote); }
    }

    [AvaloniaFact]
    public async Task Editor_bitrate_list_follows_the_export_format_not_the_download_format()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 1200, Height = 900 }; window.Show();
        try
        {
            await model.OpenEditorAsync("voice.mp4");
            model.ClipMode = ClipMode.Exact; model.AudioOnly = true; model.ExportAudioFormat = AudioFormat.Opus; model.Mono = true;
            model.AudioFormat = AudioFormat.Mp3; // the Download page's format
            Settle(window);
            var picker = window.FindNamed<ComboBox>("ExportBitratePicker");
            Assert.NotNull(picker);
            var offered = picker!.ItemsSource!.Cast<int>().ToArray();
            Assert.DoesNotContain(320, offered);
            Assert.Equal(model.ExportBitrates, offered);
        }
        finally { window.Close(); }
    }

    // ===== #8: a kept share link names its file =====

    [AvaloniaFact]
    public async Task A_kept_share_link_names_the_file_it_opens()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model, Width = 1200, Height = 900 }; window.Show();
        try
        {
            await model.OpenEditorAsync("holiday-private.mp4");
            await model.ShareFileAsync("holiday-private.mp4");
            Assert.False(model.ShareLinkIsForOtherFile);

            await model.OpenEditorAsync("for-work.mp4");
            Settle(window);

            Assert.Equal("Share link for holiday-private.mp4", model.ShareLinkLabel);
            Assert.True(model.ShareLinkIsForOtherFile);
            Assert.Contains("holiday-private.mp4", model.ShareLinkOtherFileText);
            Assert.Equal("Share link for holiday-private.mp4", window.FindNamed<TextBlock>("ShareLinkLabelText")!.Text);
            Assert.True(window.FindNamed<TextBlock>("ShareLinkOtherFileNote")!.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    // ===== #10: compression labels for audio clips =====

    [AvaloniaTheory]
    [InlineData(AudioFormat.Mp3, CompressionPreset.VerySmall, "64 kbps audio")]
    [InlineData(AudioFormat.Mp3, CompressionPreset.Light, "160 kbps audio")]
    [InlineData(AudioFormat.Wav, CompressionPreset.Strong, "Lossless audio")]
    public async Task Audio_clip_compression_label_names_what_is_applied(AudioFormat format, CompressionPreset preset, string expected)
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("voice.mp4");
        model.ClipMode = ClipMode.Exact; model.AudioOnly = true; model.ExportAudioFormat = format; model.ExportAudioKbps = 192;

        model.Compression = preset;

        Assert.Equal(expected, model.EstimateQualityText);
        Assert.DoesNotContain("compression", model.EstimateQualityText, StringComparison.OrdinalIgnoreCase);
    }

    // ===== #21: one waveform decode per open =====

    [AvaloniaTheory]
    [InlineData(10, 600)]
    [InlineData(300, 3600)]
    [InlineData(3600, 4096)]
    public async Task Opening_a_file_decodes_its_waveform_once(int seconds, int expectedPoints)
    {
        var fake = new UiFakes(); var media = new CountingMedia(fake.Media) { Duration = TimeSpan.FromSeconds(seconds) };
        using var model = new MainViewModel(Services(fake, media));

        await model.OpenEditorAsync("long.mp4");
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();

        Assert.Equal([expectedPoints], media.WaveformRequests);
        Assert.Equal(expectedPoints, model.Waveform.Count);
    }

    [AvaloniaFact]
    public async Task A_failed_open_waveform_is_retried_in_the_background_and_yields_to_other_work()
    {
        var fake = new UiFakes(); var media = new CountingMedia(fake.Media) { Duration = TimeSpan.FromMinutes(5), FailFirstWaveform = true, DetailGate = new() };
        using var model = new MainViewModel(Services(fake, media));
        await model.OpenEditorAsync("long.mp4");
        Assert.Contains("waveform could not be drawn", model.Status);
        Assert.Equal([3600, 3600], media.WaveformRequests);

        // Another operation (an export whose save dialog is cancelled) stops the pass first; it starts again afterwards.
        await model.ExportAsync();
        await Until(() => media.Cancelled == 1 && media.WaveformRequests.Count == 3);
        media.DetailGate.SetResult();
        await Until(() => model.Waveform.Count == 3600);
        Assert.Equal([3600, 3600, 3600], media.WaveformRequests);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var wait = 0; wait < 200; wait++) { Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(25); }
        Assert.Fail("The condition was not met in time.");
    }

    private static DesktopServices Services(UiFakes fake, IMediaService media) => new(fake.Downloads, media, fake.Sharing, fake.Store, fake.Components, fake.Platform, fake.Playback, "test-owned-data");

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); if (pass < 2) Thread.Sleep(60); }
    }

    /// <summary>The UI fake media with a chosen duration and sample rate, recording each waveform request's point count.</summary>
    private sealed class CountingMedia(FakeMedia inner) : IMediaService
    {
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
        public int SampleRate { get; init; } = 48000;
        public List<int> WaveformRequests { get; } = [];
        public bool FailFirstWaveform { get; set; }
        /// <summary>Holds every waveform request after the first until released.</summary>
        public TaskCompletionSource? DetailGate { get; init; }
        public int Cancelled { get; private set; }
        public Task<ProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProbeInfo(path, Duration, 2_000_000, true, true, 1920, 1080, 30, "h264", "aac", SampleRate, 2, null));
        public Task<ExportResult> ExportAsync(ExportOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => inner.ExportAsync(options, progress, cancellationToken);
        public Task<IReadOnlyList<EncoderInfo>> EncodersAsync(CancellationToken cancellationToken = default) => inner.EncodersAsync(cancellationToken);
        public async Task<IReadOnlyList<float>> WaveformAsync(string path, int points = 600, CancellationToken cancellationToken = default)
        {
            int count;
            lock (WaveformRequests) { WaveformRequests.Add(points); count = WaveformRequests.Count; }
            if (count == 1 && FailFirstWaveform) throw new MediaOperationException("FFmpeg could not process this file.");
            try { if (count > 1 && DetailGate is { } gate) await gate.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled++; throw; }
            return await inner.WaveformAsync(path, points, cancellationToken);
        }
        public ExportEstimate Estimate(ProbeInfo probe, ExportOptions options) => inner.Estimate(probe, options);
    }
}
