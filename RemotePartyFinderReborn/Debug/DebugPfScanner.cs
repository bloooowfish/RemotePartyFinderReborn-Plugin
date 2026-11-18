using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace RemotePartyFinderReborn;

internal sealed class CollectedListingBuffer {
    internal const int CollectedListingStaleTtlSeconds = 600;

    private readonly Dictionary<ulong, DebugPfListingCandidate> _collectedListings = new();
    private readonly Dictionary<ulong, DateTime> _recentlyScannedAtUtc = new();
    private readonly object _lock = new();
    private int _collectedPageBatchCount;
    private int _lastObservedBatchNumber = -1;

    internal int Count {
        get {
            lock (_lock) {
                return _collectedListings.Count;
            }
        }
    }

    internal int PageBatchCount {
        get {
            lock (_lock) {
                return _collectedPageBatchCount;
            }
        }
    }

    internal void Prune(DateTime nowUtc) {
        lock (_lock) {
            PruneExpiredLocked(nowUtc);
        }
    }

    internal bool UpsertFromListing(ulong listingId, ulong contentId, int batchNumber, DateTime nowUtc) {
        if (listingId == 0) {
            return false;
        }

        lock (_lock) {
            PruneExpiredLocked(nowUtc);

            if (_recentlyScannedAtUtc.TryGetValue(listingId, out var scannedAtUtc)
                && (nowUtc - scannedAtUtc).TotalSeconds <= CollectedListingStaleTtlSeconds) {
                return false;
            }

            if (_collectedListings.Count == 0) {
                _collectedPageBatchCount = 0;
                _lastObservedBatchNumber = -1;
            }

            if (_lastObservedBatchNumber == -1 || batchNumber != _lastObservedBatchNumber) {
                _collectedPageBatchCount++;
                _lastObservedBatchNumber = batchNumber;
            }

            if (_collectedListings.TryGetValue(listingId, out var existing)) {
                _collectedListings[listingId] = new DebugPfListingCandidate(
                    listingId,
                    contentId != 0 ? contentId : existing.ContentId,
                    existing.SeenAtUtc,
                    Math.Max(existing.BatchNumber, batchNumber),
                    nowUtc
                );
                return false;
            }

            _collectedListings[listingId] = new DebugPfListingCandidate(
                listingId,
                contentId,
                nowUtc,
                batchNumber,
                nowUtc
            );
            return true;
        }
    }

    internal DebugPfListingCandidate[] TakeOrderedSnapshot(DateTime nowUtc) {
        lock (_lock) {
            PruneExpiredLocked(nowUtc);
            return _collectedListings.Values
                .OrderBy(static value => value.SeenAtUtc)
                .ThenByDescending(static value => value.BatchNumber)
                .ToArray();
        }
    }

    internal void MarkSucceeded(ulong listingId, DateTime nowUtc) {
        if (listingId == 0) {
            return;
        }

        lock (_lock) {
            _recentlyScannedAtUtc[listingId] = nowUtc;
            _collectedListings.Remove(listingId);
            ResetBatchStateIfEmptyLocked();
        }
    }

    internal void RemoveListing(ulong listingId) {
        if (listingId == 0) {
            return;
        }

        lock (_lock) {
            if (_collectedListings.Remove(listingId)) {
                ResetBatchStateIfEmptyLocked();
            }
        }
    }

    internal int ClearAll() {
        lock (_lock) {
            var removed = _collectedListings.Count;
            _collectedListings.Clear();
            _recentlyScannedAtUtc.Clear();
            _collectedPageBatchCount = 0;
            _lastObservedBatchNumber = -1;
            return removed;
        }
    }

    private void PruneExpiredLocked(DateTime nowUtc) {
        if (_recentlyScannedAtUtc.Count > 0) {
            List<ulong> expiredDedup = null;
            foreach (var pair in _recentlyScannedAtUtc) {
                if ((nowUtc - pair.Value).TotalSeconds > CollectedListingStaleTtlSeconds) {
                    expiredDedup ??= new List<ulong>();
                    expiredDedup.Add(pair.Key);
                }
            }

            if (expiredDedup is not null) {
                foreach (var id in expiredDedup) {
                    _recentlyScannedAtUtc.Remove(id);
                }
            }
        }

        if (_collectedListings.Count > 0) {
            List<ulong> expiredListings = null;
            foreach (var pair in _collectedListings) {
                var lastSeen = pair.Value.LastSeenAtUtc ?? pair.Value.SeenAtUtc;
                if ((nowUtc - lastSeen).TotalSeconds > CollectedListingStaleTtlSeconds) {
                    expiredListings ??= new List<ulong>();
                    expiredListings.Add(pair.Key);
                }
            }

            if (expiredListings is not null) {
                foreach (var id in expiredListings) {
                    _collectedListings.Remove(id);
                }
            }
        }

        ResetBatchStateIfEmptyLocked();
    }

    private void ResetBatchStateIfEmptyLocked() {
        if (_collectedListings.Count == 0) {
            _collectedPageBatchCount = 0;
            _lastObservedBatchNumber = -1;
        }
    }
}

internal sealed class DebugPfScanner : IDisposable {
    private static readonly TimeSpan MaxGathererFlushWait = TimeSpan.FromMilliseconds(1500);

    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private readonly PartyDetailCollector _detailCollector;
    private readonly PartyDetailCaptureRuntime _detailCaptureRuntime;
    private readonly Gatherer _gatherer;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool> _isPluginOperational;
    private readonly Func<bool> _isLookingForGroupOpen;
    private readonly Func<DebugPfDetailSnapshot> _getCurrentDetailSnapshot;
    private readonly Func<DebugPfListingCandidate, bool> _openListingInGame;
    private readonly Action _triggerGathererImmediateUpload;
    private readonly Func<bool> _hasGathererPendingUploadWork;
    private readonly Func<uint, DateTime, bool> _wasListingUploadedAfter;
    private readonly Action<string> _debugLog;

    private readonly CollectedListingBuffer _collectedBuffer = new();
    private DebugPfListingCandidate[] _collectedRunSnapshot = Array.Empty<DebugPfListingCandidate>();
    private readonly object _snapshotLock = new();
    private readonly DebugPfScanStateMachine _stateMachine = new(new DebugPfListingQueue());

    private volatile bool _isRunning;
    private DateTime _awaitGathererFlushDeadlineUtc = DateTime.MinValue;
    private Guid _currentCaptureAttemptId;
    private ulong _currentCaptureAttemptListingId;
#if DEBUG
    private long _runStartedAtTimestamp;
#endif

    internal DebugPfScanner(
        Plugin plugin,
        PartyDetailCollector detailCollector,
        PartyDetailCaptureRuntime detailCaptureRuntime,
        Gatherer gatherer
    ) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
        _detailCollector = detailCollector ?? throw new ArgumentNullException(nameof(detailCollector));
        _detailCaptureRuntime = detailCaptureRuntime ?? throw new ArgumentNullException(nameof(detailCaptureRuntime));
        _gatherer = gatherer ?? throw new ArgumentNullException(nameof(gatherer));
        _utcNow = static () => DateTime.UtcNow;
        _isPluginOperational = () => _plugin.IsPluginOperational;
        _isLookingForGroupOpen = () => _plugin.GameGui.GetAddonByName("LookingForGroup", 1) != 0;
        _getCurrentDetailSnapshot = GetCurrentDetailSnapshotFromGame;
        _openListingInGame = OpenListingInGame;
        _triggerGathererImmediateUpload = () => _gatherer.TriggerImmediateUpload();
        _hasGathererPendingUploadWork = () => _gatherer.HasPendingUploadWork;
        _wasListingUploadedAfter = (uploadId, seenAtUtc) => _gatherer.WasListingUploadedAfter(uploadId, seenAtUtc);
        _debugLog = static message => Plugin.Log?.Debug(message);

        _plugin.PartyFinderGui.ReceiveListing += OnListing;
        _plugin.Framework.Update += OnUpdate;
    }

    internal DebugPfScanner(
        Configuration configuration,
        PartyDetailCollector detailCollector,
        PartyDetailCaptureRuntime detailCaptureRuntime,
        Func<DateTime> utcNow,
        Func<bool> isPluginOperational,
        Func<bool> isLookingForGroupOpen,
        Func<DebugPfDetailSnapshot> getCurrentDetailSnapshot,
        Func<DebugPfListingCandidate, bool> openListingInGame,
        Action triggerGathererImmediateUpload = null,
        Func<bool> hasGathererPendingUploadWork = null,
        Func<uint, DateTime, bool> wasListingUploadedAfter = null,
        Action<string> debugLog = null
    ) {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _detailCollector = detailCollector ?? throw new ArgumentNullException(nameof(detailCollector));
        _detailCaptureRuntime = detailCaptureRuntime ?? throw new ArgumentNullException(nameof(detailCaptureRuntime));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _isPluginOperational = isPluginOperational ?? throw new ArgumentNullException(nameof(isPluginOperational));
        _isLookingForGroupOpen = isLookingForGroupOpen ?? throw new ArgumentNullException(nameof(isLookingForGroupOpen));
        _getCurrentDetailSnapshot = getCurrentDetailSnapshot ?? throw new ArgumentNullException(nameof(getCurrentDetailSnapshot));
        _openListingInGame = openListingInGame ?? throw new ArgumentNullException(nameof(openListingInGame));
        _triggerGathererImmediateUpload = triggerGathererImmediateUpload ?? (static () => { });
        _hasGathererPendingUploadWork = hasGathererPendingUploadWork ?? (static () => false);
        _wasListingUploadedAfter = wasListingUploadedAfter ?? (static (_, _) => true);
        _debugLog = debugLog ?? (static _ => { });
    }

    private int ConfiguredActionIntervalMs => Configuration.NormalizeScanActionIntervalMs(_configuration.AutoDetailScanActionIntervalMs);
    private int ConfiguredPostCooldownMs => Configuration.NormalizeScanPostCooldownMs(_configuration.AutoDetailScanPostListingCooldownMs);
    private int ConfiguredDetailTimeoutMs => Configuration.NormalizeScanDetailTimeoutMs(_configuration.AutoDetailScanDetailTimeoutMs);
    private int ConfiguredRetryCount => Configuration.NormalizeScanRetryCount(_configuration.AutoDetailScanRetryCount);

    internal bool IsRunning => _isRunning;
    internal bool IsPartyFinderOpen => IsLookingForGroupOpen();
    internal string StateName => _stateMachine.StateName;
    internal ulong CurrentTargetListingId => _stateMachine.CurrentTargetListingId;
    internal int PendingCount => _stateMachine.PendingCount;
    internal int ProcessedCount => _stateMachine.ProcessedCount;
    internal int ConsecutiveFailures => _stateMachine.ConsecutiveFailures;

    internal int CollectedListingCount {
        get {
            _collectedBuffer.Prune(_utcNow());
            return _collectedBuffer.Count;
        }
    }

    internal int CollectedPageBatchCount => _collectedBuffer.PageBatchCount;
    internal string LastAttemptReason => _stateMachine.LastAttemptReason;
    internal bool LastAttemptSuccess => _stateMachine.LastAttemptSuccess;
    internal ulong LastAttemptListingId => _stateMachine.LastAttemptListingId;

    internal bool StartScan(out string status) {
        if (!_isPluginOperational()) {
            status = "FFLogs API registration is required.";
            return false;
        }

        if (!IsLookingForGroupOpen()) {
            status = "Open the Party Finder window first.";
            return false;
        }

        var nowUtc = _utcNow();
        var snapshot = _collectedBuffer.TakeOrderedSnapshot(nowUtc);
        if (snapshot.Length == 0) {
            status = "No collected high-end PF listings ready. Open or browse PF pages first.";
            return false;
        }

        lock (_snapshotLock) {
            _collectedRunSnapshot = snapshot;
        }

        _stateMachine.ResetRunPreservingDedup();
        ResetCaptureAttempt();
        _isRunning = true;
#if DEBUG
        _runStartedAtTimestamp = DebugPerformanceDiagnostics.Begin();
#endif
        _triggerGathererImmediateUpload();
        _awaitGathererFlushDeadlineUtc = nowUtc + MaxGathererFlushWait;
        _stateMachine.StartSyncQueue();

        status = $"Started detail scan for {snapshot.Length} collected listing(s).";
        return true;
    }

    internal void StopScan() {
        _isRunning = false;
        _awaitGathererFlushDeadlineUtc = DateTime.MinValue;
#if DEBUG
        _runStartedAtTimestamp = 0;
#endif
        SetIdle();
    }

    internal int ClearCollectedListings() {
        _isRunning = false;
        _awaitGathererFlushDeadlineUtc = DateTime.MinValue;
#if DEBUG
        _runStartedAtTimestamp = 0;
#endif
        lock (_snapshotLock) {
            _collectedRunSnapshot = Array.Empty<DebugPfListingCandidate>();
        }

        var removed = _collectedBuffer.ClearAll();
        _stateMachine.Reset();
        ResetCaptureAttempt();
        return removed;
    }

    public void Dispose() {
        if (_plugin is not null) {
            _plugin.Framework.Update -= OnUpdate;
            _plugin.PartyFinderGui.ReceiveListing -= OnListing;
        }
    }

    internal void ResetSession() {
        _stateMachine.ResetRunPreservingDedup();
        ResetCaptureAttempt();
    }

    internal void TestReceiveHighEndListing(ulong listingId, ulong contentId, int batchNumber = 1) {
        if (!_isPluginOperational() || listingId == 0) {
            return;
        }

        _collectedBuffer.UpsertFromListing(listingId, contentId, batchNumber, _utcNow());
    }

    internal void TestUpdate() {
        ProcessUpdateTick();
    }

    private void OnListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args) {
        if (!_isPluginOperational() || listing.Id == 0 || !UploadableListing.IsSourceClientHighEndDuty(listing)) {
            return;
        }

        _collectedBuffer.UpsertFromListing(listing.Id, listing.ContentId, args.BatchNumber, _utcNow());
    }

    private void OnUpdate(IFramework framework) {
        ProcessUpdateTick();
    }

    private void ProcessUpdateTick() {
        if (!_isRunning || !_isPluginOperational()) {
            if (_isRunning
                || _stateMachine.State != DebugPfScanState.Idle
                || _stateMachine.PendingCount > 0
                || _currentCaptureAttemptId != Guid.Empty) {
                StopScan();
            }

            return;
        }

#if DEBUG
        var tickStartedAt = DebugPerformanceDiagnostics.Begin();
        var tickAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
        try {
#endif
        if (!IsLookingForGroupOpen()) {
            StopScan();
            return;
        }

        var nowUtc = _utcNow();
        if (_stateMachine.State == DebugPfScanState.SyncQueue
            && _hasGathererPendingUploadWork()
            && nowUtc < _awaitGathererFlushDeadlineUtc
            && !AreSnapshotListingsUploaded()) {
            return;
        }

        if (ReachedFailureLimit(_stateMachine.ConsecutiveFailures, DebugPfScanStateMachine.DefaultMaxConsecutiveFailures)) {
            CompleteRun("max_consecutive_failures");
            return;
        }

#if DEBUG
        var actionStartedAt = DebugPerformanceDiagnostics.Begin();
        var actionAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
        try {
#endif
        for (var step = 0; step < 4 && _isRunning; step++) {
            switch (_stateMachine.State) {
                case DebugPfScanState.Idle:
                    _stateMachine.StartSyncQueue();
                    continue;

                case DebugPfScanState.SyncQueue:
                    if (_hasGathererPendingUploadWork()
                        && _utcNow() < _awaitGathererFlushDeadlineUtc
                        && !AreSnapshotListingsUploaded()) {
                        break;
                    }

                    if (ReachedFailureLimit(_stateMachine.ConsecutiveFailures, DebugPfScanStateMachine.DefaultMaxConsecutiveFailures)) {
                        CompleteRun("max_consecutive_failures");
                        break;
                    }

                    SyncQueue();
                    if (!_isRunning || _stateMachine.State != DebugPfScanState.OpenTarget) {
                        break;
                    }

                    continue;

                case DebugPfScanState.OpenTarget:
                    OpenTarget();
                    break;

                case DebugPfScanState.WaitDetailReady:
                    WaitDetailReady();
                    if (_stateMachine.State == DebugPfScanState.Cooldown) {
                        continue;
                    }

                    break;

                case DebugPfScanState.WaitCollected:
                    WaitCollected();
                    if (_stateMachine.State == DebugPfScanState.Cooldown) {
                        continue;
                    }

                    break;

                case DebugPfScanState.Cooldown:
                    _stateMachine.AdvanceCooldown(_utcNow());
                    if (_stateMachine.State == DebugPfScanState.Cooldown) {
                        break;
                    }

                    continue;
            }

            break;
        }
#if DEBUG
        } finally {
            DebugPerformanceDiagnostics.Record(DebugPerformancePath.DebugScannerStateAction, actionStartedAt, actionAllocatedBefore);
        }
#endif
#if DEBUG
        } finally {
            // Inclusive accepted-tick measurement; scanner_action is a nested sub-window.
            DebugPerformanceDiagnostics.Record(DebugPerformancePath.DebugScannerAcceptedTick, tickStartedAt, tickAllocatedBefore);
        }
#endif
    }

    private void SyncQueue() {
        DebugPfListingCandidate[] snapshot;
        lock (_snapshotLock) {
            snapshot = _collectedRunSnapshot;
        }

        var completionReason = _stateMachine.SyncQueue(_utcNow(), snapshot);
        if (completionReason is not null) {
            CompleteRun(completionReason);
        }
    }

    private void OpenTarget() {
        var target = _stateMachine.CurrentTarget;
        if (target.ListingId == 0) {
            _stateMachine.StartSyncQueue();
            return;
        }

        var nowUtc = _utcNow();
        if (!_stateMachine.IsActionReady(nowUtc)) {
            return;
        }

        var beforeAttempt = CaptureAttemptLogState();
        var opened = TryOpenTarget(target);
        var requestSerial = _currentCaptureAttemptId != Guid.Empty
            ? _detailCaptureRuntime.GetArmedScannerRequestSerial(_currentCaptureAttemptId)
            : null;
        _stateMachine.HandleOpenAttemptResult(
            nowUtc,
            opened,
            actionIntervalMs: ConfiguredActionIntervalMs,
            detailReadyTimeoutMs: ConfiguredDetailTimeoutMs,
            configuredRetries: ConfiguredRetryCount,
            postListingCooldownMs: ConfiguredPostCooldownMs,
            requestSerial: requestSerial
        );
        if (_stateMachine.State == DebugPfScanState.Cooldown && _stateMachine.CurrentTargetListingId != 0) {
            ResetCaptureAttempt();
        } else {
            LogAttemptIfChanged(beforeAttempt);
        }
    }

    private bool TryOpenTarget(DebugPfListingCandidate target) {
        ArmCaptureRequest(target);
        _detailCaptureRuntime.BeginScannerOpenAttempt(_currentCaptureAttemptId);
        try {
            return _openListingInGame(target);
        } finally {
            _detailCaptureRuntime.EndScannerOpenAttempt(_currentCaptureAttemptId);
        }
    }

    private static unsafe bool OpenListingInGame(DebugPfListingCandidate target) {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null) {
            return false;
        }

        if (agent->OpenListing(target.ListingId)) {
            return true;
        }

        return target.ContentId != 0 && agent->OpenListingByContentId(target.ContentId);
    }

    private void WaitDetailReady() {
        var beforeAttempt = CaptureAttemptLogState();
        var nowUtc = _utcNow();
        var postCooldownMs = ConfiguredPostCooldownMs;
        var configuredRetries = ConfiguredRetryCount;

        if (TryCompleteHeadlessConsumedAck(
                _stateMachine,
                _detailCaptureRuntime.IsScannerConsumedAckReady,
                nowUtc,
                postCooldownMs
            )) {
            LogAttemptIfChanged(beforeAttempt);
            return;
        }

        if (TryHandleScannerFailureSignal(
                _stateMachine,
                _detailCaptureRuntime.TryGetScannerFailureSignal,
                nowUtc,
                configuredRetries,
                postCooldownMs
            )) {
            if (_stateMachine.State == DebugPfScanState.Cooldown && _stateMachine.CurrentTargetListingId != 0) {
                ResetCaptureAttempt();
            } else {
                LogAttemptIfChanged(beforeAttempt);
            }

            return;
        }

        _stateMachine.HandleDetailReadyState(
            nowUtc,
            GetCurrentDetailSnapshot(),
            ConfiguredDetailTimeoutMs,
            configuredRetries,
            postCooldownMs
        );
        if (_stateMachine.State == DebugPfScanState.Cooldown && _stateMachine.CurrentTargetListingId != 0) {
            ResetCaptureAttempt();
        } else {
            LogAttemptIfChanged(beforeAttempt);
        }
    }

    private void WaitCollected() {
        var beforeAttempt = CaptureAttemptLogState();
        var target = _stateMachine.CurrentTarget;
        var hasConsumedAck = _stateMachine.CurrentRequestSerial is { } requestSerial
                             && _detailCaptureRuntime.IsScannerConsumedAckReady(
                                 requestSerial,
                                 target.ListingId,
                                 target.ContentId
                             );
        _stateMachine.HandleCollectedState(_utcNow(), hasConsumedAck, ConfiguredPostCooldownMs);
        LogAttemptIfChanged(beforeAttempt);
    }

    private bool AreSnapshotListingsUploaded() {
        DebugPfListingCandidate[] snapshot;
        lock (_snapshotLock) {
            snapshot = _collectedRunSnapshot;
        }

        foreach (var candidate in snapshot) {
            if (!UploadableListing.TryNormalizeListingIdForUpload(candidate.ListingId, out var uploadId)) {
                continue;
            }

            if (!_wasListingUploadedAfter(uploadId, candidate.SeenAtUtc)) {
                return false;
            }
        }

        return true;
    }

    private void SetIdle() {
        _stateMachine.SetIdle();
        ResetCaptureAttempt();
        lock (_snapshotLock) {
            _collectedRunSnapshot = Array.Empty<DebugPfListingCandidate>();
        }
    }

    private void CompleteRun(string reason) {
#if DEBUG
        if (_runStartedAtTimestamp != 0) {
            DebugPerformanceDiagnostics.RecordDuration(DebugPerformancePath.DebugScannerCompletedRun, _runStartedAtTimestamp, _stateMachine.ProcessedCount);
            _runStartedAtTimestamp = 0;
        }
#endif
        _debugLog($"DebugPfScanner: complete reason={reason} processed={_stateMachine.ProcessedCount} failures={_stateMachine.ConsecutiveFailures}");

        _isRunning = false;
        _awaitGathererFlushDeadlineUtc = DateTime.MinValue;
        SetIdle();
        _detailCollector.TriggerPendingDetailUploadNow();
    }

    private bool IsLookingForGroupOpen() {
        return _isLookingForGroupOpen();
    }

    private DebugPfDetailSnapshot GetCurrentDetailSnapshot() {
        return _getCurrentDetailSnapshot();
    }

    private unsafe DebugPfDetailSnapshot GetCurrentDetailSnapshotFromGame() {
        if (_plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1) == 0) {
            return default;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null) {
            return default;
        }

        ref var detailed = ref agent->LastViewedListing;
        var effectiveParties = Math.Max(1, (int)detailed.NumberOfParties);
        var declaredSlots = Math.Max((int)detailed.TotalSlots, effectiveParties * 8);
        var totalSlots = Math.Clamp(declaredSlots, 0, 48);
        var nonZeroMembers = 0;

        for (var i = 0; i < totalSlots; i++) {
            if (detailed.MemberContentIds[i] != 0) {
                nonZeroMembers++;
            }
        }

        return new DebugPfDetailSnapshot(
            detailed.ListingId,
            detailed.LeaderContentId,
            nonZeroMembers,
            totalSlots
        );
    }

    internal delegate bool TryGetScannerFailureSignalDelegate(
        long requestSerial,
        ulong listingId,
        ulong contentId,
        out PartyDetailScannerFailureSignal signal
    );

    internal static bool TryHandleScannerFailureSignal(
        DebugPfScanStateMachine stateMachine,
        TryGetScannerFailureSignalDelegate tryGetFailureSignal,
        DateTime nowUtc,
        int configuredRetries,
        int postListingCooldownMs
    ) {
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(tryGetFailureSignal);

        var target = stateMachine.CurrentTarget;
        if (target.ListingId == 0 || stateMachine.CurrentRequestSerial is not { } requestSerial) {
            return false;
        }

        if (!tryGetFailureSignal(requestSerial, target.ListingId, target.ContentId, out var signal)) {
            return false;
        }

        stateMachine.HandleServerFailureSignal(
            nowUtc,
            signal.LogMessageId,
            signal.IsNonRetryableUnavailable,
            configuredRetries,
            postListingCooldownMs
        );
        return true;
    }

    internal static bool TryCompleteHeadlessConsumedAck(
        DebugPfScanStateMachine stateMachine,
        Func<long, ulong, ulong, bool> isConsumedAckReady,
        DateTime nowUtc,
        int postListingCooldownMs
    ) {
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(isConsumedAckReady);

        var target = stateMachine.CurrentTarget;
        if (target.ListingId == 0 || stateMachine.CurrentRequestSerial is not { } requestSerial) {
            return false;
        }

        if (!isConsumedAckReady(requestSerial, target.ListingId, target.ContentId)) {
            return false;
        }

        stateMachine.HandleCollectedState(nowUtc, hasConsumedAck: true, postListingCooldownMs);
        return true;
    }

    internal static bool ShouldRetryTargetAfterFailure(int attemptsMade, int configuredRetries) {
        return DebugPfScanStateMachine.ShouldRetryTargetAfterFailure(attemptsMade, configuredRetries);
    }

    internal static bool ReachedFailureLimit(int consecutiveFailures, int configuredMaxFailures) {
        return consecutiveFailures >= Math.Max(configuredMaxFailures, 1);
    }

    private AttemptLogState CaptureAttemptLogState() {
        return new AttemptLogState(
            _stateMachine.LastAttemptListingId,
            _stateMachine.LastAttemptSuccess,
            _stateMachine.LastAttemptReason,
            _stateMachine.LastTerminalOutcome
        );
    }

    private void LogAttemptIfChanged(AttemptLogState before) {
        var after = CaptureAttemptLogState();
        if (after.Equals(before) || after.ListingId == 0) {
            return;
        }

        if (after.Success && after.TerminalOutcome == PartyDetailScannerAttemptOutcome.Succeeded) {
            _collectedBuffer.MarkSucceeded(after.ListingId, _utcNow());
        } else if (after.TerminalOutcome == PartyDetailScannerAttemptOutcome.Unavailable) {
            _collectedBuffer.RemoveListing(after.ListingId);
        }

        if (_currentCaptureAttemptId != Guid.Empty && _currentCaptureAttemptListingId == after.ListingId) {
            if (after.TerminalOutcome is { } outcome) {
                _detailCaptureRuntime.CompleteScannerRequest(_currentCaptureAttemptId, outcome);
            }

            if (_detailCaptureRuntime.GetArmedScannerRequestSerial(_currentCaptureAttemptId) is null) {
                _currentCaptureAttemptId = Guid.Empty;
                _currentCaptureAttemptListingId = 0UL;
            }
        }

        _debugLog($"DebugPfScanner: listing={after.ListingId} success={after.Success} reason={after.Reason}");
    }

    private void ArmCaptureRequest(DebugPfListingCandidate target) {
        if (_currentCaptureAttemptId == Guid.Empty || _currentCaptureAttemptListingId != target.ListingId) {
            _currentCaptureAttemptId = Guid.NewGuid();
            _currentCaptureAttemptListingId = target.ListingId;
        }

        _detailCaptureRuntime.ArmScannerRequest(_currentCaptureAttemptId, target.ListingId, target.ContentId);
    }

    private void ResetCaptureAttempt() {
        if (_currentCaptureAttemptId != Guid.Empty) {
            _detailCaptureRuntime.ClearScannerRequest(_currentCaptureAttemptId);
        } else {
            _detailCaptureRuntime.ResetScannerRequest();
        }

        _currentCaptureAttemptId = Guid.Empty;
        _currentCaptureAttemptListingId = 0UL;
    }

    private readonly record struct AttemptLogState(
        ulong ListingId,
        bool Success,
        string Reason,
        PartyDetailScannerAttemptOutcome? TerminalOutcome
    );
}
