namespace Klippi.Core;

public interface ITemporaryWorkspace : IDisposable
{
    string Path { get; }
    void RegisterPayload(string fileName) { }
    void RegisterDownloadBaseName(string baseName) { }
    void PreserveForRecovery();
    void PrepareForPromotion();
}

public interface ITemporaryWorkspaceService
{
    ITemporaryWorkspace Create(string parent);
    Task<TemporaryCleanupResult> CleanAsync(CancellationToken cancellationToken = default);
}

public sealed record TemporaryCleanupResult
{
    public int DeletedFolders { get; init; }
    public long DeletedBytes { get; init; }
    public int ActiveFolders { get; init; }
    public int RecoveryFolders { get; init; }
    public int SkippedFolders { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool Cancelled { get; init; }
}
