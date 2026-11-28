using System;
using System.Collections.Generic;

namespace RemotePartyFinderReborn;

internal sealed class PartyDetailCaptureState {
    internal const int MaxArrivalQueueCapacity = 16;
    internal const int MaxRecentConsumedSerials = 64;
    internal static readonly TimeSpan StaleManualRequestTtl = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<long, PartyDetailRequestCycle> _pendingRequests = new();
    private readonly Dictionary<long, DateTime> _pendingRequestCreatedAtUtc = new();
    private readonly Queue<PartyDetailArrival> _arrivalQueue = new();
    private readonly Queue<long> _consumedSerialOrder = new();
    private readonly Dictionary<long, PartyDetailArrival> _consumedArrivalsBySerial = new();
    private readonly Func<DateTime> _utcNow;

    private long _nextRequestSerial;
    private long _latestArrivalGeneration;
    private long _latestConsumedGeneration;
    private PartyDetailLatestDebugSnapshot _latestDebugSnapshot;
    private bool _hasLatestDebugSnapshot;

    internal PartyDetailCaptureState()
        : this(() => DateTime.UtcNow) {
    }

    internal PartyDetailCaptureState(Func<DateTime> utcNow) {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    internal long LatestArrivalGeneration {
        get {
            lock (_gate) {
                return _latestArrivalGeneration;
            }
        }
    }

    internal long LastConsumedGeneration {
        get {
            lock (_gate) {
                return _latestConsumedGeneration;
            }
        }
    }

    internal long? CurrentRequestSerial {
        get {
            lock (_gate) {
                return _nextRequestSerial > 0 ? _nextRequestSerial : null;
            }
        }
    }

    internal bool HasActiveRequest {
        get {
            lock (_gate) {
                return TryGetCurrentCycle(out _);
            }
        }
    }

    internal PartyDetailRequestOwner? CurrentOwner {
        get {
            lock (_gate) {
                return TryGetCurrentCycle(out var cycle) ? cycle.Owner : null;
            }
        }
    }

    internal ulong CurrentListingId {
        get {
            lock (_gate) {
                return TryGetCurrentCycle(out var cycle) ? cycle.ListingId : 0UL;
            }
        }
    }

    internal ulong CurrentContentId {
        get {
            lock (_gate) {
                return TryGetCurrentCycle(out var cycle) ? cycle.ContentId : 0UL;
            }
        }
    }

    internal PartyDetailRequestCycle BeginRequest(PartyDetailRequestOwner owner, ulong listingId, ulong contentId) {
        lock (_gate) {
            var nowUtc = _utcNow();
            PruneExpiredManualRequestsLocked(nowUtc);

            var cycle = new PartyDetailRequestCycle(++_nextRequestSerial, owner, listingId, contentId);
            _pendingRequests[cycle.RequestSerial] = cycle;
            _pendingRequestCreatedAtUtc[cycle.RequestSerial] = nowUtc;
            return cycle;
        }
    }

    public bool TryRecordArrival(long requestSerial, UploadablePartyDetail snapshot, PartyDetailDebugSnapshot debugSnapshot = null) {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate) {
            if (_consumedArrivalsBySerial.ContainsKey(requestSerial)) {
                return false;
            }

            if (!_pendingRequests.Remove(requestSerial, out var cycle)) {
                return false;
            }

            _pendingRequestCreatedAtUtc.Remove(requestSerial);

            var generation = ++_latestArrivalGeneration;
            var arrival = new PartyDetailArrival(generation, cycle, CloneSnapshot(snapshot));
            while (_arrivalQueue.Count >= MaxArrivalQueueCapacity) {
                _arrivalQueue.Dequeue();
            }

            _arrivalQueue.Enqueue(arrival);
            if (debugSnapshot is not null) {
                _latestDebugSnapshot = new PartyDetailLatestDebugSnapshot(
                    generation,
                    cycle,
                    CloneDebugSnapshot(debugSnapshot)
                );
                _hasLatestDebugSnapshot = true;
            }
            return true;
        }
    }

    public bool TryMarkConsumed(long generation) {
        if (generation <= 0) {
            return false;
        }

        lock (_gate) {
            if (_arrivalQueue.Count == 0 || generation > _latestArrivalGeneration || generation <= _latestConsumedGeneration) {
                return false;
            }

            var head = _arrivalQueue.Peek();
            if (head.Generation != generation) {
                return false;
            }

            _arrivalQueue.Dequeue();
            _latestConsumedGeneration = generation;

            if (_consumedArrivalsBySerial.TryAdd(head.Cycle.RequestSerial, head)) {
                _consumedSerialOrder.Enqueue(head.Cycle.RequestSerial);
                while (_consumedSerialOrder.Count > MaxRecentConsumedSerials) {
                    var evictedSerial = _consumedSerialOrder.Dequeue();
                    _consumedArrivalsBySerial.Remove(evictedSerial);
                }
            }

            return true;
        }
    }

    internal bool TryCancelRequest(long requestSerial) {
        if (requestSerial <= 0) {
            return false;
        }

        lock (_gate) {
            _pendingRequestCreatedAtUtc.Remove(requestSerial);
            return _pendingRequests.Remove(requestSerial);
        }
    }

    internal int PruneExpiredManualRequests() {
        lock (_gate) {
            return PruneExpiredManualRequestsLocked(_utcNow());
        }
    }

    public bool IsScannerAckReady(long requestSerial, ulong listingId, ulong contentId) {
        lock (_gate) {
            foreach (var arrival in _arrivalQueue) {
                if (arrival.Generation > _latestConsumedGeneration
                    && MatchesScannerArrival(arrival, requestSerial, listingId, contentId)) {
                    return true;
                }
            }

            return false;
        }
    }

    public bool IsScannerConsumedAckReady(long requestSerial, ulong listingId, ulong contentId) {
        lock (_gate) {
            return _consumedArrivalsBySerial.TryGetValue(requestSerial, out var consumedArrival)
                   && MatchesScannerArrival(consumedArrival, requestSerial, listingId, contentId);
        }
    }

    internal bool TryGetCurrentRequestCycle(out PartyDetailRequestCycle cycle) {
        lock (_gate) {
            return TryGetCurrentCycle(out cycle);
        }
    }

    internal bool TryGetNextUnconsumedArrival(out PartyDetailPendingArrival arrival) {
        lock (_gate) {
            if (_arrivalQueue.Count == 0) {
                arrival = default;
                return false;
            }

            var nextArrival = _arrivalQueue.Peek();
            arrival = new PartyDetailPendingArrival(
                nextArrival.Generation,
                nextArrival.Cycle,
                CloneSnapshot(nextArrival.Snapshot)
            );
            return true;
        }
    }

    private static bool MatchesScannerArrival(
        PartyDetailArrival arrival,
        long requestSerial,
        ulong listingId,
        ulong contentId
    ) {
        var requestContentMatches = arrival.Cycle.ContentId == 0
                                    || arrival.Cycle.ContentId == contentId;
        var snapshotContentMatches = contentId == 0
                                     || arrival.Snapshot.LeaderContentId == contentId;

        return arrival.Cycle.RequestSerial == requestSerial
               && arrival.Cycle.Owner == PartyDetailRequestOwner.Scanner
               && arrival.Cycle.ListingId == listingId
               && arrival.Snapshot.ListingId == listingId
               && requestContentMatches
               && snapshotContentMatches;
    }

    internal bool TryGetLatestDebugSnapshot(out PartyDetailLatestDebugSnapshot snapshot) {
        lock (_gate) {
            if (!_hasLatestDebugSnapshot) {
                snapshot = default;
                return false;
            }

            snapshot = new PartyDetailLatestDebugSnapshot(
                _latestDebugSnapshot.Generation,
                _latestDebugSnapshot.Cycle,
                CloneDebugSnapshot(_latestDebugSnapshot.Snapshot)
            );
            return true;
        }
    }

    internal static UploadablePartyDetail CloneSnapshot(UploadablePartyDetail snapshot) {
        return new UploadablePartyDetail {
            ListingId = snapshot.ListingId,
            LeaderContentId = snapshot.LeaderContentId,
            LeaderName = snapshot.LeaderName,
            HomeWorld = snapshot.HomeWorld,
            CreatedWorld = snapshot.CreatedWorld,
            LastServerRestart = snapshot.LastServerRestart,
            ObservedAtUnixMs = snapshot.ObservedAtUnixMs,
            MemberContentIds = snapshot.MemberContentIds is { } memberContentIds ? new List<ulong>(memberContentIds) : new List<ulong>(),
            MemberJobs = snapshot.MemberJobs is { } memberJobs ? new List<byte>(memberJobs) : new List<byte>(),
            SlotFlags = snapshot.SlotFlags is { } slotFlags ? new List<string>(slotFlags) : new List<string>(),
        };
    }

    private static PartyDetailDebugSnapshot CloneDebugSnapshot(PartyDetailDebugSnapshot snapshot) {
        return new PartyDetailDebugSnapshot {
            CapturedAtUtc = snapshot.CapturedAtUtc,
            RawListingId = snapshot.RawListingId,
            UploadListingId = snapshot.UploadListingId,
            LeaderAccountId = snapshot.LeaderAccountId,
            LeaderContentId = snapshot.LeaderContentId,
            LeaderName = snapshot.LeaderName,
            Comment = snapshot.Comment,
            Category = snapshot.Category,
            DutyId = snapshot.DutyId,
            World = snapshot.World,
            HomeWorld = snapshot.HomeWorld,
            CurrentWorld = snapshot.CurrentWorld,
            ObjectiveRaw = snapshot.ObjectiveRaw,
            ObjectiveName = snapshot.ObjectiveName,
            BeginnerFriendly = snapshot.BeginnerFriendly,
            CompletionStatusRaw = snapshot.CompletionStatusRaw,
            CompletionStatusName = snapshot.CompletionStatusName,
            DutyFinderSettingRaw = snapshot.DutyFinderSettingRaw,
            DutyFinderSettingName = snapshot.DutyFinderSettingName,
            LootRuleRaw = snapshot.LootRuleRaw,
            LootRuleName = snapshot.LootRuleName,
            LastPatchHotfixTimestamp = snapshot.LastPatchHotfixTimestamp,
            TimeLeft = snapshot.TimeLeft,
            AvgItemLevel = snapshot.AvgItemLevel,
            LeaderClientLanguageRaw = snapshot.LeaderClientLanguageRaw,
            LanguageFlagsRaw = snapshot.LanguageFlagsRaw,
            TotalSlots = snapshot.TotalSlots,
            SlotsFilled = snapshot.SlotsFilled,
            JoinConditionRaw = snapshot.JoinConditionRaw,
            JoinConditionName = snapshot.JoinConditionName,
            IsAlliance = snapshot.IsAlliance,
            NumberOfParties = snapshot.NumberOfParties,
            NonZeroMemberCount = snapshot.NonZeroMemberCount,
            Members = snapshot.Members is { } members
                ? new List<PartyDetailDebugMember>(members)
                : Array.Empty<PartyDetailDebugMember>(),
        };
    }

    private bool TryGetCurrentCycle(out PartyDetailRequestCycle cycle) {
        PruneExpiredManualRequestsLocked(_utcNow());
        if (_nextRequestSerial <= 0) {
            cycle = default;
            return false;
        }

        return _pendingRequests.TryGetValue(_nextRequestSerial, out cycle);
    }

    private int PruneExpiredManualRequestsLocked(DateTime nowUtc) {
        if (_pendingRequests.Count == 0) {
            return 0;
        }

        List<long> expiredSerials = null;
        foreach (var (serial, cycle) in _pendingRequests) {
            if (cycle.Owner != PartyDetailRequestOwner.Manual) {
                continue;
            }

            if (_pendingRequestCreatedAtUtc.TryGetValue(serial, out var createdAtUtc)
                && nowUtc - createdAtUtc < StaleManualRequestTtl) {
                continue;
            }

            expiredSerials ??= new List<long>();
            expiredSerials.Add(serial);
        }

        if (expiredSerials is null) {
            return 0;
        }

        foreach (var serial in expiredSerials) {
            _pendingRequests.Remove(serial);
            _pendingRequestCreatedAtUtc.Remove(serial);
        }

        return expiredSerials.Count;
    }

    private sealed record PartyDetailArrival(long Generation, PartyDetailRequestCycle Cycle, UploadablePartyDetail Snapshot);
}
