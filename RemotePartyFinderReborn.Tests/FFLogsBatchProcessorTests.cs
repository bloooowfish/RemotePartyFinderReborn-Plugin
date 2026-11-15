using System.Net;
using System.Net.Http;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsBatchProcessorTests
{
    static FFLogsBatchProcessorTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Batch_processor_selects_the_best_candidate_per_content_id()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) =>
            {
                var output = new Dictionary<string, FFLogsClient.CharacterFetchedData>();
                foreach (var query in queries)
                {
                    output[query.Key] = query.Server switch
                    {
                        "Alpha" => new FFLogsClient.CharacterFetchedData
                        {
                            Hidden = false,
                            Parses =
                            [
                                new FFLogsClient.CharacterEncounterParse
                                {
                                    EncounterId = 999,
                                    Percentile = 80.1,
                                    ClearCount = 2,
                                },
                            ],
                        },
                        "Beta" => new FFLogsClient.CharacterFetchedData
                        {
                            Hidden = false,
                            Parses =
                            [
                                new FFLogsClient.CharacterEncounterParse
                                {
                                    EncounterId = 88,
                                    Percentile = 97.3,
                                    ClearCount = 5,
                                },
                            ],
                        },
                        _ => new FFLogsClient.CharacterFetchedData(),
                    };
                }

                return Task.FromResult(output);
            },
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [
                CreateJob(
                    contentId: 1001,
                    server: "Alpha",
                    candidateServers:
                    [
                        new ParseJobCandidateServer { Server = "Alpha", Region = "JP" },
                        new ParseJobCandidateServer { Server = "Beta", Region = "JP" },
                    ])
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal("Beta", parse.MatchedServer);
        Assert.True(parse.IsEstimated);
        Assert.False(parse.IsHidden);
        Assert.Equal(97.3, parse.Encounters[88], 3);
        Assert.Equal(5, parse.ClearCounts[88]);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_processes_candidate_fetch_without_recent_report_dependency()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) =>
            {
                return Task.FromResult(new Dictionary<string, FFLogsClient.CharacterFetchedData>
                {
                    [queries[0].Key] = new FFLogsClient.CharacterFetchedData
                    {
                        Hidden = false,
                        Parses =
                        [
                            new FFLogsClient.CharacterEncounterParse
                            {
                                EncounterId = 88,
                                Percentile = 80.1,
                                ClearCount = 2,
                            },
                        ],
                    },
                });
            },
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 1101, server: "Tonberry")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal(80.1, parse.Encounters[88], 3);
        Assert.Equal(2, parse.ClearCounts[88]);
    }

    [Fact]
    public async Task Prefers_hidden_real_parse_over_visible_none()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) =>
            {
                var output = new Dictionary<string, FFLogsClient.CharacterFetchedData>();
                foreach (var query in queries)
                {
                    output[query.Key] = query.Server switch
                    {
                        "VisibleEmpty" => new FFLogsClient.CharacterFetchedData
                        {
                            Hidden = false,
                            Parses = [],
                        },
                        "HiddenParsed" => new FFLogsClient.CharacterFetchedData
                        {
                            Hidden = true,
                            Parses =
                            [
                                new FFLogsClient.CharacterEncounterParse
                                {
                                    EncounterId = 88,
                                    Percentile = 75.0,
                                    ClearCount = 1,
                                },
                            ],
                        },
                        _ => new FFLogsClient.CharacterFetchedData(),
                    };
                }

                return Task.FromResult(output);
            },
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [
                CreateJob(
                    contentId: 1501,
                    server: "VisibleEmpty",
                    candidateServers:
                    [
                        new ParseJobCandidateServer { Server = "VisibleEmpty", Region = "JP" },
                        new ParseJobCandidateServer { Server = "HiddenParsed", Region = "JP" },
                    ])
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal("HiddenParsed", parse.MatchedServer);
        Assert.True(parse.IsHidden);
    }

    [Fact]
    public async Task Batch_processor_preserves_hidden_parse_results()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) =>
            {
                return Task.FromResult(new Dictionary<string, FFLogsClient.CharacterFetchedData>
                {
                    [queries[0].Key] = new FFLogsClient.CharacterFetchedData
                    {
                        Hidden = true,
                        Parses =
                        [
                            new FFLogsClient.CharacterEncounterParse
                            {
                                EncounterId = 88,
                                Percentile = 99.9,
                                ClearCount = 9,
                            },
                        ],
                    },
                });
            },
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 2001, server: "Hidden")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.True(parse.IsHidden);
        Assert.Empty(parse.Encounters);
        Assert.Empty(parse.ClearCounts);
    }

    [Fact]
    public async Task Batch_processor_reports_transient_failure_when_fflogs_candidate_fetch_fails()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchOutcomeAsync = static (_, _, _, _) =>
                Task.FromResult(OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>.Failure(
                    transientFailure: true,
                    "candidate fetch failed")),
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 3501, server: "Tonberry")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.ShouldAbandonRemainingLeases);
        Assert.Empty(result.ProcessedResults);
    }

    [Fact]
    public async Task Batch_processor_preserves_non_transient_fflogs_failure_without_backoff_signal()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchOutcomeAsync = static (_, _, _, _) =>
                Task.FromResult(OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>.Failure(
                    transientFailure: false,
                    "Cannot query field staleField")),
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 3701, server: "Tonberry")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.False(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.ShouldAbandonRemainingLeases);
        Assert.Empty(result.ProcessedResults);
    }

    [Fact]
    public async Task Batch_processor_reports_rate_limit_cooldown_after_failed_fetch_outcome()
    {
        var cooldownChecks = 0;
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchOutcomeAsync = static (_, _, _, _) =>
                Task.FromResult(OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>.Failure(
                    transientFailure: true,
                    "rate limited")),
            OnTryGetRateLimitRemaining = () =>
            {
                cooldownChecks++;
                return cooldownChecks >= 2
                    ? (true, TimeSpan.FromMinutes(15))
                    : (false, TimeSpan.Zero);
            },
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 3801, server: "Tonberry")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(result.HadTransientFailure);
        Assert.True(result.HitRateLimitCooldown);
        Assert.True(result.ShouldAbandonRemainingLeases);
        Assert.Equal(TimeSpan.FromMinutes(15), result.CooldownRemaining);
        Assert.Empty(result.ProcessedResults);
    }

    [Fact]
    public async Task Batch_processor_reports_rate_limit_cooldown_without_owning_submit_requeue_state()
    {
        var rateLimitChecks = 0;
        var apiClient = new StubFFLogsApiClient
        {
            OnTryGetRateLimitRemaining = () =>
            {
                rateLimitChecks++;
                return rateLimitChecks >= 3
                    ? (true, TimeSpan.FromSeconds(42))
                    : (false, TimeSpan.Zero);
            },
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) =>
            {
                return Task.FromResult(new Dictionary<string, FFLogsClient.CharacterFetchedData>
                {
                    [queries[0].Key] = new FFLogsClient.CharacterFetchedData
                    {
                        Hidden = true,
                    },
                });
            },
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [
                CreateJob(contentId: 4001, server: "First", zoneId: 77),
                CreateJob(contentId: 4002, server: "Second", zoneId: 78),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal(4001UL, parse.ContentId);
        Assert.True(result.HitRateLimitCooldown);
        Assert.True(result.ShouldAbandonRemainingLeases);
        Assert.Equal(TimeSpan.FromSeconds(42), result.CooldownRemaining);
        Assert.False(result.HadTransientFailure);
    }

    private static FFLogsBatchProcessor CreateProcessor(StubFFLogsApiClient apiClient)
    {
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            apiClient,
            new ManualFFLogsTimeProvider());
        return new FFLogsBatchProcessor(seams);
    }

    private static ParseJob CreateJob(
        ulong contentId,
        string server,
        uint zoneId = 77,
        uint encounterId = 88,
        uint? secondaryEncounterId = null,
        List<ParseJobCandidateServer>? candidateServers = null)
    {
        return new ParseJob
        {
            ContentId = contentId,
            Name = $"Player-{contentId}",
            Server = server,
            Region = "JP",
            CandidateServers = candidateServers ?? [],
            ZoneId = zoneId,
            DifficultyId = 5,
            EncounterId = encounterId,
            SecondaryEncounterId = secondaryEncounterId,
            LeaseToken = $"lease-{contentId}",
        };
    }
}
