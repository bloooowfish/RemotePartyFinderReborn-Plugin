using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PfBrowserPrototypeTests {
    private const uint CatNone = 0x0;
    private const uint CatRaids = 0x20;
    private const uint CatHighEndDuty = 0x40;

    private const uint AreaDataCenter = 0x1;
    private const uint AreaPrivate = 0x2;
    private const uint AreaWorld = 0x8;

    static PfBrowserPrototypeTests() {
        DalamudAssemblyResolver.Register();
    }

    private static PfLocalMirrorStore CreateStore(Func<DateTime> utcNow) => new(utcNow);

    [Fact]
    public void FilterAndSort_filters_by_unified_all_tab_optional_dc_category_job_and_search() {
        var now = new DateTime(2026, 11, 28, 7, 0, 0, DateTimeKind.Utc);
        var viperMask = PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(41);
        var pldMask = PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(19);

        var matching = new PfListingEntry {
            Id = 1,
            LeaderName = "Alice",
            HomeWorldName = "Chocobo",
            DataCenterName = "Mana",
            Comment = "p2 cleanup game8",
            DutyName = "AAC Cruiserweight M4 (Savage)",
            CategoryRaw = CatHighEndDuty,
            IsHighEnd = true,
            SearchAreaRaw = AreaDataCenter,
            SecondsRemaining = 1800,
            MinItemLevel = 735,
            SlotsAvailable = 8,
            SlotsFilled = 6,
            JobsPresent = [19, 21, 24, 28, 34, 42, 0, 0],
            Slots = [
                new(0, pldMask), new(1, pldMask), new(2, pldMask), new(3, pldMask),
                new(4, viperMask), new(5, viperMask), new(6, viperMask), new(7, pldMask),
            ],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        var worldOnlySameDc = new PfListingEntry {
            Id = 2,
            LeaderName = "Bob",
            HomeWorldName = "Shinryu",
            DataCenterName = "Mana",
            Comment = "p2 game8 world",
            DutyName = "AAC Cruiserweight M4 (Savage)",
            CategoryRaw = CatHighEndDuty,
            IsHighEnd = true,
            SearchAreaRaw = AreaWorld,
            SecondsRemaining = 2400,
            MinItemLevel = 735,
            SlotsAvailable = 8,
            SlotsFilled = 5,
            JobsPresent = [19, 0, 0, 0, 0, 0, 0, 0],
            Slots = [new(0, pldMask), new(1, viperMask)],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        var otherDc = new PfListingEntry {
            Id = 3,
            LeaderName = "Dave",
            HomeWorldName = "Bahamut",
            DataCenterName = "Gaia",
            Comment = "p2 game8 gaia",
            DutyName = "AAC Cruiserweight M4 (Savage)",
            CategoryRaw = CatHighEndDuty,
            IsHighEnd = true,
            SearchAreaRaw = AreaDataCenter,
            SecondsRemaining = 2600,
            MinItemLevel = 735,
            SlotsAvailable = 8,
            SlotsFilled = 5,
            JobsPresent = [19, 0, 0, 0, 0, 0, 0, 0],
            Slots = [new(0, pldMask), new(1, viperMask)],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        var privateListing = new PfListingEntry {
            Id = 4,
            LeaderName = "Carol",
            DataCenterName = "Mana",
            Comment = "p2 game8",
            DutyName = "AAC Cruiserweight M4 (Savage)",
            CategoryRaw = CatHighEndDuty,
            IsHighEnd = true,
            SearchAreaRaw = AreaPrivate,
            SecondsRemaining = 3000,
            MinItemLevel = 735,
            SlotsAvailable = 8,
            SlotsFilled = 1,
            JobsPresent = [19, 0, 0, 0, 0, 0, 0, 0],
            Slots = [new(0, pldMask), new(1, viperMask)],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        var allFilter = new PfFilterState {
            AreaTab = PfSearchAreaTab.All,
            HighEndOnly = true,
            MyJobOnly = true,
            SearchText = "GAME8",
            IncludeSampleListings = false,
        };
        allFilter.SelectCategoryRaw(CatHighEndDuty);

        var allResult = PfFilterEngine.FilterAndSort([matching, worldOnlySameDc, otherDc, privateListing], allFilter, playerClassJobId: 41);
        Assert.Equal(3, allResult.Count);
        Assert.DoesNotContain(allResult, r => r.Id == 4UL);

        allFilter.SelectedDataCenter = "Gaia";
        var gaiaOnly = PfFilterEngine.FilterAndSort([matching, worldOnlySameDc, otherDc, privateListing], allFilter, playerClassJobId: 41);
        Assert.Single(gaiaOnly);
        Assert.Equal(3UL, gaiaOnly[0].Id);

        allFilter.AreaTab = PfSearchAreaTab.World;
        allFilter.SelectedDataCenter = string.Empty;
        var worldOnlyResult = PfFilterEngine.FilterAndSort([matching, worldOnlySameDc, otherDc, privateListing], allFilter, playerClassJobId: 41);
        Assert.Single(worldOnlyResult);
        Assert.Equal(2UL, worldOnlyResult[0].Id);

        var availableDcs = PfFilterEngine.GetAvailableDataCenters([matching, worldOnlySameDc, otherDc, privateListing], includeSampleListings: false);
        Assert.Equal(["Gaia", "Mana"], availableDcs.ToArray());
    }

    [Fact]
    public void FilterAndSort_sorts_by_selected_mode_and_computes_counts() {
        var now = new DateTime(2026, 11, 28, 7, 0, 0, DateTimeKind.Utc);
        var entries = new[] {
            new PfListingEntry {
                Id = 10,
                DutyName = "A",
                DataCenterName = "Mana",
                CategoryRaw = CatRaids,
                SearchAreaRaw = AreaDataCenter,
                SecondsRemaining = 600,
                SlotsFilled = 7,
                MinItemLevel = 710,
                FirstSeenUtc = now,
                LastSeenUtc = now.AddSeconds(-30),
            },
            new PfListingEntry {
                Id = 11,
                DutyName = "B",
                DataCenterName = "Gaia",
                CategoryRaw = CatNone,
                SearchAreaRaw = AreaWorld,
                SecondsRemaining = 3000,
                SlotsFilled = 3,
                MinItemLevel = 735,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            },
            new PfListingEntry {
                Id = 12,
                DutyName = "Sample",
                DataCenterName = "Mana",
                CategoryRaw = CatHighEndDuty,
                SearchAreaRaw = AreaPrivate,
                IsSample = true,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            },
        };

        var areaCountsNoSample = PfFilterEngine.ComputeAreaCounts(entries, includeSampleListings: false);
        Assert.Equal(2, areaCountsNoSample[PfSearchAreaTab.All]);
        Assert.Equal(1, areaCountsNoSample[PfSearchAreaTab.World]);
        Assert.Equal(0, areaCountsNoSample[PfSearchAreaTab.Private]);

        var catCountsAll = PfFilterEngine.ComputeCategoryCounts(entries, PfSearchAreaTab.All, includeSampleListings: false);
        Assert.Equal(2, catCountsAll["all"]);
        Assert.Equal(1, catCountsAll["raids"]);
        Assert.Equal(1, catCountsAll["other"]);

        var catCountsMana = PfFilterEngine.ComputeCategoryCounts(entries, PfSearchAreaTab.All, includeSampleListings: false, dataCenterFilter: "Mana");
        Assert.Equal(1, catCountsMana["all"]);
        Assert.Equal(1, catCountsMana["raids"]);
        Assert.Equal(0, catCountsMana["other"]);

        var byTime = PfFilterEngine.FilterAndSort(
            entries,
            new PfFilterState { SortMode = PfSortMode.TimeRemaining, IncludeSampleListings = false },
            playerClassJobId: null);
        Assert.Equal([11UL, 10UL], byTime.Select(e => e.Id).ToArray());

        var byFilled = PfFilterEngine.FilterAndSort(
            entries,
            new PfFilterState { SortMode = PfSortMode.SlotsFilled, IncludeSampleListings = false },
            playerClassJobId: null);
        Assert.Equal([10UL, 11UL], byFilled.Select(e => e.Id).ToArray());

        var byIl = PfFilterEngine.FilterAndSort(
            entries,
            new PfFilterState { SortMode = PfSortMode.ItemLevel, IncludeSampleListings = false },
            playerClassJobId: null);
        Assert.Equal([11UL, 10UL], byIl.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void SlotBarRenderer_analyzes_single_and_split_roles_and_64bit_jobs() {
        var splitMask = PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(19)
                        | PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(24)
                        | PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(42);
        var openSlot = PfSlotBarRenderer.AnalyzeSlot(0, splitMask, filledJobId: 0, playerClassJobId: 42);

        Assert.False(openSlot.IsFilled);
        Assert.True(openSlot.MatchesPlayerJob);
        Assert.Equal(
            PartyFinderSlotFlagFormatter.JobRoleFlags.Tank
            | PartyFinderSlotFlagFormatter.JobRoleFlags.Healer
            | PartyFinderSlotFlagFormatter.JobRoleFlags.Dps,
            openSlot.RoleFlags);
        Assert.Equal(3, PfSlotBarRenderer.GetBandColors(openSlot.RoleFlags).Count);

        var filledSlot = PfSlotBarRenderer.AnalyzeSlot(1, splitMask, filledJobId: 24, playerClassJobId: 42);
        Assert.True(filledSlot.IsFilled);
        Assert.False(filledSlot.MatchesPlayerJob);
        Assert.Equal(PartyFinderSlotFlagFormatter.JobRoleFlags.Healer, filledSlot.RoleFlags);
        Assert.Single(PfSlotBarRenderer.GetBandColors(filledSlot.RoleFlags));
    }

    [Fact]
    public void FormatCompactAcceptingJobs_collapses_roles_subroles_and_deduplicates_base_classes() {
        var tanksMask = 0UL;
        foreach (var id in new uint[] { 1, 3, 19, 21, 32, 37 }) {
            tanksMask |= PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(id);
        }

        var healersMask = 0UL;
        foreach (var id in new uint[] { 6, 24, 28, 33, 40 }) {
            healersMask |= PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(id);
        }

        var meleeMask = 0UL;
        foreach (var id in new uint[] { 2, 4, 29, 20, 22, 30, 34, 39, 41 }) {
            meleeMask |= PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(id);
        }

        var physRangedMask = 0UL;
        foreach (var id in new uint[] { 5, 23, 31, 38 }) {
            physRangedMask |= PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(id);
        }

        var castersMask = 0UL;
        foreach (var id in new uint[] { 7, 26, 25, 27, 35, 42 }) {
            castersMask |= PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(id);
        }

        var allStandardCombat = tanksMask | healersMask | meleeMask | physRangedMask | castersMask;
        var allWithBlu = allStandardCombat | PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(36);

        Assert.Equal("Any Job", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(0UL));
        Assert.Equal("Any Job", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(allWithBlu));
        Assert.Equal("All Combat Jobs", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(allStandardCombat));
        Assert.Equal("All Tanks", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(tanksMask));
        Assert.Equal("All Healers", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(healersMask));
        Assert.Equal("All DPS", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(meleeMask | physRangedMask | castersMask));
        Assert.Equal("Melee / Casters", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(meleeMask | castersMask));

        var customPairWithClasses =
            PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(1)
            | PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(19)
            | PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(3)
            | PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(21);
        Assert.Equal("PLD / WAR", PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(customPairWithClasses));
    }

    [Fact]
    public void LocalMirrorStore_merges_live_details_prunes_expired_and_builds_sample_and_alliance_rows() {
        var currentUtc = new DateTime(2026, 11, 28, 7, 0, 0, DateTimeKind.Utc);
        var pctMask = PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(42);
        using var store = CreateStore(() => currentUtc);

        store.UpsertLiveListing(new PfListingEntry {
            Id = 500UL,
            LeaderContentId = 1001UL,
            LeaderName = "Leader One",
            HomeWorldName = "Chocobo",
            DataCenterName = "Mana",
            DutyName = "Test Duty",
            CategoryRaw = CatHighEndDuty,
            IsHighEnd = true,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 8,
            SlotsFilled = 2,
            JobsPresent = [19, 24, 0, 0, 0, 0, 0, 0],
            Slots = Enumerable.Range(0, 8).Select(i => new PfListingSlot(i, pctMask)).ToArray(),
            FirstSeenUtc = currentUtc,
            LastSeenUtc = currentUtc,
        });

        store.UpsertLiveDetail(new UploadablePartyDetail {
            ListingId = 500UL,
            LeaderContentId = 1001UL,
            LeaderName = "Leader One",
            MemberContentIds = [1001UL, 1002UL, 0, 0, 0, 0, 0, 0],
            MemberJobs = [19, 42, 0, 0, 0, 0, 0, 0],
        });

        var live = store.TryGetListing(500UL, includeSamples: false, useSampleEnrichment: false);
        Assert.NotNull(live);
        Assert.Equal(42, live!.JobsPresent[1]);
        Assert.Null(live.LeaderParse);

        var rowsWithoutSamples = store.BuildMemberRows(live, useSampleEnrichment: false, playerClassJobId: 42);
        Assert.Equal(8, rowsWithoutSamples.Count);
        Assert.True(rowsWithoutSamples[0].IsLeader);
        Assert.Equal("Leader One", rowsWithoutSamples[0].Name);
        Assert.Equal(1002UL, rowsWithoutSamples[1].ContentId);
        Assert.True(rowsWithoutSamples[2].IsPlayerEligible);

        var samples = store.GetListingsSnapshot(includeSamples: true, useSampleEnrichment: true);
        var alliance = samples.Single(s => s.HasMultipleParties);
        var allianceRows = store.BuildMemberRows(alliance, useSampleEnrichment: true, playerClassJobId: null);
        Assert.Equal(24, allianceRows.Count);
        Assert.Contains(allianceRows, r => r.PartyIndex == 2);

        currentUtc = currentUtc.AddMinutes(11);
        store.PruneExpired();
        Assert.Equal(0, store.LiveCount);
        Assert.Null(store.TryGetListing(500UL, includeSamples: false, useSampleEnrichment: false));
    }

    [Fact]
    public void NativePfBridge_coalesces_refresh_and_isolates_executor_failures() {
        var executed = new List<NativePfAction>();
        using var bridge = new NativePfBridge(action => {
            if (action.Kind == NativePfActionKind.OpenNativePfWindow) {
                throw new InvalidOperationException("boom");
            }
            executed.Add(action);
        });

        Assert.True(bridge.QueueRequestListingsUpdate());
        Assert.False(bridge.QueueRequestListingsUpdate());
        bridge.QueueOpenNativePfWindow();
        bridge.QueueOpenListing(12345UL);

        Assert.Equal(3, bridge.QueuedCount);
        var successCount = bridge.DrainQueue();

        Assert.Equal(2, successCount);
        Assert.Equal(2, executed.Count);
        Assert.Equal(NativePfActionKind.RequestListingsUpdate, executed[0].Kind);
        Assert.Equal(NativePfActionKind.OpenListing, executed[1].Kind);
        Assert.Equal(12345UL, executed[1].ListingId);
        Assert.Contains("boom", bridge.LastStatusMessage);
        Assert.True(bridge.QueueRequestListingsUpdate());
    }

    [Fact]
    public void LocalMirrorStore_groups_multi_segment_pages_filters_strict_current_page_and_keeps_detail_lookup() {
        var currentUtc = new DateTime(2026, 11, 28, 7, 0, 0, DateTimeKind.Utc);
        using var store = CreateStore(() => currentUtc);

        // Page 1 arrives across segments 1..12 (4 items each) + segment 0 (2 items) = 50 items
        ulong id = 1000;
        for (var seg = 1; seg <= 12; seg++) {
            for (var k = 0; k < 4; k++) {
                store.UpsertLiveListing(CreateLiveTestListing(id++, seg, currentUtc, $"Page1_{seg}_{k}"));
            }
        }

        store.UpsertLiveListing(CreateLiveTestListing(id++, 0, currentUtc, "Page1_0_0"));
        store.UpsertLiveListing(CreateLiveTestListing(id++, 0, currentUtc, "Page1_0_1"));

        Assert.Equal(1, store.CurrentPageGeneration);
        Assert.Equal(50, store.CurrentPageLiveCount);

        var filter = new PfFilterState { IncludeSampleListings = false };
        var page1Result = PfFilterEngine.FilterAndSort(
            store.GetListingsSnapshot(false, false),
            filter,
            playerClassJobId: null,
            currentPageGeneration: store.CurrentPageGeneration);
        Assert.Equal(50, page1Result.Count);

        // Page 2 arrives (>3s gap or after explicit pending refresh) across segment 1 + segment 0
        currentUtc = currentUtc.AddSeconds(4);
        store.MarkPendingPageRefresh();
        store.UpsertLiveListing(CreateLiveTestListing(2001UL, 1, currentUtc, "Page2_1_0"));
        store.UpsertLiveListing(CreateLiveTestListing(2002UL, 0, currentUtc, "Page2_0_0"));

        Assert.Equal(2, store.CurrentPageGeneration);
        Assert.Equal(2, store.CurrentPageLiveCount);

        var snapshot = store.GetListingsSnapshot(false, false);
        var page2Result = PfFilterEngine.FilterAndSort(
            snapshot,
            new PfFilterState { SortMode = PfSortMode.TimeRemaining, IncludeSampleListings = false },
            playerClassJobId: null,
            currentPageGeneration: store.CurrentPageGeneration);
        Assert.Equal([2002UL, 2001UL], page2Result.Select(e => e.Id).ToArray());

        var areaCounts = PfFilterEngine.ComputeAreaCounts(snapshot, includeSampleListings: false, currentPageGeneration: store.CurrentPageGeneration);
        Assert.Equal(2, areaCounts[PfSearchAreaTab.All]);

        var catCounts = PfFilterEngine.ComputeCategoryCounts(snapshot, PfSearchAreaTab.All, includeSampleListings: false, currentPageGeneration: store.CurrentPageGeneration);
        Assert.Equal(2, catCounts["all"]);

        // Detail lookup for an open listing from Page 1 still resolves even after moving to Page 2
        Assert.NotNull(store.TryGetListing(1000UL, includeSamples: false, useSampleEnrichment: false));
    }

    [Fact]
    public void LocalMirrorStore_groups_single_segment_pages_and_reconciles_empty_pages() {
        var currentUtc = new DateTime(2026, 11, 28, 7, 30, 0, DateTimeKind.Utc);
        using var store = CreateStore(() => currentUtc);

        // Single-segment page (SegmentIndex == 0 with 3 listings in same burst) stays on PageGeneration 1
        store.UpsertLiveListing(CreateLiveTestListing(3001UL, 0, currentUtc, "Small1"));
        store.UpsertLiveListing(CreateLiveTestListing(3002UL, 0, currentUtc, "Small2"));
        store.UpsertLiveListing(CreateLiveTestListing(3003UL, 0, currentUtc, "Small3"));

        Assert.Equal(1, store.CurrentPageGeneration);
        Assert.Equal(3, store.CurrentPageLiveCount);

        // Pending refresh to an empty category does NOT clear immediately before confirmation delay
        store.MarkPendingPageRefresh();
        var emptySnapshot = new PfNativeUiSnapshot(
            IsNativeAddonOpen: false,
            IsNativeAgentInitialized: true,
            SearchAreaTab: 0,
            CategoryTab: 1,
            CategoryCounts: [50, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            AreaCounts: [50, 0, 0],
            CurrentPageText: string.Empty,
            ResultsCountText: "0(0)",
            CurrentPageIndex: 0,
            HasPreviousPage: false,
            HasNextPage: false,
            NativeOrderedListingIds: Array.Empty<ulong>(),
            CapturedAtUtc: currentUtc);

        Assert.False(store.ReconcileEmptyPageIfConfirmed(currentUtc.AddMilliseconds(200), emptySnapshot));
        Assert.Equal(3, store.CurrentPageLiveCount);

        // Once confirmation delay passes and server confirms 0 results, page advances cleanly with 0 listings
        Assert.True(store.ReconcileEmptyPageIfConfirmed(currentUtc.AddMilliseconds(500), emptySnapshot));
        Assert.Equal(2, store.CurrentPageGeneration);
        Assert.Equal(0, store.CurrentPageLiveCount);

        // Native Detailed Search initiated from FFXIV popup (without MarkPendingPageRefresh) also reconciles 0(0)
        var searchUtc = currentUtc.AddSeconds(2);
        store.UpsertLiveListing(CreateLiveTestListing(3004UL, 0, searchUtc, "BeforeZeroSearch"));
        Assert.Equal(1, store.CurrentPageLiveCount);
        var detailedZeroSnapshot = emptySnapshot with {
            CategoryTab = 17,
            ResultsCountText = "0(0)",
            CapturedAtUtc = searchUtc.AddMilliseconds(500),
        };
        Assert.True(store.ReconcileEmptyPageIfConfirmed(searchUtc.AddMilliseconds(500), detailedZeroSnapshot));
        Assert.Equal(0, store.CurrentPageLiveCount);
    }

    [Fact]
    public void IconCatalog_and_mirror_store_use_corrected_category_icons_and_expose_native_snapshot() {
        Assert.Equal(61819U, PfIconCatalog.CategoryTabs.Single(t => t.Id == "hunt").IconId);
        Assert.Equal(61846U, PfIconCatalog.CategoryTabs.Single(t => t.Id == "vc").IconId);

        using var store = CreateStore(() => new DateTime(2026, 11, 28, 7, 0, 0, DateTimeKind.Utc));
        var counts = Enumerable.Range(0, 17).Select(i => i * 2).ToArray();
        var areaCounts = new[] { 178, 0, 3 };
        var snapshot = new PfNativeUiSnapshot(
            IsNativeAddonOpen: false,
            IsNativeAgentInitialized: true,
            SearchAreaTab: 0,
            CategoryTab: 6,
            CategoryCounts: counts,
            AreaCounts: areaCounts,
            CurrentPageText: "1-50",
            ResultsCountText: "50(182)",
            CurrentPageIndex: 0,
            HasPreviousPage: false,
            HasNextPage: true,
            NativeOrderedListingIds: [101UL, 102UL],
            CapturedAtUtc: new DateTime(2026, 11, 28, 7, 0, 0, DateTimeKind.Utc));

        store.SetNativeUiSnapshotForTesting(snapshot);
        var loaded = store.GetNativeUiSnapshot();
        Assert.False(loaded.IsNativeAddonOpen);
        Assert.True(loaded.IsNativeAgentInitialized);
        Assert.True(loaded.HasServerData);
        Assert.Equal("50(182)", loaded.ResultsCountText);
        Assert.Equal(12, loaded.CategoryCounts[6]);
        Assert.Equal([178, 0, 3], loaded.AreaCounts.ToArray());
        Assert.Equal([101UL, 102UL], loaded.NativeOrderedListingIds.ToArray());
    }

    [Fact]
    public void NativeDefaultSort_mirrors_native_listing_order_and_algorithmic_category_duty_eligibility_order() {
        var now = new DateTime(2026, 11, 28, 11, 0, 0, DateTimeKind.Utc);
        var pldMask = PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(19);
        var vprMask = PartyFinderSlotFlagFormatter.GetAcceptingMaskForClassJobId(41);

        var trial = new PfListingEntry {
            Id = 10,
            DutyName = "Trial",
            CategoryRaw = 0x10, // Trials
            DutySortKey = 100,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 2,
            Slots = [new(0, pldMask), new(1, vprMask)],
            JobsPresent = [19, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        var raidOlder = new PfListingEntry {
            Id = 11,
            DutyName = "Older Raid",
            CategoryRaw = CatRaids,
            DutySortKey = 50,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 2,
            Slots = [new(0, pldMask), new(1, vprMask)],
            JobsPresent = [19, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        var highEndNewer = new PfListingEntry {
            Id = 12,
            DutyName = "Newer HighEnd",
            CategoryRaw = CatHighEndDuty,
            DutySortKey = 250,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 2,
            Slots = [new(0, pldMask), new(1, vprMask)],
            JobsPresent = [19, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        var highEndOlder = new PfListingEntry {
            Id = 13,
            DutyName = "Older HighEnd",
            CategoryRaw = CatHighEndDuty,
            DutySortKey = 200,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 2,
            Slots = [new(0, pldMask), new(1, vprMask)],
            JobsPresent = [19, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        var unopenedTrial = new PfListingEntry {
            Id = 14,
            DutyName = "Unopened Trial",
            CategoryRaw = 0x10, // Trials
            DutySortKey = 999,
            IsDutyUnopened = true,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 2,
            Slots = [new(0, pldMask), new(1, pldMask)],
            JobsPresent = [19, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        var defaultFilter = new PfFilterState { IncludeSampleListings = false };
        Assert.Equal(PfSortMode.NativeDefault, defaultFilter.SortMode);

        // Fallback order without NativeOrderedListingIds:
        // opened first -> Trials(4) -> Raids(5) -> HighEnd(6, higher SortKey first) -> unopened at bottom
        var fallbackOrder = PfFilterEngine.FilterAndSort(
            [unopenedTrial, highEndOlder, raidOlder, highEndNewer, trial],
            defaultFilter,
            playerClassJobId: 41);
        Assert.Equal([10UL, 11UL, 12UL, 13UL, 14UL], fallbackOrder.Select(e => e.Id).ToArray());

        // When live NativeOrderedListingIds is available, follow exact client order first, then fallback tail
        var liveNativeOrder = PfFilterEngine.FilterAndSort(
            [unopenedTrial, highEndOlder, raidOlder, highEndNewer, trial],
            defaultFilter,
            playerClassJobId: 41,
            currentPageGeneration: null,
            nativeOrderedListingIds: [13UL, 12UL, 10UL, 11UL]);
        Assert.Equal([13UL, 12UL, 10UL, 11UL, 14UL], liveNativeOrder.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void NativePfBridge_queues_paging_tabs_category_and_recruit_actions_cleanly() {
        var executed = new List<NativePfAction>();
        using var bridge = new NativePfBridge(action => executed.Add(action));

        Assert.True(bridge.QueueRequestListingsUpdate());
        bridge.QueueSelectAreaTab(1);
        bridge.QueueRequestCategoryListings(6);
        bridge.QueuePreviousPage();
        bridge.QueueNextPage();
        bridge.QueueOpenRecruitCondition();

        Assert.Equal(6, bridge.QueuedCount);
        Assert.Equal(6, bridge.DrainQueue());

        Assert.Equal(NativePfActionKind.RequestListingsUpdate, executed[0].Kind);
        Assert.Equal(NativePfActionKind.SelectAreaTab, executed[1].Kind);
        Assert.Equal((byte)1, executed[1].AreaTab);
        Assert.Equal(NativePfActionKind.RequestCategoryListings, executed[2].Kind);
        Assert.Equal((byte)6, executed[2].Category);
        Assert.Equal(NativePfActionKind.PreviousPage, executed[3].Kind);
        Assert.Equal(NativePfActionKind.NextPage, executed[4].Kind);
        Assert.Equal(NativePfActionKind.OpenRecruitCondition, executed[5].Kind);
    }

    [Fact]
    public void ExternalLinks_build_canonical_fflogs_and_tomestone_urls_and_reject_missing_identities() {
        Assert.True(PfExternalLinks.TryBuildFFLogsCharacterUrl("Taitama Highwind", "Fenrir", 46, null, out var fflogsJp));
        Assert.Equal("https://www.fflogs.com/character/jp/fenrir/Taitama%20Highwind", fflogsJp);

        Assert.True(PfExternalLinks.TryBuildFFLogsCharacterUrl("Jane Doe", "Gilgamesh", 0, "Aether", out var fflogsDcFallback));
        Assert.Equal("https://www.fflogs.com/character/na/gilgamesh/Jane%20Doe", fflogsDcFallback);

        Assert.False(PfExternalLinks.TryBuildFFLogsCharacterUrl("", "Fenrir", 46, "Gaia", out _));
        Assert.False(PfExternalLinks.TryBuildFFLogsCharacterUrl("Jane Doe", "UnknownWorld", 0, "", out _));

        Assert.True(PfExternalLinks.TryBuildTomestoneCharacterUrl("Taitama Highwind", "Fenrir", out var tomestoneUrl));
        Assert.Equal("https://tomestone.gg/character-name/Fenrir/Taitama%20Highwind", tomestoneUrl);
        Assert.False(PfExternalLinks.TryBuildTomestoneCharacterUrl("Taitama Highwind", "", out _));
    }

    [Fact]
    public void ParseDisplay_tracks_lock_in_and_sample_enrichment_exposes_unlocked_and_consistent_variants() {
        Assert.True(PfParseDisplay.Empty.LockedIn);
        Assert.False(PfParseDisplay.Empty.HasVisiblePercentile);

        var unlocked = new PfParseDisplay(84, null, false, false, false, false, 73, 101, LockedIn: false);
        Assert.True(unlocked.HasVisiblePercentile);
        Assert.False(unlocked.LockedIn);

        using var store = CreateStore(() => new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc));
        var sample = store.GetListingsSnapshot(includeSamples: true, useSampleEnrichment: true).First();
        var rows = store.BuildMemberRows(sample, useSampleEnrichment: true, playerClassJobId: null);

        Assert.Contains(rows, r => r.Parse.HasVisiblePercentile && !r.Parse.LockedIn);
        Assert.DoesNotContain(rows, r => r.Parse.HasVisiblePercentile && r.Progress is { Cleared: false } && !string.IsNullOrWhiteSpace(r.Progress.SummaryLabel));
    }

    [Fact]
    public void FilterAndSort_dims_to_bottom_or_hides_blocked_listings_and_excludes_from_counts_when_hidden() {
        var now = new DateTime(2026, 11, 28, 13, 0, 0, DateTimeKind.Utc);
        var blockedBest = new PfListingEntry {
            Id = 201,
            DutyName = "Blocked High Stats",
            LeaderName = "Blocked Leader",
            CategoryRaw = CatHighEndDuty,
            SearchAreaRaw = AreaDataCenter,
            SecondsRemaining = 3600,
            SlotsFilled = 7,
            MinItemLevel = 740,
            IsLeaderBlocked = true,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        var normalLower = new PfListingEntry {
            Id = 202,
            DutyName = "Normal Lower Stats",
            LeaderName = "Friendly Leader",
            CategoryRaw = CatHighEndDuty,
            SearchAreaRaw = AreaDataCenter,
            SecondsRemaining = 600,
            SlotsFilled = 2,
            MinItemLevel = 710,
            IsLeaderBlocked = false,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        var dimFilter = new PfFilterState {
            BlockedDisplayMode = PfBlockedDisplayMode.Dim,
            IncludeSampleListings = false,
        };

        foreach (var sortMode in new[] { PfSortMode.NativeDefault, PfSortMode.TimeRemaining, PfSortMode.SlotsFilled, PfSortMode.ItemLevel }) {
            dimFilter.SortMode = sortMode;
            var result = PfFilterEngine.FilterAndSort(
                [blockedBest, normalLower],
                dimFilter,
                playerClassJobId: null,
                currentPageGeneration: null,
                nativeOrderedListingIds: sortMode == PfSortMode.NativeDefault ? [201UL, 202UL] : null);
            Assert.Equal([202UL, 201UL], result.Select(e => e.Id).ToArray());
        }

        var hideFilter = new PfFilterState {
            BlockedDisplayMode = PfBlockedDisplayMode.Hide,
            IncludeSampleListings = false,
        };
        var hiddenResult = PfFilterEngine.FilterAndSort([blockedBest, normalLower], hideFilter, playerClassJobId: null);
        Assert.Equal([202UL], hiddenResult.Select(e => e.Id).ToArray());

        var areaVisible = PfFilterEngine.ComputeAreaCounts([blockedBest, normalLower], includeSampleListings: false, hideBlocked: false);
        var areaHidden = PfFilterEngine.ComputeAreaCounts([blockedBest, normalLower], includeSampleListings: false, hideBlocked: true);
        Assert.Equal(2, areaVisible[PfSearchAreaTab.All]);
        Assert.Equal(1, areaHidden[PfSearchAreaTab.All]);

        var catHidden = PfFilterEngine.ComputeCategoryCounts([blockedBest, normalLower], PfSearchAreaTab.All, includeSampleListings: false, hideBlocked: true);
        Assert.Equal(1, catHidden["all"]);
        Assert.Equal(1, catHidden["highend"]);
    }

    [Fact]
    public void LocalMirrorStore_detects_blocked_leaders_and_members_dynamically_by_name_world_and_content_id() {
        var now = new DateTime(2026, 11, 28, 13, 15, 0, DateTimeKind.Utc);
        using var store = CreateStore(() => now);

        store.UpsertLiveListing(new PfListingEntry {
            Id = 301,
            LeaderContentId = 70001UL,
            LeaderName = "Alice",
            HomeWorldName = "Chocobo",
            DutyName = "Blocked Same World",
            CategoryRaw = CatHighEndDuty,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 4,
            SlotsFilled = 2,
            JobsPresent = [19, 24, 0, 0],
            MemberContentIds = [70001UL, 70002UL, 0, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        store.UpsertLiveListing(new PfListingEntry {
            Id = 302,
            LeaderContentId = 70003UL,
            LeaderName = "Alice",
            HomeWorldName = "Shinryu",
            DutyName = "Same Name Other World",
            CategoryRaw = CatHighEndDuty,
            SearchAreaRaw = AreaDataCenter,
            SlotsAvailable = 4,
            SlotsFilled = 1,
            JobsPresent = [19, 0, 0, 0],
            MemberContentIds = [70003UL, 0, 0, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });

        store.SetBlockedPlayersForTesting(["Alice@Chocobo"], [70002UL]);

        var snapshot = store.GetListingsSnapshot(includeSamples: false, useSampleEnrichment: false);
        Assert.True(snapshot.Single(l => l.Id == 301UL).IsLeaderBlocked);
        Assert.False(snapshot.Single(l => l.Id == 302UL).IsLeaderBlocked);

        var listing301 = store.TryGetListing(301UL, includeSamples: false, useSampleEnrichment: false)!;
        var rows301 = store.BuildMemberRows(listing301, useSampleEnrichment: false, playerClassJobId: null);
        Assert.True(rows301[0].IsBlocked);
        Assert.True(rows301[1].IsBlocked);
        Assert.False(rows301[2].IsBlocked);

        var samples = store.GetListingsSnapshot(includeSamples: true, useSampleEnrichment: true);
        Assert.Contains(samples, s => s.IsSample && s.IsLeaderBlocked);
        Assert.Contains(samples, s => s.IsSample && s.IsDutyUnopened);

        store.SetUnopenedListingsForTesting([302UL]);
        var updatedSnapshot = store.GetListingsSnapshot(includeSamples: false, useSampleEnrichment: false);
        Assert.False(updatedSnapshot.Single(l => l.Id == 301UL).IsDutyUnopened);
        Assert.True(updatedSnapshot.Single(l => l.Id == 302UL).IsDutyUnopened);
    }

    [Fact]
    public void NativePfBridge_queues_hidden_native_host_ensure_and_release_actions() {
        var executed = new List<NativePfAction>();
        using var bridge = new NativePfBridge(action => executed.Add(action));

        bridge.QueueEnsureHiddenNativePfHost();
        bridge.QueueHideNativePfWindow();
        bridge.QueueOpenDetailedSearch();
        bridge.QueueReleaseHiddenNativePfHost();

        Assert.Equal(4, bridge.QueuedCount);
        Assert.Equal(4, bridge.DrainQueue());
        Assert.Equal(NativePfActionKind.EnsureHiddenNativePfHost, executed[0].Kind);
        Assert.Equal(NativePfActionKind.HideNativePfWindow, executed[1].Kind);
        Assert.Equal(NativePfActionKind.OpenDetailedSearch, executed[2].Kind);
        Assert.Equal(NativePfActionKind.ReleaseHiddenNativePfHost, executed[3].Kind);

        Assert.False(PfNativeUiSnapshot.Empty.IsNativeAddonVisible);
        Assert.False(PfNativeUiSnapshot.Empty.IsDetailedSearchActive);
        var visibleSnapshot = PfNativeUiSnapshot.Empty with { IsNativeAddonOpen = true, IsNativeAddonVisible = true, CategoryTab = 17 };
        Assert.True(visibleSnapshot.IsNativeAddonVisible);
        Assert.True(visibleSnapshot.IsDetailedSearchActive);
    }

    [Fact]
    public void LocalMirrorStore_extracts_category_counts_from_number_array_including_area_totals_and_clamping_negatives() {
        var ints = new int[429];
        Array.Fill(ints, -1);
        ints[402] = 395; // All
        ints[403] = 0;   // World
        ints[404] = 28;  // Private
        ints[405] = 71;  // Other (index 16)
        ints[406] = 1;   // Roulette (index 1)
        ints[409] = 22;  // Trials (index 4)
        ints[411] = 64;  // High-End Duty (index 6)
        ints[420] = 3;   // V&C (index 15)

        var allAreaCounts = PfLocalMirrorStore.ExtractCategoryCountsFromNumberArray(ints, areaTab: 0);
        Assert.Equal(PfIconCatalog.CategoryTabs.Count, allAreaCounts.Length);
        Assert.Equal(395, allAreaCounts[0]);
        Assert.Equal(1, allAreaCounts[1]);
        Assert.Equal(0, allAreaCounts[2]); // -1 clamped to 0
        Assert.Equal(22, allAreaCounts[4]);
        Assert.Equal(64, allAreaCounts[6]);
        Assert.Equal(3, allAreaCounts[15]);
        Assert.Equal(71, allAreaCounts[16]);

        var privateAreaCounts = PfLocalMirrorStore.ExtractCategoryCountsFromNumberArray(ints, areaTab: 2);
        Assert.Equal(28, privateAreaCounts[0]);
    }

    [Fact]
    public void FilterAndSort_normalizes_fullwidth_halfwidth_and_kana_with_space_and_exclusion_terms() {
        var now = new DateTime(2026, 11, 28, 14, 0, 0, DateTimeKind.Utc);
        var listings = new[] {
            new PfListingEntry {
                Id = 401,
                DutyName = "絶竜詩戦争",
                LeaderName = "Taitama",
                HomeWorldName = "Fenrir",
                DataCenterName = "Gaia",
                Comment = "P6練習 リリド MT〆",
                CategoryRaw = CatHighEndDuty,
                SearchAreaRaw = AreaDataCenter,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            },
            new PfListingEntry {
                Id = 402,
                DutyName = "絶竜詩戦争",
                LeaderName = "Milo",
                HomeWorldName = "Tonberry",
                DataCenterName = "Elemental",
                Comment = "P5死刻から りりど ＳＴＤ２〆 １飯ＲＣ 済",
                CategoryRaw = CatHighEndDuty,
                SearchAreaRaw = AreaDataCenter,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            },
            new PfListingEntry {
                Id = 403,
                DutyName = "絶アレキサンダー討滅戦",
                LeaderName = "Noah",
                HomeWorldName = "Tonberry",
                DataCenterName = "Elemental",
                Comment = "ﾘﾘﾄﾞ P1 ぬけまる",
                CategoryRaw = CatHighEndDuty,
                SearchAreaRaw = AreaDataCenter,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            },
        };

        var filter = new PfFilterState { IncludeSampleListings = false };

        filter.SearchText = "りりど";
        Assert.Equal([403UL, 402UL, 401UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "リリド　1飯 rc";
        Assert.Equal([402UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "d2";
        Assert.Equal([402UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "ヌケマル";
        Assert.Equal([403UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "竜詩 りりど";
        Assert.Equal([402UL, 401UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "りりど -済";
        Assert.Equal([403UL, 401UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "りりど －練習";
        Assert.Equal([403UL, 402UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "-練習";
        Assert.Equal([403UL, 402UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());

        filter.SearchText = "りりど -";
        Assert.Equal([403UL, 402UL, 401UL], PfFilterEngine.FilterAndSort(listings, filter, null).Select(e => e.Id).ToArray());
    }

    [Fact]
    public void UiLocalization_provides_all_categories_sort_blocked_and_role_labels_across_client_languages() {
        var languages = Enum.GetValues<PfUiLanguage>();
        Assert.Equal(4, languages.Length);

        foreach (var lang in languages) {
            var sortLabels = PfUiText.GetSortLabels(lang);
            Assert.Equal(4, sortLabels.Length);
            Assert.All(sortLabels, label => Assert.False(string.IsNullOrWhiteSpace(label)));

            var blockedLabels = PfUiText.GetBlockedLabels(lang);
            Assert.Equal(2, blockedLabels.Length);
            Assert.All(blockedLabels, label => Assert.False(string.IsNullOrWhiteSpace(label)));

            Assert.False(string.IsNullOrWhiteSpace(PfUiText.CategoryLabel(lang, "all", "ALL")));
            Assert.False(string.IsNullOrWhiteSpace(PfUiText.CategoryLabel(lang, "other", "OTH")));
            Assert.Equal("Fallback", PfUiText.CategoryLabel(lang, "unknown_tab_id", "Fallback"));
            Assert.False(string.IsNullOrWhiteSpace(PfUiText.FormatRemainingTime(185, lang)));
        }

        Assert.Equal("すべて", PfUiText.CategoryLabel(PfUiLanguage.Japanese, "all", "ALL"));
        Assert.Equal("All", PfUiText.CategoryLabel(PfUiLanguage.English, "all", "ALL"));

        var enRole = PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(0x1UL);
        var jaRole = PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(0x1UL, PfUiLanguage.Japanese);
        Assert.False(string.IsNullOrWhiteSpace(enRole));
        Assert.False(string.IsNullOrWhiteSpace(jaRole));
        Assert.NotEqual(enRole, jaRole);

        var jaCatalog = new PfIconCatalog(PfUiLanguage.Japanese);
        Assert.Equal(PfUiLanguage.Japanese, jaCatalog.UiLanguage);
        Assert.Equal("設定なし", PfUiText.NoneSpecified(PfUiLanguage.Japanese));
        Assert.Equal("None Specified", PfUiText.NoneSpecified(PfUiLanguage.English));
    }

    private static PfListingEntry CreateLiveTestListing(ulong id, int batchNumber, DateTime seenUtc, string dutyName) {
        return new PfListingEntry {
            Id = id,
            DutyName = dutyName,
            DataCenterName = "Mana",
            CategoryRaw = CatHighEndDuty,
            SearchAreaRaw = AreaDataCenter,
            LastBatchNumber = batchNumber,
            FirstSeenUtc = seenUtc,
            LastSeenUtc = seenUtc,
        };
    }
}
