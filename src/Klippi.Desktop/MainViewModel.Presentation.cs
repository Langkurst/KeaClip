using System.ComponentModel;
using Klippi.Core;

namespace Klippi.Desktop;

/// <summary>
/// Small read-only presentation state for the views. These values derive from plain
/// properties (Container, AudioFormat) that do not notify on their own, so the first read
/// subscribes once to the model's own change notifications and re-announces them when a
/// choice they depend on is refreshed. The subscription is to the model itself, so it
/// never keeps anything else alive.
/// </summary>
public sealed partial class MainViewModel
{
    private const string CoverArtDefaultTip = "Embeds the thumbnail as cover art inside the saved file.";
    private const string CoverArtArchiveTip = "Archives keep the thumbnail and metadata as separate files.";
    private const string CoverArtWebMNote = "WebM files can't hold cover art, so the thumbnail is skipped. Choose MKV or MP4 to embed it.";
    private const string CoverArtWavNote = "WAV files can't hold cover art, so the thumbnail is skipped. Choose another audio format to embed it.";

    private static readonly string[] CoverArtProperties = [nameof(CoverArtSupported), nameof(CanEmbedCover), nameof(CoverArtTip), nameof(CoverArtNote), nameof(ShowCoverArtNote)];
    private bool _presentationHooked;

    /// <summary>Whether the chosen download format can carry an embedded cover (WebM video and WAV audio cannot).</summary>
    public bool CoverArtSupported => Presented(!(IsVideoDownload && Container == MediaContainer.WebM) && !(IsAudioDownload && AudioFormat == AudioFormat.Wav));

    /// <summary>The cover option applies: not an archive (kept as a separate file) and a format that can hold it.</summary>
    public bool CanEmbedCover => CoverArtSupported && !IsArchiveDownload;

    /// <summary>Why the cover option is or is not available, for its tooltip and accessible help text.</summary>
    public string CoverArtTip => IsArchiveDownload ? Presented(CoverArtArchiveTip) : CoverArtNote is { Length: > 0 } note ? note : CoverArtDefaultTip;

    /// <summary>The visible explanation when the chosen format can't hold a cover; empty otherwise.</summary>
    public string CoverArtNote => IsArchiveDownload ? Presented("") : CoverArtSupported ? "" : IsAudioDownload ? CoverArtWavNote : CoverArtWebMNote;

    public bool ShowCoverArtNote => CoverArtNote.Length > 0;

    // Every presentation getter passes through here, so whichever is read first starts the updates.
    private T Presented<T>(T value)
    {
        if (!_presentationHooked)
        {
            _presentationHooked = true;
            PropertyChanged += OnPresentationSourceChanged;
        }
        return value;
    }

    private void OnPresentationSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Every container, codec or audio-format change ends in RefreshFormatChoice, which always
            // announces HasFormatConflict first (its other announcements follow, so one is enough);
            // mode changes announce DownloadMode.
            case nameof(DownloadMode):
            case nameof(Container):
            case nameof(AudioFormat):
            case nameof(HasFormatConflict):
                foreach (var name in CoverArtProperties) OnPropertyChanged(name);
                break;
        }
    }
}
