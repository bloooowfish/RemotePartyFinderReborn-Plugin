using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

#nullable enable

namespace RemotePartyFinderReborn;

internal enum PartyDetailScannerAttemptOutcome {
    OpenFailed,
    TimedOut,
    Succeeded,
}

internal enum ScannerHeadlessBackendKind {
    Disabled,
    NativeSuppression,
    PopulateCorrelation,
}

internal interface IPartyDetailCaptureHookFactory {
    IDisposable CreateHooks(
        Func<nint, ulong, bool> openListingDetour,
        Func<nint, ulong, bool> openListingByContentIdDetour,
        Action<nint, nint> populateListingDataDetour,
        Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
    );
}

internal sealed class PartyDetailCaptureRuntime : IDisposable {
    private const string OpenListingSignature =
        "48 89 5C 24 ?? 57 48 83 EC ?? 48 8B FA 48 8B D9 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private const string OpenListingByContentIdSignature =
        "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 07 C6 83 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53";
    private const string OpenAddonSignature =
        "4C 89 4C 24 20 44 89 44 24 18 53 55 56 57 41 57 48 81 EC ?? ?? ?? ?? 80 B9 ?? ?? ?? ?? ?? 48 8B F9 8B B4 24 ?? ?? ?? ?? 8B DA";
    private const uint PfDetailAddonId = 275U;
    private readonly PartyDetailCaptureState _state;
    private readonly object _gate = new();
    private readonly ScannerHeadlessBackendKind _scannerHeadlessBackendKind;
    private readonly PartyDetailRequestCoordinator _requestCoordinator;
    private IDisposable? _hookScope;
    private long _postRequestPopulationGeneration;
    private RequestPopulationGate? _requestPopulationGate;
    internal PartyDetailCaptureState CaptureState => _state;

    // Scanner-owned PF detail UI suppression is gated on the unique addon-275
    // OpenAddon seam. Manual request cycles still flow through the shared
    // request/populate/capture path unchanged.

    internal PartyDetailCaptureRuntime(
        PartyDetailCaptureState state,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled
    ) {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _scannerHeadlessBackendKind = scannerHeadlessBackendKind;
        _requestCoordinator = new PartyDetailRequestCoordinator(scannerHeadlessBackendKind);
    }

    internal PartyDetailCaptureRuntime(
        PartyDetailCaptureState state,
        IGameInteropProvider interopProvider,
        Action<string>? warningSink = null,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled
    ) : this(state, new DalamudPartyDetailCaptureHookFactory(interopProvider), warningSink, scannerHeadlessBackendKind) {
    }

    private PartyDetailCaptureRuntime(
        PartyDetailCaptureState state,
        IPartyDetailCaptureHookFactory hookFactory,
        Action<string>? warningSink = null,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled
    ) : this(state, scannerHeadlessBackendKind) {
        ArgumentNullException.ThrowIfNull(hookFactory);

        try {
            _hookScope = hookFactory.CreateHooks(
                OpenListingDetour,
                OpenListingByContentIdDetour,
                PopulateListingDataDetour,
                PfDetailOpenDetour
            );
        } catch (Exception exception) {
            warningSink?.Invoke($"PartyDetailCaptureRuntime: failed to initialize hooks. {exception.Message}");
            _hookScope = null;
        }
    }

    internal static PartyDetailCaptureRuntime CreateForTesting(
        PartyDetailCaptureState state,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind
    ) {
        return new PartyDetailCaptureRuntime(state, scannerHeadlessBackendKind);
    }

    internal static PartyDetailCaptureRuntime CreateForTesting(
        PartyDetailCaptureState state,
        IPartyDetailCaptureHookFactory hookFactory,
        Action<string>? warningSink = null,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled
    ) {
        return new PartyDetailCaptureRuntime(state, hookFactory, warningSink, scannerHeadlessBackendKind);
    }

    internal static IDisposable CreateHookScope<TPrimaryHook, TSecondaryHook>(
        Func<TPrimaryHook> createPrimaryHook,
        Func<TSecondaryHook> createSecondaryHook,
        Action<TPrimaryHook> enablePrimaryHook,
        Action<TSecondaryHook> enableSecondaryHook
    )
        where TPrimaryHook : class, IDisposable
        where TSecondaryHook : class, IDisposable {
        ArgumentNullException.ThrowIfNull(createPrimaryHook);
        ArgumentNullException.ThrowIfNull(createSecondaryHook);
        ArgumentNullException.ThrowIfNull(enablePrimaryHook);
        ArgumentNullException.ThrowIfNull(enableSecondaryHook);

        var hooks = new InteropHookBundle();
        try {
            var primaryHook = createPrimaryHook();
            hooks.Add(primaryHook);
            var secondaryHook = createSecondaryHook();
            hooks.Add(secondaryHook);
            enablePrimaryHook(primaryHook);
            enableSecondaryHook(secondaryHook);
            return hooks;
        } catch {
            hooks.Dispose();
            throw;
        }
    }

    public void Dispose() {
        _hookScope?.Dispose();
        _hookScope = null;

        lock (_gate) {
            _requestCoordinator.ResetScannerRequest();
            _requestPopulationGate = null;
        }
    }

    public void ArmScannerRequest(Guid attemptId, ulong listingId, ulong contentId, bool allowHeadless = true) {
        _requestCoordinator.ArmScannerRequest(attemptId, listingId, contentId, allowHeadless);
    }

    public long? GetArmedScannerRequestSerial(Guid attemptId) {
        return _requestCoordinator.GetArmedScannerRequestSerial(attemptId);
    }

    internal void BeginScannerOpenAttempt(Guid attemptId) {
        _requestCoordinator.BeginScannerOpenAttempt(attemptId);
    }

    internal void EndScannerOpenAttempt(Guid attemptId) {
        _requestCoordinator.EndScannerOpenAttempt(attemptId);
    }

    internal void ClearScannerRequest(Guid attemptId) {
        var requestSerial = _requestCoordinator.GetArmedScannerRequestSerial(attemptId);
        _requestCoordinator.ClearScannerRequest(attemptId);
        CancelScannerAttemptRequest(requestSerial);
    }

    internal void CompleteScannerRequest(Guid attemptId, PartyDetailScannerAttemptOutcome outcome) {
        if (!PartyDetailRequestCoordinator.ShouldClearScannerRequest(outcome)) {
            _requestCoordinator.CompleteScannerRequest(attemptId, outcome);
            return;
        }

        ClearScannerRequest(attemptId);
    }

    internal void ResetScannerRequest() {
        var requestSerial = _requestCoordinator.CurrentScannerRequestSerial;
        _requestCoordinator.ResetScannerRequest();
        CancelScannerAttemptRequest(requestSerial);
    }

    internal bool TryBeginScannerHeadlessScope(Guid attemptId) {
        return _requestCoordinator.TryBeginScannerHeadlessScope(_state, attemptId);
    }

    internal void EndScannerHeadlessScope(Guid attemptId) {
        _requestCoordinator.EndScannerHeadlessScope(attemptId);
    }

    internal bool TryRecordScannerHeadlessArrival(Guid attemptId, UploadablePartyDetail snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_scannerHeadlessBackendKind != ScannerHeadlessBackendKind.PopulateCorrelation) {
            return false;
        }

        if (!_requestCoordinator.TryGetActiveScannerHeadlessScope(attemptId, out var scope)) {
            return false;
        }

        if (!_state.TryGetCurrentRequestCycle(out var cycle)
            || cycle.RequestSerial != scope.RequestSerial
            || cycle.Owner != PartyDetailRequestOwner.Scanner) {
            return false;
        }

        if (!HasObservedPostRequestPopulation(scope.RequestSerial)) {
            return false;
        }

        if (!PartyDetailUploadPolicy.IsSnapshotReadyForEnqueue(snapshot)) {
            return false;
        }

        if (scope.ListingId != 0 && snapshot.ListingId != scope.ListingId) {
            return false;
        }

        if (scope.ContentId != 0 && snapshot.LeaderContentId != scope.ContentId) {
            return false;
        }

        if (!_state.TryRecordArrival(scope.RequestSerial, snapshot)) {
            return false;
        }

        ClearPopulationGate(scope.RequestSerial);
        EndScannerHeadlessScope(attemptId);
        return true;
    }

    internal bool ShouldSuppressScannerGameLog(uint logMessageId) {
        return _requestCoordinator.ShouldSuppressScannerGameLog(_state, logMessageId);
    }

    internal void TestInterceptOpenListing(ulong listingId, ulong contentId) {
        _ = contentId;
        EnsureRequestCycleForIntercept(listingId, 0UL);
    }

    internal void TestInterceptOpenListingByContentId(ulong contentId) {
        EnsureRequestCycleForIntercept(0UL, contentId);
    }

    internal void TestBeginManualRequestCycle(ulong listingId, ulong contentId) {
        lock (_gate) {
            BeginManualRequestCycle(listingId, contentId);
        }
    }

    internal void OnFrameworkUpdate() {
        ObserveFrameworkTick(
            PartyDetailSnapshotBuilder.TryBuildFromAgent(out var snapshot, out var debugSnapshot) ? snapshot : null,
            debugSnapshot
        );
    }

    internal void TestObservePopulationEvent() {
        RaisePostRequestPopulationSignal();
    }

    internal void TestFrameworkTick(UploadablePartyDetail? snapshot) {
        TestFrameworkTick(snapshot, null);
    }

    internal void TestFrameworkTick(UploadablePartyDetail? snapshot, PartyDetailDebugSnapshot? debugSnapshot) {
        ObserveFrameworkTick(snapshot, debugSnapshot);
    }

    private bool OpenListingDetour(nint agent, ulong listingId) {
        EnsureRequestCycleForIntercept(listingId, 0UL);
        return true;
    }

    private bool OpenListingByContentIdDetour(nint agent, ulong contentId) {
        EnsureRequestCycleForIntercept(0UL, contentId);
        return true;
    }

    private void PopulateListingDataDetour(nint agent, nint listingData) {
        _ = agent;
        _ = listingData;
        RaisePostRequestPopulationSignal();
    }

    private bool PfDetailOpenDetour(
        nint raptureAtkModule,
        uint addonId,
        nint valueCount,
        nint atkValues,
        nint parentAgent,
        nint unk,
        ushort addonRowId,
        int openFlags
    ) {
        _ = raptureAtkModule;
        _ = valueCount;
        _ = atkValues;
        _ = parentAgent;
        _ = unk;
        _ = addonRowId;
        _ = openFlags;
        var suppress = _requestCoordinator.ShouldSuppressScannerPfDetailOpen(_state, addonId);
        EnsureManualCycleForDetailOpen(addonId, suppress);

        var hasScannerAttempt = _requestCoordinator.TryGetScannerAttemptDebugState(out var scannerAttempt);
        if (Plugin.Log is not null && (addonId == PfDetailAddonId || hasScannerAttempt)) {
            Plugin.Log.Debug(
                "PartyDetailCaptureRuntime: pf_detail_open addon={AddonId} suppress={Suppress} backend={Backend} currentOwner={CurrentOwner} currentSerial={CurrentSerial} armedSerial={ArmedSerial} allowHeadless={AllowHeadless} listing={ListingId} content={ContentId}",
                addonId,
                suppress,
                _scannerHeadlessBackendKind,
                _state.CurrentOwner?.ToString() ?? "none",
                _state.CurrentRequestSerial?.ToString() ?? "none",
                hasScannerAttempt ? scannerAttempt.RequestSerial?.ToString() ?? "none" : "none",
                hasScannerAttempt && scannerAttempt.AllowHeadless,
                hasScannerAttempt ? scannerAttempt.ListingId : 0UL,
                hasScannerAttempt ? scannerAttempt.ContentId : 0UL
            );
        }

        return !suppress;
    }

    private void EnsureRequestCycleForIntercept(ulong listingId, ulong contentId) {
        var previousRequestSerial = _state.CurrentRequestSerial;
        var cycle = _requestCoordinator.EnsureRequestCycleForOpenIntercept(_state, listingId, contentId);
        if (previousRequestSerial != cycle.RequestSerial) {
            ArmPopulationGate(cycle.RequestSerial);
        }
    }

    private void BeginManualRequestCycle(ulong listingId, ulong contentId) {
        var cycle = _state.BeginRequest(PartyDetailRequestOwner.Manual, listingId, contentId);
        ArmPopulationGate(cycle.RequestSerial);
    }

    private void EnsureManualCycleForDetailOpen(uint addonId, bool suppress) {
        if (addonId != PfDetailAddonId || suppress) {
            return;
        }

        lock (_gate) {
            if (_requestCoordinator.HasActiveScannerInteraction
                || _state.HasActiveRequest) {
                return;
            }

            _state.BeginRequest(PartyDetailRequestOwner.Manual, 0UL, 0UL);
        }
    }

    private void ObserveFrameworkTick(UploadablePartyDetail? snapshot, PartyDetailDebugSnapshot? debugSnapshot) {
        if (!_state.HasActiveRequest) {
            return;
        }

        if (snapshot is null) {
            return;
        }

        TryRecordAgentSnapshot(snapshot, debugSnapshot, enforceFreshness: true);
    }

    private bool TryRecordAgentSnapshot(
        UploadablePartyDetail snapshot,
        PartyDetailDebugSnapshot? debugSnapshot,
        bool enforceFreshness
    ) {
        if (!_state.TryGetCurrentRequestCycle(out var cycle)) {
            return false;
        }

        if (enforceFreshness && !HasObservedPostRequestPopulation(cycle.RequestSerial)) {
            return false;
        }

        if (!PartyDetailUploadPolicy.IsSnapshotReadyForEnqueue(snapshot)) {
            return false;
        }

        if (cycle.ListingId != 0 && snapshot.ListingId != cycle.ListingId) {
            return false;
        }

        if (cycle.ContentId != 0 && snapshot.LeaderContentId != cycle.ContentId) {
            return false;
        }

        if (!_state.TryRecordArrival(cycle.RequestSerial, snapshot, debugSnapshot)) {
            return false;
        }

        ClearPopulationGate(cycle.RequestSerial);
        return true;
    }

    internal bool IsScannerConsumedAckReady(long requestSerial, ulong listingId, ulong contentId) {
        return _requestCoordinator.IsConsumedAckReady(_state, requestSerial, listingId, contentId);
    }

    private void CancelScannerAttemptRequest(long? requestSerial) {
        if (requestSerial is not { } serial) {
            return;
        }

        _state.TryCancelRequest(serial);
        ClearPopulationGate(serial);
    }

    private void ArmPopulationGate(long requestSerial) {
        lock (_gate) {
            _requestPopulationGate = new RequestPopulationGate(requestSerial, _postRequestPopulationGeneration);
        }
    }

    private bool HasObservedPostRequestPopulation(long requestSerial) {
        lock (_gate) {
            return _requestPopulationGate is not { } gate
                   || gate.RequestSerial != requestSerial
                   || _postRequestPopulationGeneration > gate.SignalGenerationAtRequestStart;
        }
    }

    private void ClearPopulationGate(long requestSerial) {
        lock (_gate) {
            if (_requestPopulationGate is { RequestSerial: var gatedRequestSerial } && gatedRequestSerial == requestSerial) {
                _requestPopulationGate = null;
            }
        }
    }

    private void RaisePostRequestPopulationSignal() {
        lock (_gate) {
            _postRequestPopulationGeneration++;
        }
    }

    private sealed record RequestPopulationGate(long RequestSerial, long SignalGenerationAtRequestStart);

    private sealed class DalamudPartyDetailCaptureHookFactory : IPartyDetailCaptureHookFactory {
        private readonly IGameInteropProvider _interopProvider;

        private delegate bool OpenListingDelegate(nint agent, ulong listingId);
        private delegate bool OpenListingByContentIdDelegate(nint agent, ulong contentId);
        private delegate void PopulateListingDataDelegate(nint agent, nint listingData);
        private delegate long OpenAddonDelegate(
            nint raptureAtkModule,
            uint addonId,
            nint valueCount,
            nint atkValues,
            nint parentAgent,
            nint unk,
            ushort addonRowId,
            int openFlags
        );

        internal DalamudPartyDetailCaptureHookFactory(IGameInteropProvider interopProvider) {
            _interopProvider = interopProvider ?? throw new ArgumentNullException(nameof(interopProvider));
        }

        public unsafe IDisposable CreateHooks(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
        ) {
            ArgumentNullException.ThrowIfNull(openListingDetour);
            ArgumentNullException.ThrowIfNull(openListingByContentIdDetour);
            ArgumentNullException.ThrowIfNull(populateListingDataDetour);
            ArgumentNullException.ThrowIfNull(pfDetailOpenDetour);

            Hook<OpenListingDelegate>? openListingHook = null;
            Hook<OpenListingByContentIdDelegate>? openListingByContentIdHook = null;
            Hook<PopulateListingDataDelegate>? populateListingDataHook = null;
            Hook<OpenAddonDelegate>? openAddonHook = null;
            var hooks = new InteropHookBundle();

            try {
                var openListingHookScope = CreateHookScope(
                    createPrimaryHook: () =>
                        openListingHook = _interopProvider.HookFromSignature<OpenListingDelegate>(
                            OpenListingSignature,
                            (agent, listingId) => {
                                _ = openListingDetour(agent, listingId);
                                return openListingHook!.Original(agent, listingId);
                            }
                        ),
                    createSecondaryHook: () =>
                        openListingByContentIdHook = _interopProvider.HookFromSignature<OpenListingByContentIdDelegate>(
                            OpenListingByContentIdSignature,
                            (agent, contentId) => {
                                _ = openListingByContentIdDetour(agent, contentId);
                                return openListingByContentIdHook!.Original(agent, contentId);
                            }
                        ),
                    enablePrimaryHook: static hook => hook.Enable(),
                    enableSecondaryHook: static hook => hook.Enable()
                );
                hooks.Add(openListingHookScope);

                populateListingDataHook = _interopProvider.HookFromAddress<PopulateListingDataDelegate>(
                    (nint)AgentLookingForGroup.MemberFunctionPointers.PopulateListingData,
                    (agent, listingData) => {
                        populateListingDataHook!.Original(agent, listingData);
                        populateListingDataDetour(agent, listingData);
                    }
                );
                hooks.AddAndEnable(populateListingDataHook, static hook => hook.Enable());
                openAddonHook = _interopProvider.HookFromSignature<OpenAddonDelegate>(
                    OpenAddonSignature,
                    (
                        raptureAtkModule,
                        addonId,
                        valueCount,
                        atkValues,
                        parentAgent,
                        unk,
                        addonRowId,
                        openFlags
                    ) => pfDetailOpenDetour(
                        raptureAtkModule,
                        addonId,
                        valueCount,
                        atkValues,
                        parentAgent,
                        unk,
                        addonRowId,
                        openFlags
                    )
                        ? openAddonHook!.Original(
                            raptureAtkModule,
                            addonId,
                            valueCount,
                            atkValues,
                            parentAgent,
                            unk,
                            addonRowId,
                            openFlags
                        )
                        : 0L
                );
                hooks.AddAndEnable(openAddonHook, static hook => hook.Enable());
                return hooks;
            } catch {
                hooks.Dispose();
                throw;
            }
        }
    }
}
