using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class RetryConfigurationTests {
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public void Party_detail_retry_count_is_normalized_to_non_negative_retry_budget(int configuredRetries, int expectedRetries) {
        Assert.Equal(expectedRetries, PartyDetailUploadPolicy.NormalizeRetryCount(configuredRetries));
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    [InlineData(0, 0, true)]
    public void Party_detail_drop_check_uses_retry_count_not_total_attempt_count(int currentRetryCount, int configuredRetries, bool shouldDrop) {
        Assert.Equal(shouldDrop, PartyDetailUploadPolicy.ShouldDropPendingDetail(currentRetryCount, configuredRetries));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(1, 0, false)]
    public void Scanner_retry_helper_uses_configured_retry_budget(int attemptsMade, int configuredRetries, bool shouldRetry) {
        Assert.Equal(
            shouldRetry,
            DebugPfScanStateMachine.ShouldRetryTargetAfterFailure(attemptsMade, configuredRetries));
    }

    [Fact]
    public void Scanner_retry_budget_uses_configured_retry_count() {
        var nowUtc = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc);
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var target = new DebugPfListingCandidate(9001U, 33UL, nowUtc, 1);

        stateMachine.SyncQueue(nowUtc, [target], dedupTtlSeconds: 600);

        Assert.Equal(DebugPfScanState.OpenTarget, stateMachine.State);
        Assert.Equal(target.ListingId, stateMachine.CurrentTargetListingId);

        stateMachine.HandleOpenAttemptResult(
            nowUtc,
            opened: false,
            actionIntervalMs: 400,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 1,
            postListingCooldownMs: 300
        );

        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);
        Assert.Equal(target.ListingId, stateMachine.CurrentTargetListingId);
        Assert.Equal(0, stateMachine.ConsecutiveFailures);

        stateMachine.AdvanceCooldown(nowUtc.AddMilliseconds(401));

        Assert.Equal(DebugPfScanState.OpenTarget, stateMachine.State);
        Assert.Equal(target.ListingId, stateMachine.CurrentTargetListingId);

        stateMachine.HandleOpenAttemptResult(
            nowUtc.AddMilliseconds(401),
            opened: false,
            actionIntervalMs: 400,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 1,
            postListingCooldownMs: 300
        );

        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);
        Assert.Equal(0UL, stateMachine.CurrentTargetListingId);
        Assert.Equal(1, stateMachine.ConsecutiveFailures);
        Assert.Equal("open_failed", stateMachine.LastAttemptReason);
        Assert.False(stateMachine.LastAttemptSuccess);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public void Chara_card_retry_count_maps_to_total_attempt_budget(int configuredRetries, int expectedAttempts) {
        Assert.Equal(expectedAttempts, CharaCardResolver.ResolveMaxAttemptsFromRetryCount(configuredRetries));
    }

    [Theory]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, true)]
    [InlineData(1, 0, true)]
    public void Scanner_failure_threshold_completes_run_instead_of_stalling_in_cooldown(
        int consecutiveFailures,
        int configuredMaxFailures,
        bool shouldComplete) {
        Assert.Equal(
            shouldComplete,
            DebugPfScanner.ReachedFailureLimit(consecutiveFailures, configuredMaxFailures));
    }

    [Fact]
    public void Scanner_completes_once_targets_are_collected() {
        var nowUtc = new DateTime(2026, 4, 14, 1, 0, 0, DateTimeKind.Utc);
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var target = new DebugPfListingCandidate(9101U, 44UL, nowUtc, 1);
        var detailSnapshot = new DebugPfDetailSnapshot(
            target.ListingId,
            target.ContentId,
            NonZeroMembers: 4,
            TotalSlots: 8
        );

        var completionReason = stateMachine.SyncQueue(nowUtc, [target], dedupTtlSeconds: 600);

        Assert.Null(completionReason);
        Assert.Equal(DebugPfScanState.OpenTarget, stateMachine.State);

        stateMachine.HandleOpenAttemptResult(
            nowUtc,
            opened: true,
            actionIntervalMs: 400,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 1,
            postListingCooldownMs: 300
        );

        Assert.Equal(DebugPfScanState.WaitDetailReady, stateMachine.State);

        stateMachine.HandleDetailReadyState(
            nowUtc.AddMilliseconds(10),
            detailSnapshot,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 1,
            postListingCooldownMs: 300
        );
        Assert.Equal(DebugPfScanState.WaitDetailReady, stateMachine.State);

        stateMachine.HandleDetailReadyState(
            nowUtc.AddMilliseconds(20),
            detailSnapshot,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 1,
            postListingCooldownMs: 300
        );

        Assert.Equal(DebugPfScanState.WaitCollected, stateMachine.State);

        stateMachine.HandleCollectedState(
            nowUtc.AddMilliseconds(30),
            hasConsumedAck: true,
            postListingCooldownMs: 300
        );

        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);
        Assert.Equal(1, stateMachine.ProcessedCount);
        Assert.Equal("queued", stateMachine.LastAttemptReason);
        Assert.True(stateMachine.LastAttemptSuccess);

        stateMachine.AdvanceCooldown(nowUtc.AddMilliseconds(331));
        Assert.Equal(DebugPfScanState.SyncQueue, stateMachine.State);

        completionReason = stateMachine.SyncQueue(
            nowUtc.AddMilliseconds(331),
            [target],
            dedupTtlSeconds: 600
        );

        Assert.Equal("collected_batch_complete", completionReason);
        Assert.Equal(DebugPfScanState.SyncQueue, stateMachine.State);
    }

    [Fact]
    public void Scanner_speed_normalizers_clamp_configured_bounds() {
        Assert.Equal(50, Configuration.NormalizeScanActionIntervalMs(10));
        Assert.Equal(2000, Configuration.NormalizeScanActionIntervalMs(5000));
        Assert.Equal(150, Configuration.NormalizeScanActionIntervalMs(150));

        Assert.Equal(0, Configuration.NormalizeScanPostCooldownMs(-5));
        Assert.Equal(0, Configuration.NormalizeScanPostCooldownMs(0));
        Assert.Equal(2000, Configuration.NormalizeScanPostCooldownMs(9999));

        Assert.Equal(500, Configuration.NormalizeScanDetailTimeoutMs(100));
        Assert.Equal(10000, Configuration.NormalizeScanDetailTimeoutMs(25000));
        Assert.Equal(1500, Configuration.NormalizeScanDetailTimeoutMs(1500));

        Assert.Equal(0, Configuration.NormalizeScanRetryCount(-2));
        Assert.Equal(5, Configuration.NormalizeScanRetryCount(12));
        Assert.Equal(2, Configuration.NormalizeScanRetryCount(2));
    }

    [Fact]
    public void Zero_post_cooldown_advances_queue_immediately_while_preserving_action_interval_for_next_open() {
        var t0 = new DateTime(2026, 4, 14, 2, 0, 0, DateTimeKind.Utc);
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var first = new DebugPfListingCandidate(9201U, 101UL, t0, 1);
        var second = new DebugPfListingCandidate(9202U, 102UL, t0.AddMilliseconds(1), 1);

        Assert.Null(stateMachine.SyncQueue(t0, [first, second], dedupTtlSeconds: 600));
        Assert.Equal(first.ListingId, stateMachine.CurrentTargetListingId);

        stateMachine.HandleOpenAttemptResult(
            t0,
            opened: true,
            actionIntervalMs: 150,
            detailReadyTimeoutMs: 1500,
            configuredRetries: 1,
            postListingCooldownMs: 0,
            requestSerial: 1
        );

        stateMachine.HandleCollectedState(
            t0.AddMilliseconds(40),
            hasConsumedAck: true,
            postListingCooldownMs: 0
        );
        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);

        stateMachine.AdvanceCooldown(t0.AddMilliseconds(40));
        Assert.Equal(DebugPfScanState.SyncQueue, stateMachine.State);

        Assert.Null(stateMachine.SyncQueue(t0.AddMilliseconds(40), [first, second], dedupTtlSeconds: 600));
        Assert.Equal(DebugPfScanState.OpenTarget, stateMachine.State);
        Assert.Equal(second.ListingId, stateMachine.CurrentTargetListingId);

        Assert.False(stateMachine.IsActionReady(t0.AddMilliseconds(100)));
        Assert.True(stateMachine.IsActionReady(t0.AddMilliseconds(150)));
    }

    [Fact]
    public void Server_failure_signal_fast_fails_non_retryable_unavailable_and_retries_transient_errors() {
        var t0 = new DateTime(2026, 4, 14, 3, 0, 0, DateTimeKind.Utc);
        var queue = new DebugPfListingQueue();
        var stateMachine = new DebugPfScanStateMachine(queue);
        var unavailableTarget = new DebugPfListingCandidate(9301U, 201UL, t0, 1);
        var transientTarget = new DebugPfListingCandidate(9302U, 202UL, t0.AddMilliseconds(1), 1);

        Assert.Null(stateMachine.SyncQueue(t0, [unavailableTarget, transientTarget], dedupTtlSeconds: 600));
        stateMachine.HandleOpenAttemptResult(
            t0,
            opened: true,
            actionIntervalMs: 150,
            detailReadyTimeoutMs: 1500,
            configuredRetries: 2,
            postListingCooldownMs: 0,
            requestSerial: 10
        );

        stateMachine.HandleServerFailureSignal(
            t0.AddMilliseconds(35),
            logMessageId: 974,
            isNonRetryableUnavailable: true,
            configuredRetries: 2,
            postListingCooldownMs: 0
        );

        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);
        Assert.Equal(0UL, stateMachine.CurrentTargetListingId);
        Assert.Equal(0, stateMachine.ConsecutiveFailures);
        Assert.Equal(PartyDetailScannerAttemptOutcome.Unavailable, stateMachine.LastTerminalOutcome);
        Assert.Equal("pf_unavailable_974", stateMachine.LastAttemptReason);

        stateMachine.AdvanceCooldown(t0.AddMilliseconds(35));
        Assert.Equal(DebugPfScanState.SyncQueue, stateMachine.State);
        Assert.Null(stateMachine.SyncQueue(t0.AddMilliseconds(35), [unavailableTarget, transientTarget], dedupTtlSeconds: 600));
        Assert.Equal(transientTarget.ListingId, stateMachine.CurrentTargetListingId);

        var secondOpenAt = t0.AddMilliseconds(150);
        stateMachine.HandleOpenAttemptResult(
            secondOpenAt,
            opened: true,
            actionIntervalMs: 150,
            detailReadyTimeoutMs: 1500,
            configuredRetries: 1,
            postListingCooldownMs: 0,
            requestSerial: 11
        );

        // First transient error (958) schedules retry and clears active request serial.
        stateMachine.HandleServerFailureSignal(
            secondOpenAt.AddMilliseconds(30),
            logMessageId: 958,
            isNonRetryableUnavailable: false,
            configuredRetries: 1,
            postListingCooldownMs: 0
        );
        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);
        Assert.Equal(transientTarget.ListingId, stateMachine.CurrentTargetListingId);
        Assert.Null(stateMachine.CurrentRequestSerial);
        Assert.Equal(0, stateMachine.ConsecutiveFailures);

        var retryAt = secondOpenAt.AddMilliseconds(150);
        stateMachine.AdvanceCooldown(retryAt);
        Assert.Equal(DebugPfScanState.OpenTarget, stateMachine.State);

        stateMachine.HandleOpenAttemptResult(
            retryAt,
            opened: true,
            actionIntervalMs: 150,
            detailReadyTimeoutMs: 1500,
            configuredRetries: 1,
            postListingCooldownMs: 0,
            requestSerial: 12
        );

        // Exhausted retries on second transient error terminates as TimedOut.
        stateMachine.HandleServerFailureSignal(
            retryAt.AddMilliseconds(25),
            logMessageId: 958,
            isNonRetryableUnavailable: false,
            configuredRetries: 1,
            postListingCooldownMs: 0
        );
        Assert.Equal(DebugPfScanState.Cooldown, stateMachine.State);
        Assert.Equal(0UL, stateMachine.CurrentTargetListingId);
        Assert.Equal(1, stateMachine.ConsecutiveFailures);
        Assert.Equal(PartyDetailScannerAttemptOutcome.TimedOut, stateMachine.LastTerminalOutcome);
        Assert.Equal("pf_error_958", stateMachine.LastAttemptReason);
    }
}
