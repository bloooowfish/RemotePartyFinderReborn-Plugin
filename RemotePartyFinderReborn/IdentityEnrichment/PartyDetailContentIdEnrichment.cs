using System;
using System.Collections.Generic;

#nullable enable

namespace RemotePartyFinderReborn;

internal static class PartyDetailContentIdEnrichment {
    internal static bool TryEnqueueForResolve(
        UploadablePartyDetail payload,
        Action<IEnumerable<ulong>> enqueueMany,
        Action<string>? warningSink = null
    ) {
        try {
            enqueueMany(BuildContentIdsForResolve(payload));
            return true;
        } catch (Exception exception) {
            warningSink?.Invoke($"PartyDetailContentIdEnrichment: failed to enqueue detail contentIds for enrichment. {exception.Message}");
            return false;
        }
    }

    internal static IReadOnlyList<ulong> BuildContentIdsForResolve(UploadablePartyDetail payload) {
        var contentIds = new List<ulong>(payload.MemberContentIds.Count + 1);
        var seen = new HashSet<ulong>();

        if (payload.LeaderContentId != 0 && seen.Add(payload.LeaderContentId)) {
            contentIds.Add(payload.LeaderContentId);
        }

        foreach (var memberContentId in payload.MemberContentIds) {
            if (memberContentId == 0 || !seen.Add(memberContentId)) {
                continue;
            }

            contentIds.Add(memberContentId);
        }

        return contentIds;
    }
}
