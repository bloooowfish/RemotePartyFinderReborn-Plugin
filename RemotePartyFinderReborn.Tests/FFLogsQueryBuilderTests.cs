using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsQueryBuilderTests
{
    [Fact]
    public void BuildCharacterCandidateDataBatchQuery_escapes_strings_and_uses_stable_aliases()
    {
        var query = FFLogsQueryBuilder.BuildCharacterCandidateDataBatchQuery(
            [
                new FFLogsClient.CandidateCharacterQuery
                {
                    Key = "one",
                    Name = "Alpha\\\"One",
                    Server = "Server\\One",
                    Region = "NA\"East",
                },
                new FFLogsClient.CandidateCharacterQuery
                {
                    Key = "two",
                    Name = "Beta",
                    Server = "ServerTwo",
                    Region = "EU",
                },
            ],
            zoneId: 123,
            difficultyId: 101);

        Assert.Contains("c0: character(name: \"Alpha\\\\\\\"One\", serverSlug: \"Server\\\\One\", serverRegion: \"NA\\\"East\")", query);
        Assert.Contains("c1: character(name: \"Beta\", serverSlug: \"ServerTwo\", serverRegion: \"EU\")", query);
    }

    [Fact]
    public void BuildCharacterCandidateDataBatchQuery_includes_difficulty_only_when_present()
    {
        var candidate = new FFLogsClient.CandidateCharacterQuery
        {
            Key = "one",
            Name = "Alpha",
            Server = "Server",
            Region = "NA",
        };

        var withoutDifficulty = FFLogsQueryBuilder.BuildCharacterCandidateDataBatchQuery(
            [candidate],
            zoneId: 123,
            difficultyId: null);
        var withDifficulty = FFLogsQueryBuilder.BuildCharacterCandidateDataBatchQuery(
            [candidate],
            zoneId: 123,
            difficultyId: 101);

        Assert.DoesNotContain("difficulty:", withoutDifficulty);
        Assert.Contains("difficulty: 101", withDifficulty);
    }

    [Fact]
    public void BuildCharacterCandidateDataBatchQuery_excludes_report_progress_fields()
    {
        var candidate = new FFLogsClient.CandidateCharacterQuery
        {
            Key = "one",
            Name = "Alpha",
            Server = "Server",
            Region = "NA",
        };

        var withoutReports = FFLogsQueryBuilder.BuildCharacterCandidateDataBatchQuery(
            [candidate],
            zoneId: 123,
            difficultyId: null);

        Assert.DoesNotContain("recent" + "Reports", withoutReports);
        Assert.DoesNotContain("reportData", withoutReports);
        Assert.DoesNotContain("boss" + "Percentage", withoutReports);
    }
}
