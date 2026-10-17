using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace RemotePartyFinderReborn;

internal sealed class DebugPfScanner : IDisposable {
    private readonly Plugin _plugin;
    private readonly PartyDetailCaptureRuntime _detailCaptureRuntime;

    private readonly ConcurrentQueue<DebugPfListingCandidate> _incoming = new();
    private readonly Dictionary<ulong, DebugPfListingCandidate> _collectedListings = new();
    private DebugPfListingCandidate[] _collectedRunSnapshot = Array.Empty<DebugPfListingCandidate>();
    private readonly object _collectionLock = new();
    private readonly Stopwatch _tickGate = new();
    private readonly DebugPfScanStateMachine _stateMachine = new(new DebugPfListingQueue());

    private Guid _currentCaptureAttemptId;
    private ulong _currentCaptureAttemptListingId;
#if DEBUG
    private long _runStartedAtTimestamp;
#endif
    private bool _wasEnabled;
    private bool _runFromCollectedListings;

    internal DebugPfScanner(
        Plugin plugin,
        PartyDetailCollector detailCollector,
        PartyDetailCaptureRuntime detailCaptureRuntime,
        Gatherer _unusedGatherer
    ) {
        _plugin = plugin;
        _detailCaptureRuntime = detailCaptureRuntime;
        _ = detailCollector;

        _plugin.PartyFinderGui.ReceiveListing += OnListing;
        _plugin.Framework.Update += OnUpdate;
        _tickGate.Start();
    }

    internal bool Enabled => _plugin.Configuration.EnableAutoDetailScanDebug;
    internal string StateName => _stateMachine.StateName;
    internal ulong CurrentTargetListingId => _stateMachine.CurrentTargetListingId;
    internal int PendingCount => _stateMachine.PendingCount;
    internal int ProcessedCount => _stateMachine.ProcessedCount;
    internal int ConsecutiveFailures => _stateMachine.ConsecutiveFailures;
    internal int VisibleListingCount => _stateMachine.VisibleListingCount;
    internal int CollectedListingCount {
        get {
            lock (_collectionLock) {
                return _collectedListings.Count;
            }
        }
    }
    internal string LastAttemptReason => _stateMachine.LastAttemptReason;
    internal bool LastAttemptSuccess => _stateMachine.LastAttemptSuccess;
    internal ulong LastAttemptListingId => _stateMachine.LastAttemptListingId;

    internal int ClearCollectedListings() {
        var stopCollectedRun = _runFromCollectedListings;
        int removed;
        lock (_collectionLock) {
            removed = _collectedListings.Count;
            _collectedListings.Clear();
            _collectedRunSnapshot = Array.Empty<DebugPfListingCandidate>();

            if (stopCollectedRun) {
                _runFromCollectedListings = false;
                _wasEnabled = false;
                _stateMachine.Reset();
                ResetCaptureAttempt();
            }
        }

        if (stopCollectedRun && _plugin.Configuration.EnableAutoDetailScanDebug) {
            _plugin.Configuration.EnableAutoDetailScanDebug = false;
            _plugin.Configuration.Save();
        }

        return removed;
    }

    internal int CollectCurrentPageSnapshot() {
        unsafe {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) {
                return 0;
            }

            var added = 0;
            var now = DateTime.UtcNow;
            for (var index = 0; index < 50; index++) {
                var rawListingId = agent->Listings.ListingIds[index];
                if (rawListingId == 0) {
                    continue;
                }

                var listingId = rawListingId;
                var candidate = new DebugPfListingCandidate(listingId, 0, now.AddMilliseconds(index), 1000 - index);
                if (UpsertCollectedCandidate(candidate)) {
                    added++;
                }
            }

            return added;
        }
    }

    internal bool StartCollectedBatchRun(out string status) {
        var snapshotCount = 0;
        lock (_collectionLock) {
            if (_collectedListings.Count == 0) {
                status = "no collected listings; page through PF first or capture current page snapshot.";
                return false;
            }

            _collectedRunSnapshot = _collectedListings.Values
                .OrderBy(value => value.SeenAtUtc)
                .ThenByDescending(value => value.BatchNumber)
                .ToArray();
            snapshotCount = _collectedRunSnapshot.Length;
        }

        _runFromCollectedListings = true;
        ResetSession();
        _wasEnabled = false;

        if (!_plugin.Configuration.EnableAutoDetailScanDebug) {
            _plugin.Configuration.EnableAutoDetailScanDebug = true;
            _plugin.Configuration.Save();
        }

        status = $"scheduled batch detail-click run for {snapshotCount} collected listings.";
        return true;
    }

    internal void StartCurrentPageRun() {
        ConfigureCurrentPageRun(
            _plugin.Configuration,
            ResetSession,
            _plugin.Configuration.Save,
            ref _runFromCollectedListings,
            ref _wasEnabled);
    }

    internal static void ConfigureCurrentPageRun(
        Configuration configuration,
        Action resetSession,
        Action save,
        ref bool runFromCollectedListings,
        ref bool wasEnabled
    ) {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(resetSession);
        ArgumentNullException.ThrowIfNull(save);

        runFromCollectedListings = false;
        resetSession();
        wasEnabled = false;

        if (!configuration.EnableAutoDetailScanDebug) {
            configuration.EnableAutoDetailScanDebug = true;
            save();
        }
    }

    public void Dispose() {
        _plugin.Framework.Update -= OnUpdate;
        _plugin.PartyFinderGui.ReceiveListing -= OnListing;
    }

    internal void ResetSession() {
        _incoming.Clear();
        _stateMachine.Reset();
        ResetCaptureAttempt();
    }

    private void OnListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args) {
        if (listing.Id == 0) {
            return;
        }

        if (!UploadableListing.IsSourceClientHighEndDuty(listing)) {
            return;
        }

        var candidate = new DebugPfListingCandidate(
            listing.Id,
            listing.ContentId,
            DateTime.UtcNow,
            args.BatchNumber
        );

        if (_plugin.Configuration.EnableManualPageCollectionDebug) {
            UpsertCollectedCandidate(candidate);
        }

        if (!Enabled || _runFromCollectedListings) {
            return;
        }

        _incoming.Enqueue(candidate);
    }

    private void OnUpdate(IFramework framework) {
        if (_tickGate.ElapsedMilliseconds < 50) {
            return;
        }

        _tickGate.Restart();
#if DEBUG
        var tickStartedAt = DebugPerformanceDiagnostics.Begin();
        var tickAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
        try {
#endif

        if (!Enabled) {
            if (_stateMachine.State != DebugPfScanState.Idle
                || _stateMachine.PendingCount > 0
                || _stateMachine.ProcessedCount > 0
                || _stateMachine.ConsecutiveFailures > 0) {
                ResetSession();
            }

            _runFromCollectedListings = false;
            lock (_collectionLock) {
                _collectedRunSnapshot = Array.Empty<DebugPfListingCandidate>();
            }
            _wasEnabled = false;
            return;
        }

        if (!_wasEnabled) {
            _wasEnabled = true;
            StartRunFromFirstPage();
            return;
        }

        if (!IsLookingForGroupOpen()) {
            SetIdle();
            return;
        }

        var maxFailures = Configuration.NormalizeScanMaxFailures(
            _plugin.Configuration.AutoDetailScanMaxConsecutiveFailures);
        if (ReachedFailureLimit(_stateMachine.ConsecutiveFailures, maxFailures)) {
            CompleteRun("max_consecutive_failures", clearCollectedListings: false);
            return;
        }

#if DEBUG
        var actionStartedAt = DebugPerformanceDiagnostics.Begin();
        var actionAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
        try {
#endif
        switch (_stateMachine.State) {
            case DebugPfScanState.Idle:
                _stateMachine.StartSyncQueue();
                break;

            case DebugPfScanState.SyncQueue:
                SyncQueue();
                break;

            case DebugPfScanState.OpenTarget:
                OpenTarget();
                break;

            case DebugPfScanState.WaitDetailReady:
                WaitDetailReady();
                break;

            case DebugPfScanState.WaitCollected:
                WaitCollected();
                break;

            case DebugPfScanState.Cooldown:
                _stateMachine.AdvanceCooldown(DateTime.UtcNow);
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
        if (!_runFromCollectedListings) {
            DrainIncoming();
        }

        var nowUtc = DateTime.UtcNow;
        var hasIncomingListings = !_runFromCollectedListings && !_incoming.IsEmpty;
        var completionReason = _stateMachine.SyncQueue(
            nowUtc,
            _runFromCollectedListings ? GetCollectedRunSnapshot() : Array.Empty<DebugPfListingCandidate>(),
            hasIncomingListings,
            Configuration.NormalizeScanMaxPerRun(_plugin.Configuration.AutoDetailScanMaxPerRun),
            Configuration.NormalizeScanDedupTtlSeconds(_plugin.Configuration.AutoDetailScanDedupTtlSeconds),
            _runFromCollectedListings
        );

        if (completionReason is not null) {
            CompleteRun(completionReason);
        }
    }

    private void OpenTarget() {
        _stateMachine.RefreshCurrentTarget();
        var target = _stateMachine.CurrentTarget;
        if (target.ListingId == 0) {
            _stateMachine.StartSyncQueue();
            return;
        }

        var nowUtc = DateTime.UtcNow;
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
            Configuration.NormalizeScanActionIntervalMs(_plugin.Configuration.AutoDetailScanActionIntervalMs),
            Configuration.NormalizeScanDetailTimeoutMs(_plugin.Configuration.AutoDetailScanDetailTimeoutMs),
            PartyDetailUploadPolicy.NormalizeRetryCount(_plugin.Configuration.AutoDetailScanRetryCount),
            Configuration.NormalizeScanPostCooldownMs(_plugin.Configuration.AutoDetailScanPostListingCooldownMs),
            requestSerial
        );
        LogAttemptIfChanged(beforeAttempt);
    }

    private unsafe bool TryOpenTarget(DebugPfListingCandidate target) {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null) {
            return false;
        }

        ArmCaptureRequest(target);
        _detailCaptureRuntime.BeginScannerOpenAttempt(_currentCaptureAttemptId);
        try {
            if (agent->OpenListing(target.ListingId)) {
                return true;
            }

            return target.ContentId != 0 && agent->OpenListingByContentId(target.ContentId);
        } finally {
            _detailCaptureRuntime.EndScannerOpenAttempt(_currentCaptureAttemptId);
        }
    }

    private void WaitDetailReady() {
        var beforeAttempt = CaptureAttemptLogState();
        if (TryCompleteHeadlessConsumedAck(
                _stateMachine,
                _detailCaptureRuntime.IsScannerConsumedAckReady,
                DateTime.UtcNow,
                Configuration.NormalizeScanPostCooldownMs(_plugin.Configuration.AutoDetailScanPostListingCooldownMs)
            )) {
            LogAttemptIfChanged(beforeAttempt);
            return;
        }

        _stateMachine.HandleDetailReadyState(
            DateTime.UtcNow,
            GetCurrentDetailSnapshot(),
            Configuration.NormalizeScanMinDwellMs(_plugin.Configuration.AutoDetailScanMinDwellMs),
            Configuration.NormalizeScanDetailTimeoutMs(_plugin.Configuration.AutoDetailScanDetailTimeoutMs),
            PartyDetailUploadPolicy.NormalizeRetryCount(_plugin.Configuration.AutoDetailScanRetryCount),
            Configuration.NormalizeScanPostCooldownMs(_plugin.Configuration.AutoDetailScanPostListingCooldownMs)
        );
        LogAttemptIfChanged(beforeAttempt);
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
        _stateMachine.HandleCollectedState(
            DateTime.UtcNow,
            hasConsumedAck,
            Configuration.NormalizeScanPostCooldownMs(_plugin.Configuration.AutoDetailScanPostListingCooldownMs)
        );
        LogAttemptIfChanged(beforeAttempt);
    }

    private void SetIdle() {
        _stateMachine.SetIdle();
        ResetCaptureAttempt();
        _runFromCollectedListings = false;
        lock (_collectionLock) {
            _collectedRunSnapshot = Array.Empty<DebugPfListingCandidate>();
        }
    }

    private void CompleteRun(string reason, bool clearCollectedListings = true) {
        var shouldClearCollectedListings = clearCollectedListings
                                           && _runFromCollectedListings
                                           && reason == "collected_batch_complete";
#if DEBUG
        if (_runStartedAtTimestamp != 0) {
            DebugPerformanceDiagnostics.RecordDuration(DebugPerformancePath.DebugScannerCompletedRun, _runStartedAtTimestamp, _stateMachine.ProcessedCount);
            _runStartedAtTimestamp = 0;
        }
#endif
        Plugin.Log.Debug($"DebugPfScanner: complete reason={reason} processed={_stateMachine.ProcessedCount} failures={_stateMachine.ConsecutiveFailures}");
        if (shouldClearCollectedListings) {
            var removed = ClearCompletedCollectedSnapshot();
            Plugin.Log.Debug($"DebugPfScanner: cleared collected listings after batch completion ({removed}).");
        }

        SetIdle();

        if (!_plugin.Configuration.EnableAutoDetailScanDebug) {
            return;
        }

        _plugin.Configuration.EnableAutoDetailScanDebug = false;
        _plugin.Configuration.Save();
    }

    private void DrainIncoming() {
        while (_incoming.TryDequeue(out var listing)) {
            _stateMachine.UpsertVisibleCandidate(listing);
        }
    }

    private bool UpsertCollectedCandidate(DebugPfListingCandidate incoming) {
        lock (_collectionLock) {
            if (_collectedListings.TryGetValue(incoming.ListingId, out var existing)) {
                _collectedListings[incoming.ListingId] = new DebugPfListingCandidate(
                    incoming.ListingId,
                    incoming.ContentId != 0 ? incoming.ContentId : existing.ContentId,
                    existing.SeenAtUtc,
                    Math.Max(existing.BatchNumber, incoming.BatchNumber),
                    incoming.LastSeenAtUtc ?? incoming.SeenAtUtc
                );
                return false;
            }

            _collectedListings[incoming.ListingId] = incoming with { LastSeenAtUtc = incoming.LastSeenAtUtc ?? incoming.SeenAtUtc };
            return true;
        }
    }

    private int ClearCompletedCollectedSnapshot() {
        lock (_collectionLock) {
            var removed = 0;
            foreach (var scanned in _collectedRunSnapshot) {
                if (!_collectedListings.TryGetValue(scanned.ListingId, out var latest)) {
                    continue;
                }

                var scannedLastSeen = scanned.LastSeenAtUtc ?? scanned.SeenAtUtc;
                var latestLastSeen = latest.LastSeenAtUtc ?? latest.SeenAtUtc;
                if (latestLastSeen != scannedLastSeen) {
                    continue;
                }

                if (_collectedListings.Remove(scanned.ListingId)) {
                    removed++;
                }
            }

            return removed;
        }
    }

    private IReadOnlyCollection<DebugPfListingCandidate> GetCollectedRunSnapshot() {
        return _collectedRunSnapshot;
    }

    private void StartRunFromFirstPage() {
        ResetSession();
#if DEBUG
        _runStartedAtTimestamp = DebugPerformanceDiagnostics.Begin();
#endif
        if (_runFromCollectedListings) {
            var snapshotCount = _collectedRunSnapshot.Length;
            if (snapshotCount == 0) {
                CompleteRun("collected_batch_empty");
                return;
            }

            _stateMachine.StartSyncQueue();
            Plugin.Log.Debug($"DebugPfScanner: starting deferred batch run from collected listings (seeded_listings={snapshotCount}).");
            return;
        }

        var currentPageSeeded = SeedFromCurrentPage();
        _stateMachine.StartSyncQueue();
        Plugin.Log.Debug($"DebugPfScanner: starting run from current PF page only (seeded_listings={currentPageSeeded}).");
    }

    private int SeedFromCurrentPage() {
        unsafe {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) {
                return 0;
            }

            var seeded = 0;
            var now = DateTime.UtcNow;
            for (var index = 0; index < 50; index++) {
                var rawListingId = agent->Listings.ListingIds[index];
                if (rawListingId == 0) {
                    continue;
                }

                var listingId = rawListingId;
                var seenAtUtc = now.AddMilliseconds(index);
                var batchNumber = 1000 - index;
                _stateMachine.UpsertVisibleCandidate(new DebugPfListingCandidate(listingId, 0, seenAtUtc, batchNumber));
                seeded++;
            }

            return seeded;
        }
    }

    private bool IsLookingForGroupOpen() {
        return _plugin.GameGui.GetAddonByName("LookingForGroup", 1) != 0;
    }

    private unsafe DebugPfDetailSnapshot GetCurrentDetailSnapshot() {
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

    internal static bool IsQueueDrained(int pendingTargets, bool hasIncomingListings) {
        return DebugPfScanStateMachine.IsQueueDrained(pendingTargets, hasIncomingListings);
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

        if (_currentCaptureAttemptId != Guid.Empty && _currentCaptureAttemptListingId == after.ListingId) {
            if (after.TerminalOutcome is { } outcome) {
                _detailCaptureRuntime.CompleteScannerRequest(_currentCaptureAttemptId, outcome);
            }

            if (_detailCaptureRuntime.GetArmedScannerRequestSerial(_currentCaptureAttemptId) is null) {
                _currentCaptureAttemptId = Guid.Empty;
                _currentCaptureAttemptListingId = 0UL;
            }
        }

        Plugin.Log.Debug($"DebugPfScanner: listing={after.ListingId} success={after.Success} reason={after.Reason}");
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
