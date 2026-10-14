using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;

namespace Klippi.Desktop;

public interface IPlaybackService : INotifyPropertyChanged, IDisposable
{
    MediaPlayer? NativePlayer { get; }
    bool IsPlaying { get; }
    double Position { get; }
    TimeSpan Time { get; }
    void Open(string path);
    void Toggle();
    void Seek(double position);
    void SetVolume(int value);
    void Stop();
    void Close();
    /// <summary>A short reason when the preview could not decode the open file; null while preview works.</summary>
    string? LastError => null;
}

public sealed class VlcPlaybackService : ObservableObject, IPlaybackService
{
    // A seek is shown immediately; the player's own report replaces it once it lands near the target.
    private const double SeekSettleSeconds = .5, SeekHoldSeconds = 1.2;
    private readonly string[] _options;
    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private Media? _media;
    private bool _disposed;
    // Open() loads the file paused (":start-paused"): VLC prepares the input and its first picture,
    // so seeking, the clock and marking at the playhead work before the first Play.
    private bool _priming, _playWhenReady;
    private double? _pendingSeek, _seekTarget;
    private long _seekTicks;
    private string? _lastError;
    // Captured on the UI thread when the first file opens; VLC raises events on its own threads,
    // which must never touch the static dispatcher accessor themselves.
    private Dispatcher? _dispatcher;

    public VlcPlaybackService() : this([]) { }

    /// <summary>Extra LibVLC options (for example dummy outputs for native verification runs).</summary>
    internal VlcPlaybackService(params string[] options) => _options = ["--no-video-title-show", "--quiet", .. options];

    public MediaPlayer? NativePlayer => _player;
    // While the input is still being prepared, a Play request already counts as playing for the UI.
    public bool IsPlaying => _player is not null && (_priming ? _playWhenReady : _player.IsPlaying);
    public double Position => _pendingSeek ?? _seekTarget ?? Math.Max(0, _player?.Position ?? 0);
    public TimeSpan Time
    {
        get
        {
            if ((_pendingSeek ?? _seekTarget) is { } target && _player is { Length: > 0 } player) return TimeSpan.FromMilliseconds(target * player.Length);
            return TimeSpan.FromMilliseconds(Math.Max(0, _player?.Time ?? 0));
        }
    }
    public string? LastError => _lastError;

    public void Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path)) throw new FileNotFoundException("This media file is no longer available.");
        _dispatcher ??= Dispatcher.UIThread;
        if (_vlc is null)
        {
            PlaybackRuntime.Prepare();
            LibVLCSharp.Shared.Core.Initialize();
            _vlc = new LibVLC(_options);
            _player = new MediaPlayer(_vlc);
            // VLC raises these on its own threads; all state changes are applied on the UI thread.
            _player.TimeChanged += (_, _) => Post(Refresh);
            _player.Playing += (_, _) => Post(OnPlaying);
            _player.Paused += (_, _) => Post(OnPaused);
            _player.Stopped += (_, _) => Post(Refresh);
            // Reaching the end (or a decode error) also leaves the playing state; without these the
            // UI kept showing Pause and the timeline kept animating after playback finished.
            _player.EndReached += (_, _) => Post(OnInputFinished);
            _player.EncounteredError += (_, _) => Post(OnError);
            OnPropertyChanged(nameof(NativePlayer));
        }
        _player!.Stop(); _media?.Dispose();
        _media = new Media(_vlc, Path.GetFullPath(path), FromType.FromPath);
        _media.AddOption(":start-paused");
        _player.Media = _media;
        _pendingSeek = null; _seekTarget = null;
        SetError(null);
        StartPaused(playWhenReady: false);
    }

    public void Toggle()
    {
        if (_player is null || _media is null) return;
        if (_priming) { _playWhenReady = !_playWhenReady; Refresh(); return; }
        // A finished, stopped or failed input cannot resume: restart it, then play from the restored position.
        if (_player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error or VLCState.NothingSpecial) { StartPaused(playWhenReady: true); return; }
        _player.SetPause(_player.IsPlaying);
        Refresh();
    }

    public void Seek(double position)
    {
        if (_player is null || _media is null || !double.IsFinite(position)) return;
        var value = Math.Clamp(position, 0, 1);
        _seekTarget = value; _seekTicks = Stopwatch.GetTimestamp();
        if (!_priming && _player.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering) { _pendingSeek = null; _player.Position = (float)value; }
        else
        {
            // LibVLC ignores positions until the input is open; keep the seek and apply it once it is.
            _pendingSeek = value;
            if (!_priming) StartPaused(playWhenReady: false);
        }
        Raise();
    }

    public void SetVolume(int value) { if (_player is not null) _player.Volume = Math.Clamp(value, 0, 100); }
    public void Stop() { _priming = false; _playWhenReady = false; _pendingSeek = null; _seekTarget = null; _player?.Stop(); Raise(); }
    public void Close() { Stop(); if (_player is not null) _player.Media = null; _media?.Dispose(); _media = null; Raise(); }

    private void StartPaused(bool playWhenReady)
    {
        if (_player is null || _media is null) return;
        if (_player.State is not (VLCState.NothingSpecial or VLCState.Stopped)) _player.Stop();
        _priming = true; _playWhenReady = playWhenReady;
        if (!_player.Play()) { _priming = false; _playWhenReady = false; SetError("Preview could not start this file."); }
        Raise();
    }

    private void OnPaused()
    {
        if (_disposed || _player is null) return;
        if (_priming)
        {
            _priming = false;
            if (_pendingSeek is { } seek) { _pendingSeek = null; _player.Position = (float)seek; }
            if (_playWhenReady) { _playWhenReady = false; _player.SetPause(false); }
        }
        Raise();
    }

    private void OnPlaying()
    {
        Refresh();
        if (!_priming) return;
        // Safety net: an input that cannot start paused must not keep playing behind a "paused" UI.
        DispatcherTimer.RunOnce(() =>
        {
            if (_disposed || !_priming || _player is not { State: VLCState.Playing } player) return;
            _priming = false;
            if (_pendingSeek is { } seek) { _pendingSeek = null; player.Position = (float)seek; }
            if (_playWhenReady) _playWhenReady = false; else player.SetPause(true);
            Raise();
        }, TimeSpan.FromSeconds(1.5));
    }

    private void OnInputFinished()
    {
        if (_disposed) return;
        // The input is finished; the next Play or seek restarts it (see Toggle/Seek).
        _priming = false; _playWhenReady = false; _seekTarget = null;
        Raise();
    }

    private void OnError()
    {
        if (_disposed) return;
        _priming = false; _playWhenReady = false; _pendingSeek = null; _seekTarget = null;
        SetError("Preview could not play this file.");
        Raise();
    }

    private void Refresh()
    {
        if (_disposed || _player is null) return;
        // Drop the shown seek target once the player reports a time near it (or after a short hold).
        if (_pendingSeek is null && _seekTarget is { } target)
        {
            var lengthSeconds = _player.Length / 1000d;
            var settled = lengthSeconds > 0 ? Math.Abs(_player.Position - target) * lengthSeconds <= SeekSettleSeconds : Math.Abs(_player.Position - target) < .01;
            if (settled || Stopwatch.GetElapsedTime(_seekTicks).TotalSeconds > SeekHoldSeconds) _seekTarget = null;
        }
        Raise();
    }

    private void SetError(string? error)
    {
        if (_lastError == error) return;
        _lastError = error;
        OnPropertyChanged(nameof(LastError));
    }

    private void Raise() { OnPropertyChanged(nameof(IsPlaying)); OnPropertyChanged(nameof(Position)); OnPropertyChanged(nameof(Time)); }
    private void Post(Action action) { if (!_disposed) _dispatcher?.Post(() => { if (!_disposed) action(); }); }
    public void Dispose() { if (_disposed) return; _disposed = true; _player?.Stop(); _player?.Dispose(); _media?.Dispose(); _vlc?.Dispose(); }
}
