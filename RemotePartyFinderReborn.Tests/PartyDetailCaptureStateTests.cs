using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyDetailCaptureStateTests {
    static PartyDetailCaptureStateTests() {
        DalamudAssemblyResolver.Register();
    }

    [Fact]
    public void Unarmed_open_listing_starts_manual_request_cycle() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);

        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);

        Assert.Equal(PartyDetailRequestOwner.Manual, state.CurrentOwner);
        Assert.Equal(9001U, state.CurrentListingId);
    }

    [Fact]
    public void Armed_scanner_open_listing_starts_scanner_request_cycle() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var attemptId = Guid.NewGuid();

        runtime.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);
        runtime.EndScannerOpenAttempt(attemptId);

        Assert.Equal(PartyDetailRequestOwner.Scanner, state.CurrentOwner);
        Assert.Equal(9001U, state.CurrentListingId);
        Assert.Equal(state.CurrentRequestSerial, runtime.GetArmedScannerRequestSerial(attemptId));
    }

    [Fact]
    public void Armed_scanner_fallback_open_reuses_same_request_cycle() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var attemptId = Guid.NewGuid();

        runtime.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);
        var firstRequestSerial = state.CurrentRequestSerial;

        runtime.RaiseOpenListingByContentId(contentId: 44UL);
        runtime.EndScannerOpenAttempt(attemptId);

        Assert.Equal(firstRequestSerial, state.CurrentRequestSerial);
        Assert.Equal(firstRequestSerial, runtime.GetArmedScannerRequestSerial(attemptId));
        Assert.Equal(PartyDetailRequestOwner.Scanner, state.CurrentOwner);
    }

    [Fact]
    public void Later_manual_open_of_same_listing_is_not_misclassified_as_scanner() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var attemptId = Guid.NewGuid();

        runtime.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);
        runtime.EndScannerOpenAttempt(attemptId);
        var scannerRequestSerial = state.CurrentRequestSerial;

        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);

        Assert.NotEqual(scannerRequestSerial, state.CurrentRequestSerial);
        Assert.Equal(PartyDetailRequestOwner.Manual, state.CurrentOwner);
    }

    [Fact]
    public void Armed_scanner_timeout_clears_arm() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var attemptId = Guid.NewGuid();

        runtime.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);
        runtime.EndScannerOpenAttempt(attemptId);

        runtime.CompleteScannerAttempt(attemptId, PartyDetailScannerAttemptOutcome.TimedOut);

        Assert.Null(runtime.GetArmedScannerRequestSerial(attemptId));
        Assert.Null(state.CurrentOwner);
    }

    [Fact]
    public void Armed_scanner_terminal_open_failed_clears_arm() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var attemptId = Guid.NewGuid();

        runtime.ArmScannerRequest(attemptId, listingId: 9001U, contentId: 44UL);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);
        runtime.EndScannerOpenAttempt(attemptId);

        runtime.CompleteScannerAttempt(attemptId, PartyDetailScannerAttemptOutcome.OpenFailed);

        Assert.Null(runtime.GetArmedScannerRequestSerial(attemptId));
        Assert.Null(state.CurrentOwner);
    }

    [Fact]
    public void Reset_scanner_request_does_not_clear_unrelated_population_gate() {
        var state = new PartyDetailCaptureState();
        using var runtime = new FakePartyDetailCaptureRuntime(state);
        var snapshot = CreateSnapshot(9001U, 44UL);

        runtime.BeginManualRequestCycle(9001U, 44UL);
        runtime.ResetScannerRequest();
        runtime.FrameworkTick(snapshot);

        Assert.False(state.TryGetNextUnconsumedArrival(out _));
    }

    [Fact]
    public void Hook_initialization_failure_degrades_to_passive_mode() {
        var state = new PartyDetailCaptureState();
        var warnings = new List<string>();
        using var runtime = new FakePartyDetailCaptureRuntime(
            state,
            new ThrowingHookFactory(),
            warnings.Add
        );

        runtime.RaiseOpenListing(listingId: 9001U, contentId: 44UL);

        Assert.Equal(PartyDetailRequestOwner.Manual, state.CurrentOwner);
        Assert.Single(warnings);
        Assert.Contains("failed to initialize hooks", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hook_bundle_disposes_in_reverse_registration_order() {
        var disposedHooks = new List<string>();
        var firstHook = new TrackingHook("first", disposedHooks);
        var secondHook = new TrackingHook("second", disposedHooks);
        var thirdHook = new TrackingHook("third", disposedHooks);

        using (var bundle = new InteropHookBundle()) {
            bundle.Add(firstHook);
            bundle.Add(secondHook);
            bundle.Add(thirdHook);
        }

        Assert.Equal(["third", "second", "first"], disposedHooks);
    }

    [Fact]
    public void Hook_bundle_disposes_partially_created_hooks_when_enable_fails() {
        var disposedHooks = new List<string>();
        var firstHook = new TrackingHook("first", disposedHooks);
        var secondHook = new TrackingHook("second", disposedHooks);

        var exception = Assert.Throws<InvalidOperationException>(() => {
            using var bundle = new InteropHookBundle();
            bundle.AddAndEnable(firstHook, static hook => hook.Enable());
            bundle.AddAndEnable(secondHook, static _ => throw new InvalidOperationException("simulated enable failure"));
        });

        Assert.Equal("simulated enable failure", exception.Message);
        Assert.Equal(1, firstHook.EnableCallCount);
        Assert.Equal(["second", "first"], disposedHooks);
    }

    [Fact]
    public void Hook_scope_creation_disposes_first_hook_when_second_creation_fails() {
        var openListingHook = new TrackingHook();

        Assert.Throws<InvalidOperationException>(() =>
            PartyDetailCaptureRuntime.CreateHookScope<TrackingHook, TrackingHook>(
                () => openListingHook,
                () => throw new InvalidOperationException("simulated create failure"),
                static hook => hook.Enable(),
                static hook => hook.Enable()
            ));

        Assert.True(openListingHook.IsDisposed);
        Assert.Equal(0, openListingHook.EnableCallCount);
    }

    [Fact]
    public void Hook_scope_creation_disposes_created_hooks_when_later_enable_fails() {
        var openListingHook = new TrackingHook();
        var openListingByContentIdHook = new TrackingHook();

        Assert.Throws<InvalidOperationException>(() =>
            PartyDetailCaptureRuntime.CreateHookScope<TrackingHook, TrackingHook>(
                () => openListingHook,
                () => openListingByContentIdHook,
                static hook => hook.Enable(),
                static hook => throw new InvalidOperationException("simulated enable failure")
            ));

        Assert.Equal(1, openListingHook.EnableCallCount);
        Assert.True(openListingHook.IsDisposed);
        Assert.True(openListingByContentIdHook.IsDisposed);
    }

    [Fact]
    public void State_machine_exposes_typed_open_failed_outcome() {
        var nowUtc = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var target = new DebugPfListingCandidate(9001U, 44UL, nowUtc, 1);
        stateMachine.UpsertVisibleCandidate(target);

        _ = stateMachine.SyncQueue(
            nowUtc,
            [],
            hasIncomingListings: false,
            maxPerRun: 0,
            dedupTtlSeconds: 600,
            runFromCollectedListings: false
        );

        stateMachine.HandleOpenAttemptResult(
            nowUtc,
            opened: false,
            actionIntervalMs: 400,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 0,
            postListingCooldownMs: 300,
            requestSerial: null
        );

        Assert.Equal(PartyDetailScannerAttemptOutcome.OpenFailed, stateMachine.LastTerminalOutcome);
        Assert.Equal("open_failed", stateMachine.LastAttemptReason);
    }

    [Fact]
    public void State_machine_preserves_64_bit_listing_ids() {
        var nowUtc = new DateTime(2026, 4, 15, 0, 30, 0, DateTimeKind.Utc);
        const ulong api15ListingId = (ulong)uint.MaxValue + 123UL;
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var target = new DebugPfListingCandidate(api15ListingId, 44UL, nowUtc, 1);
        stateMachine.UpsertVisibleCandidate(target);

        _ = stateMachine.SyncQueue(
            nowUtc,
            [],
            hasIncomingListings: false,
            maxPerRun: 0,
            dedupTtlSeconds: 600,
            runFromCollectedListings: false
        );

        Assert.Equal(api15ListingId, stateMachine.CurrentTargetListingId);
    }

    [Fact]
    public void State_machine_exposes_typed_success_outcome_when_listing_is_collected() {
        var nowUtc = new DateTime(2026, 4, 15, 1, 0, 0, DateTimeKind.Utc);
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var target = new DebugPfListingCandidate(9101U, 55UL, nowUtc, 1);
        stateMachine.UpsertVisibleCandidate(target);

        _ = stateMachine.SyncQueue(
            nowUtc,
            [],
            hasIncomingListings: false,
            maxPerRun: 0,
            dedupTtlSeconds: 600,
            runFromCollectedListings: false
        );

        stateMachine.HandleOpenAttemptResult(
            nowUtc,
            opened: true,
            actionIntervalMs: 400,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 0,
            postListingCooldownMs: 300
        );
        stateMachine.HandleDetailReadyState(
            nowUtc.AddMilliseconds(10),
            new DebugPfDetailSnapshot(target.ListingId, target.ContentId, NonZeroMembers: 4, TotalSlots: 8),
            minDwellMs: 800,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 0,
            postListingCooldownMs: 300
        );
        stateMachine.HandleDetailReadyState(
            nowUtc.AddMilliseconds(20),
            new DebugPfDetailSnapshot(target.ListingId, target.ContentId, NonZeroMembers: 4, TotalSlots: 8),
            minDwellMs: 800,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 0,
            postListingCooldownMs: 300
        );
        stateMachine.HandleCollectedState(
            nowUtc.AddMilliseconds(30),
            hasConsumedAck: true,
            postListingCooldownMs: 300
        );

        Assert.Equal(PartyDetailScannerAttemptOutcome.Succeeded, stateMachine.LastTerminalOutcome);
        Assert.Equal("queued", stateMachine.LastAttemptReason);
    }

    [Fact]
    public void BeginScannerRequest_issues_new_request_serial_and_owner() {
        var state = new PartyDetailCaptureState();

        var cycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, listingId: 9001, contentId: 44UL);

        Assert.Equal(1L, cycle.RequestSerial);
        Assert.Equal(PartyDetailRequestOwner.Scanner, cycle.Owner);
        Assert.Equal(9001U, cycle.ListingId);
    }

    [Fact]
    public void RecordArrival_advances_generation_once_per_cycle() {
        var state = new PartyDetailCaptureState();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Manual, 9001U, 44UL);
        var snapshot = CreateSnapshot(9001U, 44UL);

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));
        Assert.False(state.TryRecordArrival(cycle.RequestSerial, snapshot));
        Assert.Equal(1L, state.LatestArrivalGeneration);
    }

    [Fact]
    public void IsScannerAckReady_rejects_manual_generation_for_scanner_target() {
        var state = new PartyDetailCaptureState();
        var scanner = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var manual = state.BeginRequest(PartyDetailRequestOwner.Manual, 9002U, 55UL);
        var manualSnapshot = CreateSnapshot(9002U, 55UL);

        state.TryRecordArrival(manual.RequestSerial, manualSnapshot);

        Assert.False(state.IsScannerAckReady(scanner.RequestSerial, 9001U, 44UL));
    }

    [Fact]
    public void IsScannerAckReady_accepts_listing_only_scanner_target() {
        var state = new PartyDetailCaptureState();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 0UL);
        var snapshot = CreateSnapshot(9001U, 44UL);

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));

        Assert.True(state.IsScannerAckReady(cycle.RequestSerial, 9001U, 0UL));
    }

    [Fact]
    public void IsScannerAckReady_rejects_stale_generation_after_consume() {
        var state = new PartyDetailCaptureState();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var snapshot = CreateSnapshot(9001U, 44UL);

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));
        Assert.True(state.IsScannerAckReady(cycle.RequestSerial, 9001U, 44UL));

        Assert.True(state.TryMarkConsumed(state.LatestArrivalGeneration));
        Assert.Equal(1L, state.LastConsumedGeneration);
        Assert.False(state.IsScannerAckReady(cycle.RequestSerial, 9001U, 44UL));
    }

    [Fact]
    public void TryMarkConsumed_accepts_new_generation_once() {
        var state = new PartyDetailCaptureState();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var snapshot = CreateSnapshot(9001U, 44UL);

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));

        Assert.True(state.TryMarkConsumed(1L));
        Assert.Equal(1L, state.LastConsumedGeneration);
        Assert.False(state.TryMarkConsumed(1L));
        Assert.False(state.TryMarkConsumed(0L));
    }

    [Fact]
    public void TryRecordArrival_captures_stable_snapshot_before_caller_mutation() {
        var state = new PartyDetailCaptureState();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Scanner, 9001U, 44UL);
        var snapshot = CreateSnapshot(9001U, 44UL);

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot));

        snapshot.ListingId = 9002U;
        snapshot.LeaderContentId = 55UL;
        snapshot.MemberContentIds[0] = 55UL;

        Assert.True(state.IsScannerAckReady(cycle.RequestSerial, 9001U, 44UL));
    }

    [Fact]
    public void TryRecordArrival_exposes_latest_detail_debug_snapshot_after_consumption() {
        var state = new PartyDetailCaptureState();
        var cycle = state.BeginRequest(PartyDetailRequestOwner.Manual, 9001U, 44UL);
        var snapshot = CreateSnapshot(9001U, 44UL);
        var debugSnapshot = new PartyDetailDebugSnapshot {
            CapturedAtUtc = new DateTime(2026, 4, 30, 1, 2, 3, DateTimeKind.Utc),
            RawListingId = 9001UL,
            UploadListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            ObjectiveRaw = 4,
            ObjectiveName = "Practice",
            Members = new List<PartyDetailDebugMember> {
                new(0, 44UL, 19, "0x0000000000000001"),
            },
        };

        Assert.True(state.TryRecordArrival(cycle.RequestSerial, snapshot, debugSnapshot));
        Assert.True(state.TryGetLatestDebugSnapshot(out var latest));
        Assert.Equal(1L, latest.Generation);
        Assert.Equal(cycle, latest.Cycle);
        Assert.Equal(4, latest.Snapshot.ObjectiveRaw);

        Assert.True(state.TryMarkConsumed(latest.Generation));
        Assert.True(state.TryGetLatestDebugSnapshot(out var afterConsumed));
        Assert.Equal(44UL, afterConsumed.Snapshot.Members[0].ContentId);
    }

    private static UploadablePartyDetail CreateSnapshot(uint listingId, ulong leaderContentId) {
        return new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = leaderContentId,
            HomeWorld = 77,
            MemberContentIds = new List<ulong> { leaderContentId },
            MemberJobs = new List<byte> { 19 },
            SlotFlags = new List<string> { "0x0000000000000001" },
        };
    }

    private sealed class FakePartyDetailCaptureRuntime : IDisposable {
        private readonly PartyDetailCaptureRuntime _runtime;

        internal FakePartyDetailCaptureRuntime(PartyDetailCaptureState state) {
            _runtime = new PartyDetailCaptureRuntime(state);
        }

        internal FakePartyDetailCaptureRuntime(
            PartyDetailCaptureState state,
            IPartyDetailCaptureHookFactory hookFactory,
            Action<string> warningSink
        ) {
            _runtime = PartyDetailCaptureRuntime.CreateForTesting(state, hookFactory, warningSink);
        }

        internal void ArmScannerRequest(Guid attemptId, ulong listingId, ulong contentId) {
            _runtime.ArmScannerRequest(attemptId, listingId, contentId);
        }

        internal long? GetArmedScannerRequestSerial(Guid attemptId) {
            return _runtime.GetArmedScannerRequestSerial(attemptId);
        }

        internal void BeginScannerOpenAttempt(Guid attemptId) {
            _runtime.BeginScannerOpenAttempt(attemptId);
        }

        internal void EndScannerOpenAttempt(Guid attemptId) {
            _runtime.EndScannerOpenAttempt(attemptId);
        }

        internal void RaiseOpenListing(ulong listingId, ulong contentId) {
            _runtime.TestInterceptOpenListing(listingId, contentId);
        }

        internal void RaiseOpenListingByContentId(ulong contentId) {
            _runtime.TestInterceptOpenListingByContentId(contentId);
        }

        internal void CompleteScannerAttempt(Guid attemptId, PartyDetailScannerAttemptOutcome outcome) {
            _runtime.CompleteScannerRequest(attemptId, outcome);
        }

        internal void ResetScannerRequest() {
            _runtime.ResetScannerRequest();
        }

        internal void BeginManualRequestCycle(ulong listingId, ulong contentId) {
            _runtime.TestBeginManualRequestCycle(listingId, contentId);
        }

        internal void FrameworkTick(UploadablePartyDetail snapshot) {
            _runtime.TestFrameworkTick(snapshot);
        }

        public void Dispose() {
            // Tests construct the runtime without Dalamud hook initialization.
        }
    }

    private sealed class ThrowingHookFactory : IPartyDetailCaptureHookFactory {
        public IDisposable CreateHooks(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
        ) {
            throw new InvalidOperationException("simulated signature drift");
        }
    }

    private sealed class TrackingHook : IDisposable {
        private readonly string? _name;
        private readonly List<string>? _disposedHooks;

        internal int EnableCallCount { get; private set; }
        internal bool IsDisposed { get; private set; }

        internal TrackingHook() {
        }

        internal TrackingHook(string name, List<string> disposedHooks) {
            _name = name;
            _disposedHooks = disposedHooks;
        }

        internal void Enable() {
            EnableCallCount++;
        }

        public void Dispose() {
            IsDisposed = true;
            if (_name != null) {
                _disposedHooks!.Add(_name);
            }
        }
    }

    private static class DalamudAssemblyResolver {
        private static int _registered;

        internal static void Register() {
            if (Interlocked.Exchange(ref _registered, 1) != 0) {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += static (_, args) => {
                var assemblyName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrWhiteSpace(assemblyName)) {
                    return null;
                }

                var dalamudHome = Environment.GetEnvironmentVariable("DALAMUD_HOME");
                if (string.IsNullOrWhiteSpace(dalamudHome)) {
                    dalamudHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "XIVLauncher",
                        "addon",
                        "Hooks",
                        "dev"
                    );
                }

                var candidatePath = Path.Combine(dalamudHome, assemblyName + ".dll");
                return File.Exists(candidatePath) ? Assembly.LoadFrom(candidatePath) : null;
            };
        }
    }
}
