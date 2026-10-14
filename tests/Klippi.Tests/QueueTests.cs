using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;
namespace Klippi.Tests;

public sealed class QueueTests
{
    [Fact] public async Task PauseStopsNewStartsAndCancelQueuedNeverDownloads()
    {
        var fake = new BlockingDownloads(); var root = Path.Combine(Path.GetTempPath(),"Klippi-queue-"+Guid.NewGuid());
        try
        {
            await using var queue = new DownloadQueue(fake,new JsonLocalStore(root)); queue.Pause();
            var one = queue.Enqueue(Options(root),"one"); var two = queue.Enqueue(Options(root),"two");
            queue.Cancel(two.Id); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>two.Completion);
            Assert.Equal(0,fake.Starts); queue.Resume(); await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fake.Finish.TrySetResult(); await one.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1,fake.Starts); Assert.Equal(JobState.Finished,one.State);
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Fact] public async Task QueueMaintainsConcurrencyBoundAndSurvivesThrowingSubscriber()
    {
        var fake = new BlockingDownloads(); var root = Path.Combine(Path.GetTempPath(),"Klippi-queue-"+Guid.NewGuid());
        try
        {
            await using var queue = new DownloadQueue(fake,new JsonLocalStore(root),1); queue.JobChanged += _=>throw new InvalidOperationException("observer");
            var one = queue.Enqueue(Options(root),"one"); var two = queue.Enqueue(Options(root),"two");
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1,fake.Starts);
            fake.Finish.TrySetResult(); await Task.WhenAll(one.Completion,two.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1,fake.MaxActive); Assert.Equal(2,(await new JsonLocalStore(root).LoadHistoryAsync()).Count);
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Fact] public async Task DisposalCancelsPendingWithoutStartingThem()
    {
        var fake = new BlockingDownloads(); var queue = new DownloadQueue(fake,new JsonLocalStore(Path.GetTempPath())); queue.Pause();
        var job=queue.Enqueue(Options(Path.GetTempPath()),"pending"); await queue.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>job.Completion); Assert.Equal(0,fake.Starts);
        await queue.DisposeAsync();
    }
    [Fact] public async Task CancellationDuringOptionalHistoryPreservesDurableResult()
    {
        var fake=new BlockingDownloads(); fake.Finish.TrySetResult(); var store=new BlockingHistory();
        await using var queue=new DownloadQueue(fake,store);
        var job=queue.Enqueue(Options(Path.GetTempPath()),"completed");
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); queue.Cancel(job.Id);
        var result=await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("test.mp4",result.FilePath); Assert.Equal(JobState.Finished,job.State); Assert.Contains("history",job.Error!);
    }
    private static DownloadOptions Options(string root)=>new(){Url=new("https://youtube.com/watch?v=public"),OutputDirectory=root};
    private sealed class BlockingDownloads:IDownloadService
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts,Active,MaxActive;
        public async Task<DownloadResult> DownloadAsync(DownloadOptions options,IProgress<JobProgress>? progress=null,CancellationToken cancellationToken=default)
        {
            Interlocked.Increment(ref Starts); var active=Interlocked.Increment(ref Active); MaxActive=Math.Max(active,MaxActive); Started.TrySetResult();
            try { await Finish.Task.WaitAsync(cancellationToken); return new("test.mp4",null,10); } finally { Interlocked.Decrement(ref Active); }
        }
        public Task<MediaInfo> AnalyseAsync(Uri url,string? browser=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<string>>([]);
    }
    private sealed class BlockingHistory:ILocalStore
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AddHistoryAsync(HistoryEntry entry,CancellationToken cancellationToken=default){Started.TrySetResult();await Task.Delay(Timeout.Infinite,cancellationToken);}
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new AppSettings());
        public Task SaveSettingsAsync(AppSettings settings,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<HistoryEntry>>([]);
        public Task RemoveHistoryAsync(string id,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task ClearHistoryAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task PruneHistoryAsync(int? retentionDays,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    }
}
