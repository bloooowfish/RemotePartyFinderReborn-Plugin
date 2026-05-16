using System;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PendingDetailQueueTests {
    [Fact]
    public void Enqueue_replaces_same_listing_only_when_fingerprint_changes() {
        var now = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var queue = new PendingDetailQueue();
        var original = CreateDetail(slotFlag: "0x1");
        var unchanged = CreateDetail(slotFlag: "0x1");
        var changed = CreateDetail(slotFlag: "0x2");

        queue.Enqueue(original, fingerprint: 100UL, now);
        queue.Enqueue(unchanged, fingerprint: 100UL, now.AddMinutes(1));

        Assert.True(queue.TryGet(original.ListingId, out var pending));
        Assert.Same(original, pending.Detail);
        Assert.Equal(now, pending.QueuedAtUtc);
        Assert.Equal(1, queue.Count);

        queue.Enqueue(changed, fingerprint: 200UL, now.AddMinutes(2));

        Assert.True(queue.TryGet(original.ListingId, out pending));
        Assert.Same(changed, pending.Detail);
        Assert.Equal(200UL, pending.Fingerprint);
        Assert.Equal(now.AddMinutes(2), pending.QueuedAtUtc);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Select_due_batch_excludes_future_entries() {
        var now = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var queue = new PendingDetailQueue();
        queue.Enqueue(CreateDetail(9001U), fingerprint: 100UL, now);
        queue.Enqueue(CreateDetail(9002U), fingerprint: 200UL, now);
        Assert.True(queue.TryGet(9002U, out var future));
        Assert.True(queue.TryUpdate(future, future with {
            NextAttemptAtUtc = now.AddMinutes(1),
        }));

        var selected = queue.SelectNextDueBatch(now, batchSize: 10);

        var pending = Assert.Single(selected);
        Assert.Equal(9001U, pending.Detail.ListingId);
    }

    [Fact]
    public void Select_due_batch_marks_entries_in_flight() {
        var now = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var queue = new PendingDetailQueue();
        queue.Enqueue(CreateDetail(9001U), fingerprint: 100UL, now);
        queue.Enqueue(CreateDetail(9002U), fingerprint: 200UL, now);

        var first = queue.SelectNextDueBatch(now, batchSize: 1);
        var second = queue.SelectNextDueBatch(now, batchSize: 10);

        Assert.Equal(9001U, Assert.Single(first).Detail.ListingId);
        Assert.Equal(9002U, Assert.Single(second).Detail.ListingId);
    }

    [Fact]
    public void Manual_retry_resets_attempt_count_and_due_time() {
        var now = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var retryAt = now.AddMinutes(5);
        var queue = new PendingDetailQueue();
        queue.Enqueue(CreateDetail(), fingerprint: 100UL, now);
        Assert.True(queue.TryGet(9001U, out var current));
        Assert.True(queue.TryUpdate(current, current with {
            AttemptCount = 3,
            NextAttemptAtUtc = retryAt,
        }));

        var triggered = queue.TriggerRetryNow(now.AddSeconds(1));

        Assert.Equal(1, triggered);
        Assert.True(queue.TryGet(9001U, out var pending));
        Assert.Equal(0, pending.AttemptCount);
        Assert.Equal(now.AddSeconds(1), pending.NextAttemptAtUtc);
    }

    [Fact]
    public void Manual_retry_skips_in_flight_entries() {
        var now = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var queue = new PendingDetailQueue();
        queue.Enqueue(CreateDetail(), fingerprint: 100UL, now);
        Assert.True(queue.TryGet(9001U, out var current));
        Assert.True(queue.TryUpdate(current, current with {
            AttemptCount = 3,
            NextAttemptAtUtc = now,
        }));

        var selected = queue.SelectNextDueBatch(now, batchSize: 1);
        var triggered = queue.TriggerRetryNow(now.AddSeconds(1));

        Assert.Equal(0, triggered);
        Assert.True(queue.TryGet(9001U, out var pending));
        Assert.Equal(3, pending.AttemptCount);
        Assert.Equal(now, pending.NextAttemptAtUtc);
        Assert.Equal(9001U, Assert.Single(selected).Detail.ListingId);
    }

    [Fact]
    public void Remove_if_unchanged_does_not_remove_newer_fingerprint() {
        var now = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var queue = new PendingDetailQueue();
        queue.Enqueue(CreateDetail(slotFlag: "0x1"), fingerprint: 100UL, now);
        var selected = Assert.Single(queue.SelectNextDueBatch(now, batchSize: 1));

        queue.Enqueue(CreateDetail(slotFlag: "0x2"), fingerprint: 200UL, now.AddSeconds(1));
        queue.RemoveIfUnchanged(selected);

        Assert.True(queue.TryGet(9001U, out var pending));
        Assert.Equal(200UL, pending.Fingerprint);
    }

    private static UploadablePartyDetail CreateDetail(uint listingId = 9001U, string slotFlag = "0x1") {
        return new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            MemberContentIds = [44UL],
            MemberJobs = [19],
            SlotFlags = [slotFlag],
        };
    }
}
