using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class PendingDetailQueue {
    private readonly ConcurrentDictionary<uint, PendingDetailEntry> _pendingDetails = new();
    private readonly ConcurrentDictionary<uint, byte> _inFlightDetails = new();

    internal int Count => _pendingDetails.Count;
    internal int InFlightCount => _inFlightDetails.Count;

    internal void Enqueue(
        UploadablePartyDetail payload,
        ulong fingerprint,
        DateTime nowUtc,
        PartyDetailRequestOwner owner = PartyDetailRequestOwner.Manual
    ) {
        var queued = new PendingDetailEntry(
            payload,
            fingerprint,
            owner,
            nowUtc,
            0,
            nowUtc
        );

        _pendingDetails.AddOrUpdate(
            payload.ListingId,
            _ => queued,
            (_, existing) => existing.Fingerprint == fingerprint ? existing : queued
        );
    }

    internal IReadOnlyList<PendingDetailEntry> SelectNextDueBatch(DateTime nowUtc, int batchSize) {
        var selected = new List<PendingDetailEntry>(batchSize);
        foreach (var candidate in _pendingDetails.Values
            .Where(candidate => candidate.NextAttemptAtUtc <= nowUtc)
            .Where(candidate => !_inFlightDetails.ContainsKey(candidate.Detail.ListingId))
            .OrderBy(candidate => candidate.NextAttemptAtUtc)
            .ThenBy(candidate => candidate.QueuedAtUtc)) {
            if (!_inFlightDetails.TryAdd(candidate.Detail.ListingId, 0)) {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= batchSize) {
                break;
            }
        }

        return selected;
    }

    internal int TriggerRetryNow(DateTime nowUtc) {
        var triggered = 0;

        foreach (var listingId in _pendingDetails.Keys) {
            if (_inFlightDetails.ContainsKey(listingId)) {
                continue;
            }

            while (_pendingDetails.TryGetValue(listingId, out var current)) {
                if (_inFlightDetails.ContainsKey(listingId)) {
                    break;
                }

                var updated = current with {
                    AttemptCount = 0,
                    NextAttemptAtUtc = nowUtc,
                };

                if (_pendingDetails.TryUpdate(listingId, updated, current)) {
                    triggered++;
                    break;
                }
            }
        }

        return triggered;
    }

    internal void ReleaseInFlight(IEnumerable<PendingDetailEntry> entries) {
        foreach (var pending in entries) {
            _inFlightDetails.TryRemove(pending.Detail.ListingId, out _);
        }
    }

    internal void RemoveIfUnchanged(PendingDetailEntry pending) {
        if (!_pendingDetails.TryGetValue(pending.Detail.ListingId, out var current)) {
            return;
        }

        if (current.Fingerprint != pending.Fingerprint) {
            return;
        }

        TryRemoveExact(pending.Detail.ListingId, current);
    }

    internal bool TryGet(uint listingId, out PendingDetailEntry pending) {
        return _pendingDetails.TryGetValue(listingId, out pending!);
    }

    internal IReadOnlyList<PendingDetailEntry> SnapshotEntries() {
        return _pendingDetails.Values.ToArray();
    }

    internal bool IsInFlight(uint listingId) {
        return _inFlightDetails.ContainsKey(listingId);
    }

    internal bool TryUpdate(PendingDetailEntry current, PendingDetailEntry updated) {
        return current.Detail.ListingId == updated.Detail.ListingId
            && _pendingDetails.TryUpdate(current.Detail.ListingId, updated, current);
    }

    private bool TryRemoveExact(uint listingId, PendingDetailEntry current) {
        return ((ICollection<KeyValuePair<uint, PendingDetailEntry>>)_pendingDetails)
            .Remove(new KeyValuePair<uint, PendingDetailEntry>(listingId, current));
    }
}

internal sealed record PendingDetailEntry(
    UploadablePartyDetail Detail,
    ulong Fingerprint,
    PartyDetailRequestOwner Owner,
    DateTime QueuedAtUtc,
    int AttemptCount,
    DateTime NextAttemptAtUtc
);
