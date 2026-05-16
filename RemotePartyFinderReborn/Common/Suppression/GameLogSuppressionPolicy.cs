#nullable enable

namespace RemotePartyFinderReborn;

internal enum GameLogSuppressionFeature {
    CharaCardIdentity,
    PartyDetailScanner,
}

internal readonly record struct GameLogSuppressionContext(
    GameLogSuppressionFeature Feature,
    bool IsSuppressionActive,
    bool IsScannerOwnedCapture = false
);

internal static class GameLogSuppressionPolicy {
    internal static bool ShouldSuppress(uint logMessageId, GameLogSuppressionContext context) {
        if (!context.IsSuppressionActive) {
            return false;
        }

        return context.Feature switch {
            GameLogSuppressionFeature.CharaCardIdentity =>
                GameLogMessageCatalog.IsCharaCardFailureMessageId(logMessageId),
            GameLogSuppressionFeature.PartyDetailScanner =>
                context.IsScannerOwnedCapture
                && (GameLogMessageCatalog.IsPartyDetailFailureChatLogId(logMessageId)
                    || GameLogMessageCatalog.IsCharaCardFailureMessageId(logMessageId)),
            _ => false,
        };
    }
}
