using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class CharaCardResolverPreflightTests {
    [Fact]
    public void ResolverPreflight_returns_disabled_when_interop_addresses_are_unavailable() {
        var result = ResolverPreflightEvaluator.Evaluate(
            requestCharaCardAddress: 0,
            handleCurrentCharaCardDataPacketAddress: 0x1400,
            openCharaCardForPacketAddress: 0x1500
        );

        Assert.False(result.Enabled);
        Assert.Contains("RequestCharaCardForContentId", result.Reason);
    }

    [Fact]
    public void Missing_packet_handler_disables_preflight() {
        var result = ResolverPreflightEvaluator.Evaluate(
            requestCharaCardAddress: 0x1400,
            handleCurrentCharaCardDataPacketAddress: 0,
            openCharaCardForPacketAddress: 0x1500
        );

        Assert.False(result.Enabled);
        Assert.Equal("HandleCurrentCharaCardDataPacket interop address is unavailable.", result.Reason);
    }

    [Fact]
    public void Missing_open_card_handler_disables_preflight() {
        var result = ResolverPreflightEvaluator.Evaluate(
            requestCharaCardAddress: 0x1400,
            handleCurrentCharaCardDataPacketAddress: 0x1500,
            openCharaCardForPacketAddress: 0
        );

        Assert.False(result.Enabled);
        Assert.Equal("OpenCharaCardForPacket interop address is unavailable.", result.Reason);
    }
}
