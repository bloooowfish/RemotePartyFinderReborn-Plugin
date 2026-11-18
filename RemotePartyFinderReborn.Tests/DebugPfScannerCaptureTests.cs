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

        Assert.Null(stateMachine.SyncQueue(nowUtc, [target], dedupTtlSeconds: 600));
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
    public void Collected_listing_buffer_tracks_page_turns_dedups_recent_scans_and_resets_on_clear() {
        var t0 = new DateTime(2026, 4, 16, 4, 0, 0, DateTimeKind.Utc);
        var buffer = new CollectedListingBuffer();

        Assert.True(buffer.UpsertFromListing(1001UL, 11UL, batchNumber: 0, t0));
        Assert.True(buffer.UpsertFromListing(1002UL, 22UL, batchNumber: 1, t0.AddSeconds(1)));

        Assert.Equal(2, buffer.Count);
        Assert.Equal(2, buffer.PageBatchCount);

        buffer.MarkSucceeded(1001UL, t0.AddSeconds(2));
        Assert.Equal(1, buffer.Count);

        // Refresh of already-scanned listing is skipped without bumping page batch count.
        Assert.False(buffer.UpsertFromListing(1001UL, 11UL, batchNumber: 2, t0.AddSeconds(5)));
        Assert.Equal(1, buffer.Count);
        Assert.Equal(2, buffer.PageBatchCount);

        buffer.MarkSucceeded(1002UL, t0.AddSeconds(6));
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.PageBatchCount);

        // Even after buffer drains, recently scanned items remain skipped while fresh batch arrives.
        Assert.False(buffer.UpsertFromListing(1001UL, 11UL, batchNumber: 3, t0.AddSeconds(7)));
        Assert.Equal(0, buffer.PageBatchCount);

        // Clearing resets dedup history so previously scanned listings can be re-collected.
        buffer.ClearAll();
        Assert.True(buffer.UpsertFromListing(1001UL, 11UL, batchNumber: 4, t0.AddSeconds(8)));
        Assert.Equal(1, buffer.Count);
        Assert.Equal(1, buffer.PageBatchCount);

        // Entries older than the 10-minute TTL are pruned automatically.
        var snapshot = buffer.TakeOrderedSnapshot(t0.AddMinutes(11));
        Assert.Empty(snapshot);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.PageBatchCount);
    }

    [Fact]
    public void Populate_immediate_snapshot_records_arrival_without_duplicate_tick_capture() {
        var state = new PartyDetailCaptureState();
        using var runtime = new PartyDetailCaptureRuntime(state);
        var captured = new List<UploadablePartyDetail>();
        using var collector = new PartyDetailCollector(
            state,
            tryQueuePayload: payload => {
                captured.Add(payload);
                return true;
            },
            pumpPendingUploads: static () => { }
        );

        var attemptId = Guid.NewGuid();
        var snapshot = CreateSnapshot(9401U, 88UL);

        runtime.ArmScannerRequest(attemptId, 9401U, 88UL);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.TestInterceptOpenListing(9401U, 88UL);
        runtime.EndScannerOpenAttempt(attemptId);

        Assert.True(runtime.TestPopulateListingDataSnapshot(snapshot));
        collector.Update();

        var requestSerial = runtime.GetArmedScannerRequestSerial(attemptId);
        Assert.NotNull(requestSerial);
        Assert.True(runtime.IsScannerConsumedAckReady(requestSerial!.Value, 9401U, 88UL));
        Assert.Single(captured);

        // Subsequent framework tick sees no remaining active cycle and does not duplicate.
        runtime.TestFrameworkTick(snapshot);
        collector.Update();
        Assert.Single(captured);
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
