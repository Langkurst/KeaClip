using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Infrastructure.Sharing;

namespace Klippi.Desktop;

// Anything dropped on the window that isn't one video or audio file (other files, several items, whole folders)
// waits on a "Ready to share" card. Nothing uploads until Share is pressed, so a stray drop can't publish a folder.
// Folders and several items are packed into one .zip in KeaClip's data folder, shared, then deleted.
public sealed partial class MainViewModel
{
    private ShareSelection? _dropShare;
    private IAsyncRelayCommand? _shareDroppedCommand;
    private IRelayCommand? _cancelDroppedCommand;

    /// <summary>Set by the Editor while it drags the finished clip out of KeaClip.</summary>
    internal string? DraggingOutPath { get; set; }
    private string ShareBundleFolder => Path.Combine(_services.DataRoot, "ShareBundles");
    public bool HasDropShare => _dropShare is not null;
    public string DropShareName => _dropShare?.Name ?? "";
    public string DropShareDetail => _dropShare is { } s
        ? (s.FileCount == 1 ? "1 file" : $"{s.FileCount:N0} files") + " · " + Size(s.Bytes) + (s.NeedsPacking ? " · sent as one .zip" : "")
        : "";
    public string DropShareNotice => _dropShare is { } s
        ? $"Encrypted on this PC, then uploaded to file.kiwi. Anyone with the link can download it for {FreeDownloadHoursText(s.Bytes)}."
        : "";
    public IAsyncRelayCommand ShareDroppedCommand => _shareDroppedCommand ??= new AsyncRelayCommand(ShareDroppedAsync);
    public IRelayCommand CancelDroppedCommand => _cancelDroppedCommand ??= new RelayCommand(() => SetDropShare(null));

    /// <summary>Handles a drop on the window: one video or audio file opens in the Editor; anything else is offered for sharing.</summary>
    public async Task AcceptDroppedAsync(IReadOnlyList<string> paths)
    {
        // The finished clip dragged out of the result card can land back on this window (a plain click on the card is
        // a zero-distance drag); that is not a request to open or share it.
        if (paths.Count == 0 || (DraggingOutPath is { } own && paths.Count == 1 && string.Equals(Path.GetFullPath(paths[0]), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))) return;
        if (paths.Count == 1 && File.Exists(paths[0]) && IsEditableMedia(paths[0])) { await OpenEditorAsync(paths[0]); return; }
        try
        {
            var selection = await Task.Run(() => ShareBundle.Describe(paths));
            SetDropShare(selection); SelectedTab = 3;
            Status = "Ready to share " + selection.Name + ". Press Share to upload it.";
        }
        catch (MediaOperationException error) { Status = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { CaptureDiagnostics(error); Status = "KeaClip couldn't read what was dropped. Try again, or share the files one at a time."; }
    }

    // Older or camera formats FFmpeg reads too; dropping one opens it for editing as before, rather than offering to share it.
    private static readonly string[] OtherMediaExtensions = [".wmv", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".wma", ".aif", ".aiff"];
    internal static bool IsEditableMedia(string path)
    {
        var extension = Path.GetExtension(path);
        return EditorVideoExtensions.Concat(EditorAudioExtensions).Concat(OtherMediaExtensions).Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private Task ShareDroppedAsync()
    {
        if (_dropShare is not { } selection) return Task.CompletedTask;
        return RunAsync(OperationLane.Share, async ct =>
        {
            string? bundle = null;
            try
            {
                var path = selection.Items[0];
                if (selection.NeedsPacking)
                {
                    var progress = Progress(OperationLane.Share);
                    var packing = new Progress<double>(share => progress.Report(new JobProgress(JobStage.Compressing, share * 100, $"Packing {selection.FileCount:N0} files into one .zip")));
                    IsSharing = true; ShareFileName = selection.Name + ".zip";
                    try { bundle = await Task.Run(() => ShareBundle.PackAsync(selection, ShareBundleFolder, packing, ct), ct); }
                    finally { progress.Close(); IsSharing = false; }
                    path = bundle;
                }
                await ShareCoreAsync(path, automatic: false, ct);
                // Kept until the link exists, so a failed upload can simply be tried again from the same card.
                SetDropShare(null);
            }
            finally { ShareBundle.Discard(bundle, ShareBundleFolder); }
        });
    }

    private void SetDropShare(ShareSelection? selection)
    {
        _dropShare = selection;
        foreach (var name in (string[])[nameof(HasDropShare), nameof(DropShareName), nameof(DropShareDetail), nameof(DropShareNotice)]) OnPropertyChanged(name);
    }

    /// <summary>Bundles left by a crash or power cut are removed at start-up, before any share can begin.</summary>
    private void CleanShareBundles()
    {
        try { ShareBundle.CleanLeftovers(ShareBundleFolder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { CaptureDiagnostics(error); }
    }
}
