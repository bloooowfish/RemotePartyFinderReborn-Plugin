using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

#nullable enable

namespace RemotePartyFinderReborn;

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class UploadablePartyDetailBatchItem {
    public uint ListingId { get; set; }
    public ulong LeaderContentId { get; set; }
    public string LeaderName { get; set; } = string.Empty;
    public ushort HomeWorld { get; set; }
    public List<ulong> MemberContentIds { get; set; } = new();
    public List<byte> MemberJobs { get; set; } = new();
    public List<string> SlotFlags { get; set; } = new();
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? CapabilityToken { get; set; }

    internal static UploadablePartyDetailBatchItem FromDetail(
        UploadablePartyDetail detail,
        string? capabilityToken
    ) {
        return new UploadablePartyDetailBatchItem {
            ListingId = detail.ListingId,
            LeaderContentId = detail.LeaderContentId,
            LeaderName = detail.LeaderName,
            HomeWorld = detail.HomeWorld,
            MemberContentIds = detail.MemberContentIds,
            MemberJobs = detail.MemberJobs,
            SlotFlags = detail.SlotFlags,
            CapabilityToken = string.IsNullOrWhiteSpace(capabilityToken) ? null : capabilityToken,
        };
    }
}

internal readonly record struct DetailUploadQueueDebugState(
    int PendingCount,
    int ManualPendingCount,
    int ScannerPendingCount,
    int DueCount,
    int EligibleNowCount,
    int CapabilityDeferredCount,
    int CircuitBlockedCount,
    int InFlightCount,
    int ActiveWorkerCount,
    int EnabledTargetCount,
    int OpenCircuitTargetCount
);

internal readonly record struct DetailUploadRetryTriggerResult(
    int TriggeredCount,
    DetailUploadQueueDebugState QueueState
);
