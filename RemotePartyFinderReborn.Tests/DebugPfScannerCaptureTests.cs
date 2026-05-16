using System.Collections.Generic;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class DebugPfScannerCaptureTests {
    [Fact]
    public void Scanner_consume_ack_ignores_manual_open_during_run() {
        var state = new PartyDetailCaptureState();
        var scanner = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var manual = state.BeginRequest(PartyDetailRequestOwner.Manual, 9002U, 55UL);
        var manualSnapshot = CreateSnapshot(9002U, 55UL);

        Assert.True(state.TryRecordArrival(manual.RequestSerial, manualSnapshot));
        Assert.True(state.TryMarkConsumed(state.LatestArrivalGeneration));

        Assert.False(state.IsScannerConsumedAckReady(scanner.RequestSerial, 9001U, 44UL));
    }

    [Fact]
    public void Scanner_consume_ack_ignores_stale_late_response_after_retry() {
        var state = new PartyDetailCaptureState();
        var firstCycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var retryCycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var snapshot = CreateSnapshot(9001U, 44UL);

        Assert.True(state.TryRecordArrival(firstCycle.RequestSerial, snapshot));
        Assert.True(state.TryMarkConsumed(state.LatestArrivalGeneration));

        Assert.False(state.IsScannerConsumedAckReady(retryCycle.RequestSerial, 9001U, 44UL));
    }

    [Fact]
    public void Scanner_request_without_headless_suppression_captures_once() {
        using var harness = new ScannerOwnedCaptureHarness();
        var snapshot = CreateSnapshot(9001U, 44UL);

        harness.BeginScannerTarget(9001U, 44UL);
        harness.ObservePopulationEvent();
        harness.Tick(snapshot);
        harness.UpdateCollector();

        harness.Tick(snapshot);
        harness.UpdateCollector();

        Assert.Single(harness.CapturedPayloads);
    }

    [Fact]
    public void Headless_ack_completes_wait_without_visible_detail() {
        var nowUtc = new DateTime(2026, 4, 16, 3, 0, 0, DateTimeKind.Utc);
        var target = new DebugPfListingCandidate(9001U, 44UL, nowUtc, 1);
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var captureState = new PartyDetailCaptureState();
        var request = captureState.BeginRequest(PartyDetailRequestOwner.Scanner, target.ListingId, target.ContentId);
        var snapshot = CreateSnapshot((uint)target.ListingId, target.ContentId);

        stateMachine.UpsertVisibleCandidate(target);
        Assert.Null(stateMachine.SyncQueue(
            nowUtc,
            [],
            hasIncomingListings: false,
            maxPerRun: 0,
            dedupTtlSeconds: 600,
            runFromCollectedListings: false
        ));
        Assert.Equal(DebugPfScanState.OpenTarget, stateMachine.State);

        stateMachine.HandleOpenAttemptResult(
            nowUtc,
            opened: true,
            actionIntervalMs: 400,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 1,
            postListingCooldownMs: 300,
            request.RequestSerial
        );
        Assert.Equal(DebugPfScanState.WaitDetailReady, stateMachine.State);

        Assert.True(captureState.TryRecordArrival(request.RequestSerial, snapshot));
        Assert.True(captureState.TryMarkConsumed(captureState.LatestArrivalGeneration));

        Assert.True(DebugPfScanner.TryCompleteHeadlessConsumedAck(
            stateMachine,
            captureState.IsScannerConsumedAckReady,
            nowUtc.AddMilliseconds(10),
            postListingCooldownMs: 300
        ));
        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);
        Assert.Equal("queued", stateMachine.LastAttemptReason);
        Assert.True(stateMachine.LastAttemptSuccess);
    }

    [Fact]
    public void Start_current_page_run_enables_debug_scanner_without_collected_listing_mode() {
        var configuration = new Configuration {
            EnableAutoDetailScanDebug = false,
        };
        var resetCount = 0;
        var saveCount = 0;
        var runFromCollectedListings = true;
        var wasEnabled = true;

        DebugPfScanner.ConfigureCurrentPageRun(
            configuration,
            () => resetCount++,
            () => saveCount++,
            ref runFromCollectedListings,
            ref wasEnabled);

        Assert.True(configuration.EnableAutoDetailScanDebug);
        Assert.False(runFromCollectedListings);
        Assert.False(wasEnabled);
        Assert.Equal(1, resetCount);
        Assert.Equal(1, saveCount);
    }

    private static UploadablePartyDetail CreateSnapshot(uint listingId, ulong leaderContentId) {
        return new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = leaderContentId,
            LeaderName = "Leader",
            HomeWorld = 77,
            MemberContentIds = new List<ulong> { leaderContentId },
            MemberJobs = new List<byte> { 19 },
            SlotFlags = new List<string> { "0x0000000000000000" },
        };
    }

    private sealed class ScannerOwnedCaptureHarness : IDisposable {
        private readonly PartyDetailCaptureRuntime _runtime;
        private readonly PartyDetailCollector _collector;
        private readonly Guid _attemptId = Guid.NewGuid();

        internal ScannerOwnedCaptureHarness() {
            var state = new PartyDetailCaptureState();
            _runtime = new PartyDetailCaptureRuntime(state);
            _collector = new PartyDetailCollector(
                state,
                tryQueuePayload: payload => {
                    CapturedPayloads.Add(CloneSnapshot(payload));
                    return true;
                },
                pumpPendingUploads: static () => { }
            );
        }

        internal List<UploadablePartyDetail> CapturedPayloads { get; } = new();

        internal void BeginScannerTarget(ulong listingId, ulong contentId) {
            _runtime.ArmScannerRequest(_attemptId, listingId, contentId);
            _runtime.BeginScannerOpenAttempt(_attemptId);
            _runtime.TestInterceptOpenListing(listingId, contentId);
            _runtime.EndScannerOpenAttempt(_attemptId);
        }

        internal void ObservePopulationEvent() {
            _runtime.TestObservePopulationEvent();
        }

        internal void Tick(UploadablePartyDetail snapshot) {
            _runtime.TestFrameworkTick(snapshot);
        }

        internal void UpdateCollector() {
            _collector.Update();
        }

        public void Dispose() {
            _runtime.Dispose();
        }

        private static UploadablePartyDetail CloneSnapshot(UploadablePartyDetail payload) {
            return new UploadablePartyDetail {
                ListingId = payload.ListingId,
                LeaderContentId = payload.LeaderContentId,
                LeaderName = payload.LeaderName,
                HomeWorld = payload.HomeWorld,
                MemberContentIds = new List<ulong>(payload.MemberContentIds),
                MemberJobs = new List<byte>(payload.MemberJobs),
                SlotFlags = new List<string>(payload.SlotFlags),
            };
        }
    }
}
