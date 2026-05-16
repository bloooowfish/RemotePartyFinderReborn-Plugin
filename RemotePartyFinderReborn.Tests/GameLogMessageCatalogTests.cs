using System.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class GameLogMessageCatalogTests {
    [Fact]
    public void Chara_card_failure_ids_keep_known_plate_failure_range() {
        Assert.Equal(
            [
                5855u,
                5856u,
                5857u,
                5858u,
                5859u,
                5860u,
            ],
            GameLogMessageCatalog.CharaCardFailureMessageIds.Order()
        );

        Assert.Contains(5857u, GameLogMessageCatalog.CharaCardFailureMessageIds);
        Assert.Contains(5859u, GameLogMessageCatalog.CharaCardFailureMessageIds);
        Assert.DoesNotContain(5854u, GameLogMessageCatalog.CharaCardFailureMessageIds);
        Assert.DoesNotContain(5861u, GameLogMessageCatalog.CharaCardFailureMessageIds);
        Assert.DoesNotContain(5878u, GameLogMessageCatalog.CharaCardFailureMessageIds);
        Assert.DoesNotContain(974u, GameLogMessageCatalog.CharaCardFailureMessageIds);
    }

    [Fact]
    public void Party_detail_failure_ids_cover_lumina_confirmed_detail_collection_failures() {
        Assert.Equal(
            [
                958u,
                959u,
                969u,
                974u,
                7449u,
                7479u,
            ],
            GameLogMessageCatalog.PartyDetailFailureChatLogIds.Order()
        );
        Assert.False(GameLogMessageCatalog.IsPartyDetailFailureChatLogId(5857));
        Assert.False(GameLogMessageCatalog.IsPartyDetailFailureChatLogId(964));
        Assert.False(GameLogMessageCatalog.IsPartyDetailFailureChatLogId(970));
        Assert.False(GameLogMessageCatalog.IsPartyDetailFailureChatLogId(972));
        Assert.False(GameLogMessageCatalog.IsPartyDetailFailureChatLogId(975));
        Assert.False(GameLogMessageCatalog.IsPartyDetailFailureChatLogId(7491));
    }

    [Fact]
    public void Find_missing_known_ids_reports_catalog_entries_absent_from_lookup() {
        var knownIds = GameLogMessageCatalog.CharaCardFailureMessageIds
            .Concat(GameLogMessageCatalog.PartyDetailFailureChatLogIds)
            .ToHashSet();
        var missingId = knownIds.Min();
        knownIds.Remove(missingId);
        var lookup = new FakeLogMessageLookup(knownIds.Select(id => new LogMessageInfo(id, $"message {id}")));

        var missing = GameLogMessageCatalog.FindMissingKnownIds(lookup);

        Assert.Equal([missingId], missing);
    }

    [Fact]
    public void Search_suppression_candidates_matches_keywords_case_insensitively() {
        var lookup = new FakeLogMessageLookup([
            new LogMessageInfo(30, "Unable to retrieve party finder details."),
            new LogMessageInfo(10, "Adventure plate unavailable."),
            new LogMessageInfo(20, "This unrelated message should be ignored."),
        ]);

        var candidates = GameLogMessageCatalog.SearchSuppressionCandidates(
            lookup,
            [" PARTY FINDER ", "adventure PLATE", ""]
        );

        Assert.Equal([10u, 30u], candidates.Select(candidate => candidate.Id));
    }

    [Fact]
    public void Search_suppression_candidates_ignores_blank_keywords() {
        var lookup = new FakeLogMessageLookup([
            new LogMessageInfo(10, "Adventure plate unavailable."),
        ]);

        var candidates = GameLogMessageCatalog.SearchSuppressionCandidates(lookup, [" ", ""]);

        Assert.Empty(candidates);
    }

    private sealed class FakeLogMessageLookup(IEnumerable<LogMessageInfo> messages) : ILogMessageLookup {
        private readonly IReadOnlyDictionary<uint, LogMessageInfo> _messages = messages.ToDictionary(message => message.Id);

        public bool TryGet(uint logMessageId, out LogMessageInfo message) {
            return _messages.TryGetValue(logMessageId, out message);
        }

        public IEnumerable<LogMessageInfo> Search(Func<LogMessageInfo, bool> predicate) {
            return _messages.Values.Where(predicate);
        }
    }
}
