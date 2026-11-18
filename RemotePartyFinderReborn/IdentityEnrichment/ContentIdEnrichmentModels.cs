#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace RemotePartyFinderReborn;

internal enum ResolveState {
    Unknown,
    Queued,
    InFlight,
    Resolved,
    FailedTransient,
    FailedPermanent,
}

internal sealed record CharacterIdentitySnapshot(
    ulong ContentId,
    string Name,
    ushort HomeWorld,
    string WorldName,
    DateTime LastResolvedAtUtc
);

internal sealed record PartialCharacterIdentitySnapshot(
    ulong ContentId,
    string? Name,
    ushort? HomeWorld,
    string? WorldName,
    DateTime LastUpdatedAtUtc
) {
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name)
        && HomeWorld is > 0
        && !string.IsNullOrWhiteSpace(WorldName);
}

internal sealed record ResolverPreflightResult(bool Enabled, string Reason);

internal sealed record CharacterIdentityUploadPayload(
    [property: JsonPropertyName("content_id")]
    ulong ContentId,
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("home_world")]
    ushort HomeWorld,
    [property: JsonPropertyName("world_name")]
    string WorldName,
    [property: JsonPropertyName("source")]
    string Source,
    [property: JsonPropertyName("observed_at")]
    DateTime ObservedAtUtc
) {
    public static CharacterIdentityUploadPayload FromSnapshot(
        CharacterIdentitySnapshot snapshot,
        DateTime observedAtUtc,
        string source = "chara_card"
    ) {
        var normalizedObservedAtUtc = observedAtUtc.Kind switch {
            DateTimeKind.Utc => observedAtUtc,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(observedAtUtc, DateTimeKind.Utc),
            _ => observedAtUtc.ToUniversalTime(),
        };

        return new CharacterIdentityUploadPayload(
            snapshot.ContentId,
            snapshot.Name,
            snapshot.HomeWorld,
            snapshot.WorldName ?? string.Empty,
            source,
            normalizedObservedAtUtc
        );
    }
}

internal sealed record ResolveRequestStatus(
    ulong ContentId,
    ResolveState State,
    int FailureCount,
    int AttemptVersion,
    DateTime LastRequestedAtUtc,
    DateTime LastResolvedAtUtc,
    DateTime NextEligibleAttemptAtUtc
);

internal sealed class ContentIdResolveQueue {
    internal const int MaxTrackedRequests = 2048;

    private static readonly TimeSpan[] TimeoutBackoffSchedule = [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly Dictionary<ulong, ResolveRequestStatus> _requests = new();
    private readonly TimeSpan _freshIdentityTtl;
    private readonly int _maxAttempts;
    private readonly int _maxTrackedRequests;

    public ContentIdResolveQueue(
        TimeSpan? freshIdentityTtl = null,
        int maxAttempts = 2,
        int maxTrackedRequests = MaxTrackedRequests
    ) {
        _freshIdentityTtl = freshIdentityTtl ?? TimeSpan.FromHours(24);
        _maxAttempts = Math.Max(1, maxAttempts);
        _maxTrackedRequests = Math.Max(1, maxTrackedRequests);
    }

    public IReadOnlyCollection<ResolveRequestStatus> Requests =>
        new ReadOnlyCollection<ResolveRequestStatus>(_requests.Values.OrderBy(static request => request.ContentId).ToArray());

    public List<ResolveRequestStatus> FindExpiredInFlightRequests(
        DateTime nowUtc,
        TimeSpan timeout,
        Func<ulong, bool>? hasPendingProjection = null
    ) {
        List<ResolveRequestStatus>? expired = null;
        foreach (var request in _requests.Values) {
            if (request.State != ResolveState.InFlight
                || request.LastRequestedAtUtc == DateTime.MinValue
                || nowUtc - request.LastRequestedAtUtc < timeout
                || hasPendingProjection?.Invoke(request.ContentId) == true) {
                continue;
            }

            (expired ??= new List<ResolveRequestStatus>()).Add(request);
        }

        return expired ?? [];
    }

    public bool Enqueue(ulong contentId, DateTime nowUtc) {
        if (contentId == 0) {
            return false;
        }

        if (!_requests.TryGetValue(contentId, out var existing)) {
            _requests[contentId] = new ResolveRequestStatus(
                contentId,
                ResolveState.Queued,
                0,
                0,
                DateTime.MinValue,
                DateTime.MinValue,
                nowUtc
            );
            PruneTerminalEntriesIfNeeded(nowUtc, preserveContentId: contentId);
            return true;
        }

        if (existing.State is ResolveState.Queued or ResolveState.InFlight or ResolveState.FailedPermanent) {
            return false;
        }

        if (existing.State == ResolveState.Resolved
            && nowUtc - existing.LastResolvedAtUtc < _freshIdentityTtl) {
            return false;
        }

        if (existing.State == ResolveState.FailedTransient
            && nowUtc < existing.NextEligibleAttemptAtUtc) {
            return false;
        }

        _requests[contentId] = existing with {
            State = ResolveState.Queued,
            NextEligibleAttemptAtUtc = nowUtc,
            FailureCount = existing.State == ResolveState.Resolved ? 0 : existing.FailureCount,
        };
        PruneTerminalEntriesIfNeeded(nowUtc, preserveContentId: contentId);
        return true;
    }

    public ResolveState GetState(ulong contentId) {
        return _requests.TryGetValue(contentId, out var request)
            ? request.State
            : ResolveState.Unknown;
    }

    public ResolveRequestStatus GetRequest(ulong contentId) {
        return _requests[contentId];
    }

    public bool TryGetRequest(ulong contentId, out ResolveRequestStatus request) {
        return _requests.TryGetValue(contentId, out request!);
    }

    public bool TryGetInFlightRequest(out ResolveRequestStatus request) {
        foreach (var candidate in _requests.Values
                     .Where(static candidate => candidate.State == ResolveState.InFlight)
                     .OrderBy(static candidate => candidate.LastRequestedAtUtc)
                     .ThenBy(static candidate => candidate.ContentId)) {
            request = candidate;
            return true;
        }

        request = default!;
        return false;
    }

    public bool TryStartNext(DateTime nowUtc, out ResolveRequestStatus request) {
        // Enforce single-flight so concurrent CharaCard requests cannot race the single client UI/packet state.
        if (_requests.Values.Any(static candidate => candidate.State == ResolveState.InFlight)) {
            request = default!;
            return false;
        }

        foreach (var candidate in _requests.Values
                     .Where(candidate => candidate.State == ResolveState.Queued
                                         || (candidate.State == ResolveState.FailedTransient
                                             && candidate.NextEligibleAttemptAtUtc <= nowUtc))
                     .OrderBy(candidate => candidate.NextEligibleAttemptAtUtc)
                     .ThenBy(candidate => candidate.ContentId)) {
            request = candidate with {
                State = ResolveState.InFlight,
                AttemptVersion = candidate.AttemptVersion + 1,
                LastRequestedAtUtc = nowUtc,
            };
            _requests[candidate.ContentId] = request;
            return true;
        }

        request = default!;
        return false;
    }

    public bool MarkTimeout(ulong contentId, int attemptVersion, DateTime nowUtc) {
        return MarkFailure(contentId, attemptVersion, nowUtc);
    }

    public bool MarkLocalFailure(ulong contentId, int attemptVersion, DateTime nowUtc, TimeSpan? retryDelay = null) {
        return MarkFailure(contentId, attemptVersion, nowUtc, retryDelay);
    }

    private bool MarkFailure(ulong contentId, int attemptVersion, DateTime nowUtc, TimeSpan? retryDelay = null) {
        if (!_requests.TryGetValue(contentId, out var request)) {
            return false;
        }

        if (request.State != ResolveState.InFlight || request.AttemptVersion != attemptVersion) {
            return false;
        }

        var failureCount = request.FailureCount + 1;
        var nextState = failureCount >= _maxAttempts
            ? ResolveState.FailedPermanent
            : ResolveState.FailedTransient;
        _requests[contentId] = request with {
            State = nextState,
            FailureCount = failureCount,
            NextEligibleAttemptAtUtc = nextState == ResolveState.FailedPermanent
                ? DateTime.MaxValue
                : nowUtc + (retryDelay ?? GetTimeoutBackoff(failureCount)),
        };
        if (nextState == ResolveState.FailedPermanent) {
            PruneTerminalEntriesIfNeeded(nowUtc, preserveContentId: contentId);
        }
        return true;
    }

    public void MarkResolved(CharacterIdentitySnapshot snapshot) {
        if (_requests.TryGetValue(snapshot.ContentId, out var existing)) {
            _requests[snapshot.ContentId] = existing with {
                State = ResolveState.Resolved,
                FailureCount = 0,
                LastResolvedAtUtc = snapshot.LastResolvedAtUtc,
                NextEligibleAttemptAtUtc = snapshot.LastResolvedAtUtc,
            };
            PruneTerminalEntriesIfNeeded(snapshot.LastResolvedAtUtc, preserveContentId: snapshot.ContentId);
            return;
        }

        _requests[snapshot.ContentId] = new ResolveRequestStatus(
            snapshot.ContentId,
            ResolveState.Resolved,
            0,
            0,
            DateTime.MinValue,
            snapshot.LastResolvedAtUtc,
            snapshot.LastResolvedAtUtc
        );
        PruneTerminalEntriesIfNeeded(snapshot.LastResolvedAtUtc, preserveContentId: snapshot.ContentId);
    }

    private void PruneTerminalEntriesIfNeeded(DateTime nowUtc, ulong preserveContentId) {
        if (_requests.Count <= _maxTrackedRequests) {
            return;
        }

        List<ulong>? expiredKeys = null;
        foreach (var (contentId, request) in _requests) {
            if (contentId == preserveContentId) {
                continue;
            }

            if (request.State == ResolveState.Resolved
                && request.LastResolvedAtUtc != DateTime.MinValue
                && nowUtc - request.LastResolvedAtUtc >= _freshIdentityTtl) {
                (expiredKeys ??= []).Add(contentId);
            } else if (request.State == ResolveState.FailedPermanent
                       && request.LastRequestedAtUtc != DateTime.MinValue
                       && nowUtc - request.LastRequestedAtUtc >= _freshIdentityTtl) {
                (expiredKeys ??= []).Add(contentId);
            }
        }

        if (expiredKeys is not null) {
            foreach (var expiredKey in expiredKeys) {
                _requests.Remove(expiredKey);
            }
        }

        if (_requests.Count <= _maxTrackedRequests) {
            return;
        }

        var excess = _requests.Count - _maxTrackedRequests;
        var evictionCandidates = _requests.Values
            .Where(request => request.ContentId != preserveContentId
                              && request.State is ResolveState.Resolved or ResolveState.FailedPermanent)
            .OrderBy(static request => request.LastResolvedAtUtc >= request.LastRequestedAtUtc
                ? request.LastResolvedAtUtc
                : request.LastRequestedAtUtc)
            .ThenBy(static request => request.ContentId)
            .Take(excess)
            .Select(static request => request.ContentId)
            .ToArray();

        foreach (var evictionKey in evictionCandidates) {
            _requests.Remove(evictionKey);
        }
    }

    private static TimeSpan GetTimeoutBackoff(int timeoutCount) {
        if (timeoutCount <= 0) {
            return TimeoutBackoffSchedule[0];
        }

        var index = Math.Min(timeoutCount - 1, TimeoutBackoffSchedule.Length - 1);
        return TimeoutBackoffSchedule[index];
    }
}

internal static class ResolverPreflightEvaluator {
    public static ResolverPreflightResult Evaluate(
        nint requestCharaCardAddress,
        nint handleCurrentCharaCardDataPacketAddress,
        nint openCharaCardForPacketAddress
    ) {
        if (requestCharaCardAddress == 0) {
            return new ResolverPreflightResult(false, "RequestCharaCardForContentId interop address is unavailable.");
        }

        if (handleCurrentCharaCardDataPacketAddress == 0) {
            return new ResolverPreflightResult(false, "HandleCurrentCharaCardDataPacket interop address is unavailable.");
        }

        if (openCharaCardForPacketAddress == 0) {
            return new ResolverPreflightResult(false, "OpenCharaCardForPacket interop address is unavailable.");
        }

        return new ResolverPreflightResult(true, "Ready");
    }
}
