using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Microsoft.Win32;

namespace Klippi.Desktop;

public interface IDesktopPlatform
{
    void Attach(Window window);
    Task<string?> PickMediaAsync();
    Task<string?> PickFolderAsync();
    Task<string?> SaveMediaAsync(string name, string extension);
    Task<string?> SaveMediaAsync(string name, string extension, string? directory) => SaveMediaAsync(name, extension);
    Task<string?> ReadClipboardAsync();
    Task CopyAsync(string value);
    // The file itself (not its path), as Explorer's Copy does, so Ctrl+V in Discord, a chat or an email attaches it.
    Task CopyFileAsync(string path) => Task.FromException(new NotSupportedException("Copying files is not available here."));
    void OpenFile(string path);
    void RevealFile(string path);
    void OpenFolder(string path);
    void OpenUri(Uri uri);
    void SetStartup(bool enabled);
    // The startup entry carries --minimized only when "Start minimized" is on, so manual
    // launches always open normally.
    void SetStartup(bool enabled, bool minimized) => SetStartup(enabled);
    bool? GetReducedMotionPreference() => null;
}

public sealed class DesktopPlatform : IDesktopPlatform
{
    private Window? _window;
    public void Attach(Window window) => _window = window;
    public bool? GetReducedMotionPreference() => WindowsMotionPreference.ReadReducedMotion();
    private IStorageProvider? Storage => _window?.StorageProvider;
    public async Task<string?> PickMediaAsync()
    {
        if (Storage is null) return null;
        var files = await Storage.OpenFilePickerAsync(new() { Title = "Open local media", AllowMultiple = false, FileTypeFilter = [new("Media") { Patterns = ["*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi", "*.m4v", "*.mp3", "*.m4a", "*.flac", "*.wav", "*.ogg", "*.opus", "*.aac"] }, FilePickerFileTypes.All] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public async Task<string?> PickFolderAsync()
    {
        if (Storage is null) return null;
        return (await Storage.OpenFolderPickerAsync(new() { Title = "Choose an output folder", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    }
    public Task<string?> SaveMediaAsync(string name, string extension) => SaveMediaAsync(name, extension, null);
    public async Task<string?> SaveMediaAsync(string name, string extension, string? directory)
    {
        if (Storage is null) return null;
        var location = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? await Storage.TryGetFolderFromPathAsync(directory) : null;
        var file = await Storage.SaveFilePickerAsync(new() { Title = "Export media", SuggestedFileName = name, SuggestedStartLocation = location, DefaultExtension = extension.TrimStart('.'), ShowOverwritePrompt = true, FileTypeChoices = [new(extension.ToUpperInvariant()) { Patterns = ["*." + extension.TrimStart('.')] }] });
        return file?.TryGetLocalPath();
    }
    public async Task<string?> ReadClipboardAsync() => _window?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
    public async Task CopyAsync(string value) { if (_window?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(value); await KeepAfterExitAsync(clipboard); } }
    // Windows drops what an app copied when that app exits unless the clipboard is flushed: someone who copies a clip,
    // quits KeaClip and then pastes would get nothing. The copy itself already worked, so a failed flush is not an error.
    private static async Task KeepAfterExitAsync(IClipboard clipboard)
    {
        try { await clipboard.FlushAsync(); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
    public async Task CopyFileAsync(string path)
    {
        RequireFile(path);
        if (_window?.Clipboard is not { } clipboard || Storage is null) throw new InvalidOperationException("The clipboard is not available.");
        var file = await Storage.TryGetFileFromPathAsync(Path.GetFullPath(path)) ?? throw new FileNotFoundException("The file is no longer available.");
        await clipboard.SetFileAsync(file);
        await KeepAfterExitAsync(clipboard);
    }
    // A file that is no longer there is reported, never silently ignored: callers turn the
    // FileNotFoundException into "This file is no longer available".
    public void OpenFile(string path)
    {
        RequireFile(path);
        Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
    }
    public void RevealFile(string path)
    {
        RequireFile(path);
        if (OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/select,"); info.ArgumentList.Add(Path.GetFullPath(path)); Process.Start(info);
        }
        else OpenFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }
    // The path stays out of the message; it is a local media path that diagnostics must not carry.
    private static void RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The file is no longer available.");
    }
    public void OpenFolder(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }); }
    public void OpenUri(Uri uri)
    {
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Only secure web links can be opened.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    public void SetStartup(bool enabled) => SetStartup(enabled, false);
    public void SetStartup(bool enabled, bool minimized)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Startup integration is currently available on Windows.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The app executable is unavailable.");
        if (!Path.GetFileNameWithoutExtension(executable).Equals("KeaClip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use the installed or portable app to change Windows startup.");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("KeaClip", "\"" + executable + "\"" + (minimized ? " --minimized" : ""));
        else key.DeleteValue("KeaClip", false);
    }
}
