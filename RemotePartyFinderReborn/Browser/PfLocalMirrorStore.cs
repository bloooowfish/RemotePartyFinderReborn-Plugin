#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RemotePartyFinderReborn;

/// <summary>
/// Thread-safe local mirror of retail PF listings paired with observed party details,
/// local identity cache lookups, and deterministic Phase 1 debug sample data.
/// </summary>
internal sealed class PfLocalMirrorStore : IDisposable {
    internal static readonly TimeSpan DefaultListingTtl = TimeSpan.FromMinutes(10);

    private readonly Plugin? _plugin;
    private readonly PfIconCatalog _icons;
    private readonly PlayerLocalDatabase? _playerDatabase;
    private readonly PfEnrichmentClient? _enrichmentClient;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    private static readonly TimeSpan ZeroSegmentGroupingWindow = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ImplicitNewPageGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EmptyPageConfirmDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan NativeSnapshotPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BlacklistPollInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<ulong, PfListingEntry> _liveListings = new();
    private readonly Dictionary<ulong, UploadablePartyDetail> _liveDetails = new();
    private readonly List<PfListingEntry> _sampleListings;
    private readonly Dictionary<ulong, IReadOnlyList<ulong>> _sampleMemberContentIds;
    private readonly HashSet<ulong> _currentPageListingIds = new();
    private HashSet<string> _blockedNameWorldKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _blockedBareNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _testBlockedKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<ulong> _testBlockedContentIds = new();
    private HashSet<ulong> _nativeUnopenedListingIds = new();
    private HashSet<ulong> _nativeBlockedListingIds = new();
    private HashSet<ulong> _testUnopenedListingIds = new();
    private int _latestBatchNumber = -1;
    private int _currentPageGeneration;
    private bool _inZeroSegment;
    private int _zeroSegmentEntryCount;
    private int _lastSegmentIndex = -1;
    private DateTime _lastReceiveUtc = DateTime.MinValue;
    private bool _pendingNewPage;
    private DateTime _pendingNewPageRequestedAtUtc = DateTime.MinValue;
    private DateTime _lastNativeSnapshotPollUtc = DateTime.MinValue;
    private DateTime _lastBlacklistPollUtc = DateTime.MinValue;
    private bool _nativeSnapshotDirty;
    private PfNativeUiSnapshot _nativeUiSnapshot = PfNativeUiSnapshot.Empty;
    private int _disposed;

    internal PfLocalMirrorStore(
        Plugin plugin,
        PfIconCatalog icons,
        PlayerLocalDatabase? playerDatabase,
        PfEnrichmentClient? enrichmentClient = null
    ) : this(icons, playerDatabase, static () => DateTime.UtcNow, enrichmentClient) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _plugin.PartyFinderGui.ReceiveListing += OnReceiveListing;
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroup", OnLfgAddonEvent);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroup", OnLfgAddonEvent);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnLfgAddonFinalize);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroupDetail", OnDetailAddonEvent);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroupDetail", OnDetailAddonEvent);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "BlackList", OnBlackListAddonEvent);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "BlackList", OnBlackListAddonEvent);
    }

    internal PfLocalMirrorStore(Func<DateTime>? utcNow = null)
        : this(new PfIconCatalog(), null, utcNow, null) {
    }

    internal PfLocalMirrorStore(
        PfIconCatalog icons,
        PlayerLocalDatabase? playerDatabase = null,
        Func<DateTime>? utcNow = null
    ) : this(icons, playerDatabase, utcNow, null) {
    }

    internal PfLocalMirrorStore(
        PfIconCatalog icons,
        PlayerLocalDatabase? playerDatabase,
        Func<DateTime>? utcNow,
        PfEnrichmentClient? enrichmentClient
    ) {
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _playerDatabase = playerDatabase;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _enrichmentClient = enrichmentClient;
        (_sampleListings, _sampleMemberContentIds) = CreateSampleDataset(_utcNow());
    }

    internal int LiveCount {
        get {
            lock (_gate) {
                return _liveListings.Count;
            }
        }
    }

    internal int LatestBatchNumber {
        get {
            lock (_gate) {
                return _latestBatchNumber;
            }
        }
    }

    internal int CurrentPageGeneration {
        get {
            lock (_gate) {
                return _currentPageGeneration;
            }
        }
    }

    internal int CurrentPageLiveCount {
        get {
            lock (_gate) {
                return _currentPageListingIds.Count;
            }
        }
    }

    internal void MarkPendingPageRefresh() {
        lock (_gate) {
            _pendingNewPage = true;
            _pendingNewPageRequestedAtUtc = _utcNow();
            _nativeSnapshotDirty = true;
        }
    }

    internal void RequestNativeSnapshotRefresh() {
        lock (_gate) {
            _nativeSnapshotDirty = true;
        }
    }

    internal PfNativeUiSnapshot GetNativeUiSnapshot() {
        lock (_gate) {
            return _nativeUiSnapshot;
        }
    }

    internal void SetNativeUiSnapshotForTesting(PfNativeUiSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) {
            _nativeUiSnapshot = snapshot;
        }
    }

    internal bool ReconcileEmptyPageIfConfirmed(DateTime nowUtc, PfNativeUiSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) {
            var isConfirmedPendingEmpty = _pendingNewPage
                                          && (nowUtc - _pendingNewPageRequestedAtUtc) >= EmptyPageConfirmDelay
                                          && snapshot.HasServerData
                                          && IsZeroResultsSnapshot(snapshot);
            var isConfirmedZeroNativeSearch = snapshot.HasServerData
                                              && _currentPageListingIds.Count > 0
                                              && (nowUtc - _lastReceiveUtc) >= EmptyPageConfirmDelay
                                              && (snapshot.ResultsCountText == "0(0)" || snapshot.ResultsCountText == "0");
            if (!isConfirmedPendingEmpty && !isConfirmedZeroNativeSearch) {
                return false;
            }

            _currentPageGeneration++;
            _currentPageListingIds.Clear();
            _inZeroSegment = false;
            _zeroSegmentEntryCount = 0;
            _pendingNewPage = false;
            return true;
        }
    }

    private static bool IsZeroResultsSnapshot(PfNativeUiSnapshot snapshot) {
        if (!string.IsNullOrWhiteSpace(snapshot.ResultsCountText)
            && (snapshot.ResultsCountText.StartsWith("0(", StringComparison.Ordinal)
                || snapshot.ResultsCountText == "0")) {
            return true;
        }

        if (snapshot.CategoryTab < snapshot.CategoryCounts.Count
            && snapshot.CategoryCounts[snapshot.CategoryTab] == 0) {
            return true;
        }

        if (snapshot.SearchAreaTab < snapshot.AreaCounts.Count
            && snapshot.AreaCounts[snapshot.SearchAreaTab] == 0) {
            return true;
        }

        return false;
    }

    internal void UpsertLiveListing(PfListingEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) {
            PruneExpiredLocked(entry.LastSeenUtc);

            var segmentIndex = entry.LastBatchNumber;
            var nowUtc = entry.LastSeenUtc;
            int resolvedPageGen;

            if (segmentIndex >= 0) {
                var isNewPage = _currentPageGeneration == 0 || _pendingNewPage;
                if (!isNewPage) {
                    if (_inZeroSegment) {
                        var stillSameZeroSegment = segmentIndex == 0
                            && _zeroSegmentEntryCount < 4
                            && (nowUtc - _lastReceiveUtc) <= ZeroSegmentGroupingWindow
                            && !_currentPageListingIds.Contains(entry.Id);
                        isNewPage = !stillSameZeroSegment;
                    } else if ((segmentIndex > 0 && segmentIndex < _lastSegmentIndex)
                               || (nowUtc - _lastReceiveUtc) > ImplicitNewPageGap) {
                        isNewPage = true;
                    }
                }

                if (isNewPage) {
                    _currentPageGeneration++;
                    _currentPageListingIds.Clear();
                    _inZeroSegment = false;
                    _zeroSegmentEntryCount = 0;
                    _pendingNewPage = false;
                    _nativeSnapshotDirty = true;
                }

                if (segmentIndex == 0) {
                    _inZeroSegment = true;
                    _zeroSegmentEntryCount++;
                    _nativeSnapshotDirty = true;
                }

                _lastSegmentIndex = segmentIndex;
                _lastReceiveUtc = nowUtc;
                _currentPageListingIds.Add(entry.Id);
                resolvedPageGen = _currentPageGeneration;
            } else if (entry.PageGeneration > 0) {
                resolvedPageGen = entry.PageGeneration;
                if (resolvedPageGen > _currentPageGeneration) {
                    _currentPageGeneration = resolvedPageGen;
                    _currentPageListingIds.Clear();
                }

                if (resolvedPageGen == _currentPageGeneration) {
                    _currentPageListingIds.Add(entry.Id);
                }
            } else {
                if (_currentPageGeneration <= 0) {
                    _currentPageGeneration = 1;
                }

                resolvedPageGen = _currentPageGeneration;
                _currentPageListingIds.Add(entry.Id);
            }

            if (_liveListings.TryGetValue(entry.Id, out var existing)) {
                entry = new PfListingEntry {
                    Id = entry.Id,
                    LeaderContentId = entry.LeaderContentId != 0 ? entry.LeaderContentId : existing.LeaderContentId,
                    LeaderName = !string.IsNullOrWhiteSpace(entry.LeaderName) ? entry.LeaderName : existing.LeaderName,
                    Comment = entry.Comment,
                    HomeWorldId = entry.HomeWorldId != 0 ? entry.HomeWorldId : existing.HomeWorldId,
                    HomeWorldName = !string.IsNullOrWhiteSpace(entry.HomeWorldName) ? entry.HomeWorldName : existing.HomeWorldName,
                    CreatedWorldId = entry.CreatedWorldId != 0 ? entry.CreatedWorldId : existing.CreatedWorldId,
                    CreatedWorldName = !string.IsNullOrWhiteSpace(entry.CreatedWorldName) ? entry.CreatedWorldName : existing.CreatedWorldName,
                    DataCenterName = !string.IsNullOrWhiteSpace(entry.DataCenterName) ? entry.DataCenterName : existing.DataCenterName,
                    Category = entry.Category,
                    RawDuty = entry.RawDuty,
                    DutySortKey = entry.DutySortKey != 0 ? entry.DutySortKey : existing.DutySortKey,
                    DutyName = entry.DutyName,
                    DutyType = entry.DutyType,
                    IsHighEnd = entry.IsHighEnd,
                    BeginnersWelcome = entry.BeginnersWelcome,
                    SecondsRemaining = entry.SecondsRemaining,
                    MinItemLevel = entry.MinItemLevel,
                    NumParties = entry.NumParties,
                    SlotsAvailable = entry.SlotsAvailable,
                    SlotsFilled = entry.SlotsFilled,
                    LastServerRestart = entry.LastServerRestart,
                    Objective = entry.Objective,
                    Conditions = entry.Conditions,
                    DutyFinderSettings = entry.DutyFinderSettings,
                    LootRules = entry.LootRules,
                    SearchArea = entry.SearchArea,
                    Slots = entry.Slots,
                    JobsPresent = entry.JobsPresent,
                    MemberContentIds = entry.MemberContentIds.Count > 0 ? entry.MemberContentIds : existing.MemberContentIds,
                    FirstSeenUtc = existing.FirstSeenUtc,
                    LastSeenUtc = entry.LastSeenUtc,
                    LastDetailObservedUtc = existing.LastDetailObservedUtc,
                    IsSample = false,
                    LastBatchNumber = entry.LastBatchNumber >= 0 ? entry.LastBatchNumber : existing.LastBatchNumber,
                    PageGeneration = resolvedPageGen,
                    IsLeaderBlocked = entry.IsLeaderBlocked,
                    IsDutyUnopened = entry.IsDutyUnopened,
                };
            } else {
                entry = new PfListingEntry {
                    Id = entry.Id,
                    LeaderContentId = entry.LeaderContentId,
                    LeaderName = entry.LeaderName,
                    Comment = entry.Comment,
                    HomeWorldId = entry.HomeWorldId,
                    HomeWorldName = entry.HomeWorldName,
                    CreatedWorldId = entry.CreatedWorldId,
                    CreatedWorldName = entry.CreatedWorldName,
                    DataCenterName = entry.DataCenterName,
                    Category = entry.Category,
                    RawDuty = entry.RawDuty,
                    DutySortKey = entry.DutySortKey,
                    DutyName = entry.DutyName,
                    DutyType = entry.DutyType,
                    IsHighEnd = entry.IsHighEnd,
                    BeginnersWelcome = entry.BeginnersWelcome,
                    SecondsRemaining = entry.SecondsRemaining,
                    MinItemLevel = entry.MinItemLevel,
                    NumParties = entry.NumParties,
                    SlotsAvailable = entry.SlotsAvailable,
                    SlotsFilled = entry.SlotsFilled,
                    LastServerRestart = entry.LastServerRestart,
                    Objective = entry.Objective,
                    Conditions = entry.Conditions,
                    DutyFinderSettings = entry.DutyFinderSettings,
                    LootRules = entry.LootRules,
                    SearchArea = entry.SearchArea,
                    Slots = entry.Slots,
                    JobsPresent = entry.JobsPresent,
                    MemberContentIds = entry.MemberContentIds,
                    FirstSeenUtc = entry.FirstSeenUtc,
                    LastSeenUtc = entry.LastSeenUtc,
                    LastDetailObservedUtc = entry.LastDetailObservedUtc,
                    IsSample = false,
                    LastBatchNumber = entry.LastBatchNumber,
                    PageGeneration = resolvedPageGen,
                    LeaderParse = entry.LeaderParse,
                    LeaderProgress = entry.LeaderProgress,
                    CachedMemberCount = entry.CachedMemberCount,
                    IsLeaderBlocked = entry.IsLeaderBlocked,
                    IsDutyUnopened = entry.IsDutyUnopened,
                };
            }

            if (entry.LastBatchNumber >= 0) {
                _latestBatchNumber = entry.LastBatchNumber;
            }

            _liveListings[entry.Id] = entry;
        }
    }

    internal void UpsertLiveDetail(UploadablePartyDetail detail) {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.ListingId == 0) {
            return;
        }

        lock (_gate) {
            _liveDetails[detail.ListingId] = detail;
            if (_liveListings.TryGetValue(detail.ListingId, out var existing)) {
                _liveListings[detail.ListingId] = new PfListingEntry {
                    Id = existing.Id,
                    LeaderContentId = detail.LeaderContentId != 0 ? detail.LeaderContentId : existing.LeaderContentId,
                    LeaderName = !string.IsNullOrWhiteSpace(detail.LeaderName) ? detail.LeaderName : existing.LeaderName,
                    Comment = existing.Comment,
                    HomeWorldId = detail.HomeWorld != 0 ? detail.HomeWorld : existing.HomeWorldId,
                    HomeWorldName = existing.HomeWorldName,
                    CreatedWorldId = detail.CreatedWorld != 0 ? detail.CreatedWorld : existing.CreatedWorldId,
                    CreatedWorldName = existing.CreatedWorldName,
                    DataCenterName = existing.DataCenterName,
                    Category = existing.Category,
                    RawDuty = existing.RawDuty,
                    DutySortKey = existing.DutySortKey,
                    DutyName = existing.DutyName,
                    DutyType = existing.DutyType,
                    IsHighEnd = existing.IsHighEnd,
                    BeginnersWelcome = existing.BeginnersWelcome,
                    SecondsRemaining = existing.SecondsRemaining,
                    MinItemLevel = existing.MinItemLevel,
                    NumParties = existing.NumParties,
                    SlotsAvailable = existing.SlotsAvailable,
                    SlotsFilled = existing.SlotsFilled,
                    LastServerRestart = existing.LastServerRestart,
                    Objective = existing.Objective,
                    Conditions = existing.Conditions,
                    DutyFinderSettings = existing.DutyFinderSettings,
                    LootRules = existing.LootRules,
                    SearchArea = existing.SearchArea,
                    Slots = existing.Slots,
                    JobsPresent = detail.MemberJobs.Count > 0 ? detail.MemberJobs.ToArray() : existing.JobsPresent,
                    MemberContentIds = detail.MemberContentIds.ToArray(),
                    FirstSeenUtc = existing.FirstSeenUtc,
                    LastSeenUtc = existing.LastSeenUtc,
                    LastDetailObservedUtc = _utcNow(),
                    IsSample = false,
                    LastBatchNumber = existing.LastBatchNumber,
                    PageGeneration = existing.PageGeneration,
                    IsLeaderBlocked = existing.IsLeaderBlocked,
                    IsDutyUnopened = existing.IsDutyUnopened,
                };
            }
        }
    }

    internal void SetUnopenedListingsForTesting(IEnumerable<ulong>? listingIds) {
        var ids = new HashSet<ulong>();
        if (listingIds != null) {
            foreach (var id in listingIds) {
                if (id != 0) {
                    ids.Add(id);
                }
            }
        }

        lock (_gate) {
            _testUnopenedListingIds = ids;
        }
    }

    private bool IsListingUnopened(ulong listingId) {
        if (listingId == 0) {
            return false;
        }

        lock (_gate) {
            return _testUnopenedListingIds.Contains(listingId)
                   || _nativeUnopenedListingIds.Contains(listingId);
        }
    }

    private bool IsListingNativeBlocked(ulong listingId) {
        if (listingId == 0) {
            return false;
        }

        lock (_gate) {
            return _nativeBlockedListingIds.Contains(listingId);
        }
    }

    internal void SetBlockedPlayersForTesting(IEnumerable<string>? blockedKeys, IEnumerable<ulong>? blockedContentIds = null) {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (blockedKeys != null) {
            foreach (var raw in blockedKeys) {
                var trimmed = (raw ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(trimmed)) {
                    keys.Add(trimmed);
                }
            }
        }

        var cids = new HashSet<ulong>();
        if (blockedContentIds != null) {
            foreach (var cid in blockedContentIds) {
                if (cid != 0) {
                    cids.Add(cid);
                }
            }
        }

        lock (_gate) {
            _testBlockedKeys = keys;
            _testBlockedContentIds = cids;
        }
    }

    internal IReadOnlyList<PfListingEntry> GetListingsSnapshot(bool includeSamples, bool useSampleEnrichment) {
        var nowUtc = _utcNow();
        List<PfListingEntry> raw;
        lock (_gate) {
            PruneExpiredLocked(nowUtc);
            raw = new List<PfListingEntry>(_liveListings.Count + (includeSamples ? _sampleListings.Count : 0));
            foreach (var entry in _liveListings.Values) {
                raw.Add(entry);
            }

            if (includeSamples) {
                foreach (var sample in _sampleListings) {
                    raw.Add(sample);
                }
            }
        }

        var result = new List<PfListingEntry>(raw.Count);
        foreach (var entry in raw) {
            result.Add(AttachSummary(entry, useSampleEnrichment));
        }

        return result;
    }

    internal PfListingEntry? TryGetListing(ulong listingId, bool includeSamples, bool useSampleEnrichment) {
        var nowUtc = _utcNow();
        PfListingEntry? found = null;
        lock (_gate) {
            PruneExpiredLocked(nowUtc);
            if (_liveListings.TryGetValue(listingId, out var live)) {
                found = live;
            } else if (includeSamples) {
                found = _sampleListings.FirstOrDefault(s => s.Id == listingId);
            }
        }

        return found is not null ? AttachSummary(found, useSampleEnrichment) : null;
    }

    internal IReadOnlyList<PfMemberRow> BuildMemberRows(
        PfListingEntry listing,
        bool useSampleEnrichment,
        uint? playerClassJobId
    ) {
        ArgumentNullException.ThrowIfNull(listing);

        UploadablePartyDetail? liveDetail;
        IReadOnlyList<ulong>? sampleCids = null;
        lock (_gate) {
            _liveDetails.TryGetValue(listing.Id, out liveDetail);
            if (listing.IsSample) {
                _sampleMemberContentIds.TryGetValue(listing.Id, out sampleCids);
            }
        }

        var slotCount = Math.Max(
            (int)listing.SlotsAvailable,
            Math.Max(listing.Slots.Count, Math.Max(listing.JobsPresent.Count, liveDetail?.MemberContentIds.Count ?? 0)));
        if (slotCount <= 0) {
            slotCount = 8;
        }

        var contentIdsToResolve = new List<ulong>(slotCount);
        for (var i = 0; i < slotCount; i++) {
            var cid = ResolveSlotContentId(i, listing, liveDetail, sampleCids);
            if (cid != 0) {
                contentIdsToResolve.Add(cid);
            }
        }

        Dictionary<ulong, CharacterIdentitySnapshot>? localIdentities = null;
        if (_playerDatabase != null && contentIdsToResolve.Count > 0) {
            try {
                localIdentities = _playerDatabase.GetIdentities(contentIdsToResolve);
            } catch {
                localIdentities = null;
            }
        }

        var rows = new List<PfMemberRow>(slotCount);
        for (var i = 0; i < slotCount; i++) {
            var partyIndex = (byte)(i / 8);
            var acceptingMask = i < listing.Slots.Count ? listing.Slots[i].AcceptingMask : 0UL;
            var jobId = ResolveSlotJobId(i, listing, liveDetail);
            var isFilled = jobId != 0;
            var cid = ResolveSlotContentId(i, listing, liveDetail, sampleCids);
            var isLeader = i == 0 || (cid != 0 && cid == listing.LeaderContentId);

            var name = string.Empty;
            var worldId = (ushort)0;
            var worldName = string.Empty;

            if (isLeader && !string.IsNullOrWhiteSpace(listing.LeaderName)) {
                name = listing.LeaderName;
                worldId = listing.HomeWorldId;
                worldName = listing.HomeWorldName;
            } else if (cid != 0 && localIdentities != null && localIdentities.TryGetValue(cid, out var cachedIdentity)) {
                name = cachedIdentity.Name;
                worldId = cachedIdentity.HomeWorld;
                worldName = _icons.ResolveWorldName(worldId);
            } else if (listing.IsSample && isFilled) {
                (name, worldName) = BuildSampleMemberIdentity(listing.Id, i);
            }

            var isPlayerEligible = !isFilled
                                   && playerClassJobId.HasValue
                                   && PfSlotBarRenderer.DoesJobMatchAcceptingMask(playerClassJobId.Value, acceptingMask);

            var (parse, progress, enrichmentState) = BuildMemberEnrichment(
                listing,
                i,
                isFilled,
                cid,
                name,
                useSampleEnrichment);

            if (!useSampleEnrichment
                && !listing.IsSample
                && isFilled
                && _enrichmentClient != null
                && cid != 0
                && listing.RawDuty != 0
                && _enrichmentClient.TryGetMemberEnrichment(cid, listing.RawDuty, out var cachedMember)) {
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(cachedMember.Name)) {
                    name = cachedMember.Name;
                }

                if (worldId == 0 && cachedMember.HomeWorldId != 0) {
                    worldId = cachedMember.HomeWorldId;
                    worldName = _icons.ResolveWorldName(worldId);
                }

                parse = cachedMember.Parse;
                progress = cachedMember.Progress;
                enrichmentState = cachedMember.State;
            }

            var isBlocked = isFilled
                            && ((isLeader && listing.IsLeaderBlocked)
                                || IsPlayerBlocked(0, cid, name, worldName));

            rows.Add(new PfMemberRow(
                i,
                partyIndex,
                cid,
                jobId,
                acceptingMask,
                isLeader,
                name,
                worldName,
                worldId,
                parse,
                progress,
                enrichmentState,
                isPlayerEligible,
                isBlocked));
        }

        return rows;
    }

    internal (IReadOnlyList<ulong> MemberContentIds, IReadOnlyList<PfKnownMemberHint> KnownMembers) GetEnrichmentInputs(PfListingEntry listing) {
        ArgumentNullException.ThrowIfNull(listing);

        UploadablePartyDetail? liveDetail;
        lock (_gate) {
            _liveDetails.TryGetValue(listing.Id, out liveDetail);
        }

        var slotCount = Math.Max(
            (int)listing.SlotsAvailable,
            Math.Max(listing.Slots.Count, Math.Max(listing.JobsPresent.Count, liveDetail?.MemberContentIds.Count ?? 0)));
        if (slotCount <= 0) {
            slotCount = 8;
        }

        var memberIds = new List<ulong>(slotCount);
        var seenIds = new HashSet<ulong>();
        for (var i = 0; i < slotCount; i++) {
            var cid = ResolveSlotContentId(i, listing, liveDetail, sampleCids: null);
            if (cid != 0 && seenIds.Add(cid)) {
                memberIds.Add(cid);
            }
        }

        if (listing.LeaderContentId != 0 && seenIds.Add(listing.LeaderContentId)) {
            memberIds.Add(listing.LeaderContentId);
        }

        Dictionary<ulong, CharacterIdentitySnapshot>? localIdentities = null;
        if (_playerDatabase != null && memberIds.Count > 0) {
            try {
                localIdentities = _playerDatabase.GetIdentities(memberIds);
            } catch {
                localIdentities = null;
            }
        }

        var knownMembers = new List<PfKnownMemberHint>(memberIds.Count);
        var seenHints = new HashSet<ulong>();
        if (listing.LeaderContentId != 0
            && listing.HomeWorldId != 0
            && !string.IsNullOrWhiteSpace(listing.LeaderName)
            && seenHints.Add(listing.LeaderContentId)) {
            knownMembers.Add(new PfKnownMemberHint(listing.LeaderContentId, listing.LeaderName.Trim(), listing.HomeWorldId));
        }

        if (localIdentities != null) {
            foreach (var cid in memberIds) {
                if (seenHints.Contains(cid)) {
                    continue;
                }

                if (localIdentities.TryGetValue(cid, out var identity)
                    && identity.HomeWorld != 0
                    && !string.IsNullOrWhiteSpace(identity.Name)
                    && seenHints.Add(cid)) {
                    knownMembers.Add(new PfKnownMemberHint(cid, identity.Name.Trim(), identity.HomeWorld));
                }
            }
        }

        return (memberIds, knownMembers);
    }

    internal void PruneExpired() {
        lock (_gate) {
            PruneExpiredLocked(_utcNow());
        }
    }

    public void Dispose() {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        if (_plugin is not null) {
            _plugin.PartyFinderGui.ReceiveListing -= OnReceiveListing;
            _plugin.AddonLifecycle.UnregisterListener(OnLfgAddonEvent);
            _plugin.AddonLifecycle.UnregisterListener(OnLfgAddonFinalize);
            _plugin.AddonLifecycle.UnregisterListener(OnDetailAddonEvent);
            _plugin.AddonLifecycle.UnregisterListener(OnBlackListAddonEvent);
        }
    }

    private void OnBlackListAddonEvent(AddonEvent type, AddonArgs args) {
        if (Volatile.Read(ref _disposed) != 0 || _plugin == null) {
            return;
        }

        RefreshBlacklistCacheFromGame(force: true);
    }

    internal unsafe void RefreshBlacklistCacheFromGame(bool force = false) {
        if (Volatile.Read(ref _disposed) != 0 || _plugin == null) {
            return;
        }

        var nowUtc = _utcNow();
        lock (_gate) {
            if (!force && (nowUtc - _lastBlacklistPollUtc) < BlacklistPollInterval) {
                return;
            }
        }

        try {
            var infoModule = InfoModule.Instance();
            var proxyPtr = infoModule != null ? infoModule->GetInfoProxyById(InfoProxyId.Blacklist) : null;
            var blacklistProxy = (InfoProxyBlacklist*)proxyPtr;
            var strArr = BlackListStringArray.Instance();

            var blockedCount = blacklistProxy != null
                ? Math.Clamp(blacklistProxy->BlockedCharactersCount, 0, 200)
                : 200;

            var nameWorldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bareNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var namesWithWorld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (blockedCount > 0 && strArr != null) {
                for (var i = 0; i < blockedCount; i++) {
                    var playerName = strArr->PlayerNames[i].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(playerName)) {
                        continue;
                    }

                    var homeWorld = strArr->Homeworlds[i].ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(homeWorld)) {
                        nameWorldKeys.Add($"{playerName}@{homeWorld}");
                        namesWithWorld.Add(playerName);
                    } else {
                        bareNames.Add(playerName);
                    }
                }
            }

            if (blacklistProxy != null && blockedCount > 0) {
                for (var i = 0; i < blockedCount; i++) {
                    var playerName = blacklistProxy->BlockedCharacters[i].Name.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(playerName) && !namesWithWorld.Contains(playerName)) {
                        bareNames.Add(playerName);
                    }
                }
            }

            lock (_gate) {
                _blockedNameWorldKeys = nameWorldKeys;
                _blockedBareNames = bareNames;
                _lastBlacklistPollUtc = nowUtc;
            }
        } catch (Exception ex) {
            Plugin.Log?.Debug($"PfLocalMirrorStore: failed reading BlackList state: {ex.Message}");
        }
    }

    internal unsafe bool IsPlayerBlocked(ulong accountId, ulong contentId, string? name, string? homeWorldName) {
        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedWorld = (homeWorldName ?? string.Empty).Trim();

        lock (_gate) {
            if (contentId != 0 && _testBlockedContentIds.Contains(contentId)) {
                return true;
            }

            if (!string.IsNullOrEmpty(trimmedName)) {
                if (!string.IsNullOrEmpty(trimmedWorld)
                    && (_testBlockedKeys.Contains($"{trimmedName}@{trimmedWorld}")
                        || _blockedNameWorldKeys.Contains($"{trimmedName}@{trimmedWorld}"))) {
                    return true;
                }

                if (_testBlockedKeys.Contains(trimmedName)
                    || _blockedBareNames.Contains(trimmedName)) {
                    return true;
                }
            }
        }

        if (_plugin == null || (accountId == 0 && contentId == 0)) {
            return false;
        }

        try {
            var infoModule = InfoModule.Instance();
            var proxyPtr = infoModule != null ? infoModule->GetInfoProxyById(InfoProxyId.Blacklist) : null;
            if (proxyPtr == null) {
                return false;
            }

            var blacklistProxy = (InfoProxyBlacklist*)proxyPtr;
            var result = blacklistProxy->GetBlockResultType(accountId, contentId);
            return result is InfoProxyBlacklist.BlockResultType.BlockedByAccountId
                or InfoProxyBlacklist.BlockResultType.BlockedByContentId;
        } catch {
            return false;
        }
    }

    private void OnLfgAddonEvent(AddonEvent type, AddonArgs args) {
        if (Volatile.Read(ref _disposed) != 0 || _plugin == null) {
            return;
        }

        RefreshNativeUiSnapshotFromGame(force: true);
    }

    internal static int[] ExtractCategoryCountsFromNumberArray(ReadOnlySpan<int> intArray, byte areaTab) {
        var counts = new int[PfIconCatalog.CategoryTabs.Count];
        var activeAreaIdx = Math.Clamp((int)areaTab, 0, 2);
        counts[0] = Math.Max(0, intArray[402 + activeAreaIdx]);
        for (var c = 1; c <= 15; c++) {
            counts[c] = Math.Max(0, intArray[405 + c]);
        }

        counts[16] = Math.Max(0, intArray[405]);
        return counts;
    }

    internal unsafe PfNativeUiSnapshot RefreshNativeUiSnapshotFromGame(bool force = false) {
        if (Volatile.Read(ref _disposed) != 0 || _plugin == null) {
            return GetNativeUiSnapshot();
        }

        var nowUtc = _utcNow();
        lock (_gate) {
            if (!force
                && !_nativeSnapshotDirty
                && (nowUtc - _lastNativeSnapshotPollUtc) < NativeSnapshotPollInterval) {
                return _nativeUiSnapshot;
            }
        }

        try {
            RefreshBlacklistCacheFromGame(force: false);

            var agent = AgentLookingForGroup.Instance();
            var isAgentInitialized = agent != null && agent->InfoProxyCrossRealm != null;
            var areaTab = agent != null ? agent->SearchAreaTab : (byte)0;
            var categoryTab = agent != null ? agent->CategoryTab : (byte)0;

            var addonPtr = _plugin.GameGui.GetAddonByName("LookingForGroup", 1);
            var isAddonOpen = !addonPtr.IsNull;
            var addon = isAddonOpen ? (AddonLookingForGroup*)addonPtr.Address : null;

            var stage = AtkStage.Instance();
            var numArr = stage != null ? stage->GetNumberArrayData(NumberArrayType.LookingForGroup) : null;
            var strArr = stage != null ? stage->GetStringArrayData(StringArrayType.LookingForGroup) : null;

            var isAddonVisible = addon != null && ((AtkUnitBase*)addon)->IsVisible;
            var hasValidNumArr = numArr != null && numArr->IntArray != null && numArr->Size > 428;
            var hasValidCounts = numArr != null && numArr->IntArray != null && numArr->Size > 420 && numArr->IntArray[402] >= 0;

            var areaCounts = Array.Empty<int>();
            if (hasValidCounts) {
                areaCounts = [
                    Math.Max(0, numArr->IntArray[402]),
                    Math.Max(0, numArr->IntArray[403]),
                    Math.Max(0, numArr->IntArray[404]),
                ];
            } else if (isAddonVisible && addon != null) {
                var addonAreaCounts = new int[3];
                var foundAnyAreaCount = false;
                for (var i = 0; i < addonAreaCounts.Length; i++) {
                    var parsedArea = TryReadRadioButtonCount(addon->WorldSelectButtons[i].Value);
                    addonAreaCounts[i] = parsedArea;
                    if (parsedArea >= 0) {
                        foundAnyAreaCount = true;
                    }
                }

                if (foundAnyAreaCount) {
                    areaCounts = addonAreaCounts;
                }
            }

            var categoryCounts = Array.Empty<int>();
            if (hasValidCounts) {
                categoryCounts = ExtractCategoryCountsFromNumberArray(
                    new ReadOnlySpan<int>(numArr->IntArray, (int)numArr->Size),
                    areaTab);
            } else if (isAddonVisible && addon != null) {
                var counts = new int[PfIconCatalog.CategoryTabs.Count];
                for (var i = 0; i < counts.Length; i++) {
                    var textNode = addon->CategoryCountTextNodes[i].Value;
                    if (textNode != null) {
                        var rawText = textNode->NodeText.ToString().Trim();
                        counts[i] = int.TryParse(rawText, out var parsed) ? parsed : 0;
                    }
                }

                categoryCounts = counts;
            }

            var currentPageText = string.Empty;
            var resultsCountText = string.Empty;
            if (strArr != null && strArr->StringArray != null && strArr->Size >= 2) {
                if (strArr->StringArray[0].Value != null) {
                    resultsCountText = strArr->StringArray[0].ToString().Trim();
                }

                if (strArr->StringArray[1].Value != null) {
                    currentPageText = strArr->StringArray[1].ToString().Trim();
                }
            }

            if (isAddonVisible && addon != null) {
                if (string.IsNullOrEmpty(currentPageText) && addon->CurrentPageTextNode != null) {
                    currentPageText = addon->CurrentPageTextNode->NodeText.ToString().Trim();
                }

                if (string.IsNullOrEmpty(resultsCountText) && addon->ResultsCountTextNode != null) {
                    resultsCountText = addon->ResultsCountTextNode->NodeText.ToString().Trim();
                }
            }

            var currentPageIndex = hasValidNumArr ? Math.Max(0, numArr->IntArray[428]) : 0;
            var hasPrev = hasValidNumArr
                ? numArr->IntArray[427] >= 0
                : (isAddonVisible && addon != null && addon->PreviousPageButton != null && addon->PreviousPageButton->IsEnabled);
            var hasNext = hasValidNumArr
                ? numArr->IntArray[426] > 0
                : (isAddonVisible && addon != null && addon->NextPageButton != null && addon->NextPageButton->IsEnabled);

            var nativeOrderedListingIds = Array.Empty<ulong>();
            var nativeUnopenedIds = new HashSet<ulong>();
            var nativeBlockedIds = new HashSet<ulong>();
            if (isAgentInitialized) {
                var maxListingsCapacity = sizeof(AgentLookingForGroup.ListingsSub) / sizeof(ulong);
                var displayedCount = Math.Min((int)agent->NumberOfListingsDisplayed, maxListingsCapacity);
                if (displayedCount > 0) {
                    var idsPtr = (ulong*)&agent->Listings;
                    var undiscoveredDutyText = _icons.ResolveAddonText(11090, "???");
                    var orderedList = new List<ulong>(displayedCount);
                    for (var i = 0; i < displayedCount; i++) {
                        var listingId = idsPtr[i];
                        if (listingId == 0) {
                            continue;
                        }

                        orderedList.Add(listingId);

                        var isUnopened = false;
                        var dutyColIdx = (4 * i) + 4;
                        if (strArr != null
                            && strArr->StringArray != null
                            && dutyColIdx < strArr->Size
                            && strArr->StringArray[dutyColIdx].Value != null) {
                            var nativeDutyTitle = strArr->StringArray[dutyColIdx].ToString().Trim();
                            if (!string.IsNullOrEmpty(undiscoveredDutyText)
                                && string.Equals(nativeDutyTitle, undiscoveredDutyText, StringComparison.Ordinal)) {
                                isUnopened = true;
                            }
                        }

                        var isNativeDimmed = false;
                        var flagsIntIdx = (4 * i) + 2;
                        if (numArr != null && numArr->IntArray != null && flagsIntIdx < numArr->Size) {
                            isNativeDimmed = numArr->IntArray[flagsIntIdx] < 0;
                        }

                        if (isUnopened) {
                            nativeUnopenedIds.Add(listingId);
                        } else if (isNativeDimmed) {
                            nativeBlockedIds.Add(listingId);
                        }
                    }

                    if (orderedList.Count > 0) {
                        nativeOrderedListingIds = orderedList.ToArray();
                    }
                }
            }

            var snapshot = new PfNativeUiSnapshot(
                IsNativeAddonOpen: isAddonOpen,
                IsNativeAgentInitialized: isAgentInitialized,
                SearchAreaTab: areaTab,
                CategoryTab: categoryTab,
                CategoryCounts: categoryCounts,
                AreaCounts: areaCounts,
                CurrentPageText: currentPageText,
                ResultsCountText: resultsCountText,
                CurrentPageIndex: currentPageIndex,
                HasPreviousPage: hasPrev,
                HasNextPage: hasNext,
                NativeOrderedListingIds: nativeOrderedListingIds,
                CapturedAtUtc: nowUtc,
                IsNativeAddonVisible: isAddonVisible);

            lock (_gate) {
                _nativeUiSnapshot = snapshot;
                _nativeUnopenedListingIds = nativeUnopenedIds;
                _nativeBlockedListingIds = nativeBlockedIds;
                _lastNativeSnapshotPollUtc = nowUtc;
                _nativeSnapshotDirty = false;
            }

            ReconcileEmptyPageIfConfirmed(nowUtc, snapshot);
            return snapshot;
        } catch (Exception ex) {
            Plugin.Log?.Debug($"PfLocalMirrorStore: failed reading LookingForGroup UI state: {ex.Message}");
            return GetNativeUiSnapshot();
        }
    }

    private static unsafe int TryReadRadioButtonCount(AtkComponentRadioButton* radioButton) {
        if (radioButton == null) {
            return -1;
        }

        ref var uld = ref radioButton->AtkComponentButton.AtkComponentBase.UldManager;
        if (uld.NodeList == null || uld.NodeListCount == 0) {
            return -1;
        }

        for (var i = 0; i < uld.NodeListCount; i++) {
            var node = uld.NodeList[i];
            if (node == null || node->Type != NodeType.Text) {
                continue;
            }

            var textNode = (AtkTextNode*)node;
            var raw = textNode->NodeText.ToString().Trim();
            if (int.TryParse(raw, out var count)) {
                return count;
            }
        }

        return -1;
    }

    private void OnLfgAddonFinalize(AddonEvent type, AddonArgs args) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        lock (_gate) {
            _nativeSnapshotDirty = true;
            if (_nativeUiSnapshot.IsNativeAddonOpen || _nativeUiSnapshot.IsNativeAddonVisible) {
                _nativeUiSnapshot = _nativeUiSnapshot with {
                    IsNativeAddonOpen = false,
                    IsNativeAddonVisible = false,
                };
            }
        }
    }

    private void OnDetailAddonEvent(AddonEvent type, AddonArgs args) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        try {
            if (PartyDetailSnapshotBuilder.TryBuildFromAgent(out var snapshot, out _)
                && snapshot.ListingId != 0) {
                UpsertLiveDetail(snapshot);
            }
        } catch (Exception ex) {
            Plugin.Log?.Debug($"PfLocalMirrorStore: failed capturing open native PF detail: {ex.Message}");
        }
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        try {
            var converted = FromDalamudListing(listing, _icons, _utcNow(), args.BatchNumber);
            UpsertLiveListing(converted);
        } catch (Exception ex) {
            Plugin.Log?.Debug($"PfLocalMirrorStore: failed capturing listing {listing.Id}: {ex.Message}");
        }
    }

    internal static PfListingEntry FromDalamudListing(
        IPartyFinderListing source,
        PfIconCatalog icons,
        DateTime nowUtc,
        int batchNumber = -1
    ) {
        var homeWorldId = (ushort)source.HomeWorld.RowId;
        var createdWorldId = (ushort)source.World.RowId;
        var dutyFallback = source.Duty.ValueNullable?.Name.ExtractText();
        var primaryDcWorldId = createdWorldId != 0 ? createdWorldId : homeWorldId;
        var dcFallback = createdWorldId != 0
            ? source.World.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText()
            : source.HomeWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText();
        var dataCenterName = icons.ResolveDataCenterName(primaryDcWorldId, dcFallback);
        if (string.IsNullOrWhiteSpace(dataCenterName) && homeWorldId != 0 && homeWorldId != primaryDcWorldId) {
            dataCenterName = icons.ResolveDataCenterName(
                homeWorldId,
                source.HomeWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText());
        }

        var slots = new List<PfListingSlot>(source.Slots.Count);
        var slotIdx = 0;
        foreach (var rawSlot in source.Slots) {
            var uploadSlot = new UploadableSlot(rawSlot);
            slots.Add(new PfListingSlot(slotIdx++, uploadSlot.Accepting));
        }

        return new PfListingEntry {
            Id = source.Id,
            LeaderContentId = source.ContentId,
            LeaderName = source.Name.TextValue.Trim(),
            Comment = source.Description.TextValue.Trim(),
            HomeWorldId = homeWorldId,
            HomeWorldName = icons.ResolveWorldName(homeWorldId, source.HomeWorld.ValueNullable?.Name.ExtractText()),
            CreatedWorldId = createdWorldId,
            CreatedWorldName = icons.ResolveWorldName(createdWorldId, source.World.ValueNullable?.Name.ExtractText()),
            DataCenterName = dataCenterName,
            Category = source.Category,
            RawDuty = source.RawDuty,
            DutySortKey = icons.ResolveDutySortKey(source.RawDuty, source.DutyType),
            DutyName = icons.ResolveDutyName(source.RawDuty, source.DutyType, dutyFallback),
            DutyType = source.DutyType,
            IsHighEnd = UploadableListing.IsSourceClientHighEndDuty(source),
            BeginnersWelcome = source.BeginnersWelcome,
            SecondsRemaining = source.SecondsRemaining,
            MinItemLevel = source.MinimumItemLevel,
            NumParties = source.Parties,
            SlotsAvailable = source.SlotsAvailable,
            SlotsFilled = source.SlotsFilled,
            LastServerRestart = source.LastPatchHotfixTimestamp <= 0 ? 0U : (uint)source.LastPatchHotfixTimestamp,
            Objective = source.Objective,
            Conditions = source.Conditions,
            DutyFinderSettings = source.DutyFinderSettings,
            LootRules = source.LootRules,
            SearchArea = source.SearchArea,
            Slots = slots,
            JobsPresent = source.RawJobsPresent.ToArray(),
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
            IsSample = false,
            LastBatchNumber = batchNumber,
        };
    }

    private PfListingEntry AttachSummary(PfListingEntry entry, bool useSampleEnrichment) {
        var isLeaderBlocked = entry.IsLeaderBlocked
                              || IsListingNativeBlocked(entry.Id)
                              || IsPlayerBlocked(0, entry.LeaderContentId, entry.LeaderName, entry.HomeWorldName);
        var isDutyUnopened = entry.IsDutyUnopened || IsListingUnopened(entry.Id);

        CachedLeaderEnrichment? cachedSummary = null;
        if (!useSampleEnrichment
            && !entry.IsSample
            && _enrichmentClient != null
            && entry.RawDuty != 0
            && entry.LeaderContentId != 0) {
            _enrichmentClient.TryGetLeaderSummary(entry.Id, entry.RawDuty, entry.LeaderContentId, out cachedSummary);
        }

        if (!useSampleEnrichment
            && cachedSummary == null
            && isLeaderBlocked == entry.IsLeaderBlocked
            && isDutyUnopened == entry.IsDutyUnopened) {
            return entry;
        }

        var leaderParse = entry.LeaderParse;
        var leaderProgress = entry.LeaderProgress;
        var cachedMemberCount = entry.CachedMemberCount;

        if (useSampleEnrichment) {
            (leaderParse, leaderProgress, _) = BuildMemberEnrichment(
                entry,
                slotIndex: 0,
                isFilled: true,
                contentId: entry.LeaderContentId,
                name: entry.LeaderName,
                useSampleEnrichment: true);
            cachedMemberCount = Math.Min((int)entry.SlotsFilled, Math.Max(1, entry.SlotsFilled - 1));
        } else if (cachedSummary != null) {
            leaderParse = cachedSummary.Parse;
            leaderProgress = cachedSummary.Progress;
            cachedMemberCount = cachedSummary.CachedMemberCount;
        }

        return new PfListingEntry {
            Id = entry.Id,
            LeaderContentId = entry.LeaderContentId,
            LeaderName = entry.LeaderName,
            Comment = entry.Comment,
            HomeWorldId = entry.HomeWorldId,
            HomeWorldName = entry.HomeWorldName,
            CreatedWorldId = entry.CreatedWorldId,
            CreatedWorldName = entry.CreatedWorldName,
            DataCenterName = entry.DataCenterName,
            Category = entry.Category,
            RawDuty = entry.RawDuty,
            DutySortKey = entry.DutySortKey,
            DutyName = entry.DutyName,
            DutyType = entry.DutyType,
            IsHighEnd = entry.IsHighEnd,
            BeginnersWelcome = entry.BeginnersWelcome,
            SecondsRemaining = entry.SecondsRemaining,
            MinItemLevel = entry.MinItemLevel,
            NumParties = entry.NumParties,
            SlotsAvailable = entry.SlotsAvailable,
            SlotsFilled = entry.SlotsFilled,
            LastServerRestart = entry.LastServerRestart,
            Objective = entry.Objective,
            Conditions = entry.Conditions,
            DutyFinderSettings = entry.DutyFinderSettings,
            LootRules = entry.LootRules,
            SearchArea = entry.SearchArea,
            Slots = entry.Slots,
            JobsPresent = entry.JobsPresent,
            MemberContentIds = entry.MemberContentIds,
            FirstSeenUtc = entry.FirstSeenUtc,
            LastSeenUtc = entry.LastSeenUtc,
            LastDetailObservedUtc = entry.LastDetailObservedUtc,
            IsSample = entry.IsSample,
            LastBatchNumber = entry.LastBatchNumber,
            PageGeneration = entry.PageGeneration,
            LeaderParse = leaderParse,
            LeaderProgress = leaderProgress,
            CachedMemberCount = cachedMemberCount,
            IsLeaderBlocked = isLeaderBlocked,
            IsDutyUnopened = isDutyUnopened,
        };
    }

    private void PruneExpiredLocked(DateTime nowUtc) {
        List<ulong>? expired = null;
        foreach (var pair in _liveListings) {
            if (nowUtc - pair.Value.LastSeenUtc > DefaultListingTtl) {
                expired ??= [];
                expired.Add(pair.Key);
            }
        }

        if (expired != null) {
            foreach (var id in expired) {
                _liveListings.Remove(id);
                _liveDetails.Remove(id);
                _currentPageListingIds.Remove(id);
            }
        }
    }

    private static ulong ResolveSlotContentId(
        int slotIndex,
        PfListingEntry listing,
        UploadablePartyDetail? detail,
        IReadOnlyList<ulong>? sampleCids
    ) {
        if (detail != null && slotIndex < detail.MemberContentIds.Count && detail.MemberContentIds[slotIndex] != 0) {
            return detail.MemberContentIds[slotIndex];
        }

        if (slotIndex < listing.MemberContentIds.Count && listing.MemberContentIds[slotIndex] != 0) {
            return listing.MemberContentIds[slotIndex];
        }

        if (sampleCids != null && slotIndex < sampleCids.Count && sampleCids[slotIndex] != 0) {
            return sampleCids[slotIndex];
        }

        return slotIndex == 0 ? listing.LeaderContentId : 0UL;
    }

    private static byte ResolveSlotJobId(int slotIndex, PfListingEntry listing, UploadablePartyDetail? detail) {
        if (detail != null && slotIndex < detail.MemberJobs.Count && detail.MemberJobs[slotIndex] != 0) {
            return detail.MemberJobs[slotIndex];
        }

        return slotIndex < listing.JobsPresent.Count ? listing.JobsPresent[slotIndex] : (byte)0;
    }

    private static (PfParseDisplay Parse, PfProgressDisplay Progress, PfMemberEnrichmentState State) BuildMemberEnrichment(
        PfListingEntry listing,
        int slotIndex,
        bool isFilled,
        ulong contentId,
        string name,
        bool useSampleEnrichment
    ) {
        if (!isFilled) {
            return (PfParseDisplay.Empty, PfProgressDisplay.Empty, PfMemberEnrichmentState.None);
        }

        if (!useSampleEnrichment) {
            var resolved = !string.IsNullOrWhiteSpace(name) || contentId != 0;
            return (
                PfParseDisplay.Empty,
                PfProgressDisplay.Empty,
                resolved ? PfMemberEnrichmentState.None : PfMemberEnrichmentState.Unresolved);
        }

        var isSplit = listing.IsHighEnd && (listing.Id % 2 == 1 || listing.DutyName.Contains("4", StringComparison.Ordinal));
        var variant = Math.Abs((int)((listing.Id + (ulong)(slotIndex * 3)) % 6));

        return variant switch {
            0 => (
                new PfParseDisplay(91, isSplit ? (byte)96 : null, isSplit, false, false, false, 73, 101, LockedIn: true),
                new PfProgressDisplay(true, 18, null, null, [new("Clear", 42, null, true)], "Cleared"),
                PfMemberEnrichmentState.Cached),
            1 => (
                new PfParseDisplay(84, isSplit ? (byte)78 : null, isSplit, false, false, false, 73, 101, LockedIn: false),
                new PfProgressDisplay(true, 3, null, null, [new("Clear", 5, null, true)], "Cleared"),
                PfMemberEnrichmentState.Cached),
            2 => (
                new PfParseDisplay(72, null, isSplit, false, false, true, 73, 101, LockedIn: true),
                new PfProgressDisplay(true, 5, null, null, [new("Clear", 9, null, true)], "Cleared"),
                PfMemberEnrichmentState.Cached),
            3 => (
                new PfParseDisplay(null, null, isSplit, true, false, false, 73, 101, LockedIn: true),
                new PfProgressDisplay(false, null, 14.2f, (byte)(isSplit ? 2 : 1), [new("P2 Enrage", 12, 14.2f, false)], isSplit ? "P2 14.2%" : "14.2%"),
                PfMemberEnrichmentState.Cached),
            4 => (
                PfParseDisplay.Empty,
                PfProgressDisplay.Empty,
                PfMemberEnrichmentState.Loading),
            _ => (
                PfParseDisplay.Empty,
                new PfProgressDisplay(false, null, null, null, Array.Empty<PfPhaseProgressItem>(), string.IsNullOrWhiteSpace(name) ? string.Empty : "P1 38%"),
                string.IsNullOrWhiteSpace(name) ? PfMemberEnrichmentState.Unresolved : PfMemberEnrichmentState.Cached),
        };
    }

    private static (string Name, string World) BuildSampleMemberIdentity(ulong listingId, int slotIndex) {
        if ((listingId + (ulong)slotIndex) % 6 == 5 && slotIndex > 0) {
            return (string.Empty, string.Empty);
        }

        string[] firstNames = ["Alice", "Riku", "Sora", "Mina", "Kuro", "Yuna", "Hana", "Leo"];
        string[] lastNames = ["Valkyrie", "Crescent", "Aether", "Ashen", "Lumina", "Noctis", "Rift", "Vesper"];
        string[] worlds = ["Chocobo", "Shinryu", "Pandaemonium", "Asura", "Ixion", "Mandragora", "Hades", "Titan"];

        var idx = (slotIndex + (int)(listingId % 5)) % firstNames.Length;
        return ($"{firstNames[idx]} {lastNames[(idx + slotIndex) % lastNames.Length]}", worlds[(slotIndex + 2) % worlds.Length]);
    }

    private static (List<PfListingEntry> Listings, Dictionary<ulong, IReadOnlyList<ulong>> Cids) CreateSampleDataset(DateTime nowUtc) {
        const ulong allTanks = (ulong)(JobFlags.Paladin | JobFlags.Warrior | JobFlags.DarkKnight | JobFlags.Gunbreaker);
        const ulong allHealers = (ulong)(JobFlags.WhiteMage | JobFlags.Scholar | JobFlags.Astrologian | JobFlags.Sage);
        const ulong meleeDps = (ulong)(JobFlags.Monk | JobFlags.Dragoon | JobFlags.Ninja | JobFlags.Samurai | JobFlags.Reaper | JobFlags.Viper);
        const ulong physRanged = (ulong)(JobFlags.Bard | JobFlags.Machinist | JobFlags.Dancer);
        const ulong casters = (ulong)(JobFlags.BlackMage | JobFlags.Summoner | JobFlags.RedMage | JobFlags.Pictomancer);
        const ulong allDps = meleeDps | physRanged | casters;
        const ulong anyCombat = allTanks | allHealers | allDps;

        var cids = new Dictionary<ulong, IReadOnlyList<ulong>>();

        var sample1 = new PfListingEntry {
            Id = 99000001UL,
            LeaderContentId = 18014398509481985UL,
            LeaderName = "Alice Valkyrie",
            Comment = "[Sample] 2페 초반 연습 / 매크로 게임8 / 1밥",
            HomeWorldId = 52,
            HomeWorldName = "Chocobo",
            CreatedWorldId = 52,
            CreatedWorldName = "Chocobo",
            DataCenterName = "Mana",
            Category = DutyCategory.HighEndDuty,
            RawDuty = 1004,
            DutyName = "AAC Cruiserweight M4 (Savage)",
            DutyType = DutyType.Normal,
            IsHighEnd = true,
            SecondsRemaining = 2880,
            MinItemLevel = 735,
            NumParties = 1,
            SlotsAvailable = 8,
            SlotsFilled = 6,
            Objective = ObjectiveFlags.Practice,
            Conditions = ConditionFlags.DutyIncomplete,
            DutyFinderSettings = DutyFinderSettingsFlags.None,
            LootRules = LootRuleFlags.Normal,
            SearchArea = SearchAreaFlags.DataCenter | SearchAreaFlags.OnePlayerPerJob,
            JobsPresent = [19, 21, 24, 28, 34, 42, 0, 0],
            Slots = [
                new(0, allTanks), new(1, allTanks), new(2, allHealers), new(3, allHealers),
                new(4, meleeDps), new(5, casters), new(6, meleeDps | physRanged), new(7, casters),
            ],
            FirstSeenUtc = nowUtc.AddMinutes(-3),
            LastSeenUtc = nowUtc.AddSeconds(-4),
            IsSample = true,
        };
        cids[sample1.Id] = [18014398509481985UL, 18014398509481986UL, 18014398509481987UL, 18014398509481988UL, 18014398509481989UL, 18014398509481990UL, 0, 0];

        var sample2 = new PfListingEntry {
            Id = 99000002UL,
            LeaderContentId = 18014398509482001UL,
            LeaderName = "Riku Crescent",
            Comment = "[Sample] Futures Rewritten P5 cleanup / C4x ok / 3 pulls",
            HomeWorldId = 29,
            HomeWorldName = "Shinryu",
            CreatedWorldId = 29,
            CreatedWorldName = "Shinryu",
            DataCenterName = "Mana",
            Category = DutyCategory.HighEndDuty,
            RawDuty = 1006,
            DutyName = "Futures Rewritten (Ultimate)",
            DutyType = DutyType.Normal,
            IsHighEnd = true,
            SecondsRemaining = 1420,
            MinItemLevel = 735,
            NumParties = 1,
            SlotsAvailable = 8,
            SlotsFilled = 7,
            Objective = ObjectiveFlags.DutyCompletion,
            Conditions = ConditionFlags.DutyComplete,
            DutyFinderSettings = DutyFinderSettingsFlags.None,
            LootRules = LootRuleFlags.Normal,
            SearchArea = SearchAreaFlags.DataCenter | SearchAreaFlags.OnePlayerPerJob,
            JobsPresent = [37, 32, 33, 40, 30, 41, 38, 0],
            Slots = [
                new(0, allTanks), new(1, allTanks), new(2, allHealers), new(3, allHealers),
                new(4, meleeDps), new(5, meleeDps), new(6, physRanged), new(7, casters),
            ],
            FirstSeenUtc = nowUtc.AddMinutes(-8),
            LastSeenUtc = nowUtc.AddSeconds(-12),
            IsSample = true,
        };
        cids[sample2.Id] = [18014398509482001UL, 18014398509482002UL, 18014398509482003UL, 18014398509482004UL, 18014398509482005UL, 18014398509482006UL, 18014398509482007UL, 0];

        var sample3 = new PfListingEntry {
            Id = 99000003UL,
            LeaderContentId = 18014398509482020UL,
            LeaderName = "Sora Aether",
            Comment = "[Sample] Mount farm 5x / Lootmaster off",
            HomeWorldId = 44,
            HomeWorldName = "Asura",
            CreatedWorldId = 44,
            CreatedWorldName = "Asura",
            DataCenterName = "Mana",
            Category = DutyCategory.Trials,
            RawDuty = 998,
            DutyName = "Recollection (Extreme)",
            DutyType = DutyType.Normal,
            IsHighEnd = true,
            SecondsRemaining = 3200,
            MinItemLevel = 710,
            NumParties = 1,
            SlotsAvailable = 8,
            SlotsFilled = 4,
            Objective = ObjectiveFlags.Loot,
            Conditions = ConditionFlags.DutyComplete,
            DutyFinderSettings = DutyFinderSettingsFlags.None,
            LootRules = LootRuleFlags.GreedOnly,
            SearchArea = SearchAreaFlags.DataCenter,
            JobsPresent = [21, 24, 39, 23, 0, 0, 0, 0],
            Slots = [
                new(0, allTanks), new(1, allHealers), new(2, allDps), new(3, allDps),
                new(4, anyCombat), new(5, anyCombat), new(6, allTanks | allHealers), new(7, allDps),
            ],
            FirstSeenUtc = nowUtc.AddMinutes(-1),
            LastSeenUtc = nowUtc.AddSeconds(-2),
            IsSample = true,
        };

        var sample4 = new PfListingEntry {
            Id = 99000004UL,
            LeaderContentId = 18014398509482040UL,
            LeaderName = "Mina Lumina",
            Comment = "[Sample] World-only treasure map party (need 2)",
            HomeWorldId = 52,
            HomeWorldName = "Chocobo",
            CreatedWorldId = 52,
            CreatedWorldName = "Chocobo",
            DataCenterName = "Mana",
            Category = DutyCategory.TreasureHunts,
            RawDuty = 0,
            DutyName = "Cenote Ja Ja Gural",
            DutyType = DutyType.Other,
            IsHighEnd = false,
            BeginnersWelcome = true,
            SecondsRemaining = 1900,
            MinItemLevel = 0,
            NumParties = 1,
            SlotsAvailable = 8,
            SlotsFilled = 5,
            Objective = ObjectiveFlags.Loot,
            Conditions = ConditionFlags.None,
            SearchArea = SearchAreaFlags.World,
            JobsPresent = [19, 28, 34, 38, 25, 0, 0, 0],
            Slots = [
                new(0, anyCombat), new(1, anyCombat), new(2, anyCombat), new(3, anyCombat),
                new(4, anyCombat), new(5, anyCombat), new(6, anyCombat), new(7, anyCombat),
            ],
            FirstSeenUtc = nowUtc.AddMinutes(-5),
            LastSeenUtc = nowUtc.AddSeconds(-25),
            IsSample = true,
        };

        var sample5 = new PfListingEntry {
            Id = 99000005UL,
            LeaderContentId = 18014398509482060UL,
            LeaderName = "Hana Noctis",
            Comment = "[Sample] Alliance raid casual run / A-B-C all open",
            HomeWorldId = 28,
            HomeWorldName = "Pandaemonium",
            CreatedWorldId = 28,
            CreatedWorldName = "Pandaemonium",
            DataCenterName = "Mana",
            Category = DutyCategory.Raids,
            RawDuty = 990,
            DutyName = "Jeuno: The First Walk",
            DutyType = DutyType.Normal,
            IsHighEnd = false,
            SecondsRemaining = 2400,
            MinItemLevel = 695,
            NumParties = 3,
            SlotsAvailable = 24,
            SlotsFilled = 14,
            Objective = ObjectiveFlags.DutyCompletion,
            Conditions = ConditionFlags.None,
            SearchArea = SearchAreaFlags.DataCenter | SearchAreaFlags.AllianceRaid,
            JobsPresent = [
                19, 24, 28, 34, 30, 23, 42, 35,
                32, 33, 41, 38, 0, 0, 0, 0,
                37, 40, 0, 0, 0, 0, 0, 0,
            ],
            Slots = Enumerable.Range(0, 24).Select(i => new PfListingSlot(i, anyCombat)).ToArray(),
            FirstSeenUtc = nowUtc.AddMinutes(-2),
            LastSeenUtc = nowUtc.AddSeconds(-18),
            IsSample = true,
        };

        var sample6 = new PfListingEntry {
            Id = 99000006UL,
            LeaderContentId = 18014398509482080UL,
            LeaderName = "Kuro Rift",
            Comment = "[Sample] Blocked leader preview — switch Blocked: Dim / Hide in toolbar",
            HomeWorldId = 47,
            HomeWorldName = "Hades",
            CreatedWorldId = 47,
            CreatedWorldName = "Hades",
            DataCenterName = "Mana",
            Category = DutyCategory.HighEndDuty,
            RawDuty = 1003,
            DutyName = "AAC Cruiserweight M3 (Savage)",
            DutyType = DutyType.Normal,
            IsHighEnd = true,
            SecondsRemaining = 3100,
            MinItemLevel = 735,
            NumParties = 1,
            SlotsAvailable = 8,
            SlotsFilled = 3,
            Objective = ObjectiveFlags.Practice,
            Conditions = ConditionFlags.None,
            DutyFinderSettings = DutyFinderSettingsFlags.None,
            LootRules = LootRuleFlags.Normal,
            SearchArea = SearchAreaFlags.DataCenter | SearchAreaFlags.OnePlayerPerJob,
            JobsPresent = [21, 33, 39, 0, 0, 0, 0, 0],
            Slots = [
                new(0, allTanks), new(1, allTanks), new(2, allHealers), new(3, allHealers),
                new(4, meleeDps), new(5, meleeDps), new(6, physRanged), new(7, casters),
            ],
            FirstSeenUtc = nowUtc.AddMinutes(-4),
            LastSeenUtc = nowUtc.AddSeconds(-9),
            IsSample = true,
            IsLeaderBlocked = true,
        };

        var sample7 = new PfListingEntry {
            Id = 99000007UL,
            LeaderContentId = 18014398509482099UL,
            LeaderName = "Yuna Vesper",
            Comment = "[Sample] Unopened duty preview — compare cool-slate Lock vs crimson Blocked row",
            HomeWorldId = 63,
            HomeWorldName = "Ixion",
            CreatedWorldId = 63,
            CreatedWorldName = "Ixion",
            DataCenterName = "Mana",
            Category = DutyCategory.HighEndDuty,
            RawDuty = 1006,
            DutyName = "Futures Rewritten (Ultimate)",
            DutyType = DutyType.Normal,
            IsHighEnd = true,
            SecondsRemaining = 1980,
            MinItemLevel = 735,
            NumParties = 1,
            SlotsAvailable = 8,
            SlotsFilled = 5,
            Objective = ObjectiveFlags.Practice,
            Conditions = ConditionFlags.None,
            DutyFinderSettings = DutyFinderSettingsFlags.None,
            LootRules = LootRuleFlags.Normal,
            SearchArea = SearchAreaFlags.DataCenter | SearchAreaFlags.OnePlayerPerJob,
            JobsPresent = [19, 24, 34, 38, 42, 0, 0, 0],
            Slots = [
                new(0, allTanks), new(1, allTanks), new(2, allHealers), new(3, allHealers),
                new(4, meleeDps), new(5, physRanged), new(6, casters), new(7, meleeDps),
            ],
            FirstSeenUtc = nowUtc.AddMinutes(-6),
            LastSeenUtc = nowUtc.AddSeconds(-15),
            IsSample = true,
            IsDutyUnopened = true,
        };

        return ([sample1, sample2, sample3, sample4, sample5, sample6, sample7], cids);
    }
}
