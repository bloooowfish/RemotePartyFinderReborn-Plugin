using RemotePartyFinderReborn.Tomestone;
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
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _, _) =>
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
                            RecentReportCodes = ["ALPHA"],
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
                            RecentReportCodes = ["BETA"],
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
    public async Task Prefers_hidden_real_parse_over_visible_none()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _, _) =>
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
                            RecentReportCodes = [],
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
                            RecentReportCodes = ["HIDDEN-REPORT"],
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
    public async Task Batch_processor_preserves_hidden_parse_results_without_progress_enrichment()
    {
        var progressCalls = 0;
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _, _) =>
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
                        RecentReportCodes = ["HIDDEN"],
                    },
                });
            },
            OnFetchBestBossPercentByReportAsync = (_, _, _, _) =>
            {
                progressCalls++;
                return Task.FromResult(new Dictionary<string, double>());
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
        Assert.Empty(parse.BossPercentages);
        Assert.Equal(0, progressCalls);
    }

    [Fact]
    public async Task Batch_processor_merges_recent_report_progress_for_needed_encounters()
    {
        var progressEncounterIds = new List<int>();
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _, _) =>
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
                                Percentile = 91.2,
                                ClearCount = 4,
                            },
                        ],
                        RecentReportCodes = ["REP1", "REP2", "REP3"],
                    },
                });
            },
            OnFetchBestBossPercentByReportAsync = (_, encounterId, _, _) =>
            {
                progressEncounterIds.Add(encounterId);
                return Task.FromResult(encounterId switch
                {
                    88 => new Dictionary<string, double>
                    {
                        ["REP1"] = 17.5,
                        ["REP2"] = 9.8,
                    },
                    99 => new Dictionary<string, double>
                    {
                        ["REP2"] = 43.2,
                        ["REP3"] = 12.4,
                    },
                    _ => new Dictionary<string, double>(),
                });
            },
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 3001, server: "Visible", secondaryEncounterId: 99)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal([88, 99], progressEncounterIds.OrderBy(static value => value));
        Assert.Equal(9.8, parse.BossPercentages[88], 3);
        Assert.Equal(12.4, parse.BossPercentages[99], 3);
        Assert.Equal(91.2, parse.Encounters[88], 3);
    }

    [Fact]
    public async Task Batch_processor_reports_transient_failure_when_fflogs_candidate_fetch_fails()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchOutcomeAsync = static (_, _, _, _, _) =>
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
    public async Task Batch_processor_reports_transient_failure_when_best_boss_percent_fetch_fails()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _, _) =>
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
                                Percentile = 91.2,
                                ClearCount = 4,
                            },
                        ],
                        RecentReportCodes = ["REP1"],
                    },
                });
            },
            OnFetchBestBossPercentByReportOutcomeAsync = static (_, _, _, _) =>
                Task.FromResult(OperationOutcome<Dictionary<string, double>>.Failure(
                    transientFailure: true,
                    "best boss fetch failed")),
        };
        var processor = CreateProcessor(apiClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 3601, server: "Tonberry")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.True(result.HadTransientFailure);
        Assert.Empty(parse.BossPercentages);
        Assert.Equal(91.2, parse.Encounters[88], 3);
    }

    [Fact]
    public async Task Batch_processor_preserves_non_transient_fflogs_failure_without_backoff_signal()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchOutcomeAsync = static (_, _, _, _, _) =>
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
            OnFetchCharacterCandidateDataBatchOutcomeAsync = static (_, _, _, _, _) =>
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
    public async Task Batch_processor_adds_tomestone_phase_progress_for_ultimate_jobs()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = static (_, _, _, _, _) =>
                Task.FromResult(new TomestoneProgressData(
                [
                    new PhaseProgress
                    {
                        PhaseKey = "p2",
                        PhaseName = "P2",
                        Order = 2,
                        BossPercentage = 44.4,
                        DisplayText = "P2 44.4%",
                    },
                ],
                null)),
        };
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5001, server: "Tonberry", zoneId: 65, encounterId: 1079)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        var phase = Assert.Single(parse.PhaseProgress[1079]);
        Assert.Equal("p2", phase.PhaseKey);
        Assert.Equal(44.4, phase.BossPercentage);
        var request = Assert.Single(tomestoneClient.Requests);
        Assert.Equal(("Player-5001", "Tonberry", 65u, 1079u), request);
        Assert.False(result.HadTransientFailure);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_adds_tomestone_boss_percent_for_current_savage_jobs()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 101);
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = static (_, _, _, _, _) =>
                Task.FromResult(new TomestoneProgressData([], 23.4)),
        };
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5010, server: "Tonberry", zoneId: 73, encounterId: 101)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal(23.4, parse.BossPercentages[101], 3);
        Assert.Empty(parse.PhaseProgress);
        var request = Assert.Single(tomestoneClient.Requests);
        Assert.Equal(("Player-5010", "Tonberry", 73u, 101u), request);
        Assert.False(result.HadTransientFailure);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_adds_tomestone_boss_percent_for_current_savage_secondary_final_boss()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 104);
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = static (_, _, _, _, _) =>
                Task.FromResult(new TomestoneProgressData([], 8.7)),
        };
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5011, server: "Tonberry", zoneId: 73, encounterId: 104, secondaryEncounterId: 105)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal(8.7, parse.BossPercentages[105], 3);
        Assert.Empty(parse.PhaseProgress);
        var request = Assert.Single(tomestoneClient.Requests);
        Assert.Equal(("Player-5011", "Tonberry", 73u, 105u), request);
        Assert.False(result.HadTransientFailure);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_paces_between_multiple_tomestone_calls()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var delays = new List<TimeSpan>();
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = static (_, _, _, encounterId, _) =>
                Task.FromResult(new TomestoneProgressData(
                [
                    new PhaseProgress
                    {
                        PhaseKey = $"phase-{encounterId}",
                        PhaseName = $"Phase {encounterId}",
                        Order = 1,
                        BossPercentage = 50.0,
                        DisplayText = $"Phase {encounterId} 50%",
                    },
                ],
                null)),
        };
        var processor = CreateProcessor(
            apiClient,
            tomestoneClient,
            tomestoneRequestIntervalMs: 1234,
            tomestoneRequestDelayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5007, server: "Tonberry", zoneId: 59, encounterId: 1077, secondaryEncounterId: 1073)]);

        await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Equal(
            [
                ("Player-5007", "Tonberry", 59u, 1077u),
                ("Player-5007", "Tonberry", 59u, 1073u),
            ],
            tomestoneClient.Requests);
        Assert.Equal([TimeSpan.FromMilliseconds(1234)], delays);
    }

    [Fact]
    public async Task Batch_processor_does_not_apply_tomestone_pacing_when_no_tomestone_calls_are_made()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1068);
        var delays = new List<TimeSpan>();
        var tomestoneClient = new StubTomestoneApiClient();
        var processor = CreateProcessor(
            apiClient,
            tomestoneClient,
            tomestoneRequestIntervalMs: 1234,
            tomestoneRequestDelayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5008, server: "Tonberry", zoneId: 54, encounterId: 1068)]);

        await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Empty(tomestoneClient.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Adds_tomestone_progress_for_mapped_encounters()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = static (_, _, _, encounterId, _) =>
                Task.FromResult(new TomestoneProgressData(
                [
                    new PhaseProgress
                    {
                        PhaseKey = $"phase-{encounterId}",
                        PhaseName = $"Phase {encounterId}",
                        Order = 1,
                        BossPercentage = encounterId == 1079 ? 66.6 : 33.3,
                        DisplayText = $"Phase {encounterId}",
                    },
                ],
                null)),
        };
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5009, server: "Tonberry", zoneId: 65, encounterId: 1079, secondaryEncounterId: 1077)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.True(parse.PhaseProgress.ContainsKey(1079));
        Assert.True(parse.PhaseProgress.ContainsKey(1077));
        Assert.Equal(66.6, Assert.Single(parse.PhaseProgress[1079]).BossPercentage);
        Assert.Equal(33.3, Assert.Single(parse.PhaseProgress[1077]).BossPercentage);
        Assert.Equal(
            [
                ("Player-5009", "Tonberry", 65u, 1079u),
                ("Player-5009", "Tonberry", 59u, 1077u),
            ],
            tomestoneClient.Requests);
    }

    [Fact]
    public async Task Batch_processor_does_not_call_tomestone_for_unmapped_jobs()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1068);
        var tomestoneClient = new StubTomestoneApiClient();
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5002, server: "Tonberry", zoneId: 54, encounterId: 1068)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Empty(parse.PhaseProgress);
        Assert.Empty(tomestoneClient.Requests);
    }

    [Fact]
    public async Task Batch_processor_submits_fflogs_result_when_tomestone_throws()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var tomestoneClient = new ThrowingTomestoneApiClient();
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5003, server: "Tonberry", zoneId: 65, encounterId: 1079)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal(5003UL, parse.ContentId);
        Assert.Empty(parse.PhaseProgress);
        Assert.Single(tomestoneClient.Requests);
        Assert.True(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_reports_transient_failure_when_tomestone_progress_fetch_fails()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataOutcomeAsync = static (_, _, _, _, _) =>
                Task.FromResult(OperationOutcome<TomestoneProgressData>.Failure(
                    transientFailure: true,
                    "tomestone network failed")),
        };
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5003, server: "Tonberry", zoneId: 65, encounterId: 1079)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal(5003UL, parse.ContentId);
        Assert.Empty(parse.PhaseProgress);
        Assert.True(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_preserves_non_transient_tomestone_failure_without_backoff_signal()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataOutcomeAsync = static (_, _, _, _, _) =>
                Task.FromResult(OperationOutcome<TomestoneProgressData>.Failure(
                    transientFailure: false,
                    "tomestone payload malformed")),
        };
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5003, server: "Tonberry", zoneId: 65, encounterId: 1079)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal(5003UL, parse.ContentId);
        Assert.Empty(parse.PhaseProgress);
        Assert.False(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_skips_tomestone_when_tomestone_cooldown_is_active()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var tomestoneClient = new StubTomestoneApiClient
        {
            RateLimitCooldownUntilUtc = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc),
            OnTryGetRateLimitRemaining = static () => (true, TimeSpan.FromMinutes(10)),
            OnFetchProgressionDataAsync = static (_, _, _, _, _) =>
                throw new InvalidOperationException("should not call Tomestone while cooled down"),
        };
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5004, server: "Tonberry", zoneId: 65, encounterId: 1079)]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Empty(parse.PhaseProgress);
        Assert.Empty(tomestoneClient.Requests);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.ShouldAbandonRemainingLeases);
    }

    [Fact]
    public async Task Batch_processor_uses_matched_candidate_server_for_tomestone_lookup()
    {
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _, _) =>
            {
                var output = new Dictionary<string, FFLogsClient.CharacterFetchedData>();
                foreach (var query in queries)
                {
                    output[query.Key] = new FFLogsClient.CharacterFetchedData
                    {
                        Hidden = false,
                        Parses =
                        [
                            new FFLogsClient.CharacterEncounterParse
                            {
                                EncounterId = 1079,
                                Percentile = query.Server == "Beta" ? 95.0 : 10.0,
                            },
                        ],
                        RecentReportCodes = [],
                    };
                }

                return Task.FromResult(output);
            },
        };
        var tomestoneClient = new StubTomestoneApiClient();
        var processor = CreateProcessor(apiClient, tomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [
                CreateJob(
                    contentId: 5005,
                    server: "Alpha",
                    zoneId: 65,
                    encounterId: 1079,
                    candidateServers:
                    [
                        new ParseJobCandidateServer { Server = "Alpha", Region = "JP" },
                        new ParseJobCandidateServer { Server = "Beta", Region = "JP" },
                    ])
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(result.ProcessedResults);
        Assert.Equal("Beta", parse.MatchedServer);
        var request = Assert.Single(tomestoneClient.Requests);
        Assert.Equal("Beta", request.Server);
    }

    [Fact]
    public async Task Batch_processor_does_not_call_tomestone_when_disabled_unconfigured_or_noop()
    {
        var apiClient = CreateVisibleApiClient(encounterId: 1079);
        var disabledTomestoneClient = new StubTomestoneApiClient
        {
            IsConfigured = false,
            OnFetchProgressionDataAsync = static (_, _, _, _, _) =>
                throw new InvalidOperationException("should not call disabled Tomestone seam"),
        };
        var processor = CreateProcessor(apiClient, disabledTomestoneClient);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://session-owner.example/"),
            [CreateJob(contentId: 5006, server: "Tonberry", zoneId: 65, encounterId: 1079)]);

        var disabledResult = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var disabledParse = Assert.Single(disabledResult.ProcessedResults);
        Assert.Empty(disabledParse.PhaseProgress);
        Assert.Empty(disabledTomestoneClient.Requests);

        var noopProcessor = CreateProcessor(CreateVisibleApiClient(encounterId: 1079));
        var noopResult = await noopProcessor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var noopParse = Assert.Single(noopResult.ProcessedResults);
        Assert.Empty(noopParse.PhaseProgress);
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
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _, _) =>
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

    [Fact]
    public async Task Tomestone_progress_enricher_maps_primary_and_secondary_ultimate_encounters()
    {
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = static (_, _, _, encounterId, _) =>
                Task.FromResult(new TomestoneProgressData(
                [
                    new PhaseProgress
                    {
                        PhaseKey = $"phase-{encounterId}",
                        PhaseName = $"Phase {encounterId}",
                        Order = 1,
                        BossPercentage = encounterId == 1079 ? 66.6 : 33.3,
                        DisplayText = $"Phase {encounterId}",
                    },
                ],
                null)),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            new ManualFFLogsTimeProvider(),
            tomestoneClient,
            new Configuration(),
            static (_, _) => Task.CompletedTask);
        var enricher = new TomestoneProgressEnricher(seams);
        var job = CreateJob(contentId: 8001, server: "Tonberry", zoneId: 65, encounterId: 1079, secondaryEncounterId: 1077);
        var parseResult = new ParseResult
        {
            ContentId = job.ContentId,
            MatchedServer = "Tonberry",
        };
        var resultsByContentId = new Dictionary<ulong, ParseResult>
        {
            [job.ContentId] = parseResult,
        };

        var hadTransientFailure = await enricher.EnrichProgressAsync(
            [job],
            resultsByContentId,
            zoneId: 65,
            CancellationToken.None);

        Assert.False(hadTransientFailure);
        Assert.Equal(66.6, Assert.Single(parseResult.PhaseProgress[1079]).BossPercentage);
        Assert.Equal(33.3, Assert.Single(parseResult.PhaseProgress[1077]).BossPercentage);
        Assert.Equal(
            [
                ("Player-8001", "Tonberry", 65u, 1079u),
                ("Player-8001", "Tonberry", 59u, 1077u),
            ],
            tomestoneClient.Requests);
    }

    [Fact]
    public async Task Tomestone_progress_enricher_uses_configured_max_concurrency()
    {
        var activeRequests = 0;
        var maxActiveRequests = 0;
        var tomestoneClient = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = async (_, _, _, encounterId, cancellationToken) =>
            {
                var currentActive = Interlocked.Increment(ref activeRequests);
                UpdateMax(ref maxActiveRequests, currentActive);
                try
                {
                    await Task.Delay(100, cancellationToken);
                    return new TomestoneProgressData(
                    [
                        new PhaseProgress
                        {
                            PhaseKey = $"phase-{encounterId}",
                            PhaseName = $"Phase {encounterId}",
                            Order = 1,
                            BossPercentage = 10.0,
                            DisplayText = $"Phase {encounterId}",
                        },
                    ],
                    null);
                }
                finally
                {
                    Interlocked.Decrement(ref activeRequests);
                }
            },
        };
        var configuration = new Configuration
        {
            TomestoneRequestIntervalMs = 0,
            TomestoneMaxConcurrency = 3,
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            new ManualFFLogsTimeProvider(),
            tomestoneClient,
            configuration,
            static (_, _) => Task.CompletedTask);
        var enricher = new TomestoneProgressEnricher(seams);
        var jobs = new[]
        {
            CreateJob(contentId: 8201, server: "Tonberry", zoneId: 65, encounterId: 1079),
            CreateJob(contentId: 8202, server: "Tonberry", zoneId: 65, encounterId: 1079),
            CreateJob(contentId: 8203, server: "Tonberry", zoneId: 65, encounterId: 1079),
        };
        var resultsByContentId = jobs.ToDictionary(
            static job => job.ContentId,
            static job => new ParseResult
            {
                ContentId = job.ContentId,
                MatchedServer = job.Server,
            });

        var hadTransientFailure = await enricher.EnrichProgressAsync(
            jobs,
            resultsByContentId,
            zoneId: 65,
            CancellationToken.None);

        Assert.False(hadTransientFailure);
        Assert.Equal(3, Volatile.Read(ref maxActiveRequests));
        Assert.All(resultsByContentId.Values, parseResult =>
        {
            var phase = Assert.Single(parseResult.PhaseProgress[1079]);
            Assert.Equal(10.0, phase.BossPercentage);
        });
    }

    [Fact]
    public async Task FFLogs_boss_percent_enricher_merges_lowest_recent_report_percent_per_encounter()
    {
        var requests = new List<(List<string> ReportCodes, int EncounterId, int? DifficultyId)>();
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchBestBossPercentByReportAsync = (reportCodes, encounterId, difficultyId, _) =>
            {
                requests.Add((reportCodes, encounterId, difficultyId));
                return Task.FromResult(encounterId switch
                {
                    88 => new Dictionary<string, double>
                    {
                        ["REP1"] = 17.5,
                        ["REP2"] = 9.8,
                    },
                    99 => new Dictionary<string, double>
                    {
                        ["REP2"] = 43.2,
                        ["REP5"] = 12.4,
                    },
                    _ => new Dictionary<string, double>(),
                });
            },
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            apiClient,
            new ManualFFLogsTimeProvider());
        var enricher = new FFLogsBossPercentEnricher(seams);
        var job = CreateJob(contentId: 8101, server: "Tonberry", encounterId: 88, secondaryEncounterId: 99);
        var parseResult = new ParseResult { ContentId = job.ContentId };
        var resultsByContentId = new Dictionary<ulong, ParseResult>
        {
            [job.ContentId] = parseResult,
        };
        var chosenDataByContentId = new Dictionary<ulong, FFLogsClient.CharacterFetchedData>
        {
            [job.ContentId] = new()
            {
                RecentReportCodes = ["REP1", "REP2", "REP3", "REP4", "REP5", "REP6"],
            },
        };

        var result = await enricher.EnrichBossPercentagesAsync(
            [job],
            resultsByContentId,
            chosenDataByContentId,
            difficultyId: 5,
            CancellationToken.None);

        Assert.False(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.Equal(9.8, parseResult.BossPercentages[88], 3);
        Assert.Equal(12.4, parseResult.BossPercentages[99], 3);
        Assert.Equal([88, 99], requests.Select(static request => request.EncounterId).OrderBy(static value => value));
        Assert.All(requests, request =>
        {
            Assert.Equal(["REP1", "REP2", "REP3", "REP4", "REP5"], request.ReportCodes);
            Assert.Equal(5, request.DifficultyId);
        });
    }

    private static FFLogsBatchProcessor CreateProcessor(
        StubFFLogsApiClient apiClient,
        ITomestoneApiClient? tomestoneApiClient = null,
        int tomestoneRequestIntervalMs = 0,
        Func<TimeSpan, CancellationToken, Task>? tomestoneRequestDelayAsync = null)
    {
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            apiClient,
            new ManualFFLogsTimeProvider(),
            tomestoneApiClient,
            new Configuration { TomestoneRequestIntervalMs = tomestoneRequestIntervalMs },
            tomestoneRequestDelayAsync);
        return new FFLogsBatchProcessor(seams);
    }

    private static void UpdateMax(ref int currentMax, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref currentMax);
            if (candidate <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref currentMax, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private static StubFFLogsApiClient CreateVisibleApiClient(int encounterId)
    {
        return new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = (queries, _, _, _, _) =>
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
                                EncounterId = encounterId,
                                Percentile = 88.8,
                                ClearCount = 3,
                            },
                        ],
                        RecentReportCodes = [],
                    },
                });
            },
        };
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

    private sealed class ThrowingTomestoneApiClient : ITomestoneApiClient
    {
        public bool IsConfigured => true;

        public DateTime RateLimitCooldownUntilUtc => DateTime.MinValue;

        public List<(string Name, string Server, uint ZoneId, uint EncounterId)> Requests { get; } = [];

        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        public void ResetRateLimitCooldown()
        {
        }

        public Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
            string name,
            string server,
            uint zoneId,
            uint encounterId,
            CancellationToken cancellationToken)
        {
            Requests.Add((name, server, zoneId, encounterId));
            throw new InvalidOperationException("tomestone failed");
        }
    }
}
