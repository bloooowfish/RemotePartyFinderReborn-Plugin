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
            difficultyId: 101,
            recentReportsLimit: 2);

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
            difficultyId: null,
            recentReportsLimit: 0);
        var withDifficulty = FFLogsQueryBuilder.BuildCharacterCandidateDataBatchQuery(
            [candidate],
            zoneId: 123,
            difficultyId: 101,
            recentReportsLimit: 0);

        Assert.DoesNotContain("difficulty:", withoutDifficulty);
        Assert.Contains("difficulty: 101", withDifficulty);
    }

    [Fact]
    public void BuildCharacterCandidateDataBatchQuery_includes_recent_report_limit_only_when_positive()
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
            difficultyId: null,
            recentReportsLimit: 0);
        var withReports = FFLogsQueryBuilder.BuildCharacterCandidateDataBatchQuery(
            [candidate],
            zoneId: 123,
            difficultyId: null,
            recentReportsLimit: 3);

        Assert.DoesNotContain("recentReports", withoutReports);
        Assert.Contains("recentReports(limit: 3)", withReports);
    }

    [Fact]
    public void BuildBestBossPercentByReportQuery_escapes_codes_and_uses_stable_aliases()
    {
        var query = FFLogsQueryBuilder.BuildBestBossPercentByReportQuery(
            ["ABC\\123", "DEF\"456"],
            encounterId: 777,
            difficultyId: 101);

        Assert.Contains("r0: report(code: \"ABC\\\\123\")", query);
        Assert.Contains("r1: report(code: \"DEF\\\"456\")", query);
    }

    [Fact]
    public void BuildBestBossPercentByReportQuery_includes_difficulty_only_when_present()
    {
        var withoutDifficulty = FFLogsQueryBuilder.BuildBestBossPercentByReportQuery(
            ["ABC123"],
            encounterId: 777,
            difficultyId: null);
        var withDifficulty = FFLogsQueryBuilder.BuildBestBossPercentByReportQuery(
            ["ABC123"],
            encounterId: 777,
            difficultyId: 101);

        Assert.DoesNotContain("difficulty:", withoutDifficulty);
        Assert.Contains("difficulty: 101", withDifficulty);
    }
}
