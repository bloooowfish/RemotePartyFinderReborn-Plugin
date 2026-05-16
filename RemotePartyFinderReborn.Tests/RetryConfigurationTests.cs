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
    [InlineData(-1, 100)]
    [InlineData(0, 100)]
    [InlineData(400, 400)]
    [InlineData(2_000, 2_000)]
    [InlineData(2_001, 2_000)]
    public void Scanner_action_interval_is_normalized_consistently(int configured, int expected) {
        Assert.Equal(expected, Configuration.NormalizeScanActionIntervalMs(configured));
    }

    [Theory]
    [InlineData(-1, 500)]
    [InlineData(0, 500)]
    [InlineData(3_500, 3_500)]
    [InlineData(10_000, 10_000)]
    [InlineData(10_001, 10_000)]
    public void Scanner_detail_timeout_is_normalized_consistently(int configured, int expected) {
        Assert.Equal(expected, Configuration.NormalizeScanDetailTimeoutMs(configured));
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 100)]
    [InlineData(800, 800)]
    [InlineData(3_000, 3_000)]
    [InlineData(3_001, 3_000)]
    public void Scanner_min_dwell_is_normalized_consistently(int configured, int expected) {
        Assert.Equal(expected, Configuration.NormalizeScanMinDwellMs(configured));
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 50)]
    [InlineData(300, 300)]
    [InlineData(3_000, 3_000)]
    [InlineData(3_001, 3_000)]
    public void Scanner_post_listing_cooldown_is_normalized_consistently(int configured, int expected) {
        Assert.Equal(expected, Configuration.NormalizeScanPostCooldownMs(configured));
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(0, 30)]
    [InlineData(600, 600)]
    [InlineData(3_600, 3_600)]
    [InlineData(3_601, 3_600)]
    public void Scanner_dedup_ttl_is_normalized_consistently(int configured, int expected) {
        Assert.Equal(expected, Configuration.NormalizeScanDedupTtlSeconds(configured));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    public void Scanner_max_failure_count_is_normalized_consistently(int configured, int expected) {
        Assert.Equal(expected, Configuration.NormalizeScanMaxFailures(configured));
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
        stateMachine.UpsertVisibleCandidate(target);

        stateMachine.SyncQueue(
            nowUtc,
            [],
            hasIncomingListings: false,
            maxPerRun: 0,
            dedupTtlSeconds: 600,
            runFromCollectedListings: false
        );

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
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    public void Scanner_completion_helper_requires_no_pending_targets_and_no_incoming_listings(
        int pendingTargets,
        bool hasIncomingListings,
        bool shouldComplete) {
        Assert.Equal(
            shouldComplete,
            DebugPfScanStateMachine.IsQueueDrained(pendingTargets, hasIncomingListings));
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
        stateMachine.UpsertVisibleCandidate(target);

        var completionReason = stateMachine.SyncQueue(
            nowUtc,
            [],
            hasIncomingListings: false,
            maxPerRun: 0,
            dedupTtlSeconds: 600,
            runFromCollectedListings: false
        );

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
            minDwellMs: 800,
            detailReadyTimeoutMs: 3500,
            configuredRetries: 1,
            postListingCooldownMs: 300
        );
        Assert.Equal(DebugPfScanState.WaitDetailReady, stateMachine.State);

        stateMachine.HandleDetailReadyState(
            nowUtc.AddMilliseconds(20),
            detailSnapshot,
            minDwellMs: 800,
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
            [],
            hasIncomingListings: false,
            maxPerRun: 0,
            dedupTtlSeconds: 600,
            runFromCollectedListings: false
        );

        Assert.Equal("current_page_complete", completionReason);
        Assert.Equal(DebugPfScanState.SyncQueue, stateMachine.State);
    }
}
