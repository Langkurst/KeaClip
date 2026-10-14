using CommunityToolkit.Mvvm.Input;

namespace Klippi.Desktop;

// A finished card's share line: an automatic share waiting or uploading, a large file waiting for the
// usual confirmation, a failed share with its reason and Retry share, or the link made for this file
// in this session with Copy link. The link itself (with its secret) stays in the view model's memory.
public sealed partial class QueueRow
{
    private static readonly string[] ShareProperties =
    [
        nameof(ShareState), nameof(ShowShareProgress), nameof(ShareProgressText), nameof(ShowShareFailed), nameof(ShareFailureText),
        nameof(ShowShareConfirm), nameof(ShareConfirmText), nameof(HasShareLink),
    ];
    private IAsyncRelayCommand? _copyLinkCommand;
    private IRelayCommand? _retryShareCommand;

    public AutoShareState ShareState => IsFinished ? _owner.AutoShareStateFor(Id) : AutoShareState.None;
    public bool ShowShareProgress => ShareState is AutoShareState.Waiting or AutoShareState.Uploading;
    public string ShareProgressText => ShareState == AutoShareState.Waiting ? "Sharing next · waiting for the current upload to finish" : ShareState == AutoShareState.Uploading ? "Sharing · encrypting and uploading to File.Kiwi" : "";
    public bool ShowShareFailed => ShareState == AutoShareState.Failed;
    public string ShareFailureText => ShowShareFailed ? "Saved, but not shared. " + _owner.AutoShareReasonFor(Id) : "";
    public bool ShowShareConfirm => ShareState == AutoShareState.NeedsConfirmation;
    public string ShareConfirmText => ShowShareConfirm ? "This file is very large, so it was not shared automatically. Choose Share to upload it anyway." : "";
    /// <summary>A share link was made for this file in this session (and no automatic share is still in progress for it).</summary>
    public bool HasShareLink => IsFinished && ShareState == AutoShareState.None && _owner.ShareLinkFor(FilePath) is not null;

    public IAsyncRelayCommand CopyLinkCommand => _copyLinkCommand ??= new AsyncRelayCommand(() => _owner.CopyShareLinkAsync(FilePath));
    public IRelayCommand RetryShareCommand => _retryShareCommand ??= new RelayCommand(() => _owner.RetryAutoShare(Id));

    internal void ShareChanged() { foreach (var name in ShareProperties) OnPropertyChanged(name); }
}
