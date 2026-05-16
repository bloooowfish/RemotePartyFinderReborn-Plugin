using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class GameLogSuppressionPolicyTests {
    [Fact]
    public void Chara_card_failure_log_requires_active_suppression_context() {
        Assert.True(GameLogSuppressionPolicy.ShouldSuppress(
            5857,
            new GameLogSuppressionContext(GameLogSuppressionFeature.CharaCardIdentity, IsSuppressionActive: true)
        ));
        Assert.False(GameLogSuppressionPolicy.ShouldSuppress(
            5857,
            new GameLogSuppressionContext(GameLogSuppressionFeature.CharaCardIdentity, IsSuppressionActive: false)
        ));
    }

    [Fact]
    public void Chara_card_policy_rejects_unknown_log_ids() {
        Assert.False(GameLogSuppressionPolicy.ShouldSuppress(
            1234,
            new GameLogSuppressionContext(GameLogSuppressionFeature.CharaCardIdentity, IsSuppressionActive: true)
        ));
    }

    [Fact]
    public void Party_detail_policy_suppresses_scanner_owned_detail_and_plate_failure_ids() {
        Assert.True(GameLogSuppressionPolicy.ShouldSuppress(
            958,
            new GameLogSuppressionContext(
                GameLogSuppressionFeature.PartyDetailScanner,
                IsSuppressionActive: true,
                IsScannerOwnedCapture: true
            )
        ));
        Assert.True(GameLogSuppressionPolicy.ShouldSuppress(
            5857,
            new GameLogSuppressionContext(
                GameLogSuppressionFeature.PartyDetailScanner,
                IsSuppressionActive: true,
                IsScannerOwnedCapture: true
            )
        ));
    }

    [Fact]
    public void Party_detail_policy_does_not_suppress_when_context_is_not_scanner_owned() {
        Assert.False(GameLogSuppressionPolicy.ShouldSuppress(
            958,
            new GameLogSuppressionContext(
                GameLogSuppressionFeature.PartyDetailScanner,
                IsSuppressionActive: true,
                IsScannerOwnedCapture: false
            )
        ));
    }
}
