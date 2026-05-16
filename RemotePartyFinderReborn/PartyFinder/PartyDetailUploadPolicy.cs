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

    internal static int NormalizeRetryCount(int configuredRetries) {
        return Math.Max(0, configuredRetries);
    }

    internal static bool ShouldDropPendingDetail(int currentRetryCount, int configuredRetries) {
        return currentRetryCount >= NormalizeRetryCount(configuredRetries);
    }

    internal static bool IsSnapshotReadyForEnqueue(UploadablePartyDetail payload) {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.ListingId == 0 || payload.LeaderContentId == 0) {
            return false;
        }

        if (payload.HomeWorld == 0 || payload.HomeWorld >= 1000) {
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

    internal static ulong ComputeFingerprint(
        List<ulong> memberContentIds,
        List<byte> memberJobs,
        List<string> slotFlags
    ) {
        unchecked {
            var hash = 1469598103934665603UL;
            hash = MixFnv(hash, (ulong)memberContentIds.Count);
            for (var i = 0; i < memberContentIds.Count; i++) {
                hash = MixFnv(hash, memberContentIds[i]);
                hash = MixFnv(hash, memberJobs[i]);
            }

            foreach (var slotFlag in slotFlags) {
                foreach (var c in slotFlag) {
                    hash = MixFnv(hash, c);
                }
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
