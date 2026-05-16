using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class DebugPfListingQueue {
    private readonly Dictionary<ulong, DebugPfListingCandidate> _latestVisible = new();
    private readonly Dictionary<ulong, DateTime> _attemptedAt = new();
    private readonly Queue<DebugPfListingCandidate> _pending = new();

    internal int VisibleCount => _latestVisible.Count;
    internal int PendingCount => _pending.Count;

    internal void Reset() {
        _latestVisible.Clear();
        _attemptedAt.Clear();
        _pending.Clear();
    }

    internal void UpsertVisible(DebugPfListingCandidate incoming) {
        if (_latestVisible.TryGetValue(incoming.ListingId, out var existing)) {
            _latestVisible[incoming.ListingId] = new DebugPfListingCandidate(
                incoming.ListingId,
                incoming.ContentId != 0 ? incoming.ContentId : existing.ContentId,
                existing.SeenAtUtc,
                Math.Max(existing.BatchNumber, incoming.BatchNumber)
            );
            return;
        }

        _latestVisible[incoming.ListingId] = incoming;
    }

    internal void RebuildPendingQueueFromVisible(DateTime nowUtc, int dedupTtlSeconds) {
        _pending.Clear();

        foreach (var listing in _latestVisible.Values
                     .OrderBy(value => value.SeenAtUtc)
                     .ThenByDescending(value => value.BatchNumber)) {
            if (IsRecentlyAttempted(listing.ListingId, nowUtc, dedupTtlSeconds)) {
                continue;
            }

            _pending.Enqueue(listing);
        }
    }

    internal void RebuildPendingQueueFromSnapshot(IEnumerable<DebugPfListingCandidate> snapshot, DateTime nowUtc, int dedupTtlSeconds) {
        _pending.Clear();

        foreach (var listing in snapshot) {
            if (IsRecentlyAttempted(listing.ListingId, nowUtc, dedupTtlSeconds)) {
                continue;
            }

            _pending.Enqueue(listing);
        }
    }

    internal bool TryTakeNextReadyTarget(DateTime nowUtc, int dedupTtlSeconds, out DebugPfListingCandidate target) {
        var remaining = _pending.Count;
        while (remaining-- > 0) {
            var candidate = _pending.Dequeue();
            if (IsRecentlyAttempted(candidate.ListingId, nowUtc, dedupTtlSeconds)) {
                continue;
            }

            target = candidate;
            return true;
        }

        target = default;
        return false;
    }

    internal void RecordAttempt(ulong listingId, DateTime attemptedAtUtc) {
        _attemptedAt[listingId] = attemptedAtUtc;
    }

    internal void PruneCaches(DateTime nowUtc, int dedupTtlSeconds) {
        var visibleTtl = TimeSpan.FromSeconds(30);
        foreach (var staleId in _latestVisible
                     .Where(kvp => nowUtc - kvp.Value.SeenAtUtc > visibleTtl)
                     .Select(kvp => kvp.Key)
                     .ToList()) {
            _latestVisible.Remove(staleId);
        }

        var dedupTtl = TimeSpan.FromSeconds(Configuration.NormalizeScanDedupTtlSeconds(dedupTtlSeconds));
        foreach (var staleId in _attemptedAt
                     .Where(kvp => nowUtc - kvp.Value > dedupTtl)
                     .Select(kvp => kvp.Key)
                     .ToList()) {
            _attemptedAt.Remove(staleId);
        }
    }

    private bool IsRecentlyAttempted(ulong listingId, DateTime nowUtc, int dedupTtlSeconds) {
        if (!_attemptedAt.TryGetValue(listingId, out var lastAttemptUtc)) {
            return false;
        }

        var dedupTtl = TimeSpan.FromSeconds(Configuration.NormalizeScanDedupTtlSeconds(dedupTtlSeconds));
        return nowUtc - lastAttemptUtc < dedupTtl;
    }
}
