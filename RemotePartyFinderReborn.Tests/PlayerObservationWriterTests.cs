using System.Collections.Concurrent;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PlayerObservationWriterTests {
    [Fact]
    public void PendingCountGetterUsesCachedValueWithoutCallingDatabaseReader() {
        var databaseReads = 0;
        var cache = new PlayerPendingCountCache(() => {
            Interlocked.Increment(ref databaseReads);
            return 17;
        });

        Assert.Equal(0, cache.Value);
        Assert.Equal(0, Volatile.Read(ref databaseReads));

        cache.Refresh();

        Assert.Equal(17, cache.Value);
        Assert.Equal(1, Volatile.Read(ref databaseReads));
        Assert.Equal(17, cache.Value);
        Assert.Equal(1, Volatile.Read(ref databaseReads));
    }

    [Fact]
    public async Task EnqueueCoalescesOverflowAndDisposeDrainsItAfterEarlierBatches() {
        using var firstWriteEntered = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<UploadablePlayer[]>();
        var frameworkThreadId = Environment.CurrentManagedThreadId;
        var databaseThreadIds = new ConcurrentBag<int>();
        using var writer = new PlayerObservationWriter(players => {
            databaseThreadIds.Add(Environment.CurrentManagedThreadId);
            var batch = players.ToArray();
            writes.Enqueue(batch);
            if (batch.Any(static player => player.ContentId == 1)) {
                firstWriteEntered.Set();
                releaseFirstWrite.Wait(TimeSpan.FromSeconds(5));
            }
        });

        writer.Enqueue([Player(1, "first")]);
        Assert.True(firstWriteEntered.Wait(TimeSpan.FromSeconds(5)));
        writer.Enqueue([Player(2, "queued")]);
        writer.Enqueue([Player(3, "overflow-old")]);
        writer.Enqueue([Player(3, "overflow-latest")]);

        var dispose = Task.Run(writer.Dispose);
        releaseFirstWrite.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        var batches = writes.ToArray();
        Assert.Equal("first", Assert.Single(batches[0]).Name);
        var drainedPlayers = batches.Skip(1).SelectMany(static batch => batch).ToArray();
        Assert.Equal([2UL, 3UL], drainedPlayers.Select(static player => player.ContentId));
        Assert.Equal(["queued", "overflow-latest"], drainedPlayers.Select(static player => player.Name));
        Assert.All(databaseThreadIds, threadId => Assert.NotEqual(frameworkThreadId, threadId));
        Assert.Equal((ushort)21, drainedPlayers[1].HomeWorld);
        Assert.Equal((ushort)21, drainedPlayers[1].CurrentWorld);
        Assert.Equal(103UL, drainedPlayers[1].AccountId);
    }

    [Fact]
    public async Task FailedUpsertIsRetriedBeforeLaterObservations() {
        var writes = new ConcurrentQueue<string>();
        var failFirst = 1;
        using var writer = new PlayerObservationWriter(players => {
            var player = Assert.Single(players);
            writes.Enqueue(player.Name);
            if (Interlocked.Exchange(ref failFirst, 0) == 1) {
                throw new InvalidOperationException("temporary sqlite failure");
            }
        });

        writer.Enqueue([Player(1, "retry")]);
        await WaitUntilAsync(() => writes.Count >= 1);
        writer.Enqueue([Player(2, "later")]);
        await WaitUntilAsync(() => writes.Count >= 3);
        writer.Dispose();

        Assert.Equal(["retry", "retry", "later"], writes.ToArray());
    }

    [Fact]
    public async Task OverflowIsHardBoundedAndStillUpdatesAlreadyQueuedContentIds() {
        using var firstWriteEntered = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<UploadablePlayer[]>();
        var warnings = new ConcurrentQueue<string>();
        using var writer = new PlayerObservationWriter(players => {
            var batch = players.ToArray();
            writes.Enqueue(batch);
            if (batch.Any(static player => player.ContentId == 1)) {
                firstWriteEntered.Set();
                releaseFirstWrite.Wait(TimeSpan.FromSeconds(5));
            }
        }, warnings.Enqueue);

        writer.Enqueue([Player(1, "blocking")]);
        Assert.True(firstWriteEntered.Wait(TimeSpan.FromSeconds(5)));
        writer.Enqueue([Player(2, "queued")]);
        writer.Enqueue(Enumerable.Range(3, PlayerObservationWriter.MaxCoalescedOverflowPlayers + 1)
            .Select(id => Player((ulong)id, $"player-{id}")));
        writer.Enqueue([Player(3, "latest-retained")]);
        writer.Enqueue([Player((ulong)(PlayerObservationWriter.MaxCoalescedOverflowPlayers + 4), "still-dropped")]);

        Assert.Contains(warnings, warning => warning.Contains("dropped 1 new unique", StringComparison.Ordinal));
        Assert.True(warnings.Count == 1, "Overflow warning should be rate limited.");

        var dispose = Task.Run(writer.Dispose);
        releaseFirstWrite.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        var drainedOverflow = writes.Last();
        Assert.Equal(PlayerObservationWriter.MaxCoalescedOverflowPlayers, drainedOverflow.Length);
        Assert.Equal("latest-retained", drainedOverflow.Single(player => player.ContentId == 3).Name);
        Assert.DoesNotContain(drainedOverflow, player => player.ContentId == (ulong)(PlayerObservationWriter.MaxCoalescedOverflowPlayers + 3));
    }

    [Fact]
    public async Task DisposeStopsAfterFiniteFailedDrainAttempts() {
        var writeAttempts = 0;
        var warnings = new ConcurrentQueue<string>();
        var writer = new PlayerObservationWriter(
            _ => {
                Interlocked.Increment(ref writeAttempts);
                throw new InvalidOperationException("database unavailable");
            },
            warnings.Enqueue,
            _ => { });
        writer.Enqueue([Player(1, "pending")]);
        await WaitUntilAsync(() => Volatile.Read(ref writeAttempts) >= 1);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(writer.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        Assert.InRange(writeAttempts, 1, 4);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(warnings, warning => warning.Contains("dropped 1 pending player observations", StringComparison.Ordinal));
    }

    private static UploadablePlayer Player(ulong contentId, string name) => new() {
        ContentId = contentId,
        Name = name,
        HomeWorld = 21,
        CurrentWorld = 21,
        AccountId = contentId + 100
    };

    private static async Task WaitUntilAsync(Func<bool> predicate) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate()) {
            if (DateTime.UtcNow >= deadline) {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
