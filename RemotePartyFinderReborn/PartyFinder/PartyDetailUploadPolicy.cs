using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace RemotePartyFinderReborn;

internal static class PartyDetailUploadPolicy {
    internal static int NormalizeDetailUploadTimeoutMs(int configuredMs) {
        return Math.Clamp(configuredMs <= 0 ? 5000 : configuredMs, 1000, 30000);
    }

    internal static int NormalizeDetailUploadConcurrency(int configured) {
        return Math.Clamp(configured <= 0 ? 1 : configured, 1, 4);
    }

    internal static int NormalizeDetailUploadBatchSize(int configured) {
        return Math.Clamp(configured <= 0 ? 1 : configured, 1, 16);
    }

    internal static DateTime ComputeNextAttemptAtUtc(
        DateTime nowUtc,
        int attemptCount,
        int? retryAfterSeconds,
        Func<int, int, int> nextJitterMs
    ) {
        if (retryAfterSeconds.HasValue) {
            var boundedSeconds = Math.Clamp(retryAfterSeconds.Value, 0, 30);
            return nowUtc.AddSeconds(boundedSeconds).AddMilliseconds(nextJitterMs(50, 250));
        }

        return nowUtc.AddMilliseconds(ComputeRetryDelayMs(attemptCount, nextJitterMs));
    }

    internal static DateTime ComputeDeferredNextAttemptAtUtc(
        DateTime nowUtc,
        int deferCount,
        Func<int, int, int> nextJitterMs
    ) {
        // First deferral waits 4000ms (>= Gatherer.UploadInterval of 3000ms) so out-of-order listing creation can land first.
        return nowUtc.AddMilliseconds(ComputeRetryDelayMs(Math.Max(1, deferCount) + 1, nextJitterMs));
    }

    internal static DateTime ComputeCircuitBlockedNextAttemptAtUtc(
        DateTime nowUtc,
        TimeSpan? minRemainingBreakDuration,
        Func<int, int, int> nextJitterMs
    ) {
        TimeSpan baseDelay;
        if (minRemainingBreakDuration is { } remaining) {
            baseDelay = remaining > TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1000.0, 30_000.0))
                : TimeSpan.FromSeconds(2);
        } else {
            baseDelay = TimeSpan.FromSeconds(30);
        }

        return nowUtc.Add(baseDelay).AddMilliseconds(nextJitterMs(50, 250));
    }

    internal static int NormalizeRetryCount(int configuredRetries) {
        return Math.Max(0, configuredRetries);
    }

    internal static int NormalizeDeferredRetryCap(int configuredRetries) {
        return Math.Max(2, NormalizeRetryCount(configuredRetries) + 1);
    }

    internal static bool ShouldDropPendingDetail(int currentRetryCount, int configuredRetries) {
        return currentRetryCount >= NormalizeRetryCount(configuredRetries);
    }

    internal static bool ShouldDropDeferredPendingDetail(int currentDeferCount, int configuredRetries) {
        return currentDeferCount >= NormalizeDeferredRetryCap(configuredRetries);
    }

    internal static bool IsSnapshotReadyForEnqueue(UploadablePartyDetail payload) {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.ListingId == 0 || payload.LeaderContentId == 0) {
            return false;
        }

        if (payload.HomeWorld == 0 || payload.HomeWorld >= 1000) {
            return false;
        }

        if (payload.CreatedWorld == 0 || payload.CreatedWorld >= 1000) {
            return false;
        }

        if (payload.MemberContentIds is null || payload.MemberJobs is null || payload.SlotFlags is null) {
            return false;
        }

        if (payload.MemberContentIds.Count == 0
            || payload.MemberJobs.Count != payload.MemberContentIds.Count
            || payload.SlotFlags.Count != payload.MemberContentIds.Count) {
            return false;
        }

        return payload.MemberContentIds.Any(static memberContentId => memberContentId != 0);
    }

    internal static ulong ComputeFingerprint(UploadablePartyDetail payload) {
        ArgumentNullException.ThrowIfNull(payload);

        return ComputeFingerprint(
            payload.LeaderContentId,
            payload.HomeWorld,
            payload.CreatedWorld,
            payload.LeaderName,
            payload.MemberContentIds,
            payload.MemberJobs,
            payload.SlotFlags
        );
    }

    internal static ulong ComputeFingerprint(
        ulong leaderContentId,
        ushort homeWorld,
        ushort createdWorld,
        string? leaderName,
        IReadOnlyList<ulong> memberContentIds,
        IReadOnlyList<byte> memberJobs,
        IReadOnlyList<string> slotFlags
    ) {
        unchecked {
            var hash = 1469598103934665603UL;
            hash = MixFnv(hash, leaderContentId);
            hash = MixFnv(hash, homeWorld);
            hash = MixFnv(hash, createdWorld);
            if (!string.IsNullOrEmpty(leaderName)) {
                foreach (var c in leaderName) {
                    hash = MixFnv(hash, c);
                }
            }
            hash = MixFnv(hash, 0xFFUL);

            hash = MixFnv(hash, (ulong)memberContentIds.Count);
            for (var i = 0; i < memberContentIds.Count; i++) {
                hash = MixFnv(hash, memberContentIds[i]);
                hash = MixFnv(hash, i < memberJobs.Count ? memberJobs[i] : 0UL);
            }

            foreach (var slotFlag in slotFlags) {
                if (slotFlag is not null) {
                    foreach (var c in slotFlag) {
                        hash = MixFnv(hash, c);
                    }
                }
                hash = MixFnv(hash, 0xFEUL);
            }

            return hash;
        }
    }

    private static int ComputeRetryDelayMs(int attemptCount, Func<int, int, int> nextJitterMs) {
        var exponent = Math.Min(attemptCount, 5);
        var baseDelay = 1000 * (1 << exponent);
        var boundedDelay = Math.Min(baseDelay, 15000);
        return boundedDelay + nextJitterMs(50, 250);
    }

    private static ulong MixFnv(ulong hash, ulong value) {
        unchecked {
            hash ^= value;
            hash *= 1099511628211UL;
            return hash;
        }
    }
}
