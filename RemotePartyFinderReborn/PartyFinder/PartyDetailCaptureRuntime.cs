using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

#nullable enable

namespace RemotePartyFinderReborn;

internal enum PartyDetailScannerAttemptOutcome {
    OpenFailed,
    TimedOut,
    Unavailable,
    Succeeded,
}

internal enum ScannerHeadlessBackendKind {
    Disabled,
    NativeSuppression,
    PopulateCorrelation,
}

internal sealed record PartyDetailCaptureHookRegistration(
    IDisposable HookScope,
    bool HasPopulateHook,
    bool HasOpenListingHook,
    bool HasOpenListingByContentIdHook,
    bool HasOpenAddonHook
) {
    internal bool IsScannerCaptureReady =>
        HasPopulateHook && HasOpenListingHook && HasOpenListingByContentIdHook && HasOpenAddonHook;
}

internal interface IPartyDetailCaptureHookFactory {
    IDisposable CreateHooks(
        Func<nint, ulong, bool> openListingDetour,
        Func<nint, ulong, bool> openListingByContentIdDetour,
        Action<nint, nint> populateListingDataDetour,
        Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
    );

    PartyDetailCaptureHookRegistration CreateHooksWithStatus(
        Func<nint, ulong, bool> openListingDetour,
        Func<nint, ulong, bool> openListingByContentIdDetour,
        Action<nint, nint> populateListingDataDetour,
        Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour,
        Action<string>? warningSink = null
    ) {
        return new PartyDetailCaptureHookRegistration(
            CreateHooks(
                openListingDetour,
                openListingByContentIdDetour,
                populateListingDataDetour,
                pfDetailOpenDetour
            ),
            HasPopulateHook: true,
            HasOpenListingHook: true,
            HasOpenListingByContentIdHook: true,
            HasOpenAddonHook: true
        );
    }
}

internal sealed class PartyDetailCaptureRuntime : IDisposable {
    internal const uint DefaultPfDetailAddonId = 275U;
    private readonly PartyDetailCaptureState _state;
    private readonly object _gate = new();
    private readonly ScannerHeadlessBackendKind _scannerHeadlessBackendKind;
    private readonly Func<bool> _isCaptureEnabled;
    private readonly Func<uint?> _pfDetailAddonIdResolver;
    private readonly PartyDetailRequestCoordinator _requestCoordinator;
    private IDisposable? _hookScope;
    private bool _hasPopulateHook;
    private bool _isScannerCaptureReady;
    private int _cachedPfDetailAddonId = -1;
    private long _postRequestPopulationGeneration;
    private RequestPopulationGate? _requestPopulationGate;
    internal PartyDetailCaptureState CaptureState => _state;
    internal bool IsManualCaptureReady => Volatile.Read(ref _hasPopulateHook) && _hookScope is not null;
    internal bool IsScannerCaptureReady => Volatile.Read(ref _isScannerCaptureReady) && _hookScope is not null;

    // Scanner-owned PF detail UI suppression is gated on the dynamically resolved
    // LookingForGroupDetail OpenAddon seam and requires all four capture hooks.
    // Manual request cycles still flow through the shared request/populate/capture path
    // whenever PopulateListingData is hooked.

    internal PartyDetailCaptureRuntime(
        PartyDetailCaptureState state,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled,
        Func<bool>? isCaptureEnabled = null,
        Func<uint?>? pfDetailAddonIdResolver = null
    ) {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _scannerHeadlessBackendKind = scannerHeadlessBackendKind;
        _isCaptureEnabled = isCaptureEnabled ?? (static () => true);
        _pfDetailAddonIdResolver = pfDetailAddonIdResolver ?? ResolveLookingForGroupDetailAddonIdFromModule;
        _requestCoordinator = new PartyDetailRequestCoordinator(scannerHeadlessBackendKind);
    }

    internal PartyDetailCaptureRuntime(
        PartyDetailCaptureState state,
        IGameInteropProvider interopProvider,
        Action<string>? warningSink = null,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled,
        Func<bool>? isCaptureEnabled = null
    ) : this(state, new DalamudPartyDetailCaptureHookFactory(interopProvider), warningSink, scannerHeadlessBackendKind, isCaptureEnabled) {
    }

    private PartyDetailCaptureRuntime(
        PartyDetailCaptureState state,
        IPartyDetailCaptureHookFactory hookFactory,
        Action<string>? warningSink = null,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled,
        Func<bool>? isCaptureEnabled = null,
        Func<uint?>? pfDetailAddonIdResolver = null
    ) : this(state, scannerHeadlessBackendKind, isCaptureEnabled, pfDetailAddonIdResolver) {
        ArgumentNullException.ThrowIfNull(hookFactory);

        try {
            var registration = hookFactory.CreateHooksWithStatus(
                OpenListingDetour,
                OpenListingByContentIdDetour,
                PopulateListingDataDetour,
                PfDetailOpenDetour,
                warningSink
            );
            _hookScope = registration.HookScope;
            Volatile.Write(ref _hasPopulateHook, registration.HasPopulateHook);
            Volatile.Write(ref _isScannerCaptureReady, registration.IsScannerCaptureReady);
        } catch (Exception exception) {
            warningSink?.Invoke($"PartyDetailCaptureRuntime: failed to initialize hooks. {exception.Message}");
            _hookScope = null;
            Volatile.Write(ref _hasPopulateHook, false);
            Volatile.Write(ref _isScannerCaptureReady, false);
        }
    }

    internal static PartyDetailCaptureRuntime CreateForTesting(
        PartyDetailCaptureState state,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind,
        Func<bool>? isCaptureEnabled = null,
        Func<uint?>? pfDetailAddonIdResolver = null
    ) {
        return new PartyDetailCaptureRuntime(state, scannerHeadlessBackendKind, isCaptureEnabled, pfDetailAddonIdResolver);
    }

    internal static PartyDetailCaptureRuntime CreateForTesting(
        PartyDetailCaptureState state,
        IPartyDetailCaptureHookFactory hookFactory,
        Action<string>? warningSink = null,
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled,
        Func<bool>? isCaptureEnabled = null,
        Func<uint?>? pfDetailAddonIdResolver = null
    ) {
        return new PartyDetailCaptureRuntime(state, hookFactory, warningSink, scannerHeadlessBackendKind, isCaptureEnabled, pfDetailAddonIdResolver);
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
        Volatile.Write(ref _hasPopulateHook, false);
        Volatile.Write(ref _isScannerCaptureReady, false);
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
        if (!_isCaptureEnabled()) {
            return false;
        }

        return _requestCoordinator.ShouldSuppressScannerGameLog(_state, logMessageId);
    }

    internal bool TryGetScannerFailureSignal(
        long requestSerial,
        ulong listingId,
        ulong contentId,
        out PartyDetailScannerFailureSignal signal
    ) {
        return _requestCoordinator.TryGetScannerFailureSignal(requestSerial, listingId, contentId, out signal);
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
        OnFrameworkUpdate(BuildAgentSnapshot);
    }

    internal void TestFrameworkUpdate(
        Func<(UploadablePartyDetail? Snapshot, PartyDetailDebugSnapshot? DebugSnapshot)> snapshotFactory
    ) {
        ArgumentNullException.ThrowIfNull(snapshotFactory);
        OnFrameworkUpdate(snapshotFactory);
    }

    internal void TestObservePopulationEvent() {
        RaisePostRequestPopulationSignal();
    }

    internal bool TestPopulateListingDataSnapshot(
        UploadablePartyDetail? snapshot,
        PartyDetailDebugSnapshot? debugSnapshot = null
    ) {
        if (!_isCaptureEnabled()) {
            return false;
        }

        RaisePostRequestPopulationSignal();
        if (snapshot is null || !_state.TryGetCurrentRequestCycle(out var cycle)) {
            return false;
        }

        return TryRecordAgentSnapshot(
            snapshot,
            debugSnapshot,
            enforceFreshness: true,
            expectedRequestSerial: cycle.RequestSerial
        );
    }

    internal void TestFrameworkTick(UploadablePartyDetail? snapshot) {
        TestFrameworkTick(snapshot, null);
    }

    internal void TestFrameworkTick(UploadablePartyDetail? snapshot, PartyDetailDebugSnapshot? debugSnapshot) {
        ObserveFrameworkTick(snapshot, debugSnapshot);
    }

    private bool OpenListingDetour(nint agent, ulong listingId) {
        if (!_isCaptureEnabled()) {
            return true;
        }

        try {
            EnsureRequestCycleForIntercept(listingId, 0UL);
        } catch (Exception ex) {
            Plugin.Log?.Warning(ex, "PartyDetailCaptureRuntime: OpenListingDetour failed.");
        }

        return true;
    }

    private bool OpenListingByContentIdDetour(nint agent, ulong contentId) {
        if (!_isCaptureEnabled()) {
            return true;
        }

        try {
            EnsureRequestCycleForIntercept(0UL, contentId);
        } catch (Exception ex) {
            Plugin.Log?.Warning(ex, "PartyDetailCaptureRuntime: OpenListingByContentIdDetour failed.");
        }

        return true;
    }

    private void PopulateListingDataDetour(nint agent, nint listingData) {
        _ = listingData;
        if (!_isCaptureEnabled()) {
            return;
        }

        try {
            RaisePostRequestPopulationSignal();
            if (agent != 0) {
                TryCaptureFromPopulatedAgent(agent);
            }
        } catch (Exception ex) {
            Plugin.Log?.Warning(ex, "PartyDetailCaptureRuntime: PopulateListingDataDetour failed.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe void TryCaptureFromPopulatedAgent(nint agent) {
        if (!NativePointerValidator.IsValidUserPointer(agent) || !_state.TryGetCurrentRequestCycle(out var cycle)) {
            return;
        }

        var expectedAgent = AgentLookingForGroup.Instance();
        if (expectedAgent == null || (AgentLookingForGroup*)agent != expectedAgent) {
            return;
        }

        if (!PartyDetailSnapshotBuilder.TryBuildFromAgent(
                (AgentLookingForGroup*)agent,
                out var snapshot,
                out var debugSnapshot
            )) {
            return;
        }

        _ = TryRecordAgentSnapshot(
            snapshot,
            debugSnapshot,
            enforceFreshness: true,
            expectedRequestSerial: cycle.RequestSerial
        );
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
        if (!_isCaptureEnabled()) {
            return true;
        }

        try {
            var pfDetailAddonId = ResolvePfDetailAddonId();
            var suppress = IsScannerCaptureReady
                && _requestCoordinator.ShouldSuppressScannerPfDetailOpen(_state, addonId, pfDetailAddonId);
            EnsureManualCycleForDetailOpen(addonId, pfDetailAddonId, suppress);

            var hasScannerAttempt = _requestCoordinator.TryGetScannerAttemptDebugState(out var scannerAttempt);
            if (Plugin.Log is not null && (addonId == pfDetailAddonId || hasScannerAttempt)) {
                Plugin.Log.Debug(
                    "PartyDetailCaptureRuntime: pf_detail_open addon={AddonId} expected={ExpectedAddonId} suppress={Suppress} backend={Backend} currentOwner={CurrentOwner} currentSerial={CurrentSerial} armedSerial={ArmedSerial} allowHeadless={AllowHeadless} listing={ListingId} content={ContentId}",
                    addonId,
                    pfDetailAddonId,
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
        } catch (Exception ex) {
            Plugin.Log?.Warning(ex, "PartyDetailCaptureRuntime: PfDetailOpenDetour failed.");
            return true;
        }
    }

    internal uint ResolvePfDetailAddonId() {
        var cached = Volatile.Read(ref _cachedPfDetailAddonId);
        if (cached >= 0) {
            return (uint)cached;
        }

        var resolved = _pfDetailAddonIdResolver();
        if (resolved is { } addonId) {
            if (addonId <= int.MaxValue) {
                Interlocked.CompareExchange(ref _cachedPfDetailAddonId, (int)addonId, -1);
            }

            return addonId;
        }

        return DefaultPfDetailAddonId;
    }

    private static unsafe uint? ResolveLookingForGroupDetailAddonIdFromModule() {
        try {
            var raptureAtkModule = RaptureAtkModule.Instance();
            if (!NativePointerValidator.IsValidUserPointer(raptureAtkModule)) {
                return null;
            }

            var addonNames = raptureAtkModule->AddonNames.AsSpan();
            for (var i = 0; i < addonNames.Length; i++) {
                if (addonNames[i].AsSpan().SequenceEqual("LookingForGroupDetail"u8)) {
                    return (uint)i;
                }
            }
        } catch {
            // Fall back to DefaultPfDetailAddonId if native memory cannot be read.
        }

        return null;
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

    private void EnsureManualCycleForDetailOpen(uint addonId, uint expectedPfDetailAddonId, bool suppress) {
        if (addonId != expectedPfDetailAddonId || suppress) {
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
        if (!_isCaptureEnabled() || !_state.HasActiveRequest) {
            return;
        }

        if (snapshot is null) {
            return;
        }

        TryRecordAgentSnapshot(snapshot, debugSnapshot, enforceFreshness: true);
    }

    private static (UploadablePartyDetail? Snapshot, PartyDetailDebugSnapshot? DebugSnapshot) BuildAgentSnapshot() {
        return PartyDetailSnapshotBuilder.TryBuildFromAgent(out var snapshot, out var debugSnapshot)
            ? (snapshot, debugSnapshot)
            : (null, debugSnapshot);
    }

    private void OnFrameworkUpdate(
        Func<(UploadablePartyDetail? Snapshot, PartyDetailDebugSnapshot? DebugSnapshot)> snapshotFactory
    ) {
        if (!_isCaptureEnabled()) {
            return;
        }

        // Snapshot construction reads native agent state and allocates several
        // per-member collections. Skip it unless this request is currently
        // eligible to accept a post-population snapshot.
        if (!_state.TryGetCurrentRequestCycle(out var cycle)) {
#if DEBUG
            DebugPerformanceDiagnostics.Count(DebugPerformancePath.PartySnapshotSkippedNoRequest);
#endif
            return;
        }

        if (!HasObservedPostRequestPopulation(cycle.RequestSerial)) {
#if DEBUG
            DebugPerformanceDiagnostics.Count(DebugPerformancePath.PartySnapshotSkippedAwaitPopulation);
#endif
            return;
        }

        (UploadablePartyDetail? Snapshot, PartyDetailDebugSnapshot? DebugSnapshot) snapshotResult;
#if DEBUG
        var snapshotStartedAt = DebugPerformanceDiagnostics.Begin();
        var snapshotAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
        try {
            snapshotResult = snapshotFactory();
        } finally {
            DebugPerformanceDiagnostics.Record(DebugPerformancePath.PartySnapshotBuild, snapshotStartedAt, snapshotAllocatedBefore);
        }
#else
        snapshotResult = snapshotFactory();
#endif
        var (snapshot, debugSnapshot) = snapshotResult;
        if (snapshot is null) {
#if DEBUG
            DebugPerformanceDiagnostics.Count(DebugPerformancePath.PartySnapshotEmpty);
#endif
            return;
        }

        // The request may have changed while the native snapshot was being
        // read. Validate the original serial and freshness again before record.
        var recorded = TryRecordAgentSnapshot(snapshot, debugSnapshot, enforceFreshness: true, expectedRequestSerial: cycle.RequestSerial);
#if DEBUG
        DebugPerformanceDiagnostics.Count(recorded
            ? DebugPerformancePath.PartySnapshotAccepted
            : DebugPerformancePath.PartySnapshotRejected);
#endif
    }

    private bool TryRecordAgentSnapshot(
        UploadablePartyDetail snapshot,
        PartyDetailDebugSnapshot? debugSnapshot,
        bool enforceFreshness,
        long? expectedRequestSerial = null
    ) {
        if (!_state.TryGetCurrentRequestCycle(out var cycle)) {
            return false;
        }

        if (expectedRequestSerial is { } expectedSerial && cycle.RequestSerial != expectedSerial) {
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

    internal sealed class DalamudPartyDetailCaptureHookFactory : IPartyDetailCaptureHookFactory {
        private readonly IGameInteropProvider _interopProvider;

        internal DalamudPartyDetailCaptureHookFactory(IGameInteropProvider interopProvider) {
            _interopProvider = interopProvider ?? throw new ArgumentNullException(nameof(interopProvider));
        }

        public IDisposable CreateHooks(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
        ) {
            return CreateHooksWithStatus(
                openListingDetour,
                openListingByContentIdDetour,
                populateListingDataDetour,
                pfDetailOpenDetour
            ).HookScope;
        }

        public PartyDetailCaptureHookRegistration CreateHooksWithStatus(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour,
            Action<string>? warningSink = null
        ) {
            unsafe {
                return CreateHooksWithStatusFromAddresses(
                    AgentLookingForGroup.Addresses.OpenListing.Value,
                    AgentLookingForGroup.Addresses.OpenListingByContentId.Value,
                    (nint)AgentLookingForGroup.MemberFunctionPointers.PopulateListingData,
                    RaptureAtkModule.Addresses.OpenAddon.Value,
                    openListingDetour,
                    openListingByContentIdDetour,
                    populateListingDataDetour,
                    pfDetailOpenDetour,
                    warningSink
                );
            }
        }

        internal IDisposable CreateHooksFromAddresses(
            nint openListingAddress,
            nint openListingByContentIdAddress,
            nint populateListingDataAddress,
            nint openAddonAddress,
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
        ) {
            return CreateHooksWithStatusFromAddresses(
                openListingAddress,
                openListingByContentIdAddress,
                populateListingDataAddress,
                openAddonAddress,
                openListingDetour,
                openListingByContentIdDetour,
                populateListingDataDetour,
                pfDetailOpenDetour
            ).HookScope;
        }

        internal unsafe PartyDetailCaptureHookRegistration CreateHooksWithStatusFromAddresses(
            nint openListingAddress,
            nint openListingByContentIdAddress,
            nint populateListingDataAddress,
            nint openAddonAddress,
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour,
            Action<string>? warningSink = null
        ) {
            ArgumentNullException.ThrowIfNull(openListingDetour);
            ArgumentNullException.ThrowIfNull(openListingByContentIdDetour);
            ArgumentNullException.ThrowIfNull(populateListingDataDetour);
            ArgumentNullException.ThrowIfNull(pfDetailOpenDetour);

            if (populateListingDataAddress == 0) {
                throw new InvalidOperationException("AgentLookingForGroup.PopulateListingData address is unavailable.");
            }

            Hook<AgentLookingForGroup.Delegates.OpenListing>? openListingHook = null;
            Hook<AgentLookingForGroup.Delegates.OpenListingByContentId>? openListingByContentIdHook = null;
            Hook<AgentLookingForGroup.Delegates.PopulateListingData>? populateListingDataHook = null;
            Hook<RaptureAtkModule.Delegates.OpenAddon>? openAddonHook = null;
            var hooks = new InteropHookBundle();

            try {
                populateListingDataHook = _interopProvider.HookFromAddress<AgentLookingForGroup.Delegates.PopulateListingData>(
                    populateListingDataAddress,
                    (thisPtr, listingData) => {
                        populateListingDataHook!.Original(thisPtr, listingData);
                        populateListingDataDetour((nint)thisPtr, (nint)listingData);
                    }
                );
                hooks.AddAndEnable(populateListingDataHook, static hook => hook.Enable());
            } catch {
                hooks.Dispose();
                throw;
            }

            var hasOpenListingHook = TryAddOptionalHook(
                hooks,
                openListingAddress,
                "AgentLookingForGroup.OpenListing",
                address => openListingHook = _interopProvider.HookFromAddress<AgentLookingForGroup.Delegates.OpenListing>(
                    address,
                    (thisPtr, listingId) => {
                        _ = openListingDetour((nint)thisPtr, listingId);
                        return openListingHook!.Original(thisPtr, listingId);
                    }
                ),
                static hook => hook.Enable(),
                warningSink
            );

            var hasOpenListingByContentIdHook = TryAddOptionalHook(
                hooks,
                openListingByContentIdAddress,
                "AgentLookingForGroup.OpenListingByContentId",
                address => openListingByContentIdHook = _interopProvider.HookFromAddress<AgentLookingForGroup.Delegates.OpenListingByContentId>(
                    address,
                    (thisPtr, contentId) => {
                        _ = openListingByContentIdDetour((nint)thisPtr, contentId);
                        return openListingByContentIdHook!.Original(thisPtr, contentId);
                    }
                ),
                static hook => hook.Enable(),
                warningSink
            );

            var hasOpenAddonHook = TryAddOptionalHook(
                hooks,
                openAddonAddress,
                "RaptureAtkModule.OpenAddon",
                address => openAddonHook = _interopProvider.HookFromAddress<RaptureAtkModule.Delegates.OpenAddon>(
                    address,
                    (thisPtr, addonNameId, valueCount, values, eventInterface, eventKind, parentAddonId, depthLayer) =>
                        InvokeOpenAddonDetour(
                            pfDetailOpenDetour,
                            () => openAddonHook!.Original(
                                thisPtr,
                                addonNameId,
                                valueCount,
                                values,
                                eventInterface,
                                eventKind,
                                parentAddonId,
                                depthLayer
                            ),
                            (nint)thisPtr,
                            addonNameId,
                            (nint)valueCount,
                            (nint)values,
                            (nint)eventInterface,
                            unchecked((nint)eventKind),
                            parentAddonId,
                            depthLayer
                        )
                ),
                static hook => hook.Enable(),
                warningSink
            );

            return new PartyDetailCaptureHookRegistration(
                hooks,
                HasPopulateHook: true,
                HasOpenListingHook: hasOpenListingHook,
                HasOpenListingByContentIdHook: hasOpenListingByContentIdHook,
                HasOpenAddonHook: hasOpenAddonHook
            );
        }

        private static bool TryAddOptionalHook<THook>(
            InteropHookBundle hooks,
            nint address,
            string hookName,
            Func<nint, THook> createHook,
            Action<THook> enableHook,
            Action<string>? warningSink
        )
            where THook : class, IDisposable {
            if (address == 0) {
                warningSink?.Invoke($"PartyDetailCaptureRuntime: optional {hookName} address is unavailable; scanner capture disabled.");
                return false;
            }

            THook? hook = null;
            try {
                hook = createHook(address);
                enableHook(hook);
                hooks.Add(hook);
                return true;
            } catch (Exception exception) {
                hook?.Dispose();
                warningSink?.Invoke($"PartyDetailCaptureRuntime: optional {hookName} hook failed; scanner capture disabled. {exception.Message}");
                return false;
            }
        }

        internal static ushort InvokeOpenAddonDetour(
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour,
            Func<ushort> callOriginal,
            nint raptureAtkModule,
            uint addonNameId,
            nint valueCount,
            nint values,
            nint eventInterface,
            nint eventKind,
            ushort parentAddonId,
            int depthLayer
        ) {
            return pfDetailOpenDetour(
                raptureAtkModule,
                addonNameId,
                valueCount,
                values,
                eventInterface,
                eventKind,
                parentAddonId,
                depthLayer
            )
                ? callOriginal()
                : (ushort)0;
        }
    }
}
