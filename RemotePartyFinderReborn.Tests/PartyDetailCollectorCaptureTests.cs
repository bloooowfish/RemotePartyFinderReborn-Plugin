using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyDetailCollectorCaptureTests {
    static PartyDetailCollectorCaptureTests() {
        DalamudAssemblyResolver.Register();
    }

    [Fact]
    public void Update_enqueues_only_once_for_same_arrival_generation() {
        var state = new PartyDetailCaptureState();
        var harness = new CollectorHarness(state);
        var collector = harness.CreateCollector();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Manual, 9001U, 44UL);
        var snapshot = CreateCompleteSnapshot();

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));

        collector.Update();
        collector.Update();

        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, state.LastConsumedGeneration);
    }

    [Fact]
    public void Update_preserves_alliance_detail_payload_without_truncating_to_eight_slots() {
        var state = new PartyDetailCaptureState();
        var harness = new CollectorHarness(state);
        var collector = harness.CreateCollector();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var snapshot = CreateAllianceSnapshot();

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));

        collector.Update();

        var captured = Assert.Single(harness.CapturedPayloads);
        Assert.Equal(24, captured.MemberContentIds.Count);
        Assert.Equal(24, captured.MemberJobs.Count);
        Assert.Equal(24, captured.SlotFlags.Count);
        Assert.Equal(10_008UL, captured.MemberContentIds[8]);
        Assert.Equal(10_023UL, captured.MemberContentIds[23]);
        Assert.Equal((byte)22, captured.MemberJobs[23]);
        Assert.Equal("0x0000000000000017", captured.SlotFlags[23]);
        Assert.Equal(1L, state.LastConsumedGeneration);
    }

    [Fact]
    public void Update_retries_same_generation_when_enqueue_fails() {
        var state = new PartyDetailCaptureState();
        var harness = new CollectorHarness(state, failFirstEnqueue: true);
        var collector = harness.CreateCollector();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Manual, 9001U, 44UL);
        var snapshot = CreateCompleteSnapshot();

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));

        collector.Update();
        collector.Update();

        Assert.Equal(2, harness.EnqueueAttempts);
        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, state.LastConsumedGeneration);
    }

    [Fact]
    public void Manual_request_never_routes_through_scanner_headless_boundary() {
        using var harness = new ScannerHeadlessBoundaryHarness();
        var snapshot = CreateCompleteSnapshot();

        harness.BeginManualRequest(9001U, 44UL);
        harness.ObserveSharedPopulate();
        harness.Tick(snapshot);
        harness.UpdateCollector();

        Assert.False(harness.ScannerHeadlessBoundaryTouched);
        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, harness.CaptureState.LastConsumedGeneration);
    }

    [Fact]
    public void Manual_request_updates_latest_pf_detail_debug_snapshot() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        using var httpClient = new HttpClient(new StubHttpMessageHandler());
        var collector = new PartyDetailCollector(
            state,
            new Configuration {
                UploadUrls = [],
            },
            httpClient
        );
        var snapshot = CreateCompleteSnapshot();
        var debugSnapshot = new PartyDetailDebugSnapshot {
            CapturedAtUtc = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            RawListingId = 9001UL,
            UploadListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            ObjectiveRaw = 4,
            ObjectiveName = "Practice",
            Members = [new PartyDetailDebugMember(0, 44UL, 19, "0x0000000000000001")],
        };

        runtime.BeginManualCycle(9001U, 44UL);
        runtime.ObservePopulationEvent();
        runtime.Tick(snapshot, debugSnapshot);
        collector.Update();

        Assert.True(state.TryGetLatestDebugSnapshot(out var latest));
        Assert.Equal(PartyDetailRequestOwner.Manual, latest.Cycle.Owner);
        Assert.Equal(9001UL, latest.Cycle.ListingId);
        Assert.Equal(4, latest.Snapshot.ObjectiveRaw);
        var queue = collector.GetUploadQueueDebugState();
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.ManualPendingCount);
        Assert.Equal(0, queue.ScannerPendingCount);
    }

    [Fact]
    public void Scanner_headless_scope_is_disabled_by_default() {
        using var harness = new ScannerHeadlessBoundaryHarness();
        var snapshot = CreateCompleteSnapshot();

        harness.BeginScannerTarget(9001U, 44UL);

        Assert.False(harness.TryBeginScannerHeadlessScope());
        Assert.False(harness.TryRecordScannerHeadlessArrival(snapshot));
        Assert.False(harness.ScannerHeadlessBoundaryTouched);
    }

    [Fact]
    public void Scanner_headless_scope_respects_allow_headless_flag() {
        using var harness = new ScannerHeadlessBoundaryHarness(ScannerHeadlessBackendKind.PopulateCorrelation);

        harness.BeginScannerTarget(9001U, 44UL, allowHeadless: false);

        Assert.False(harness.TryBeginScannerHeadlessScope());
        Assert.False(harness.ScannerHeadlessBoundaryTouched);
    }

    [Fact]
    public void Scanner_headless_arrival_must_reenter_shared_capture_state_and_collector() {
        using var harness = new ScannerHeadlessBoundaryHarness(ScannerHeadlessBackendKind.PopulateCorrelation);
        var snapshot = CreateCompleteSnapshot();

        harness.BeginScannerTarget(9001U, 44UL);
        harness.ObserveSharedPopulate();

        Assert.True(harness.TryBeginScannerHeadlessScope());
        Assert.True(harness.TryRecordScannerHeadlessArrival(snapshot));

        harness.UpdateCollector();

        Assert.True(harness.ScannerHeadlessBoundaryTouched);
        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, harness.CaptureState.LastConsumedGeneration);
    }

    [Fact]
    public void Scanner_request_suppresses_open_but_captures_once() {
        using var harness = new NativeSuppressionHarness();
        var snapshot = CreateCompleteSnapshot();

        harness.BeginScannerTarget(9001U, 44UL, allowHeadless: true);

        Assert.True(harness.RaisePfDetailOpen());

        harness.ObserveSharedPopulate();
        harness.Tick(snapshot);
        harness.UpdateCollector();

        Assert.Equal(1, harness.PfDetailOpenInterceptCount);
        Assert.Equal(0, harness.PfDetailOpenOriginalCallCount);
        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, harness.CaptureState.LastConsumedGeneration);
    }

    [Fact]
    public void Scanner_attempt_suppresses_repeated_open_until_complete() {
        using var harness = new NativeSuppressionHarness();
        var snapshot = CreateCompleteSnapshot();

        harness.BeginScannerTarget(9001U, 44UL, allowHeadless: true);

        Assert.True(harness.RaisePfDetailOpen());

        harness.ObserveSharedPopulate();
        harness.Tick(snapshot);
        harness.UpdateCollector();

        Assert.True(harness.RaisePfDetailOpen());

        harness.CompleteScannerAttempt(PartyDetailScannerAttemptOutcome.Succeeded);

        Assert.False(harness.RaisePfDetailOpen());
        Assert.Equal(3, harness.PfDetailOpenInterceptCount);
        Assert.Equal(1, harness.PfDetailOpenOriginalCallCount);
    }

    [Fact]
    public void Terminal_scanner_open_failed_releases_pf_detail_open_suppression() {
        using var harness = new NativeSuppressionHarness();

        harness.BeginScannerTarget(9001U, 44UL, allowHeadless: true);
        harness.CompleteScannerAttempt(PartyDetailScannerAttemptOutcome.OpenFailed);

        Assert.False(harness.RaisePfDetailOpen());
        Assert.Equal(1, harness.PfDetailOpenInterceptCount);
        Assert.Equal(1, harness.PfDetailOpenOriginalCallCount);
        Assert.Equal(PartyDetailRequestOwner.Manual, harness.CaptureState.CurrentOwner);
    }

    [Fact]
    public void Terminal_scanner_timeout_releases_pf_detail_open_suppression() {
        using var harness = new NativeSuppressionHarness();

        harness.BeginScannerTarget(9001U, 44UL, allowHeadless: true);
        harness.CompleteScannerAttempt(PartyDetailScannerAttemptOutcome.TimedOut);

        Assert.False(harness.RaisePfDetailOpen());
        Assert.Equal(1, harness.PfDetailOpenInterceptCount);
        Assert.Equal(1, harness.PfDetailOpenOriginalCallCount);
        Assert.Equal(PartyDetailRequestOwner.Manual, harness.CaptureState.CurrentOwner);
    }

    [Fact]
    public void New_manual_request_not_suppressed_after_scanner_arrival() {
        using var harness = new NativeSuppressionHarness();
        var snapshot = CreateCompleteSnapshot();

        harness.BeginScannerTarget(9001U, 44UL, allowHeadless: true);
        Assert.True(harness.RaisePfDetailOpen());

        harness.ObserveSharedPopulate();
        harness.Tick(snapshot);
        harness.UpdateCollector();

        harness.BeginManualRequest(9002U, 55UL);

        Assert.False(harness.RaisePfDetailOpen());
        Assert.Equal(2, harness.PfDetailOpenInterceptCount);
        Assert.Equal(1, harness.PfDetailOpenOriginalCallCount);
    }

    [Fact]
    public void Manual_request_never_suppresses_pf_detail_open_and_shared_path_stays_intact() {
        using var harness = new NativeSuppressionHarness();
        var snapshot = CreateCompleteSnapshot();

        harness.BeginManualRequest(9001U, 44UL);

        Assert.False(harness.RaisePfDetailOpen());

        harness.ObserveSharedPopulate();
        harness.Tick(snapshot);
        harness.UpdateCollector();

        Assert.Equal(1, harness.PfDetailOpenInterceptCount);
        Assert.Equal(1, harness.PfDetailOpenOriginalCallCount);
        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, harness.CaptureState.LastConsumedGeneration);
    }

    [Fact]
    public void Manual_pf_detail_addon_open_without_listing_hook_updates_latest_debug_snapshot() {
        using var harness = new NativeSuppressionHarness();
        var snapshot = CreateCompleteSnapshot();
        var debugSnapshot = new PartyDetailDebugSnapshot {
            CapturedAtUtc = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            RawListingId = 9001UL,
            UploadListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            ObjectiveRaw = 2,
            ObjectiveName = "DutyCompletion",
            Members = [new PartyDetailDebugMember(0, 44UL, 19, "0x0000000000000001")],
        };

        Assert.False(harness.RaisePfDetailOpen());
        harness.Tick(snapshot, debugSnapshot);

        Assert.True(harness.CaptureState.TryGetLatestDebugSnapshot(out var latest));
        Assert.Equal(PartyDetailRequestOwner.Manual, latest.Cycle.Owner);
        Assert.Equal(9001U, latest.Snapshot.UploadListingId);
        Assert.Equal(2, latest.Snapshot.ObjectiveRaw);
    }

    [Fact]
    public void Scanner_request_with_allowHeadless_false_does_not_suppress_pf_detail_open() {
        using var harness = new NativeSuppressionHarness();

        harness.BeginScannerTarget(9001U, 44UL, allowHeadless: false);

        Assert.False(harness.RaisePfDetailOpen());
        Assert.Equal(1, harness.PfDetailOpenOriginalCallCount);
    }

    [Fact]
    public void ComposePartyDetailCapture_reuses_single_capture_state_for_runtime_and_collector() {
        var composition = Plugin.ComposePartyDetailCapture(
            plugin: null,
            runtimeFactory: static state => new PartyDetailCaptureRuntime(state),
            collectorFactory: static state => new PartyDetailCollector(
                state,
                tryQueuePayload: static _ => true,
                pumpPendingUploads: static () => { }
            )
        );

        Assert.Same(composition.CaptureState, composition.CaptureRuntime.CaptureState);
        Assert.Same(composition.CaptureState, composition.PartyDetailCollector.CaptureState);
    }

    [Fact]
    public void Reopen_after_new_population_requeues_matching_payload() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var harness = new CollectorHarness(state);
        var collector = harness.CreateCollector();
        var snapshot = CreateCompleteSnapshot();

        runtime.BeginManualCycle(9001U, 44UL);
        runtime.ObservePopulationEvent();
        runtime.Tick(snapshot);
        collector.Update();

        runtime.BeginManualCycle(9001U, 44UL);
        runtime.ObservePopulationEvent();
        runtime.Tick(snapshot);
        collector.Update();

        Assert.Equal(2, harness.CapturedPayloads.Count);
        Assert.Equal(2L, state.LastConsumedGeneration);
    }

    [Fact]
    public void Reopen_waits_for_new_population_snapshot() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var harness = new CollectorHarness(state);
        var collector = harness.CreateCollector();
        var snapshot = CreateCompleteSnapshot();

        runtime.Tick(snapshot);
        runtime.BeginManualCycle(9001U, 44UL);

        runtime.Tick(snapshot);
        collector.Update();

        Assert.Empty(harness.CapturedPayloads);
        Assert.Equal(0L, state.LastConsumedGeneration);

        runtime.ObservePopulationEvent();
        collector.Update();

        Assert.Empty(harness.CapturedPayloads);
        Assert.Equal(0L, state.LastConsumedGeneration);

        runtime.Tick(snapshot);
        collector.Update();

        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, state.LastConsumedGeneration);
    }

    [Fact]
    public void Valid_snapshot_can_record_without_visibility_state_after_population_event() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var harness = new CollectorHarness(state);
        var collector = harness.CreateCollector();
        var snapshot = CreateCompleteSnapshot();

        runtime.BeginManualCycle(9001U, 44UL);
        runtime.ObservePopulationEvent();
        collector.Update();

        Assert.Empty(harness.CapturedPayloads);
        Assert.Equal(0L, state.LastConsumedGeneration);

        runtime.Tick(snapshot);
        collector.Update();

        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, state.LastConsumedGeneration);
    }

    [Fact]
    public void Incomplete_snapshot_is_not_consumed_and_retries_on_next_tick() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var harness = new CollectorHarness(state);
        var collector = harness.CreateCollector();
        var incompleteSnapshot = CreateIncompleteSnapshot();
        var completeSnapshot = CreateCompleteSnapshot();

        runtime.BeginManualCycle(9001U, 44UL);
        runtime.ObservePopulationEvent();
        runtime.Tick(incompleteSnapshot);
        collector.Update();

        Assert.Empty(harness.CapturedPayloads);
        Assert.Equal(0L, state.LastConsumedGeneration);

        runtime.Tick(completeSnapshot);
        collector.Update();

        Assert.Single(harness.CapturedPayloads);
        Assert.Equal(1L, state.LastConsumedGeneration);
    }

    [Fact]
    public void Runtime_and_collector_keep_the_same_shared_capture_state_instance() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var harness = new CollectorHarness(state);
        var collector = harness.CreateCollector();

        Assert.Same(state, runtime.CaptureState);
        Assert.Same(state, collector.CaptureState);
    }

    [Fact]
    public void DalamudPartyDetailCaptureHookFactory_uses_expected_ffxiv_client_structs_delegate_signatures_and_rejects_zero_addresses() {
        var ffxivAsm = Assembly.Load("FFXIVClientStructs");
        var openListingDelegate = ffxivAsm.GetType("FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup+Delegates+OpenListing");
        var openListingByContentIdDelegate = ffxivAsm.GetType("FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup+Delegates+OpenListingByContentId");
        var populateDelegate = ffxivAsm.GetType("FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup+Delegates+PopulateListingData");
        var openAddonDelegate = ffxivAsm.GetType("FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule+Delegates+OpenAddon");

        Assert.NotNull(openListingDelegate);
        Assert.NotNull(openListingByContentIdDelegate);
        Assert.NotNull(populateDelegate);
        Assert.NotNull(openAddonDelegate);
        Assert.Equal(typeof(bool), openListingDelegate.GetMethod("Invoke")?.ReturnType);
        Assert.Equal(typeof(bool), openListingByContentIdDelegate.GetMethod("Invoke")?.ReturnType);
        Assert.Equal(typeof(void), populateDelegate.GetMethod("Invoke")?.ReturnType);
        Assert.Equal(typeof(ushort), openAddonDelegate.GetMethod("Invoke")?.ReturnType);

        var openAddonParams = openAddonDelegate.GetMethod("Invoke")!.GetParameters();
        Assert.Equal(8, openAddonParams.Length);
        Assert.Equal(typeof(uint), openAddonParams[1].ParameterType);
        Assert.Equal(typeof(uint), openAddonParams[2].ParameterType);
        Assert.Equal(typeof(ulong), openAddonParams[5].ParameterType);
        Assert.Equal(typeof(ushort), openAddonParams[6].ParameterType);
        Assert.Equal(typeof(int), openAddonParams[7].ParameterType);

        var dalamudAsm = Assembly.Load("Dalamud");
        var interopProviderType = dalamudAsm.GetType("Dalamud.Plugin.Services.IGameInteropProvider");
        Assert.NotNull(interopProviderType);

        var createMethod = typeof(DispatchProxy).GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            [typeof(Type), typeof(Type)],
            null
        );
        Assert.NotNull(createMethod);
        var proxyInteropProvider = createMethod.Invoke(null, [interopProviderType, typeof(NoOpInteropProxy)])!;
        var factoryCtor = typeof(PartyDetailCaptureRuntime.DalamudPartyDetailCaptureHookFactory).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            [interopProviderType],
            null
        );
        Assert.NotNull(factoryCtor);
        var factory = (PartyDetailCaptureRuntime.DalamudPartyDetailCaptureHookFactory)factoryCtor.Invoke([proxyInteropProvider]);

        var zeroPopulateEx = Assert.Throws<InvalidOperationException>(() =>
            factory.CreateHooksFromAddresses(
                0x1000,
                0x2000,
                0,
                0x4000,
                static (_, _) => true,
                static (_, _) => true,
                static (_, _) => { },
                static (_, _, _, _, _, _, _, _) => true
            ));
        Assert.Contains("PopulateListingData", zeroPopulateEx.Message);

        var warnings = new List<string>();
        using var runtime = PartyDetailCaptureRuntime.CreateForTesting(
            new PartyDetailCaptureState(),
            factory,
            warnings.Add
        );
        var warning = Assert.Single(warnings);
        Assert.Contains("failed to initialize hooks", warning);
        Assert.False(runtime.IsManualCaptureReady);
        Assert.False(runtime.IsScannerCaptureReady);
    }

    [Fact]
    public void Decoupled_hooks_keep_manual_capture_active_when_optional_scanner_hooks_fail() {
        var state = new PartyDetailCaptureState();
        var partialFactory = new CapturingFullHookFactory(
            hasOpenListingHook: false,
            hasOpenListingByContentIdHook: true,
            hasOpenAddonHook: true
        );
        using var runtime = PartyDetailCaptureRuntime.CreateForTesting(
            state,
            partialFactory,
            warningSink: null,
            scannerHeadlessBackendKind: ScannerHeadlessBackendKind.NativeSuppression
        );

        Assert.True(runtime.IsManualCaptureReady);
        Assert.False(runtime.IsScannerCaptureReady);

        // Even with an armed scanner attempt, OpenAddon suppression stays disabled when IsScannerCaptureReady is false.
        var attemptId = Guid.NewGuid();
        runtime.ArmScannerRequest(attemptId, 9001UL, 44UL, allowHeadless: true);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.TestInterceptOpenListing(9001UL, 44UL);
        runtime.EndScannerOpenAttempt(attemptId);

        Assert.True(partialFactory.PfDetailOpenDetour(0, PartyDetailCaptureRuntime.DefaultPfDetailAddonId, 0, 0, 0, 0, 0, 0));

        // Manual capture via PopulateListingData still triggers population signal and captures snapshot.
        runtime.TestBeginManualRequestCycle(9001UL, 44UL);
        partialFactory.RaisePopulateListingData(0, 0);
        runtime.TestFrameworkTick(CreateCompleteSnapshot());

        Assert.True(state.TryGetNextUnconsumedArrival(out var arrival));
        Assert.Equal(9001UL, arrival.Payload.ListingId);
    }

    [Fact]
    public void Dynamic_pf_detail_addon_id_falls_back_to_275_when_unavailable_then_caches_resolved_id() {
        var state = new PartyDetailCaptureState();
        var hookFactory = new CapturingFullHookFactory();
        uint? resolvedId = null;
        var resolverCalls = 0;
        using var runtime = PartyDetailCaptureRuntime.CreateForTesting(
            state,
            hookFactory,
            warningSink: null,
            scannerHeadlessBackendKind: ScannerHeadlessBackendKind.NativeSuppression,
            pfDetailAddonIdResolver: () => {
                resolverCalls++;
                return resolvedId;
            }
        );

        Assert.True(runtime.IsScannerCaptureReady);
        Assert.Equal(275U, runtime.ResolvePfDetailAddonId());
        Assert.Equal(1, resolverCalls);

        // Once AddonNames becomes available, the resolved addon ID is cached.
        resolvedId = 281U;
        Assert.Equal(281U, runtime.ResolvePfDetailAddonId());
        Assert.Equal(281U, runtime.ResolvePfDetailAddonId());
        Assert.Equal(2, resolverCalls);

        // Scanner suppression now targets the resolved addon ID (281U), not stale 275U.
        var attemptId = Guid.NewGuid();
        runtime.ArmScannerRequest(attemptId, 9001UL, 44UL, allowHeadless: true);
        runtime.BeginScannerOpenAttempt(attemptId);
        Assert.True(hookFactory.RaiseOpenListing(0, 9001UL));
        runtime.EndScannerOpenAttempt(attemptId);

        Assert.True(hookFactory.PfDetailOpenDetour(0, 275U, 0, 0, 0, 0, 0, 0));
        Assert.False(hookFactory.PfDetailOpenDetour(0, 281U, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public unsafe void PartyDetailSnapshotBuilder_respects_4_man_total_slots_and_prefers_viewed_listing_leader_name() {
        Assert.Equal(4, PartyDetailSnapshotBuilder.ComputeSlotCount(totalSlots: 4, numberOfParties: 1));
        Assert.Equal(8, PartyDetailSnapshotBuilder.ComputeSlotCount(totalSlots: 0, numberOfParties: 1));
        Assert.Equal(24, PartyDetailSnapshotBuilder.ComputeSlotCount(totalSlots: 24, numberOfParties: 3));
        Assert.Equal(48, PartyDetailSnapshotBuilder.ComputeSlotCount(totalSlots: 64, numberOfParties: 6));

        var agent = (AgentLookingForGroup*)NativeMemory.AllocZeroed((nuint)sizeof(AgentLookingForGroup));
        try {
            agent->LastViewedListing.ListingId = 5_000_000_001UL;
            agent->LastViewedListing.LeaderContentId = 101UL;
            agent->LastViewedListing.HomeWorld = 74;
            agent->LastViewedListing.World = 74;
            agent->LastViewedListing.TotalSlots = 4;
            agent->LastViewedListing.NumberOfParties = 1;
            for (var i = 0; i < 8; i++) {
                agent->LastViewedListing.MemberContentIds[i] = 101UL + (ulong)i;
                agent->LastViewedListing.Jobs[i] = (byte)(19 + i);
            }

            Encoding.UTF8.GetBytes("Viewed Leader").CopyTo(agent->LastViewedListing.Leader);

            Assert.True(PartyDetailSnapshotBuilder.TryBuildFromAgent(agent, out var snapshot, out var debugSnapshot));
            Assert.Equal(5_000_000_001UL, snapshot.ListingId);
            Assert.Equal(4, snapshot.MemberContentIds.Count);
            Assert.Equal(4, snapshot.MemberJobs.Count);
            Assert.Equal(4, snapshot.SlotFlags.Count);
            Assert.Equal("Viewed Leader", snapshot.LeaderName);
            Assert.Equal("Viewed Leader", debugSnapshot!.LeaderName);

            // When viewedListing.Leader is empty, fall back to LastLeader.
            agent->LastViewedListing.Leader.Clear();
            var fallbackBytes = Encoding.UTF8.GetBytes("Fallback Leader\0");
            var fallbackBuf = (byte*)NativeMemory.AllocZeroed((nuint)fallbackBytes.Length);
            try {
                Marshal.Copy(fallbackBytes, 0, (nint)fallbackBuf, fallbackBytes.Length);
                agent->LastLeader.StringPtr = fallbackBuf;
                agent->LastLeader.BufSize = fallbackBytes.Length;
                agent->LastLeader.BufUsed = fallbackBytes.Length;
                agent->LastLeader.StringLength = fallbackBytes.Length - 1;

                Assert.True(PartyDetailSnapshotBuilder.TryBuildFromAgent(agent, out var fallbackSnapshot, out var fallbackDebug));
                Assert.Equal("Fallback Leader", fallbackSnapshot.LeaderName);
                Assert.Equal("Fallback Leader", fallbackDebug!.LeaderName);
            } finally {
                NativeMemory.Free(fallbackBuf);
            }
        } finally {
            NativeMemory.Free(agent);
        }
    }

    [Fact]
    public void OpenAddon_wrapper_returns_zero_ushort_when_suppressed_and_original_ushort_when_allowed() {
        var state = new PartyDetailCaptureState();
        var hookFactory = new CapturingFullHookFactory();
        using var runtime = PartyDetailCaptureRuntime.CreateForTesting(
            state,
            hookFactory,
            warningSink: null,
            scannerHeadlessBackendKind: ScannerHeadlessBackendKind.NativeSuppression
        );

        var attemptId = Guid.NewGuid();
        runtime.ArmScannerRequest(attemptId, 9001UL, 44UL, allowHeadless: true);
        runtime.BeginScannerOpenAttempt(attemptId);
        Assert.True(hookFactory.RaiseOpenListing(0, 9001UL));
        runtime.EndScannerOpenAttempt(attemptId);

        var originalCalls = 0;
        var suppressedResult = PartyDetailCaptureRuntime.DalamudPartyDetailCaptureHookFactory.InvokeOpenAddonDetour(
            hookFactory.PfDetailOpenDetour,
            () => {
                originalCalls++;
                return (ushort)77;
            },
            0,
            275U,
            0,
            0,
            0,
            0,
            0,
            4
        );

        Assert.Equal((ushort)0, suppressedResult);
        Assert.Equal(0, originalCalls);

        runtime.CompleteScannerRequest(attemptId, PartyDetailScannerAttemptOutcome.Succeeded);

        var allowedResult = PartyDetailCaptureRuntime.DalamudPartyDetailCaptureHookFactory.InvokeOpenAddonDetour(
            hookFactory.PfDetailOpenDetour,
            () => {
                originalCalls++;
                return (ushort)77;
            },
            0,
            275U,
            0,
            0,
            0,
            0,
            0,
            4
        );

        Assert.Equal((ushort)77, allowedResult);
        Assert.Equal(1, originalCalls);
    }

    [Fact]
    public void OpenListing_and_OpenListingByContentId_hook_detours_begin_capture_cycles() {
        var state = new PartyDetailCaptureState();
        var hookFactory = new CapturingFullHookFactory();
        using var runtime = PartyDetailCaptureRuntime.CreateForTesting(state, hookFactory);

        Assert.True(hookFactory.RaiseOpenListing(0, 9001UL));
        Assert.True(state.TryGetCurrentRequestCycle(out var firstCycle));
        Assert.Equal(PartyDetailRequestOwner.Manual, firstCycle.Owner);
        Assert.Equal(9001UL, firstCycle.ListingId);
        Assert.Equal(0UL, firstCycle.ContentId);

        Assert.True(hookFactory.RaiseOpenListingByContentId(0, 44UL));
        Assert.True(state.TryGetCurrentRequestCycle(out var secondCycle));
        Assert.Equal(PartyDetailRequestOwner.Manual, secondCycle.Owner);
        Assert.Equal(0UL, secondCycle.ListingId);
        Assert.Equal(44UL, secondCycle.ContentId);
    }

    private static UploadablePartyDetail CreateCompleteSnapshot() {
        return new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 77,
            MemberContentIds = [44UL, 55UL],
            MemberJobs = [19, 24],
            SlotFlags = ["0x0000000000000000", "0x0000000000000000"],
        };
    }

    private static UploadablePartyDetail CreateAllianceSnapshot() {
        var memberContentIds = new List<ulong>(24);
        var memberJobs = new List<byte>(24);
        var slotFlags = new List<string>(24);

        for (var index = 0; index < 24; index++) {
            memberContentIds.Add(10_000UL + (ulong)index);
            memberJobs.Add((byte)(19 + index % 10));
            slotFlags.Add($"0x{(ulong)index:X16}");
        }

        memberContentIds[0] = 44UL;

        return new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 77,
            MemberContentIds = memberContentIds,
            MemberJobs = memberJobs,
            SlotFlags = slotFlags,
        };
    }

    private static UploadablePartyDetail CreateIncompleteSnapshot() {
        return new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 77,
            MemberContentIds = [0UL, 0UL],
            MemberJobs = [19, 24],
            SlotFlags = ["0x0000000000000000", "0x0000000000000000"],
        };
    }

    private sealed class CollectorHarness {
        private readonly PartyDetailCaptureState _state;
        private readonly bool _failFirstEnqueue;

        internal CollectorHarness(PartyDetailCaptureState state, bool failFirstEnqueue = false) {
            _state = state;
            _failFirstEnqueue = failFirstEnqueue;
        }

        internal List<UploadablePartyDetail> CapturedPayloads { get; } = new();
        internal int EnqueueAttempts { get; private set; }

        internal PartyDetailCollector CreateCollector() {
            return new PartyDetailCollector(
                _state,
                tryQueuePayload: payload => {
                    EnqueueAttempts++;
                    if (_failFirstEnqueue && EnqueueAttempts == 1) {
                        return false;
                    }

                    CapturedPayloads.Add(ClonePayload(payload));
                    return true;
                },
                pumpPendingUploads: static () => { }
            );
        }

        private static UploadablePartyDetail ClonePayload(UploadablePartyDetail payload) {
            return new UploadablePartyDetail {
                ListingId = payload.ListingId,
                LeaderContentId = payload.LeaderContentId,
                LeaderName = payload.LeaderName,
                HomeWorld = payload.HomeWorld,
                CreatedWorld = payload.CreatedWorld,
                LastServerRestart = payload.LastServerRestart,
                ObservedAtUnixMs = payload.ObservedAtUnixMs,
                MemberContentIds = [.. payload.MemberContentIds],
                MemberJobs = [.. payload.MemberJobs],
                SlotFlags = [.. payload.SlotFlags],
            };
        }
    }

    private sealed class FakePartyDetailCaptureRuntime : IDisposable {
        private readonly PartyDetailCaptureState _state;
        private readonly PartyDetailCaptureRuntime _runtime;

        internal FakePartyDetailCaptureRuntime(PartyDetailCaptureState state) {
            _state = state;
            _runtime = new PartyDetailCaptureRuntime(state);
        }

        internal void BeginManualCycle(ulong listingId, ulong contentId) {
            _runtime.TestBeginManualRequestCycle(listingId, contentId);
        }

        internal void ObservePopulationEvent() {
            _runtime.TestObservePopulationEvent();
        }

        internal void Tick(UploadablePartyDetail? snapshot) {
            _runtime.TestFrameworkTick(snapshot);
        }

        internal void Tick(UploadablePartyDetail? snapshot, PartyDetailDebugSnapshot debugSnapshot) {
            _runtime.TestFrameworkTick(snapshot, debugSnapshot);
        }

        internal PartyDetailCaptureState CaptureState => _runtime.CaptureState;

        public void Dispose() {
            _runtime.Dispose();
        }
    }

    private sealed class ScannerHeadlessBoundaryHarness : IDisposable {
        private readonly PartyDetailCaptureRuntime _runtime;
        private readonly PartyDetailCollector _collector;
        private readonly Guid _attemptId = Guid.NewGuid();

        internal ScannerHeadlessBoundaryHarness(
            ScannerHeadlessBackendKind backendKind = ScannerHeadlessBackendKind.Disabled
        ) {
            CaptureState = new PartyDetailCaptureState();
            _runtime = PartyDetailCaptureRuntime.CreateForTesting(CaptureState, backendKind);
            _collector = new PartyDetailCollector(
                CaptureState,
                tryQueuePayload: payload => {
                    CapturedPayloads.Add(CloneCapturedPayload(payload));
                    return true;
                },
                pumpPendingUploads: static () => { }
            );
        }

        internal PartyDetailCaptureState CaptureState { get; }
        internal List<UploadablePartyDetail> CapturedPayloads { get; } = new();
        internal bool ScannerHeadlessBoundaryTouched { get; private set; }

        internal void BeginManualRequest(ulong listingId, ulong contentId) {
            _runtime.TestBeginManualRequestCycle(listingId, contentId);
        }

        internal void BeginScannerTarget(ulong listingId, ulong contentId, bool allowHeadless = true) {
            _runtime.ArmScannerRequest(_attemptId, listingId, contentId, allowHeadless);
            _runtime.BeginScannerOpenAttempt(_attemptId);
            _runtime.TestInterceptOpenListing(listingId, contentId);
            _runtime.EndScannerOpenAttempt(_attemptId);
        }

        internal void ObserveSharedPopulate() {
            _runtime.TestObservePopulationEvent();
        }

        internal void Tick(UploadablePartyDetail snapshot) {
            _runtime.TestFrameworkTick(snapshot);
        }

        internal bool TryBeginScannerHeadlessScope() {
            var began = _runtime.TryBeginScannerHeadlessScope(_attemptId);
            ScannerHeadlessBoundaryTouched |= began;
            return began;
        }

        internal bool TryRecordScannerHeadlessArrival(UploadablePartyDetail snapshot) {
            var recorded = _runtime.TryRecordScannerHeadlessArrival(_attemptId, snapshot);
            ScannerHeadlessBoundaryTouched |= recorded;
            return recorded;
        }

        internal void UpdateCollector() {
            _collector.Update();
        }

        public void Dispose() {
            _runtime.Dispose();
        }

        private static UploadablePartyDetail CloneCapturedPayload(UploadablePartyDetail payload) {
            return new UploadablePartyDetail {
                ListingId = payload.ListingId,
                LeaderContentId = payload.LeaderContentId,
                LeaderName = payload.LeaderName,
                HomeWorld = payload.HomeWorld,
                CreatedWorld = payload.CreatedWorld,
                LastServerRestart = payload.LastServerRestart,
                ObservedAtUnixMs = payload.ObservedAtUnixMs,
                MemberContentIds = [.. payload.MemberContentIds],
                MemberJobs = [.. payload.MemberJobs],
                SlotFlags = [.. payload.SlotFlags],
            };
        }
    }

    private sealed class NativeSuppressionHarness : IDisposable {
        private readonly TrackingSuppressionHookFactory _hookFactory;
        private readonly PartyDetailCaptureRuntime _runtime;
        private readonly PartyDetailCollector _collector;
        private readonly Guid _attemptId = Guid.NewGuid();

        internal NativeSuppressionHarness() {
            CaptureState = new PartyDetailCaptureState();
            _hookFactory = new TrackingSuppressionHookFactory();
            _runtime = PartyDetailCaptureRuntime.CreateForTesting(
                CaptureState,
                _hookFactory,
                warningSink: null,
                scannerHeadlessBackendKind: ScannerHeadlessBackendKind.NativeSuppression
            );
            _collector = new PartyDetailCollector(
                CaptureState,
                tryQueuePayload: payload => {
                    CapturedPayloads.Add(ClonePayload(payload));
                    return true;
                },
                pumpPendingUploads: static () => { }
            );
        }

        internal PartyDetailCaptureState CaptureState { get; }
        internal List<UploadablePartyDetail> CapturedPayloads { get; } = new();
        internal int PfDetailOpenInterceptCount => _hookFactory.PfDetailOpenInterceptCount;
        internal int PfDetailOpenOriginalCallCount => _hookFactory.PfDetailOpenOriginalCallCount;

        internal void BeginScannerTarget(ulong listingId, ulong contentId, bool allowHeadless = true) {
            _runtime.ArmScannerRequest(_attemptId, listingId, contentId, allowHeadless);
            _runtime.BeginScannerOpenAttempt(_attemptId);
            _runtime.TestInterceptOpenListing(listingId, contentId);
            _runtime.EndScannerOpenAttempt(_attemptId);
        }

        internal void BeginManualRequest(ulong listingId, ulong contentId) {
            _runtime.TestBeginManualRequestCycle(listingId, contentId);
        }

        internal void CompleteScannerAttempt(PartyDetailScannerAttemptOutcome outcome) {
            _runtime.CompleteScannerRequest(_attemptId, outcome);
        }

        internal bool RaisePfDetailOpen() {
            return _hookFactory.RaisePfDetailOpen();
        }

        internal void ObserveSharedPopulate() {
            _runtime.TestObservePopulationEvent();
        }

        internal void Tick(UploadablePartyDetail snapshot) {
            _runtime.TestFrameworkTick(snapshot);
        }

        internal void Tick(UploadablePartyDetail snapshot, PartyDetailDebugSnapshot debugSnapshot) {
            _runtime.TestFrameworkTick(snapshot, debugSnapshot);
        }

        internal void UpdateCollector() {
            _collector.Update();
        }

        public void Dispose() {
            _runtime.Dispose();
        }

        private static UploadablePartyDetail ClonePayload(UploadablePartyDetail payload) {
            return new UploadablePartyDetail {
                ListingId = payload.ListingId,
                LeaderContentId = payload.LeaderContentId,
                LeaderName = payload.LeaderName,
                HomeWorld = payload.HomeWorld,
                CreatedWorld = payload.CreatedWorld,
                LastServerRestart = payload.LastServerRestart,
                ObservedAtUnixMs = payload.ObservedAtUnixMs,
                MemberContentIds = [.. payload.MemberContentIds],
                MemberJobs = [.. payload.MemberJobs],
                SlotFlags = [.. payload.SlotFlags],
            };
        }
    }

    private sealed class TrackingSuppressionHookFactory : IPartyDetailCaptureHookFactory, IDisposable {
        private Func<nint, uint, nint, nint, nint, nint, ushort, int, bool>? _pfDetailOpenDetour;

        internal int PfDetailOpenInterceptCount { get; private set; }
        internal int PfDetailOpenOriginalCallCount { get; private set; }

        public IDisposable CreateHooks(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
        ) {
            _ = openListingDetour;
            _ = openListingByContentIdDetour;
            _ = populateListingDataDetour;
            _pfDetailOpenDetour = pfDetailOpenDetour;
            return this;
        }

        internal bool RaisePfDetailOpen() {
            ArgumentNullException.ThrowIfNull(_pfDetailOpenDetour);

            PfDetailOpenInterceptCount++;
            var allowOriginal = _pfDetailOpenDetour(0, 275U, 153, 0, 0, 2, 0, 4);
            if (allowOriginal) {
                PfDetailOpenOriginalCallCount++;
                return false;
            }

            return true;
        }

        public void Dispose() {
        }
    }

    private sealed class CapturingFullHookFactory(
        bool hasPopulateHook = true,
        bool hasOpenListingHook = true,
        bool hasOpenListingByContentIdHook = true,
        bool hasOpenAddonHook = true
    ) : IPartyDetailCaptureHookFactory, IDisposable {
        private Func<nint, ulong, bool>? _openListingDetour;
        private Func<nint, ulong, bool>? _openListingByContentIdDetour;
        private Action<nint, nint>? _populateListingDataDetour;
        private Func<nint, uint, nint, nint, nint, nint, ushort, int, bool>? _pfDetailOpenDetour;

        internal Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> PfDetailOpenDetour =>
            _pfDetailOpenDetour ?? throw new InvalidOperationException("Hooks not initialized.");

        public IDisposable CreateHooks(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
        ) {
            _openListingDetour = openListingDetour;
            _openListingByContentIdDetour = openListingByContentIdDetour;
            _populateListingDataDetour = populateListingDataDetour;
            _pfDetailOpenDetour = pfDetailOpenDetour;
            return this;
        }

        public PartyDetailCaptureHookRegistration CreateHooksWithStatus(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour,
            Action<string>? warningSink = null
        ) {
            return new PartyDetailCaptureHookRegistration(
                CreateHooks(openListingDetour, openListingByContentIdDetour, populateListingDataDetour, pfDetailOpenDetour),
                HasPopulateHook: hasPopulateHook,
                HasOpenListingHook: hasOpenListingHook,
                HasOpenListingByContentIdHook: hasOpenListingByContentIdHook,
                HasOpenAddonHook: hasOpenAddonHook
            );
        }

        internal bool RaiseOpenListing(nint agent, ulong listingId) {
            ArgumentNullException.ThrowIfNull(_openListingDetour);
            return _openListingDetour(agent, listingId);
        }

        internal bool RaiseOpenListingByContentId(nint agent, ulong contentId) {
            ArgumentNullException.ThrowIfNull(_openListingByContentIdDetour);
            return _openListingByContentIdDetour(agent, contentId);
        }

        internal void RaisePopulateListingData(nint agent, nint listingData) {
            ArgumentNullException.ThrowIfNull(_populateListingDataDetour);
            _populateListingDataDetour(agent, listingData);
        }

        public void Dispose() {
        }
    }

    public class NoOpInteropProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            throw new InvalidOperationException("No request should be sent by this test.");
        }
    }

}
