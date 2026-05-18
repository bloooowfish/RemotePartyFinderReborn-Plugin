using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsCandidateMatcherTests
{
    [Fact]
    public void Visible_candidate_with_matching_parse_beats_visible_no_parse()
    {
        var matcher = CreateMatcher(
        [
            CreateJob(
                contentId: 101,
                candidateServers:
                [
                    new ParseJobCandidateServer { Server = "NoParse", Region = "JP" },
                    new ParseJobCandidateServer { Server = "Parsed", Region = "JP" },
                ]),
        ]);
        var queries = matcher.BuildCandidateQueries();

        var result = matcher.MatchFetchedCandidates(new Dictionary<string, FFLogsClient.CharacterFetchedData>
        {
            [queries[0].Key] = new FFLogsClient.CharacterFetchedData
            {
                Hidden = false,
                Parses = [],
            },
            [queries[1].Key] = new FFLogsClient.CharacterFetchedData
            {
                Hidden = false,
                Parses =
                [
                    new FFLogsClient.CharacterEncounterParse
                    {
                        EncounterId = 88,
                        Percentile = 74.5,
                        ClearCount = 3,
                    },
                ],
            },
        });

        var parse = Assert.Single(result.ResultsByContentId.Values);
        Assert.Equal("Parsed", parse.MatchedServer);
        Assert.False(parse.IsHidden);
        Assert.True(parse.IsEstimated);
        Assert.Equal(74.5, parse.Encounters[88], 3);
        Assert.Equal(3, parse.ClearCounts[88]);
    }

    [Fact]
    public void Hidden_candidate_with_real_parse_beats_visible_none()
    {
        var matcher = CreateMatcher(
        [
            CreateJob(
                contentId: 102,
                candidateServers:
                [
                    new ParseJobCandidateServer { Server = "VisibleEmpty", Region = "JP" },
                    new ParseJobCandidateServer { Server = "HiddenParsed", Region = "JP" },
                ]),
        ]);
        var queries = matcher.BuildCandidateQueries();

        var result = matcher.MatchFetchedCandidates(new Dictionary<string, FFLogsClient.CharacterFetchedData>
        {
            [queries[0].Key] = new FFLogsClient.CharacterFetchedData
            {
                Hidden = false,
                Parses = [],
            },
            [queries[1].Key] = new FFLogsClient.CharacterFetchedData
            {
                Hidden = true,
                Parses =
                [
                    new FFLogsClient.CharacterEncounterParse
                    {
                        EncounterId = 88,
                        Percentile = 12.3,
                        ClearCount = 1,
                    },
                ],
            },
        });

        var parse = Assert.Single(result.ResultsByContentId.Values);
        Assert.Equal("HiddenParsed", parse.MatchedServer);
        Assert.True(parse.IsHidden);
        Assert.Empty(parse.Encounters);
        Assert.Empty(parse.ClearCounts);
    }

    [Fact]
    public void Secondary_encounter_contributes_to_score()
    {
        var matcher = CreateMatcher(
        [
            CreateJob(
                contentId: 103,
                secondaryEncounterId: 99,
                candidateServers:
                [
                    new ParseJobCandidateServer { Server = "PrimaryOnly", Region = "JP" },
                    new ParseJobCandidateServer { Server = "SecondaryHit", Region = "JP" },
                ]),
        ]);
        var queries = matcher.BuildCandidateQueries();

        var result = matcher.MatchFetchedCandidates(new Dictionary<string, FFLogsClient.CharacterFetchedData>
        {
            [queries[0].Key] = new FFLogsClient.CharacterFetchedData
            {
                Hidden = false,
                Parses =
                [
                    new FFLogsClient.CharacterEncounterParse
                    {
                        EncounterId = 88,
                        Percentile = 10.0,
                    },
                ],
            },
            [queries[1].Key] = new FFLogsClient.CharacterFetchedData
            {
                Hidden = false,
                Parses =
                [
                    new FFLogsClient.CharacterEncounterParse
                    {
                        EncounterId = 99,
                        Percentile = 90.0,
                    },
                ],
            },
        });

        var parse = Assert.Single(result.ResultsByContentId.Values);
        Assert.Equal("SecondaryHit", parse.MatchedServer);
        Assert.Equal(90.0, parse.Encounters[99], 3);
    }

    [Fact]
    public void No_candidates_yields_no_result()
    {
        var matcher = CreateMatcher(
        [
            CreateJob(contentId: 104, server: "", region: "", candidateServers: []),
        ]);

        Assert.Empty(matcher.BuildCandidateQueries());

        var result = matcher.MatchFetchedCandidates(new Dictionary<string, FFLogsClient.CharacterFetchedData>());

        Assert.Empty(result.ResultsByContentId);
    }

    [Fact]
    public void Duplicate_content_ids_use_first_job_deterministically()
    {
        var matcher = CreateMatcher(
        [
            CreateJob(contentId: 105, server: "First", leaseToken: "first-lease"),
            CreateJob(contentId: 105, server: "Second", leaseToken: "second-lease"),
        ]);

        var query = Assert.Single(matcher.BuildCandidateQueries());
        Assert.Equal("105:0", query.Key);
        Assert.Equal("First", query.Server);

        var result = matcher.MatchFetchedCandidates(new Dictionary<string, FFLogsClient.CharacterFetchedData>
        {
            [query.Key] = new FFLogsClient.CharacterFetchedData
            {
                Hidden = false,
                Parses =
                [
                    new FFLogsClient.CharacterEncounterParse
                    {
                        EncounterId = 88,
                        Percentile = 64.2,
                    },
                ],
            },
        });

        var parse = Assert.Single(result.ResultsByContentId.Values);
        Assert.Equal("First", parse.MatchedServer);
        Assert.Equal("first-lease", parse.LeaseToken);
        Assert.Equal(64.2, parse.Encounters[88], 3);
    }

    private static FFLogsCandidateMatcher CreateMatcher(IReadOnlyList<ParseJob> jobs)
        => new(jobs, zoneId: 77, difficultyId: 5);

    private static ParseJob CreateJob(
        ulong contentId,
        string server = "Default",
        string region = "JP",
        uint secondaryEncounterId = 0,
        List<ParseJobCandidateServer>? candidateServers = null,
        string? leaseToken = null)
    {
        return new ParseJob
        {
            ContentId = contentId,
            Name = $"Player-{contentId}",
            Server = server,
            Region = region,
            CandidateServers = candidateServers ?? [],
            ZoneId = 77,
            DifficultyId = 5,
            EncounterId = 88,
            SecondaryEncounterId = secondaryEncounterId == 0 ? null : secondaryEncounterId,
            LeaseToken = leaseToken ?? $"lease-{contentId}",
        };
    }
}
