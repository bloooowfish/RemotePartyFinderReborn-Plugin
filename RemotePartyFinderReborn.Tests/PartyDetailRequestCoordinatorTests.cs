using System.Collections.Generic;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyDetailRequestCoordinatorTests {
    [Fact]
    public void Scanner_attempt_gets_request_serial_only_when_open_intercept_matches_target() {
        var state = new PartyDetailCaptureState();
        var coordinator = new PartyDetailRequestCoordinator(ScannerHeadlessBackendKind.NativeSuppression);
        var attemptId = Guid.NewGuid();

        coordinator.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL, allowHeadless: true);
        coordinator.BeginScannerOpenAttempt(attemptId);

        var manualCycle = coordinator.EnsureRequestCycleForOpenIntercept(state, listingId: 9002U, contentId: 55UL);
        Assert.Equal(PartyDetailRequestOwner.Manual, manualCycle.Owner);
        Assert.Null(coordinator.GetArmedScannerRequestSerial(attemptId));

        var scannerCycle = coordinator.EnsureRequestCycleForOpenIntercept(state, listingId: 9001U, contentId: 44UL);
        coordinator.EndScannerOpenAttempt(attemptId);

        Assert.Equal(PartyDetailRequestOwner.Scanner, scannerCycle.Owner);
        Assert.Equal(scannerCycle.RequestSerial, coordinator.GetArmedScannerRequestSerial(attemptId));
    }

    [Fact]
    public void Scanner_suppression_requires_scanner_owner_and_matching_serial() {
        var state = new PartyDetailCaptureState();
        var coordinator = new PartyDetailRequestCoordinator(ScannerHeadlessBackendKind.NativeSuppression);
        var attemptId = Guid.NewGuid();

        coordinator.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL, allowHeadless: true);
        coordinator.BeginScannerOpenAttempt(attemptId);
        coordinator.EnsureRequestCycleForOpenIntercept(state, listingId: 9001U, contentId: 44UL);
        coordinator.EndScannerOpenAttempt(attemptId);

        Assert.True(coordinator.ShouldSuppressScannerPfDetailOpen(state, 275U));
        Assert.False(coordinator.ShouldSuppressScannerPfDetailOpen(state, 1U));

        var manualState = new PartyDetailCaptureState();
        manualState.BeginRequest(PartyDetailRequestOwner.Manual, 9001U, 44UL);
        Assert.False(coordinator.ShouldSuppressScannerPfDetailOpen(manualState, 275U));

        var noHeadless = new PartyDetailRequestCoordinator(ScannerHeadlessBackendKind.NativeSuppression);
        var noHeadlessAttemptId = Guid.NewGuid();
        var noHeadlessState = new PartyDetailCaptureState();
        noHeadless.ArmScannerRequest(noHeadlessAttemptId, listingId: 9001U, contentId: 44UL, allowHeadless: false);
        noHeadless.BeginScannerOpenAttempt(noHeadlessAttemptId);
        noHeadless.EnsureRequestCycleForOpenIntercept(noHeadlessState, listingId: 9001U, contentId: 44UL);
        Assert.False(noHeadless.ShouldSuppressScannerPfDetailOpen(noHeadlessState, 275U));
    }

    [Fact]
    public void Scanner_game_log_suppression_requires_scanner_owned_matching_request() {
        var state = new PartyDetailCaptureState();
        var coordinator = new PartyDetailRequestCoordinator(ScannerHeadlessBackendKind.NativeSuppression);
        var attemptId = Guid.NewGuid();

        coordinator.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL, allowHeadless: true);
        coordinator.BeginScannerOpenAttempt(attemptId);
        coordinator.EnsureRequestCycleForOpenIntercept(state, listingId: 9001U, contentId: 44UL);
        coordinator.EndScannerOpenAttempt(attemptId);

        Assert.True(coordinator.ShouldSuppressScannerGameLog(state, 958));
        Assert.True(coordinator.ShouldSuppressScannerGameLog(state, 5857));

        var manualState = new PartyDetailCaptureState();
        manualState.BeginRequest(PartyDetailRequestOwner.Manual, 9001U, 44UL);
        Assert.False(coordinator.ShouldSuppressScannerGameLog(manualState, 958));
        Assert.False(coordinator.ShouldSuppressScannerGameLog(manualState, 5857));
    }

    [Fact]
    public void Terminal_scanner_outcome_releases_suppression() {
        var state = new PartyDetailCaptureState();
        var coordinator = new PartyDetailRequestCoordinator(ScannerHeadlessBackendKind.NativeSuppression);
        var attemptId = Guid.NewGuid();

        coordinator.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL, allowHeadless: true);
        coordinator.BeginScannerOpenAttempt(attemptId);
        coordinator.EnsureRequestCycleForOpenIntercept(state, listingId: 9001U, contentId: 44UL);
        coordinator.EndScannerOpenAttempt(attemptId);

        Assert.True(coordinator.ShouldSuppressScannerPfDetailOpen(state, 275U));

        coordinator.CompleteScannerRequest(attemptId, PartyDetailScannerAttemptOutcome.Succeeded);

        Assert.Null(coordinator.GetArmedScannerRequestSerial(attemptId));
        Assert.False(coordinator.ShouldSuppressScannerPfDetailOpen(state, 275U));
    }

    [Fact]
    public void Consumed_ack_requires_scanner_owner_matching_serial_listing_and_content() {
        var state = new PartyDetailCaptureState();
        var coordinator = new PartyDetailRequestCoordinator();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var snapshot = CreateSnapshot(9001U, 44UL);

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));
        Assert.True(state.TryMarkConsumed(state.LatestArrivalGeneration));

        Assert.True(coordinator.IsConsumedAckReady(state, cycle.RequestSerial, 9001U, 44UL));
        Assert.False(coordinator.IsConsumedAckReady(state, cycle.RequestSerial, 9002U, 44UL));
        Assert.False(coordinator.IsConsumedAckReady(state, cycle.RequestSerial, 9001U, 55UL));

        var manualState = new PartyDetailCaptureState();
        var manualCycle = manualState.BeginRequest(PartyDetailRequestOwner.Manual, 9001U, 44UL);
        Assert.True(manualState.TryRecordArrival(manualCycle.RequestSerial, snapshot));
        Assert.True(manualState.TryMarkConsumed(manualState.LatestArrivalGeneration));

        Assert.False(coordinator.IsConsumedAckReady(manualState, manualCycle.RequestSerial, 9001U, 44UL));
    }

    private static UploadablePartyDetail CreateSnapshot(uint listingId, ulong leaderContentId) {
        return new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = leaderContentId,
            LeaderName = "Leader",
            HomeWorld = 77,
            MemberContentIds = new List<ulong> { leaderContentId },
            MemberJobs = new List<byte> { 19 },
            SlotFlags = new List<string> { "0x0000000000000001" },
        };
    }
}
