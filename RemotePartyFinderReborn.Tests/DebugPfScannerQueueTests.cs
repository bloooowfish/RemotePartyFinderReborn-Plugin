using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class DebugPfScannerQueueTests {
    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(5000)]
    public void Snapshot_batch_scales_and_remains_finite_after_dedup_ttl(int candidateCount) {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var candidates = Enumerable.Range(1, candidateCount)
            .Select(index => new DebugPfListingCandidate((ulong)index, (ulong)(index + 10000), now.AddMilliseconds(index), candidateCount - index))
            .ToArray();
        var queue = new DebugPfListingQueue();
        queue.RebuildPendingQueueFromSnapshot(candidates, now, dedupTtlSeconds: 30);

        var processed = new HashSet<ulong>();
        while (queue.TryTakeNextReadyTarget(now, dedupTtlSeconds: 30, out var target)) {
            Assert.True(processed.Add(target.ListingId));
            queue.RecordAttempt(target.ListingId, now);
        }

        Assert.Equal(candidateCount, processed.Count);
        queue.RebuildPendingQueueFromSnapshot(candidates, now.AddSeconds(31), dedupTtlSeconds: 30);
        Assert.False(queue.TryTakeNextReadyTarget(now.AddSeconds(31), dedupTtlSeconds: 30, out _));
    }

    [Fact]
    public void Candidate_updates_keep_first_seen_and_refresh_last_seen_for_expiry() {
        var start = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var buffer = new CollectedListingBuffer();
        Assert.True(buffer.UpsertFromListing(1001, 0, 1, start));
        Assert.False(buffer.UpsertFromListing(1001, 0, 2, start.AddSeconds(25)));

        buffer.Prune(start.AddSeconds(600));

        Assert.Equal(1, buffer.Count);
        var updated = Assert.Single(buffer.TakeOrderedSnapshot(start.AddSeconds(600)));
        Assert.Equal(start, updated.SeenAtUtc);
        Assert.Equal(start.AddSeconds(25), updated.LastSeenAtUtc);

        buffer.Prune(start.AddSeconds(626));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Pending_target_uses_latest_nonzero_content_id_from_snapshot() {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var buffer = new CollectedListingBuffer();
        buffer.UpsertFromListing(1002, 0, 1, now);
        buffer.UpsertFromListing(1002, 987654, 2, now.AddSeconds(29));

        var snapshot = buffer.TakeOrderedSnapshot(now.AddSeconds(60));
        var queue = new DebugPfListingQueue();
        queue.RebuildPendingQueueFromSnapshot(snapshot, now.AddSeconds(60), dedupTtlSeconds: 600);

        Assert.True(queue.TryTakeNextReadyTarget(now.AddSeconds(60), 600, out var target));

        Assert.Equal(1002UL, target.ListingId);
        Assert.Equal(987654UL, target.ContentId);
    }

    [Fact]
    public void Collected_batch_finishes_after_terminal_failure_even_when_dedup_ttl_expires() {
        var start = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var target = new DebugPfListingCandidate(1003, 456, start, 1);
        var queue = new DebugPfListingQueue();
        var machine = new DebugPfScanStateMachine(queue);

        Assert.Null(machine.SyncQueue(start, [target], dedupTtlSeconds: 30));
        Assert.Equal(target.ListingId, machine.CurrentTargetListingId);
        machine.HandleOpenAttemptResult(
            start,
            opened: false,
            actionIntervalMs: 100,
            detailReadyTimeoutMs: 500,
            configuredRetries: 0,
            postListingCooldownMs: 50);
        machine.AdvanceCooldown(start.AddMilliseconds(51));

        var completion = machine.SyncQueue(
            start.AddSeconds(31),
            [target],
            dedupTtlSeconds: 30);

        Assert.Equal("collected_batch_complete", completion);
        Assert.Equal(1, machine.ConsecutiveFailures);
        Assert.Equal(0, machine.PendingCount);
    }

    [Fact]
    public void Collected_batch_finishes_after_success_even_when_dedup_ttl_expires() {
        var start = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var target = new DebugPfListingCandidate(1004, 789, start, 1);
        var queue = new DebugPfListingQueue();
        var machine = new DebugPfScanStateMachine(queue);

        Assert.Null(machine.SyncQueue(start, [target], dedupTtlSeconds: 30));
        machine.HandleOpenAttemptResult(start, true, 100, 500, 0, 50, requestSerial: 1);
        var snapshot = new DebugPfDetailSnapshot(target.ListingId, target.ContentId, NonZeroMembers: 4, TotalSlots: 8);
        machine.HandleDetailReadyState(start.AddMilliseconds(1), snapshot, detailReadyTimeoutMs: 500, configuredRetries: 0, postListingCooldownMs: 50);
        machine.HandleDetailReadyState(start.AddMilliseconds(2), snapshot, detailReadyTimeoutMs: 500, configuredRetries: 0, postListingCooldownMs: 50);
        Assert.Equal(DebugPfScanState.WaitCollected, machine.State);
        machine.HandleCollectedState(start.AddMilliseconds(3), hasConsumedAck: true, postListingCooldownMs: 50);
        machine.AdvanceCooldown(start.AddMilliseconds(54));

        var completion = machine.SyncQueue(
            start.AddSeconds(31),
            [target],
            dedupTtlSeconds: 30);

        Assert.Equal("collected_batch_complete", completion);
        Assert.Equal(1, machine.ProcessedCount);
        Assert.Equal(0, machine.PendingCount);
    }
}
