using System;
using System.Collections.Generic;

namespace RemotePartyFinderReborn;

internal enum PartyDetailRequestOwner {
    Manual,
    Scanner,
}

internal readonly record struct PartyDetailRequestCycle(
    long RequestSerial,
    PartyDetailRequestOwner Owner,
    ulong ListingId,
    ulong ContentId
);

internal readonly record struct PartyDetailPendingArrival(
    long Generation,
    PartyDetailRequestCycle Cycle,
    UploadablePartyDetail Payload
);

internal readonly record struct PartyDetailDebugMember(
    int SlotIndex,
    ulong ContentId,
    byte Job,
    string SlotFlag
);

internal sealed class PartyDetailDebugSnapshot {
    public DateTime CapturedAtUtc { get; init; }
    public ulong RawListingId { get; init; }
    public uint UploadListingId { get; init; }
    public ulong LeaderAccountId { get; init; }
    public ulong LeaderContentId { get; init; }
    public string LeaderName { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public uint Category { get; init; }
    public ushort DutyId { get; init; }
    public ushort World { get; init; }
    public ushort HomeWorld { get; init; }
    public ushort CurrentWorld { get; init; }
    public byte ObjectiveRaw { get; init; }
    public string ObjectiveName { get; init; } = string.Empty;
    public byte BeginnerFriendly { get; init; }
    public byte CompletionStatusRaw { get; init; }
    public string CompletionStatusName { get; init; } = string.Empty;
    public byte DutyFinderSettingRaw { get; init; }
    public string DutyFinderSettingName { get; init; } = string.Empty;
    public byte LootRuleRaw { get; init; }
    public string LootRuleName { get; init; } = string.Empty;
    public uint LastPatchHotfixTimestamp { get; init; }
    public uint TimeLeft { get; init; }
    public ushort AvgItemLevel { get; init; }
    public byte LeaderClientLanguageRaw { get; init; }
    public byte LanguageFlagsRaw { get; init; }
    public byte TotalSlots { get; init; }
    public byte SlotsFilled { get; init; }
    public byte JoinConditionRaw { get; init; }
    public string JoinConditionName { get; init; } = string.Empty;
    public bool IsAlliance { get; init; }
    public byte NumberOfParties { get; init; }
    public int NonZeroMemberCount { get; init; }
    public IReadOnlyList<PartyDetailDebugMember> Members { get; init; } = Array.Empty<PartyDetailDebugMember>();
}

internal readonly record struct PartyDetailLatestDebugSnapshot(
    long Generation,
    PartyDetailRequestCycle Cycle,
    PartyDetailDebugSnapshot Snapshot
);
