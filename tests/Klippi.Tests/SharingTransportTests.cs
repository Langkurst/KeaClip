using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure.Sharing;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Klippi.Tests;

/// <summary>
/// Transport behaviour of the file.kiwi adapter: deadlines, outage retries, progress, keep-alive, threading and checksums. No external network is used;
/// the real-socket cases talk to a TLS listener on 127.0.0.1 only.
/// </summary>
public sealed class SharingTransportTests
{
    public enum StorageMode { RefuseEarlyAndClose, RefuseAfterBody, Accept, CutOffOnceThenAccept, CutOffOnceThenAcceptIgnoringExpect }

    [Fact]
    public async Task SlowButSteadyChunkFinishesDespiteShortRequestAndClientTimeouts()
    {
        // About 1.5 s of steady upload against a 300 ms request deadline and a 500 ms HttpClient timeout.
        using var file = new TestFile(Pattern(2 * 1024 * 1024));
        using var server = new TransportServer(file.Bytes.Length, 4 * 1024 * 1024) { SliceDelay = TimeSpan.FromMilliseconds(40) };
        using var client = new HttpClient(server) { Timeout = TimeSpan.FromMilliseconds(500) };
        var service = new FileKiwiSharingService(client, options: new()
        {
            RequestTimeout = TimeSpan.FromMilliseconds(300), UploadStallTimeout = TimeSpan.FromSeconds(3), ConnectionOutageBudget = TimeSpan.Zero,
            RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(10),
        });
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        var stopwatch = Stopwatch.StartNew();
        var result = await service.ShareAsync(file.Path);
        Assert.True(stopwatch.Elapsed > TimeSpan.FromMilliseconds(500), "The fake link must be slower than both fixed deadlines for this test to mean anything.");
        Assert.Equal([1], server.PutAttempts);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task StalledChunkUploadStillTimesOutWithinTheOutageBudget()
    {
        using var file = new TestFile(Pattern(256 * 1024));
        using var server = new TransportServer(file.Bytes.Length, 1024 * 1024) { StallAfterWrites = 1 };
        using var client = new HttpClient(server);
        var options = new FileKiwiSharingOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(30), UploadStallTimeout = TimeSpan.FromMilliseconds(200), ConnectionOutageBudget = TimeSpan.FromMilliseconds(300),
            RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(20),
        };
        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: options).ShareAsync(file.Path));
        Assert.Contains("in time", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(server.PutAttempts.Count >= 2, "A stall is a connection failure and is retried within the outage budget.");
        Assert.Equal(0, server.StatusRequests);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task ShortConnectionDropIsRetriedWithoutResendingAcceptedChunks()
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        long outageStarted = 0;
        using var server = new TransportServer(file.Bytes.Length, 4)
        {
            // The network drops from the second PUT for 1.2 s: far longer than the previous 3-attempt (about 150 ms here) window.
            NetworkFailure = attempt =>
            {
                if (attempt < 2) return false;
                if (outageStarted == 0) outageStarted = Stopwatch.GetTimestamp();
                return Stopwatch.GetElapsedTime(outageStarted) < TimeSpan.FromMilliseconds(1200);
            },
        };
        using var client = new HttpClient(server);
        var progress = new CaptureProgress();
        var result = await new FileKiwiSharingService(client, options: new()
        {
            RetryDelay = TimeSpan.FromMilliseconds(50), MaximumRetryDelay = TimeSpan.FromMilliseconds(200), ConnectionOutageBudget = TimeSpan.FromSeconds(20),
        }).ShareAsync(file.Path, progress: progress);
        Assert.Equal([1, 3, 2], server.AcceptedOrder);
        Assert.True(server.PutAttempts.Count(chunk => chunk == 3) > 3);
        Assert.Equal(1, server.PutAttempts.Count(chunk => chunk == 1));
        Assert.Contains(progress.Values, value => value.Message?.StartsWith("Connection lost. Retrying the encrypted upload in", StringComparison.Ordinal) == true);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task ConnectionOutageLongerThanTheBudgetFailsWithoutLeakingProviderText()
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new TransportServer(3, 4) { NetworkFailure = _ => true };
        using var client = new HttpClient(server);
        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: new()
        {
            RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(40), ConnectionOutageBudget = TimeSpan.FromMilliseconds(300),
        }).ShareAsync(file.Path));
        Assert.Contains("could not be reached", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(server.PutAttempts.Count >= 2);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("synthetic-provider-secret", exception.ToString() + exception.Details);
        Assert.Equal(0, server.StatusRequests);
    }

    [Fact]
    public async Task UploadProgressAdvancesWithinASingleChunkAndNeverSitsAtFullUpload()
    {
        using var file = new TestFile(Pattern(1024 * 1024));
        using var server = new TransportServer(file.Bytes.Length, 2 * 1024 * 1024);
        using var client = new HttpClient(server);
        var progress = new CaptureProgress();
        await new FileKiwiSharingService(client).ShareAsync(file.Path, progress: progress);
        var values = progress.Values;
        Assert.Equal(JobStage.Uploading, values[0].Stage); // No whole-file pass before the upload starts.
        var uploading = values.Where(value => value.Stage == JobStage.Uploading).ToList();
        Assert.Contains(uploading, value => value.Percent is > 0 and < 100 && value.Message == "Uploading encrypted data…");
        Assert.True(uploading.Zip(uploading.Skip(1)).All(pair => pair.Second.Percent >= pair.First.Percent));
        int lastUpload = values.FindLastIndex(value => value.Stage == JobStage.Uploading);
        Assert.Equal(100, values[lastUpload].Percent);
        Assert.Equal(JobStage.Verifying, values[lastUpload + 1].Stage);
        Assert.Equal(JobStage.Finished, values[^1].Stage);
    }

    [Fact]
    public async Task FullSourceRecheckReportsVerifyingProgress()
    {
        using var file = new TestFile(Pattern(4 * 1024 * 1024));
        using var server = new TransportServer(file.Bytes.Length, 8 * 1024 * 1024);
        using var client = new HttpClient(server);
        var progress = new CaptureProgress();
        var result = await new FileKiwiSharingService(client, options: new() { AlwaysRecheckSource = true }).ShareAsync(file.Path, progress: progress);
        var values = progress.Values;
        int lastUpload = values.FindLastIndex(value => value.Stage == JobStage.Uploading);
        var after = values.Skip(lastUpload + 1).ToList();
        Assert.All(after, value => Assert.True(value.Stage is JobStage.Verifying or JobStage.Finished));
        Assert.Contains(after, value => value.Stage == JobStage.Verifying && value.Percent is > 0 and < 100);
        Assert.Equal(Hex(file.Bytes), result.Sha256);
    }

    [Theory]
    [InlineData(1, 4, false)]
    [InlineData(4, 4, false)]
    [InlineData(5, 4, false)]
    [InlineData(8, 4, true)]
    [InlineData(10, 4, false)]
    [InlineData(17, 4, true)]
    [InlineData(13, 3, false)]
    public async Task ChecksumIsBuiltFromTheUploadedBytesForEveryChunkLayout(int size, int chunkSize, bool recheck)
    {
        using var file = new TestFile(Pattern(size));
        using var server = new TransportServer(size, chunkSize);
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client, options: new() { AlwaysRecheckSource = recheck }).ShareAsync(file.Path);
        Assert.Equal(Hex(file.Bytes), result.Sha256);
        Assert.Equal(file.Bytes, Restore(server, result));
        Assert.Equal(server.Chunks, server.PutAttempts.Count);
    }

    [Fact]
    public async Task LongUploadsCallUploadStatusPeriodicallyToKeepTheFileAlive()
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 4) { AfterPut = () => clock.Advance(TimeSpan.FromMinutes(31)) };
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client, options: new() { TimeProvider = clock }).ShareAsync(file.Path);
        // One keep-alive after each of the first two chunks (both incomplete), then the final completion check.
        Assert.Equal([false, false, true], server.StatusCompleteFlags);
        Assert.Contains("may have started when it began", result.Warning);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task SuccessfulKeepAliveRestartsTheThirtyMinuteInterval()
    {
        // Five chunks, 20 minutes each: checks at 40 and 80 minutes only. A keep-alive that never counted as successful would also check at 60.
        using var file = new TestFile(Pattern(20));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 4) { AfterPut = () => clock.Advance(TimeSpan.FromMinutes(20)) };
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client, options: new() { TimeProvider = clock }).ShareAsync(file.Path);
        Assert.Equal([false, false, true], server.StatusCompleteFlags);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task FailedKeepAliveIsRetriedAfterTheNextChunkWithoutStoppingTheUpload()
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 4)
        {
            AfterPut = () => clock.Advance(TimeSpan.FromMinutes(31)),
            StatusResponse = number => number == 1 ? HttpStatusCode.ServiceUnavailable : null,
        };
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client, options: new() { TimeProvider = clock }).ShareAsync(file.Path);
        Assert.Equal(3, server.StatusRequests);
        Assert.Equal([false, true], server.StatusCompleteFlags);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task MissingUploadDuringKeepAliveIsReportedAsExpired(HttpStatusCode code)
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 4) { AfterPut = () => clock.Advance(TimeSpan.FromMinutes(31)), StatusResponse = _ => code };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: new() { TimeProvider = clock }).ShareAsync(file.Path));
        Assert.Equal("file.kiwi upload expired.", exception.Message);
        Assert.Equal([1], server.PutAttempts);
        Assert.DoesNotContain(TransportServer.SyntheticAuth, exception.ToString() + exception.Details);
    }

    [Fact]
    public async Task ForbiddenChunkUploadIsReportedAsExpiredWithoutRetrying()
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new TransportServer(3, 4) { PutStatus = _ => HttpStatusCode.Forbidden };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Equal("file.kiwi upload expired.", exception.Message);
        Assert.Contains("HTTP 403", exception.Details);
        Assert.Equal([1], server.PutAttempts);
        Assert.Equal(0, server.StatusRequests);
        Assert.DoesNotContain("synthetic-provider-secret", exception.ToString() + exception.Details);
    }

    [Fact]
    public async Task UploadLinksOlderThan36HoursFailBeforeSendingAnotherChunk()
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 4) { AfterPut = () => clock.Advance(TimeSpan.FromHours(37)) };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: new() { TimeProvider = clock }).ShareAsync(file.Path));
        Assert.Equal("file.kiwi upload expired.", exception.Message);
        Assert.Equal([1], server.PutAttempts);
    }

    [Fact]
    public async Task SharingNeverRunsOnTheCallersSynchronizationContext()
    {
        using var file = new TestFile(Pattern(300_000));
        using var server = new TransportServer(file.Bytes.Length, 100_000);
        using var client = new HttpClient(server);
        var service = new FileKiwiSharingService(client);
        var progress = new CaptureProgress();
        using var ui = new PumpContext();
        var result = await ui.Run(() => service.ShareAsync(file.Path, progress: progress));
        Assert.NotEmpty(server.HandlerThreads);
        Assert.DoesNotContain(ui.ThreadId, server.HandlerThreads);
        Assert.DoesNotContain(ui.ThreadId, progress.Threads);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_519)]
    [InlineData(65_520)]
    [InlineData(200_000)]
    public void EncryptIntoReusedBufferMatchesTheAllocatingEncoder(int length)
    {
        byte[] key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        byte[] plain = Pattern(length);
        // A dirty, oversized buffer that already held a larger message: every header and record byte must be rewritten.
        byte[] buffer = Enumerable.Repeat((byte)0xAA, Rfc8188Encryption.EncryptedLength(200_000) + 7).ToArray();
        Rfc8188Encryption.EncryptInto(Pattern(200_000), buffer, key);
        int written = Rfc8188Encryption.EncryptInto(plain, buffer, key);
        Assert.Equal(Rfc8188Encryption.EncryptedLength(length), written);
        byte[] message = buffer.AsSpan(0, written).ToArray();
        Assert.Equal(plain, Rfc8188Encryption.Decrypt(message, key));
        Assert.Equal(message, Rfc8188Encryption.EncryptWithSalt(plain, key, message.AsSpan(0, 16), Rfc8188Encryption.DefaultRecordSize));
        Assert.Throws<ArgumentException>(() => Rfc8188Encryption.EncryptInto(plain, new byte[written - 1], key));
    }

    [Fact]
    public void InvalidTransportLimitsAreRejected()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(client, options: new() { UploadStallTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(client, options: new() { UploadStallTimeout = TimeSpan.FromHours(2) }));
        Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(client, options: new() { ConnectionOutageBudget = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(client, options: new() { ConnectionOutageBudget = TimeSpan.FromHours(2) }));
        Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(client, options: new() { TimeProvider = null! }));
        Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(client, options: new() { UploadStatusTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(client, options: new() { UploadStatusTimeout = TimeSpan.FromHours(2) }));
    }

    [Fact]
    public async Task InterruptedAttemptsThatWereStillSendingDoNotUseUpTheOutageBudget()
    {
        // Each of the first four attempts streams steadily for about 400 ms and then loses the connection; the fifth finishes. The gaps without
        // progress are tiny, but measured from the first failure the drops add up to about 1.2 s, far beyond the 700 ms budget.
        using var file = new TestFile(Pattern(1024 * 1024));
        using var server = new TransportServer(file.Bytes.Length, 2 * 1024 * 1024)
        {
            SliceDelay = TimeSpan.FromMilliseconds(50), InterruptAfterWrites = attempt => attempt <= 4 ? 8 : -1,
        };
        using var client = new HttpClient(server);
        var progress = new CaptureProgress();
        var result = await new FileKiwiSharingService(client, options: new()
        {
            RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(20), ConnectionOutageBudget = TimeSpan.FromMilliseconds(700),
        }).ShareAsync(file.Path, progress: progress);
        Assert.Equal([1, 1, 1, 1, 1], server.PutAttempts);
        Assert.Equal([1], server.AcceptedOrder);
        Assert.Contains(progress.Values, value => value.Message?.StartsWith("Connection lost. Retrying the encrypted upload in", StringComparison.Ordinal) == true);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task ChunkThatIsAlwaysCutOffMidBodyStopsAfterTheInterruptionCap()
    {
        // Every attempt sends one slice and then loses the connection, so the outage budget alone would never run out. The fake would accept
        // attempt 31, so an uncapped retry loop would wrongly report success.
        using var file = new TestFile(Pattern(256 * 1024));
        using var server = new TransportServer(file.Bytes.Length, 1024 * 1024) { InterruptAfterWrites = attempt => attempt <= 30 ? 1 : -1 };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: new()
        {
            RetryDelay = TimeSpan.FromMilliseconds(5), MaximumRetryDelay = TimeSpan.FromMilliseconds(10), ConnectionOutageBudget = TimeSpan.FromMinutes(5),
        }).ShareAsync(file.Path));
        Assert.Equal("file.kiwi could not be reached.", exception.Message);
        Assert.Contains("kept dropping", exception.Details);
        Assert.Equal(FileKiwiSharingService.MaximumInterruptedAttempts, server.PutAttempts.Count);
        Assert.Empty(server.AcceptedOrder);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("synthetic-provider-secret", exception.ToString() + exception.Details);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public async Task SleepInTheMiddleOfAChunkStillGetsARetryAfterWaking(int sleepMinutes)
    {
        // The computer sleeps while a chunk is being sent (Windows timestamps keep counting) and the dead connection fails on waking. The network is
        // back, so the share must retry instead of failing at once because the last sent slice is now longer ago than the 5-minute budget.
        using var file = new TestFile(Pattern(256 * 1024));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 1024 * 1024)
        {
            InterruptAfterWrites = attempt => attempt == 1 ? 2 : -1, BeforeInterrupt = () => clock.Advance(TimeSpan.FromMinutes(sleepMinutes)),
        };
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client, options: new()
        {
            TimeProvider = clock, RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(20),
        }).ShareAsync(file.Path);
        Assert.Equal([1, 1], server.PutAttempts);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task StalledChunkThatWasSendingIsRetriedEvenWhenTheBudgetIsShorterThanTheStallDeadline()
    {
        // Both limits are valid on their own. A stall is only detected after the stall deadline, so measuring the outage from the last sent slice
        // would already exceed this budget and turn every stall into an immediate failure.
        using var file = new TestFile(Pattern(256 * 1024));
        using var server = new TransportServer(file.Bytes.Length, 1024 * 1024) { StallAfterWritesOn = attempt => attempt == 1 ? 2 : -1 };
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client, options: new()
        {
            UploadStallTimeout = TimeSpan.FromMilliseconds(300), ConnectionOutageBudget = TimeSpan.FromMilliseconds(100),
            RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(20),
        }).ShareAsync(file.Path);
        Assert.Equal([1, 1], server.PutAttempts);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task SleepDuringAConnectionOutageDoesNotUseUpTheBudget()
    {
        // The network is down for three attempts and the computer sleeps for an hour between the first two. Only time spent awake and retrying counts.
        using var file = new TestFile(Pattern(1000));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 4096)
        {
            NetworkFailure = attempt =>
            {
                if (attempt == 2) clock.Advance(TimeSpan.FromHours(1));
                return attempt <= 3;
            },
        };
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client, options: new()
        {
            TimeProvider = clock, RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(20),
        }).ShareAsync(file.Path);
        Assert.Equal([1, 1, 1, 1], server.PutAttempts);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task AwakeOutageTimeStillCountsAgainstTheBudget()
    {
        // Each failed attempt takes 50 s of clock time, which one back-off plus the 1-minute attempt deadline explains, so all of it counts:
        // the budget of 5 minutes ends the share at the seventh failure (six counted gaps after the first failure).
        using var file = new TestFile(Pattern(1000));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 4096)
        {
            NetworkFailure = _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(50));
                return true;
            },
        };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: new()
        {
            TimeProvider = clock, RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(20),
        }).ShareAsync(file.Path));
        Assert.Equal("file.kiwi could not be reached.", exception.Message);
        Assert.Equal(7, server.PutAttempts.Count);
    }

    [Theory]
    [InlineData(StorageMode.RefuseEarlyAndClose)]
    [InlineData(StorageMode.RefuseAfterBody)]
    public async Task StorageRefusalOverRealSocketsIsReportedAsExpiredWithoutResendingTheChunk(StorageMode mode)
    {
        // 16 MiB is more than the loopback socket buffers hold, so storage that answers early and closes really cuts the body off. The first attempt
        // then looks like a dropped connection; its retry asks to continue first, so the refusal arrives before the body is sent again.
        using var file = new TestFile(Pattern(16 * 1024 * 1024));
        using var api = new TransportServer(file.Bytes.Length, 32 * 1024 * 1024);
        await using var storage = new LoopbackStorage(mode, api);
        // A longer wait for "100 Continue" than production's 1 s keeps a slow test machine from sending the body before the refusal arrives.
        using var client = new HttpClient(new RoutingHandler(api, storage.CreateHandler(TimeSpan.FromSeconds(10))));
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: new()
        {
            RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(50), ConnectionOutageBudget = TimeSpan.FromSeconds(2),
        }).ShareAsync(file.Path));
        Assert.Equal("file.kiwi upload expired.", exception.Message);
        Assert.Contains("HTTP 403", exception.Details);
        bool[] expected = mode == StorageMode.RefuseEarlyAndClose ? [false, true] : [false];
        Assert.Equal(expected, storage.ExpectContinue);
        Assert.Equal(0, api.StatusRequests);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task ChunkUploadsOverRealSocketsDoNotAskToContinueFirst()
    {
        // Like file.kiwi's browser uploader, normal chunks are sent without "Expect: 100-continue", so storage that never answers it costs nothing.
        using var file = new TestFile(Pattern(1024 * 1024 + 4099));
        using var api = new TransportServer(file.Bytes.Length, 1024 * 1024);
        await using var storage = new LoopbackStorage(StorageMode.Accept, api);
        using var client = new HttpClient(new RoutingHandler(api, storage.CreateHandler(null)));
        var result = await new FileKiwiSharingService(client).ShareAsync(file.Path);
        Assert.Equal([false, false], storage.ExpectContinue);
        Assert.Equal([1, 2], api.AcceptedOrder);
        Assert.Equal(Hex(file.Bytes), result.Sha256);
        Assert.Equal(file.Bytes, Restore(api, result));
    }

    [Theory]
    [InlineData(StorageMode.CutOffOnceThenAccept)]
    [InlineData(StorageMode.CutOffOnceThenAcceptIgnoringExpect)]
    public async Task ChunkCutOffMidBodyOverRealSocketsIsRetriedAskingToContinueFirst(StorageMode mode)
    {
        // The first attempt loses its connection part-way through the body. The retry asks to continue first and still finishes when storage never
        // answers "100 Continue", at the cost of the handler's default 1 s wait.
        using var file = new TestFile(Pattern(4 * 1024 * 1024));
        using var api = new TransportServer(file.Bytes.Length, 8 * 1024 * 1024);
        await using var storage = new LoopbackStorage(mode, api);
        using var client = new HttpClient(new RoutingHandler(api, storage.CreateHandler(null)));
        var result = await new FileKiwiSharingService(client, options: new()
        {
            RetryDelay = TimeSpan.FromMilliseconds(10), MaximumRetryDelay = TimeSpan.FromMilliseconds(50),
        }).ShareAsync(file.Path);
        Assert.Equal([false, true], storage.ExpectContinue);
        Assert.Equal([1], api.AcceptedOrder);
        Assert.Equal(Hex(file.Bytes), result.Sha256);
        Assert.Equal(file.Bytes, Restore(api, result));
    }

    [Fact]
    public async Task HangingKeepAliveUsesItsOwnShortDeadlineAndBacksOffInsteadOfDelayingEveryChunk()
    {
        // 40 chunks of one minute each while file.kiwi's API hangs: keep-alives fall due at 30 minutes and, after each failure, 5 minutes later.
        using var file = new TestFile(Pattern(40));
        var clock = new ManualClock();
        using var server = new TransportServer(file.Bytes.Length, 1) { AfterPut = () => clock.Advance(TimeSpan.FromMinutes(1)), StatusHangsWhileIncomplete = true };
        using var client = new HttpClient(server);
        var stopwatch = Stopwatch.StartNew();
        var result = await new FileKiwiSharingService(client, options: new()
        {
            TimeProvider = clock, RequestTimeout = TimeSpan.FromSeconds(10), UploadStatusTimeout = TimeSpan.FromMilliseconds(100),
        }).ShareAsync(file.Path);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Hung keep-alives must not wait the full request timeout ({stopwatch.Elapsed}).");
        // Hung checks after chunks 30 and 35 only (not after every chunk from 30 to 39), then the final completion check.
        Assert.Equal(3, server.StatusRequests);
        Assert.Equal([true], server.StatusCompleteFlags);
        Assert.Equal(file.Bytes, Restore(server, result));
    }

    [Fact]
    public async Task UsedClientWithAFiniteTimeoutIsRejectedInsteadOfKeepingItsLimit()
    {
        string statusUrl = "https://api.file.kiwi/v2/folders/folder123/files/file123/upload-status?uploadAuth=" + TransportServer.SyntheticAuth;
        using var server = new TransportServer(3, 4);
        using var used = new HttpClient(server, disposeHandler: false);
        (await used.GetAsync(statusUrl)).Dispose();
        var exception = Assert.Throws<ArgumentException>(() => new FileKiwiSharingService(used));
        Assert.Equal("httpClient", exception.ParamName);
        Assert.Equal(TimeSpan.FromSeconds(100), used.Timeout);
        // A used client that already has no whole-request limit is fine.
        using var dedicated = new HttpClient(server, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        (await dedicated.GetAsync(statusUrl)).Dispose();
        _ = new FileKiwiSharingService(dedicated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceReachedThroughALinkIsClassifiedByWhereTheFileReallyIs(bool realFolderOnNetwork)
    {
        using var folders = new LinkedFolders();
        byte[] bytes = Pattern(300_000);
        string real = Path.Combine(folders.Target, "clip.bin"), viaLink = Path.Combine(folders.Link, "clip.bin");
        await File.WriteAllBytesAsync(real, bytes);
        string? finalPath = null;
        if (OperatingSystem.IsWindows())
        {
            using var direct = File.OpenHandle(real);
            using var linked = File.OpenHandle(viaLink);
            finalPath = FileKiwiSharingService.FinalPath(direct);
            Assert.NotNull(finalPath);
            Assert.Equal(finalPath, FileKiwiSharingService.FinalPath(linked), ignoreCase: true);
            Assert.DoesNotContain(Path.GetFileName(folders.Link), finalPath, StringComparison.OrdinalIgnoreCase);
        }
        // The link sits on a local fixed drive; only the real folder may be on a network share (as a symbolic link to \\nas\videos would be).
        var classified = new ConcurrentQueue<string>();
        var options = new FileKiwiSharingOptions
        {
            DriveTypeOf = path =>
            {
                classified.Enqueue(path);
                return realFolderOnNetwork && string.Equals(path, finalPath, StringComparison.OrdinalIgnoreCase) ? DriveType.Network : DriveType.Fixed;
            },
        };
        using var server = new TransportServer(bytes.Length, 1024 * 1024);
        using var client = new HttpClient(server);
        var progress = new CaptureProgress();
        var result = await new FileKiwiSharingService(client, options: options).ShareAsync(viaLink, progress: progress);
        if (realFolderOnNetwork || !OperatingSystem.IsWindows()) Assert.Contains(progress.Values, value => value.Message == "Checking that the file did not change…");
        else Assert.DoesNotContain(progress.Values, value => value.Message == "Checking that the file did not change…");
        if (OperatingSystem.IsWindows()) Assert.Equal([finalPath!], classified);
        Assert.Equal(Hex(bytes), result.Sha256);
        Assert.Equal(bytes, Restore(server, result));
    }

    [Fact]
    public void OnlyDriveLetterPathsOnLocalVolumesCountAsLocked()
    {
        Func<string, DriveType> fixedDrive = _ => DriveType.Fixed;
        Assert.False(FileKiwiSharingService.LocalVolumePath(null, fixedDrive));
        Assert.False(FileKiwiSharingService.LocalVolumePath(@"\\?\UNC\nas\videos\clip.mp4", fixedDrive));
        Assert.False(FileKiwiSharingService.LocalVolumePath(@"\\nas\videos\clip.mp4", fixedDrive));
        Assert.False(FileKiwiSharingService.LocalVolumePath(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\clip.mp4", fixedDrive));
        Assert.False(FileKiwiSharingService.LocalVolumePath(@"C:\videos\clip.mp4", _ => DriveType.Network));
        Assert.Equal(OperatingSystem.IsWindows(), FileKiwiSharingService.LocalVolumePath(@"C:\videos\clip.mp4", fixedDrive));
        Assert.Equal(OperatingSystem.IsWindows(), FileKiwiSharingService.LocalVolumePath(@"E:\clip.mp4", _ => DriveType.Removable));
    }

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)((i * 31 + 7) % 251)).ToArray();
    private static string Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] Restore(TransportServer server, ShareResult result)
    {
        string secret = result.ShareUrl.Split('#')[1].Replace('-', '+').Replace('_', '/');
        byte[] key = Convert.FromBase64String(secret + new string('=', (4 - secret.Length % 4) % 4));
        return server.Uploaded.OrderBy(pair => pair.Key).SelectMany(pair => Rfc8188Encryption.Decrypt(pair.Value, key)).ToArray();
    }

    private sealed class TestFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keaclip-sharing-transport-" + Guid.NewGuid().ToString("N") + ".bin");
        public byte[] Bytes { get; }
        public TestFile(byte[] bytes) { Bytes = bytes; File.WriteAllBytes(Path, bytes); }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class CaptureProgress : IProgress<JobProgress>
    {
        private readonly object _gate = new();
        private readonly List<JobProgress> _values = [];
        private readonly List<int> _threads = [];
        public List<JobProgress> Values { get { lock (_gate) return [.. _values]; } }
        public List<int> Threads { get { lock (_gate) return [.. _threads]; } }
        public void Report(JobProgress value) { lock (_gate) { _values.Add(value); _threads.Add(Environment.CurrentManagedThreadId); } }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _offsetTicks;
        private TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        public void Advance(TimeSpan value) => Interlocked.Add(ref _offsetTicks, value.Ticks);
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
        public override long GetTimestamp() => base.GetTimestamp() + (long)(Offset.TotalSeconds * TimestampFrequency);
    }

    /// <summary>A single-threaded context standing in for the UI dispatcher.</summary>
    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        public PumpContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true, Name = "Simulated UI thread" };
            _thread.Start();
        }
        public int ThreadId => _thread.ManagedThreadId;
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        public Task<T> Run<T>(Func<Task<T>> action)
        {
            var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { started.SetResult(action()); }
                catch (Exception exception) { started.SetException(exception); }
            }, null);
            return started.Task.Unwrap();
        }
        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Consumes a request body like a network link: optionally slowly (per 64 KiB written), stalling or breaking after some writes.</summary>
    private sealed class SlowSink(TimeSpan sliceDelay, int stallAfterWrites, int failAfterWrites = -1, Action? beforeFailure = null) : Stream
    {
        private readonly MemoryStream _data = new();
        private int _writes;
        public byte[] ToArray() => _data.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _data.Length;
        public override long Position { get => _data.Length; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAfterWrites >= 0 && _writes >= stallAfterWrites) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (failAfterWrites >= 0 && _writes >= failAfterWrites)
            {
                beforeFailure?.Invoke();
                throw new IOException("Synthetic connection reset.");
            }
            _writes++;
            if (sliceDelay > TimeSpan.Zero) await Task.Delay(sliceDelay * Math.Ceiling(buffer.Length / 65_536d), cancellationToken);
            _data.Write(buffer.Span);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _data.Dispose(); base.Dispose(disposing); }
    }

    private sealed class TransportServer(int sourceLength, int chunkSize) : HttpMessageHandler
    {
        public const string SyntheticAuth = "synthetic-transport-upload-auth";
        private readonly object _gate = new();
        private readonly List<int> _putAttempts = [], _accepted = [];
        private readonly List<bool> _statusFlags = [];
        private readonly Dictionary<int, byte[]> _uploaded = [];
        private int _statusRequests;
        public TimeSpan SliceDelay { get; init; }
        public int StallAfterWrites { get; init; } = -1;
        /// <summary>Called with the 1-based PUT attempt number; a value of 0 or more stalls that attempt's body after that many writes.</summary>
        public Func<int, int>? StallAfterWritesOn { get; init; }
        /// <summary>Runs after the last body write of an interrupted attempt, just before its connection breaks.</summary>
        public Action? BeforeInterrupt { get; init; }
        /// <summary>Called with the 1-based PUT attempt number; true throws a network error before the body is read.</summary>
        public Func<int, bool>? NetworkFailure { get; init; }
        /// <summary>Called with the 1-based chunk number; a status fails that PUT.</summary>
        public Func<int, HttpStatusCode?>? PutStatus { get; init; }
        /// <summary>Called with the 1-based upload-status request number; a status replaces the normal answer.</summary>
        public Func<int, HttpStatusCode?>? StatusResponse { get; init; }
        public Action? AfterPut { get; init; }
        /// <summary>Called with the 1-based PUT attempt number; a value of 0 or more breaks the connection after that many body writes.</summary>
        public Func<int, int>? InterruptAfterWrites { get; init; }
        /// <summary>Upload-status requests hang until cancelled while any chunk is still missing.</summary>
        public bool StatusHangsWhileIncomplete { get; init; }
        public ConcurrentQueue<int> HandlerThreads { get; } = new();
        public int Chunks => Math.Max(1, (sourceLength + chunkSize - 1) / chunkSize);
        public List<int> PutAttempts { get { lock (_gate) return [.. _putAttempts]; } }
        public List<int> AcceptedOrder { get { lock (_gate) return [.. _accepted]; } }
        public List<bool> StatusCompleteFlags { get { lock (_gate) return [.. _statusFlags]; } }
        public Dictionary<int, byte[]> Uploaded { get { lock (_gate) return new(_uploaded); } }
        public int StatusRequests { get { lock (_gate) return _statusRequests; } }
        /// <summary>Records a chunk that a real storage listener accepted, so status checks and restores see it.</summary>
        public void Accept(int chunk, byte[] body) { lock (_gate) { _putAttempts.Add(chunk); _uploaded[chunk] = body; _accepted.Add(chunk); } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HandlerThreads.Enqueue(Environment.CurrentManagedThreadId);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v2/folders", request.RequestUri!.AbsolutePath);
                return Json(JsonSerializer.Serialize(new
                {
                    folderId = "folder123", folderUrl = "https://file.kiwi/folder123", uploadAuth = SyntheticAuth,
                    files = new[] { new { fileId = "file123", chunkSize, chunks = Chunks, freeDownloadHours = 24,
                        uploadUrls = new { head = "https://filekiwi.test.r2.cloudflarestorage.com/", path = "opaque/folder/1/file", tail = "X-Amz-Algorithm=AWS4-HMAC-SHA256",
                            signatures = Enumerable.Repeat("a1b2c3", Chunks).ToArray(), headers = new Dictionary<string, string> { ["x-amz-meta-folder_id"] = "folder123" } } } },
                }));
            }
            if (request.Method == HttpMethod.Put)
            {
                int chunk = int.Parse(request.RequestUri!.Segments[^1], CultureInfo.InvariantCulture), attempt;
                lock (_gate) { _putAttempts.Add(chunk); attempt = _putAttempts.Count; }
                if (NetworkFailure?.Invoke(attempt) == true)
                    throw new HttpRequestException("https://filekiwi.test.r2.cloudflarestorage.com/?X-Amz-Signature=synthetic-provider-secret");
                int failAfter = InterruptAfterWrites?.Invoke(attempt) ?? -1;
                using var sink = new SlowSink(SliceDelay, StallAfterWritesOn?.Invoke(attempt) ?? StallAfterWrites, failAfter, BeforeInterrupt);
                try { await request.Content!.CopyToAsync(sink, cancellationToken); }
                catch (Exception exception) when (exception is IOException or HttpRequestException && failAfter >= 0)
                {
                    // The link broke part-way through the body; SocketsHttpHandler reports that as an HttpRequestException.
                    throw new HttpRequestException("https://filekiwi.test.r2.cloudflarestorage.com/?X-Amz-Signature=synthetic-provider-secret");
                }
                Assert.Equal(request.Content.Headers.ContentLength, sink.Length);
                Assert.True(request.Headers.Contains("x-amz-meta-folder_id"));
                if (PutStatus?.Invoke(chunk) is { } failure) return Json("{\"error\":\"synthetic-provider-secret\"}", failure);
                lock (_gate) { _uploaded[chunk] = sink.ToArray(); _accepted.Add(chunk); }
                AfterPut?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v2/folders/folder123/files/file123/upload-status", request.RequestUri!.AbsolutePath);
            Assert.Equal("?uploadAuth=" + SyntheticAuth, request.RequestUri.Query);
            int number;
            lock (_gate) number = ++_statusRequests;
            if (StatusResponse?.Invoke(number) is { } code) return Json("{\"error\":\"synthetic-provider-secret\"}", code);
            int[] missing;
            lock (_gate) missing = Enumerable.Range(1, Chunks).Where(chunk => !_uploaded.ContainsKey(chunk)).ToArray();
            if (StatusHangsWhileIncomplete && missing.Length > 0) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            lock (_gate) _statusFlags.Add(missing.Length == 0);
            return Json(JsonSerializer.Serialize(new { complete = missing.Length == 0, missing }));
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>Sends file.kiwi API calls to the in-memory fake and storage calls to a real SocketsHttpHandler; anything else is refused.</summary>
    private sealed class RoutingHandler(HttpMessageHandler api, HttpMessageHandler storage) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _api = new(api, disposeHandler: false), _storage = new(storage, disposeHandler: true);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => request.RequestUri!.Host switch
        {
            LoopbackStorage.Host => _storage.SendAsync(request, cancellationToken),
            "api.file.kiwi" => _api.SendAsync(request, cancellationToken),
            _ => throw new InvalidOperationException("Tests must not reach the network."),
        };
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _api.Dispose(); _storage.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>A real HTTPS listener on 127.0.0.1 standing in for R2 storage. Connections reach it through the handler's connect callback.</summary>
    private sealed class LoopbackStorage : IAsyncDisposable
    {
        public const string Host = "filekiwi.test.r2.cloudflarestorage.com";
        private const string RefusalBody = "<Error><Code>AccessDenied</Code><Message>synthetic-provider-secret</Message></Error>";
        private readonly StorageMode _mode;
        private readonly TransportServer _api;
        private readonly X509Certificate2 _certificate;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<bool> _expect = new();
        private readonly ConcurrentQueue<Task> _connections = new();
        private readonly Task _accepting;
        private int _requests;

        public LoopbackStorage(StorageMode mode, TransportServer api)
        {
            (_mode, _api) = (mode, api);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + Host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(Host);
            request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // Windows TLS cannot serve with an ephemeral key, so the test certificate is round-tripped through PKCS#12.
            _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
            _listener.Start();
            _accepting = AcceptAsync();
        }

        /// <summary>Whether each storage request carried "Expect: 100-continue", in arrival order.</summary>
        public List<bool> ExpectContinue => [.. _expect];

        public SocketsHttpHandler CreateHandler(TimeSpan? expectContinueTimeout)
        {
            string pinned = _certificate.GetCertHashString();
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    if (context.DnsEndPoint.Host != Host) throw new InvalidOperationException("Unexpected storage host.");
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync((IPEndPoint)_listener.LocalEndpoint, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception)
                    {
                        socket.Dispose();
                        throw;
                    }
                },
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == pinned },
            };
            if (expectContinueTimeout is { } timeout) handler.Expect100ContinueTimeout = timeout;
            return handler;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true) _connections.Enqueue(ServeAsync(await _listener.AcceptSocketAsync(_stop.Token)));
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task ServeAsync(Socket socket)
        {
            try
            {
                await using var network = new NetworkStream(socket, ownsSocket: true);
                await using var tls = new SslStream(network);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                byte[] buffer = new byte[65_536];
                int filled = 0, end;
                while ((end = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8)) < 0)
                {
                    if (filled == buffer.Length) return;
                    int read = await tls.ReadAsync(buffer.AsMemory(filled), _stop.Token);
                    if (read == 0) return;
                    filled += read;
                }
                string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
                string Header(string name) => lines.Skip(1).Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2 && parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(parts => parts[1].Trim()).FirstOrDefault() ?? "";
                bool expect = Header("Expect").Equals("100-continue", StringComparison.OrdinalIgnoreCase);
                _expect.Enqueue(expect);
                if (_mode == StorageMode.RefuseEarlyAndClose)
                {
                    // Like S3-style storage refusing a presigned PUT: answer as soon as the headers arrive and close without reading the body.
                    await WriteAsync(tls, Refusal());
                    return;
                }
                if (_mode is StorageMode.CutOffOnceThenAccept or StorageMode.CutOffOnceThenAcceptIgnoringExpect && Interlocked.Increment(ref _requests) == 1)
                {
                    // Like a Wi-Fi drop: take part of the body, then reset the connection without answering.
                    for (long seen = filled - end - 4; seen < 256 * 1024;)
                    {
                        int read = await tls.ReadAsync(buffer, _stop.Token);
                        if (read == 0) return;
                        seen += read;
                    }
                    socket.LingerState = new LingerOption(true, 0);
                    return;
                }
                if (expect && _mode != StorageMode.CutOffOnceThenAcceptIgnoringExpect) await WriteAsync(tls, "HTTP/1.1 100 Continue\r\n\r\n");
                long length = long.Parse(Header("Content-Length"), CultureInfo.InvariantCulture);
                using var body = new MemoryStream();
                body.Write(buffer, end + 4, filled - end - 4);
                while (body.Length < length)
                {
                    int read = await tls.ReadAsync(buffer, _stop.Token);
                    if (read == 0) return;
                    body.Write(buffer, 0, read);
                }
                if (_mode == StorageMode.RefuseAfterBody)
                {
                    await WriteAsync(tls, Refusal());
                    return;
                }
                _api.Accept(int.Parse(lines[0].Split(' ')[1].Split('?')[0].Split('/')[^1], CultureInfo.InvariantCulture), body.ToArray());
                await WriteAsync(tls, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException) { }
        }

        private static string Refusal() => $"HTTP/1.1 403 Forbidden\r\nContent-Type: application/xml\r\nContent-Length: {RefusalBody.Length}\r\nConnection: close\r\n\r\n{RefusalBody}";

        private async Task WriteAsync(SslStream stream, string text)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accepting;
            await Task.WhenAll(_connections);
            _certificate.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>A temporary folder reached through a directory junction (a symbolic link off Windows).</summary>
    private sealed class LinkedFolders : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "keaclip-sharing-links-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "real");
        public string Link { get; }

        public LinkedFolders()
        {
            Link = Path.Combine(Root, "link-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(Target);
                if (!OperatingSystem.IsWindows())
                {
                    Directory.CreateSymbolicLink(Link, Target);
                    return;
                }
                // A junction is a real reparse point that needs neither developer mode nor the symbolic-link privilege.
                Directory.CreateDirectory(Link);
                using var handle = CreateFileW(Link, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("Test junction handle failed.");
                byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + Target), printed = Encoding.Unicode.GetBytes(Target), buffer = new byte[20 + substitute.Length + printed.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(buffer.Length - 8));
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)printed.Length);
                substitute.CopyTo(buffer, 16);
                printed.CopyTo(buffer, 18 + substitute.Length);
                if (!DeviceIoControl(handle, 0x000900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new IOException("Test junction creation failed.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            // Remove the link itself first so that deleting the folder never follows it.
            if (Directory.Exists(Link)) Directory.Delete(Link, false);
            string root = Path.GetFullPath(Root);
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test folder.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
    }
}
