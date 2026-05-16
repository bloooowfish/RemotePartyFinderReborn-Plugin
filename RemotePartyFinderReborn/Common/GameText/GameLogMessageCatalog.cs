using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace RemotePartyFinderReborn;

internal static class GameLogMessageCatalog {
    // PCR/OpenRadar use the exclusive range > 5854 && < 5861. Keep the
    // resolved IDs explicit: datamine shows 5854 and 5861 are not plate fetch
    // failures, and nearby portrait/plate update errors must not be swept in.
    private static readonly HashSet<uint> CharaCardFailureMessageIdSet = [
        5855, // アドベンチャラープレートの情報を取得できませんでした。
        5856, // 対象プレイヤーのアドベンチャラープレートが未設定のため表示できません。
        5857, // 対象プレイヤーのアドベンチャラープレートが未設定のため表示できません。
        5858, // 公開設定にされていないため表示できません。
        5859, // 公開設定にされていないため表示できません。
        5860, // 対象プレイヤーが異なるデータセンターにいるか、データが取得できなかったため表示できません。
    ];

    // Scanner-owned PF detail collection can trigger these retrieval/open
    // failures. The policy additionally requires a matching scanner request
    // cycle before any of these chat log messages are suppressed.
    private static readonly HashSet<uint> PartyDetailFailureChatLogIdSet = [
        958, // パーティメンバーの情報の取得に問題が発生しました。
        959, // パーティ募集情報の取得に失敗しました。
        969, // この募集者は現在パーティ募集をしていません。
        974, // パーティ募集者の情報が取得できません。
        7449, // パーティ募集サーバーに問題が発生しました。
        7479, // パーティ募集の設定により、情報が取得できませんでした。
    ];

    internal static IReadOnlySet<uint> CharaCardFailureMessageIds => CharaCardFailureMessageIdSet;
    internal static IReadOnlySet<uint> PartyDetailFailureChatLogIds => PartyDetailFailureChatLogIdSet;

    internal static bool IsCharaCardFailureMessageId(uint logMessageId) {
        return CharaCardFailureMessageIdSet.Contains(logMessageId);
    }

    internal static bool IsPartyDetailFailureChatLogId(uint logMessageId) {
        return PartyDetailFailureChatLogIdSet.Contains(logMessageId);
    }

    internal static IReadOnlyList<uint> FindMissingKnownIds(ILogMessageLookup lookup) {
        return KnownSuppressionLogMessageIds()
            .Where(id => !lookup.TryGet(id, out _))
            .ToArray();
    }

    internal static IReadOnlyList<LogMessageInfo> SearchSuppressionCandidates(
        ILogMessageLookup lookup,
        IEnumerable<string> keywords
    ) {
        var normalizedKeywords = keywords
            .Select(keyword => keyword.Trim())
            .Where(keyword => keyword.Length > 0)
            .ToArray();

        if (normalizedKeywords.Length == 0) {
            return [];
        }

        return lookup
            .Search(message => normalizedKeywords.Any(keyword =>
                message.Text.Contains(keyword, System.StringComparison.OrdinalIgnoreCase)))
            .OrderBy(message => message.Id)
            .ToArray();
    }

    private static IEnumerable<uint> KnownSuppressionLogMessageIds() {
        return CharaCardFailureMessageIdSet
            .Concat(PartyDetailFailureChatLogIdSet)
            .Distinct();
    }
}
