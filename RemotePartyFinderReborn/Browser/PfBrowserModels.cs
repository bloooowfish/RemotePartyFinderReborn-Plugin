#nullable enable

using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.PartyFinder.Types;

namespace RemotePartyFinderReborn;

internal enum PfSearchAreaTab {
    All,
    World,
    Private,
}

internal enum PfListViewMode {
    Standard,
    Compact,
}

internal enum PfSortMode {
    NativeDefault,
    TimeRemaining,
    SlotsFilled,
    ItemLevel,
}

public enum PfBlockedDisplayMode {
    Dim = 0,
    Hide = 1,
}

internal enum PfMemberEnrichmentState {
    None,
    Cached,
    Loading,
    Unresolved,
}

internal sealed record PfPhaseProgressItem(
    string PhaseName,
    int BestPulls,
    float? BestHpPercent,
    bool Cleared);

internal sealed record PfParseDisplay(
    byte? PrimaryPercentile,
    byte? SecondaryPercentile,
    bool HasSecondary,
    bool Hidden,
    bool OriginallyHidden,
    bool Estimated,
    uint ZoneId,
    int DifficultyId,
    bool LockedIn = true) {
    internal static PfParseDisplay Empty { get; } = new(null, null, false, false, false, false, 0, 0, true);
    internal bool HasVisiblePercentile => !Hidden && (PrimaryPercentile.HasValue || SecondaryPercentile.HasValue);
}

internal sealed record PfProgressDisplay(
    bool Cleared,
    ushort? ClearCount,
    float? BossPercentage,
    byte? BossPhase,
    IReadOnlyList<PfPhaseProgressItem> PhaseProgress,
    string SummaryLabel) {
    internal static PfProgressDisplay Empty { get; } = new(false, null, null, null, Array.Empty<PfPhaseProgressItem>(), string.Empty);
}

internal sealed record PfListingSlot(int SlotIndex, ulong AcceptingMask);

internal sealed record PfMemberRow(
    int SlotIndex,
    byte PartyIndex,
    ulong ContentId,
    byte JobId,
    ulong AcceptingMask,
    bool IsLeader,
    string Name,
    string HomeWorldName,
    ushort HomeWorldId,
    PfParseDisplay Parse,
    PfProgressDisplay Progress,
    PfMemberEnrichmentState EnrichmentState,
    bool IsPlayerEligible,
    bool IsBlocked = false);

internal sealed record PfNativeUiSnapshot(
    bool IsNativeAddonOpen,
    bool IsNativeAgentInitialized,
    byte SearchAreaTab,
    byte CategoryTab,
    IReadOnlyList<int> CategoryCounts,
    IReadOnlyList<int> AreaCounts,
    string CurrentPageText,
    string ResultsCountText,
    int CurrentPageIndex,
    bool HasPreviousPage,
    bool HasNextPage,
    IReadOnlyList<ulong> NativeOrderedListingIds,
    DateTime CapturedAtUtc,
    bool IsNativeAddonVisible = false) {
    internal static PfNativeUiSnapshot Empty { get; } = new(
        false,
        false,
        0,
        0,
        Array.Empty<int>(),
        Array.Empty<int>(),
        string.Empty,
        string.Empty,
        0,
        false,
        false,
        Array.Empty<ulong>(),
        DateTime.MinValue,
        false);

    internal bool HasServerData => IsNativeAddonOpen
        || (IsNativeAgentInitialized
            && (AreaCounts.Count == 3
                || CategoryCounts.Count > 0
                || !string.IsNullOrEmpty(ResultsCountText)
                || HasPreviousPage
                || HasNextPage));

    internal bool IsDetailedSearchActive => CategoryTab == 17;
}

internal sealed class PfListingEntry {
    public required ulong Id { get; init; }
    public ulong LeaderContentId { get; init; }
    public string LeaderName { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public ushort HomeWorldId { get; init; }
    public string HomeWorldName { get; init; } = string.Empty;
    public ushort CreatedWorldId { get; init; }
    public string CreatedWorldName { get; init; } = string.Empty;
    public string DataCenterName { get; init; } = string.Empty;
    public DutyCategory Category { get; init; }
    internal uint CategoryRaw {
        get => (uint)Category;
        init => Category = (DutyCategory)value;
    }
    public ushort RawDuty { get; init; }
    public ushort DutySortKey { get; init; }
    public string DutyName { get; init; } = string.Empty;
    public DutyType DutyType { get; init; }
    public bool IsHighEnd { get; init; }
    public bool BeginnersWelcome { get; init; }
    public ushort SecondsRemaining { get; init; }
    public ushort MinItemLevel { get; init; }
    public byte NumParties { get; init; } = 1;
    public byte SlotsAvailable { get; init; } = 8;
    public byte SlotsFilled { get; init; }
    public uint LastServerRestart { get; init; }
    public ObjectiveFlags Objective { get; init; }
    public ConditionFlags Conditions { get; init; }
    public DutyFinderSettingsFlags DutyFinderSettings { get; init; }
    public LootRuleFlags LootRules { get; init; }
    public SearchAreaFlags SearchArea { get; init; }
    internal uint SearchAreaRaw {
        get => (uint)SearchArea;
        init => SearchArea = (SearchAreaFlags)value;
    }
    public IReadOnlyList<PfListingSlot> Slots { get; init; } = Array.Empty<PfListingSlot>();
    public IReadOnlyList<byte> JobsPresent { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<ulong> MemberContentIds { get; init; } = Array.Empty<ulong>();
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public DateTime? LastDetailObservedUtc { get; init; }
    public bool IsSample { get; init; }
    public int LastBatchNumber { get; init; } = -1;
    public int PageGeneration { get; init; } = -1;
    public PfParseDisplay? LeaderParse { get; init; }
    public PfProgressDisplay? LeaderProgress { get; init; }
    public int CachedMemberCount { get; init; }
    public bool IsLeaderBlocked { get; init; }
    public bool IsDutyUnopened { get; init; }

    internal bool IsPrivate => (SearchArea & SearchAreaFlags.Private) == SearchAreaFlags.Private;

    internal bool IsWorldOnly => !IsPrivate
                                 && (SearchArea & SearchAreaFlags.World) == SearchAreaFlags.World
                                 && (SearchArea & SearchAreaFlags.DataCenter) == 0;

    internal PfSearchAreaTab SearchAreaTab => ResolveSearchAreaTab(SearchArea);

    internal bool MatchesAreaTab(PfSearchAreaTab tab) => tab switch {
        PfSearchAreaTab.Private => IsPrivate,
        PfSearchAreaTab.World => IsWorldOnly,
        _ => !IsPrivate,
    };

    internal bool HasMultipleParties => NumParties > 1 || SlotsAvailable > 8 || JobsPresent.Count > 8 || Slots.Count > 8;

    internal static PfSearchAreaTab ResolveSearchAreaTab(SearchAreaFlags flags) {
        if ((flags & SearchAreaFlags.Private) == SearchAreaFlags.Private) {
            return PfSearchAreaTab.Private;
        }

        if ((flags & SearchAreaFlags.World) == SearchAreaFlags.World
            && (flags & SearchAreaFlags.DataCenter) == 0) {
            return PfSearchAreaTab.World;
        }

        return PfSearchAreaTab.All;
    }

    internal static string FormatRemainingTime(ushort secondsRemaining) {
        if (secondsRemaining <= 0) {
            return "<1m";
        }

        var minutes = (secondsRemaining + 59) / 60;
        if (minutes >= 60) {
            return $"{minutes / 60}h{minutes % 60:00}m";
        }

        return $"{minutes}m";
    }
}

internal sealed class PfFilterState {
    public PfSearchAreaTab AreaTab { get; set; } = PfSearchAreaTab.All;
    public string SelectedDataCenter { get; set; } = string.Empty;
    public DutyCategory? SelectedCategory { get; set; }
    public bool OtherCategorySelected { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public bool HighEndOnly { get; set; }
    public bool MyJobOnly { get; set; }
    public PfSortMode SortMode { get; set; } = PfSortMode.NativeDefault;
    public PfListViewMode ViewMode { get; set; } = PfListViewMode.Standard;
    public PfBlockedDisplayMode BlockedDisplayMode { get; set; } = PfBlockedDisplayMode.Dim;
    public bool IncludeSampleListings { get; set; } = false;
    public bool UseSampleEnrichment { get; set; } = false;

    internal bool MatchesCategory(DutyCategory category) {
        if (OtherCategorySelected) {
            return category == DutyCategory.None;
        }

        if (!SelectedCategory.HasValue) {
            return true;
        }

        return (category & SelectedCategory.Value) == SelectedCategory.Value;
    }

    internal void SelectAllCategories() {
        SelectedCategory = null;
        OtherCategorySelected = false;
    }

    internal void SelectCategory(DutyCategory category) {
        SelectedCategory = category;
        OtherCategorySelected = false;
    }

    internal void SelectCategoryRaw(uint rawCategory) {
        SelectedCategory = (DutyCategory)rawCategory;
        OtherCategorySelected = false;
    }

    internal void SelectOtherCategory() {
        SelectedCategory = null;
        OtherCategorySelected = true;
    }
}
