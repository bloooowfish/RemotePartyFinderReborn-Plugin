using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class DebugPfListingQueue {
    private readonly Dictionary<ulong, DateTime> _attemptedAt = new();
    private readonly HashSet<ulong> _completedInRun = new();
    private readonly Queue<DebugPfListingCandidate> _pending = new();
    private readonly HashSet<ulong> _pendingIds = new();

    internal int PendingCount => _pending.Count;

    internal void Reset() {
        _attemptedAt.Clear();
        ResetRun();
    }

    internal void ResetRun() {
        _completedInRun.Clear();
        _pending.Clear();
        _pendingIds.Clear();
    }

    internal void RebuildPendingQueueFromSnapshot(IEnumerable<DebugPfListingCandidate> snapshot, DateTime nowUtc, int dedupTtlSeconds) {
        _pending.Clear();
        _pendingIds.Clear();

        foreach (var listing in snapshot) {
            if (IsRecentlyAttempted(listing.ListingId, nowUtc, dedupTtlSeconds)) {
                continue;
            }

            EnqueuePending(listing);
        }
    }

    internal bool TryTakeNextReadyTarget(DateTime nowUtc, int dedupTtlSeconds, out DebugPfListingCandidate target) {
        var remaining = _pending.Count;
        while (remaining-- > 0) {
            var candidate = _pending.Dequeue();
            _pendingIds.Remove(candidate.ListingId);
            if (IsRecentlyAttempted(candidate.ListingId, nowUtc, dedupTtlSeconds)) {
                continue;
            }

            target = candidate;
            return true;
        }

        target = default;
        return false;
    }

    internal void RecordAttempt(ulong listingId, DateTime attemptedAtUtc, bool recordCrossRunDedup = true) {
        _completedInRun.Add(listingId);
        if (recordCrossRunDedup) {
            _attemptedAt[listingId] = attemptedAtUtc;
        }
    }

    internal bool IsRecentlyDeduped(ulong listingId, DateTime nowUtc, int dedupTtlSeconds) {
        if (!_attemptedAt.TryGetValue(listingId, out var lastAttemptUtc)) {
            return false;
        }

        var dedupTtl = TimeSpan.FromSeconds(NormalizeDedupTtlSeconds(dedupTtlSeconds));
        return nowUtc - lastAttemptUtc < dedupTtl;
    }

    internal void PruneCaches(DateTime nowUtc, int dedupTtlSeconds) {
        var dedupTtl = TimeSpan.FromSeconds(NormalizeDedupTtlSeconds(dedupTtlSeconds));
        foreach (var staleId in _attemptedAt
                     .Where(kvp => nowUtc - kvp.Value > dedupTtl)
                     .Select(kvp => kvp.Key)
                     .ToList()) {
            _attemptedAt.Remove(staleId);
        }
    }

    private bool IsRecentlyAttempted(ulong listingId, DateTime nowUtc, int dedupTtlSeconds) {
        if (_completedInRun.Contains(listingId)) {
            return true;
        }

        return IsRecentlyDeduped(listingId, nowUtc, dedupTtlSeconds);
    }

    private void EnqueuePending(DebugPfListingCandidate listing) {
        if (_pendingIds.Add(listing.ListingId)) {
            _pending.Enqueue(listing);
        }
    }

    internal static int NormalizeDedupTtlSeconds(int dedupTtlSeconds)
        => Math.Clamp(dedupTtlSeconds, 30, 3_600);
}
