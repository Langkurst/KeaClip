using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;

namespace Klippi.Desktop;

public sealed class PlaylistChoice : ObservableObject
{
    private readonly Func<PlaylistChoice, Task>? _openAsPlaylist;
    private IAsyncRelayCommand? _openAsPlaylistCommand;
    private bool _selected;
    public PlaylistChoice(PlaylistEntry entry) : this(entry, -1, false, null) { }
    /// <param name="index">The entry's position in the analysed listing; -1 when unknown.</param>
    /// <param name="isList">True when the entry is a list itself (a channel tab, a playlist in a channel).</param>
    /// <param name="openAsPlaylist">Analyses the entry's own address as a playlist.</param>
    public PlaylistChoice(PlaylistEntry entry, int index, bool isList, Func<PlaylistChoice, Task>? openAsPlaylist)
    {
        Entry = entry; Index = index; IsList = isList; _openAsPlaylist = openAsPlaylist;
    }
    public PlaylistEntry Entry { get; }
    public int Index { get; }
    public string Title => Entry.Title;
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    /// <summary>A list cannot be downloaded as one item: Download skips it, and Open as playlist shows its videos.</summary>
    public bool IsList { get; }
    public bool CanOpenAsPlaylist => IsList && Entry.Url is not null && _openAsPlaylist is not null;
    public IAsyncRelayCommand OpenAsPlaylistCommand => _openAsPlaylistCommand ??= new AsyncRelayCommand(() => _openAsPlaylist?.Invoke(this) ?? Task.CompletedTask, () => CanOpenAsPlaylist);
}

public sealed class BatchLinkChoice(Uri url) : ObservableObject
{
    private bool _selected;
    public Uri Url { get; } = url;
    public string Label => Url.AbsoluteUri;
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
}

public sealed class PendingDownload
{
    public PendingDownload(MainViewModel owner, DownloadOptions options, string title, bool editAfter, Uri? thumbnail, HistoryEntry existing, bool shareAfter = false)
    {
        Options = options; Title = title; EditAfter = editAfter; Thumbnail = thumbnail; Existing = existing; ShareAfter = shareAfter;
        OpenCommand = new RelayCommand(() => owner.ResolveDuplicate(this, "open")); AgainCommand = new RelayCommand(() => owner.ResolveDuplicate(this, "again")); ReplaceCommand = new RelayCommand(() => owner.ResolveDuplicate(this, "replace")); SkipCommand = new RelayCommand(() => owner.ResolveDuplicate(this, "skip"));
    }
    public DownloadOptions Options { get; }
    public string Title { get; }
    public bool EditAfter { get; }
    /// <summary>Share when finished was on when this request was made.</summary>
    public bool ShareAfter { get; }
    public Uri? Thumbnail { get; }
    public HistoryEntry Existing { get; }
    public string Detail => Path.GetFileName(Existing.FilePath) + " · " + MainViewModel.Size(Existing.SizeBytes) + " · saved " + Existing.Date.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    // Replace overwrites a file, so it is offered only when the link identifies exactly
    // one media item (or the match is the expected output file itself), and only when this
    // request writes exactly the saved file: its folder, its name and its format. A saved WebM
    // and an MP4 request would otherwise leave both files while the card promised an overwrite.
    public bool CanReplace => ReplaceBlocker is null;
    /// <summary>Why Replace is unavailable, or what it does; shown as the Replace button's tip.</summary>
    public string ReplaceTip => ReplaceBlocker ?? "Overwrites " + Path.GetFileName(Existing.FilePath) + " in its folder with this download.";
    /// <summary>The saved file's name as the engine writes it when Replace passes that name as the template.</summary>
    internal string? ReplaceName => DownloadService.LiteralOutputName(Path.GetFileNameWithoutExtension(Existing.FilePath));
    private string? ReplaceBlocker
    {
        get
        {
            if (Options.Mode == DownloadMode.Archive) return "Archives can only be kept or copied. Choose Download again to make a separate copy.";
            if (Existing.Mode != Options.Mode.ToString()) return "The saved file was downloaded in a different mode. Choose Download again to keep both.";
            if (Existing.Id != "existing-output" && !MediaValidation.SafeSourceUrlIsExact(Options.Url)) return "This link may not name exactly one saved item, so Replace is not offered. Choose Download again to keep both.";
            if (WritesSavedFile()) return null;
            var extensions = DownloadService.OutputExtensions(Options);
            return extensions.Contains(Path.GetExtension(Existing.FilePath), StringComparer.OrdinalIgnoreCase)
                ? "The saved file's name cannot be written exactly again, so Replace would save a second file instead of overwriting it. Choose Download again to keep both."
                : "This download would save a different format (" + string.Join(" or ", extensions) + ") than the saved " + Path.GetExtension(Existing.FilePath).ToLowerInvariant() + " file, so it cannot overwrite it. Choose Download again to keep both.";
        }
    }
    private bool WritesSavedFile()
    {
        try
        {
            if (Path.GetDirectoryName(Existing.FilePath) is not { Length: > 0 } folder || ReplaceName is not { } name) return false;
            var saved = Path.GetFullPath(Existing.FilePath);
            return DownloadService.PotentialOutputPaths(folder, name, Options).Any(path => string.Equals(Path.GetFullPath(path), saved, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand AgainCommand { get; }
    public IRelayCommand ReplaceCommand { get; }
    public IRelayCommand SkipCommand { get; }
}
