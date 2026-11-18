using System;
using System.Collections.Generic;

namespace RemotePartyFinderReborn;

internal readonly record struct DebugPfListingCandidate(
    ulong ListingId,
    ulong ContentId,
    DateTime SeenAtUtc,
    int BatchNumber,
    DateTime? LastSeenAtUtc = null);

internal readonly record struct DebugPfDetailSnapshot(ulong ListingId, ulong LeaderContentId, int NonZeroMembers, int TotalSlots);

internal enum DebugPfScanState {
    Idle,
    SyncQueue,
    OpenTarget,
    WaitDetailReady,
    WaitCollected,
    Cooldown,
}

internal sealed class DebugPfScanStateMachine {
    internal const int DefaultActionIntervalMs = Configuration.DefaultScanActionIntervalMs;
    internal const int DefaultDetailReadyTimeoutMs = Configuration.DefaultScanDetailTimeoutMs;
    internal const int DefaultPostListingCooldownMs = Configuration.DefaultScanPostListingCooldownMs;
    internal const int DefaultDedupTtlSeconds = 600;
    internal const int DefaultMaxConsecutiveFailures = 5;
    internal const int DefaultMaxRetries = Configuration.DefaultScanRetryCount;
    private const int DetailCollectionTimeoutMs = 3500;

    private readonly DebugPfListingQueue _queue;

    private DebugPfScanState _state = DebugPfScanState.Idle;
    private DebugPfListingCandidate _target;
    private bool _hasTarget;
    private bool _retryTargetAfterCooldown;
    private int _openAttemptsForTarget;
    private int _consecutiveFailures;
    private int _processedCount;
    private DateTime _nextOpenAtUtc = DateTime.MinValue;
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private DateTime _stateDeadlineUtc = DateTime.MinValue;
    private long? _activeRequestSerial;
    private DebugPfDetailSnapshot _lastReadySnapshot;
    private int _readyStableTicks;
    private string _lastAttemptReason = "none";
    private PartyDetailScannerAttemptOutcome? _lastTerminalOutcome;
    private bool _lastAttemptSuccess;
    private ulong _lastAttemptListingId;

    internal DebugPfScanStateMachine(DebugPfListingQueue queue) {
        _queue = queue;
    }

    internal DebugPfScanState State => _state;
    internal string StateName => _state.ToString();
    internal DebugPfListingCandidate CurrentTarget => _target;
    internal ulong CurrentTargetListingId => _hasTarget ? _target.ListingId : 0UL;
    internal int PendingCount => _queue.PendingCount;
    internal int ProcessedCount => _processedCount;
    internal int ConsecutiveFailures => _consecutiveFailures;
    internal string LastAttemptReason => _lastAttemptReason;
    internal PartyDetailScannerAttemptOutcome? LastTerminalOutcome => _lastTerminalOutcome;
    internal bool LastAttemptSuccess => _lastAttemptSuccess;
    internal ulong LastAttemptListingId => _lastAttemptListingId;
    internal long? CurrentRequestSerial => _activeRequestSerial;

    internal void Reset() {
        _queue.Reset();
        ResetRunState();
    }

    internal void ResetRunPreservingDedup() {
        _queue.ResetRun();
        ResetRunState();
    }

    private void ResetRunState() {
        _state = DebugPfScanState.Idle;
        _hasTarget = false;
        _retryTargetAfterCooldown = false;
        _openAttemptsForTarget = 0;
        _consecutiveFailures = 0;
        _processedCount = 0;
        _nextOpenAtUtc = DateTime.MinValue;
        _cooldownUntilUtc = DateTime.MinValue;
        _stateDeadlineUtc = DateTime.MinValue;
        _activeRequestSerial = null;
        _lastReadySnapshot = default;
        _readyStableTicks = 0;
        _lastAttemptReason = "none";
        _lastTerminalOutcome = null;
        _lastAttemptSuccess = false;
        _lastAttemptListingId = 0UL;
    }

    internal void StartSyncQueue() {
        _state = DebugPfScanState.SyncQueue;
    }

    internal bool IsActionReady(DateTime nowUtc) {
        return nowUtc >= _nextOpenAtUtc;
    }

    internal string SyncQueue(
        DateTime nowUtc,
        IReadOnlyCollection<DebugPfListingCandidate> collectedSnapshot,
        int dedupTtlSeconds = DefaultDedupTtlSeconds
    ) {
        _queue.PruneCaches(nowUtc, dedupTtlSeconds);

        if (_queue.PendingCount == 0) {
            _queue.RebuildPendingQueueFromSnapshot(collectedSnapshot, nowUtc, dedupTtlSeconds);
        }

        if (!_queue.TryTakeNextReadyTarget(nowUtc, dedupTtlSeconds, out var nextTarget)) {
            return "collected_batch_complete";
        }

        _target = nextTarget;
        _hasTarget = true;
        _openAttemptsForTarget = 0;
        _retryTargetAfterCooldown = false;
        _state = DebugPfScanState.OpenTarget;
        return null;
    }

    internal void HandleOpenAttemptResult(
        DateTime nowUtc,
        bool opened,
        int actionIntervalMs = DefaultActionIntervalMs,
        int detailReadyTimeoutMs = DefaultDetailReadyTimeoutMs,
        int configuredRetries = DefaultMaxRetries,
        int postListingCooldownMs = DefaultPostListingCooldownMs,
        long? requestSerial = null
    ) {
        if (!_hasTarget) {
            _state = DebugPfScanState.SyncQueue;
            return;
        }

        _openAttemptsForTarget++;
        var normalizedActionIntervalMs = Configuration.NormalizeScanActionIntervalMs(actionIntervalMs);
        _nextOpenAtUtc = nowUtc.AddMilliseconds(normalizedActionIntervalMs);

        if (opened) {
            _stateDeadlineUtc = nowUtc.AddMilliseconds(GetDetailReadyTimeoutMs(detailReadyTimeoutMs));
            _activeRequestSerial = requestSerial;
            _readyStableTicks = 0;
            _lastReadySnapshot = default;
            _state = DebugPfScanState.WaitDetailReady;
            return;
        }

        if (ShouldRetryTargetAfterFailure(_openAttemptsForTarget, Configuration.NormalizeScanRetryCount(configuredRetries))) {
            _retryTargetAfterCooldown = true;
            _cooldownUntilUtc = _nextOpenAtUtc;
            _state = DebugPfScanState.Cooldown;
            return;
        }

        MarkAttempt(nowUtc, success: false, "open_failed", PartyDetailScannerAttemptOutcome.OpenFailed, postListingCooldownMs);
    }

    internal void AttachRequestSerial(long? requestSerial) {
        _activeRequestSerial = requestSerial;
    }

    internal void HandleDetailReadyState(
        DateTime nowUtc,
        DebugPfDetailSnapshot snapshot,
        int detailReadyTimeoutMs = DefaultDetailReadyTimeoutMs,
        int configuredRetries = DefaultMaxRetries,
        int postListingCooldownMs = DefaultPostListingCooldownMs
    ) {
        if (!_hasTarget) {
            _state = DebugPfScanState.SyncQueue;
            return;
        }

        var listingMatches = snapshot.ListingId == _target.ListingId;
        var leaderMatches = _target.ContentId == 0 || snapshot.LeaderContentId == _target.ContentId;
        var hasMembers = snapshot.NonZeroMembers > 0;

        if (listingMatches && leaderMatches && hasMembers) {
            if (_readyStableTicks > 0 && snapshot.Equals(_lastReadySnapshot)) {
                _readyStableTicks++;
            } else {
                _lastReadySnapshot = snapshot;
                _readyStableTicks = 1;
            }

            var requiredStableTicks = _target.ContentId == 0 ? 4 : 2;
            if (_readyStableTicks >= requiredStableTicks) {
                _stateDeadlineUtc = nowUtc.AddMilliseconds(GetDetailCollectionTimeoutMs(detailReadyTimeoutMs));
                _state = DebugPfScanState.WaitCollected;
                return;
            }
        } else {
            _readyStableTicks = 0;
            _lastReadySnapshot = default;
        }

        if (nowUtc <= _stateDeadlineUtc) {
            return;
        }

        if (ShouldRetryTargetAfterFailure(_openAttemptsForTarget, Configuration.NormalizeScanRetryCount(configuredRetries))) {
            _retryTargetAfterCooldown = true;
            _activeRequestSerial = null;
            _readyStableTicks = 0;
            _lastReadySnapshot = default;
            var postCooldownDeadline = nowUtc.AddMilliseconds(Configuration.NormalizeScanPostCooldownMs(postListingCooldownMs));
            _cooldownUntilUtc = _nextOpenAtUtc >= postCooldownDeadline ? _nextOpenAtUtc : postCooldownDeadline;
            _state = DebugPfScanState.Cooldown;
            return;
        }

        MarkAttempt(nowUtc, success: false, "detail_timeout", PartyDetailScannerAttemptOutcome.TimedOut, postListingCooldownMs);
    }

    internal void HandleServerFailureSignal(
        DateTime nowUtc,
        uint logMessageId,
        bool isNonRetryableUnavailable,
        int configuredRetries = DefaultMaxRetries,
        int postListingCooldownMs = DefaultPostListingCooldownMs
    ) {
        if (!_hasTarget) {
            _state = DebugPfScanState.SyncQueue;
            return;
        }

        if (isNonRetryableUnavailable) {
            _queue.RecordAttempt(_target.ListingId, nowUtc, recordCrossRunDedup: false);
            _lastAttemptListingId = _target.ListingId;
            _lastAttemptReason = $"pf_unavailable_{logMessageId}";
            _lastTerminalOutcome = PartyDetailScannerAttemptOutcome.Unavailable;
            _lastAttemptSuccess = false;

            _hasTarget = false;
            _retryTargetAfterCooldown = false;
            _openAttemptsForTarget = 0;
            _activeRequestSerial = null;
            _readyStableTicks = 0;
            _lastReadySnapshot = default;
            _cooldownUntilUtc = nowUtc.AddMilliseconds(Configuration.NormalizeScanPostCooldownMs(postListingCooldownMs));
            _state = DebugPfScanState.Cooldown;
            return;
        }

        if (ShouldRetryTargetAfterFailure(_openAttemptsForTarget, Configuration.NormalizeScanRetryCount(configuredRetries))) {
            _retryTargetAfterCooldown = true;
            _activeRequestSerial = null;
            _readyStableTicks = 0;
            _lastReadySnapshot = default;
            var postCooldownDeadline = nowUtc.AddMilliseconds(Configuration.NormalizeScanPostCooldownMs(postListingCooldownMs));
            _cooldownUntilUtc = _nextOpenAtUtc >= postCooldownDeadline ? _nextOpenAtUtc : postCooldownDeadline;
            _state = DebugPfScanState.Cooldown;
            return;
        }

        MarkAttempt(
            nowUtc,
            success: false,
            $"pf_error_{logMessageId}",
            PartyDetailScannerAttemptOutcome.TimedOut,
            postListingCooldownMs
        );
    }

    internal void HandleCollectedState(
        DateTime nowUtc,
        bool hasConsumedAck,
        int postListingCooldownMs = DefaultPostListingCooldownMs
    ) {
        if (!_hasTarget) {
            _state = DebugPfScanState.SyncQueue;
            return;
        }

        if (hasConsumedAck) {
            MarkAttempt(nowUtc, success: true, "queued", PartyDetailScannerAttemptOutcome.Succeeded, postListingCooldownMs);
            return;
        }

        if (nowUtc <= _stateDeadlineUtc) {
            return;
        }

        MarkAttempt(nowUtc, success: false, "collector_timeout", PartyDetailScannerAttemptOutcome.TimedOut, postListingCooldownMs);
    }

    internal void AdvanceCooldown(DateTime nowUtc) {
        if (nowUtc < _cooldownUntilUtc) {
            return;
        }

        if (_retryTargetAfterCooldown && _hasTarget) {
            _retryTargetAfterCooldown = false;
            _state = DebugPfScanState.OpenTarget;
            return;
        }

        _state = DebugPfScanState.SyncQueue;
    }

    internal void SetIdle() {
        _state = DebugPfScanState.Idle;
        _hasTarget = false;
        _retryTargetAfterCooldown = false;
        _openAttemptsForTarget = 0;
        _activeRequestSerial = null;
    }

    internal static bool ShouldRetryTargetAfterFailure(int attemptsMade, int configuredRetries) {
        return attemptsMade <= PartyDetailUploadPolicy.NormalizeRetryCount(configuredRetries);
    }

    private void MarkAttempt(
        DateTime nowUtc,
        bool success,
        string reason,
        PartyDetailScannerAttemptOutcome outcome,
        int postListingCooldownMs
    ) {
        if (_hasTarget) {
            _queue.RecordAttempt(_target.ListingId, nowUtc, recordCrossRunDedup: success);
            _lastAttemptListingId = _target.ListingId;
        }

        _lastAttemptReason = reason;
        _lastTerminalOutcome = outcome;
        _lastAttemptSuccess = success;

        if (success) {
            _processedCount++;
            _consecutiveFailures = 0;
        } else {
            _consecutiveFailures++;
        }

        _hasTarget = false;
        _retryTargetAfterCooldown = false;
        _openAttemptsForTarget = 0;
        _activeRequestSerial = null;
        _readyStableTicks = 0;
        _lastReadySnapshot = default;
        _cooldownUntilUtc = nowUtc.AddMilliseconds(Configuration.NormalizeScanPostCooldownMs(postListingCooldownMs));
        _state = DebugPfScanState.Cooldown;
    }

    private static int GetDetailReadyTimeoutMs(int configuredTimeoutMs)
        => Configuration.NormalizeScanDetailTimeoutMs(configuredTimeoutMs);

    private static int GetDetailCollectionTimeoutMs(int configuredTimeoutMs)
        => Math.Clamp(Math.Max(GetDetailReadyTimeoutMs(configuredTimeoutMs), DetailCollectionTimeoutMs), 1500, 10_000);
}
