using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneBatchProcessorTests
{
    static TomestoneBatchProcessorTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Batch_processor_preserves_encounter_id_and_lease_token_1_to_1()
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = (_, _, _, encounterId, _) =>
            {
                if (encounterId == 1079)
                {
                    return Task.FromResult(new TomestoneProgressData(
                        [
                            new PhaseProgress
                            {
                                PhaseKey = "p4",
                                PhaseName = "P4",
                                Order = 4,
                                BossPercentage = 41.13,
                                DisplayText = "P4 41.13%",
                                Source = "tomestone_api",
                            },
                        ],
                        BossPercentage: null));
                }

                return Task.FromResult(new TomestoneProgressData([], BossPercentage: 22.4));
            },
        };
        var processor = CreateProcessor(progression);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                CreateJob(contentId: 1001, zoneId: 65, encounterId: 1079, name: "Ult Player", server: "Tonberry"),
                CreateJob(contentId: 1002, zoneId: 73, encounterId: 102, name: "Savage Player", server: "Chocobo"),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.False(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.HitApiKeyRejected);
        Assert.Empty(result.AbandonedLeases);
        Assert.Equal(2, result.ProcessedResults.Count);

        var ult = Assert.Single(result.ProcessedResults, r => r.ContentId == 1001);
        Assert.Equal(1079u, ult.EncounterId);
        Assert.Equal("lease-1001-1079", ult.LeaseToken);
        Assert.Equal("P4 41.13%", Assert.Single(ult.PhaseProgress[1079]).DisplayText);
        Assert.Equal([1079], ult.TomestoneQueriedEncounters);

        var savage = Assert.Single(result.ProcessedResults, r => r.ContentId == 1002);
        Assert.Equal(102u, savage.EncounterId);
        Assert.Equal("lease-1002-102", savage.LeaseToken);
        Assert.Equal(22.4, savage.BossPercentages[102], 2);
        Assert.Equal([102], savage.TomestoneQueriedEncounters);
    }

    [Fact]
    public async Task Batch_processor_abandons_missing_identity_and_unmapped_encounters_without_querying_tomestone()
    {
        var progression = new StubTomestoneProgressionService();
        var processor = CreateProcessor(progression);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                CreateJob(contentId: 2001, zoneId: 73, encounterId: 101, name: " ", server: "Tonberry"),
                CreateJob(contentId: 2002, zoneId: 999, encounterId: 9999, name: "Alpha", server: "Tonberry"),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Empty(progression.Requests);
        Assert.Empty(result.ProcessedResults);
        Assert.Equal(2, result.AbandonedLeases.Count);
        Assert.Contains(result.AbandonedLeases, a => a.ContentId == 2001 && a.Reason == "worker_non_transient_failure" && a.FailureCode == "tomestone_missing_identity");
        Assert.Contains(result.AbandonedLeases, a => a.ContentId == 2002 && a.Reason == "worker_unmapped_encounter" && a.FailureCode == "tomestone_unmapped_encounter");
    }

    [Fact]
    public async Task Batch_processor_applies_request_pacing_between_waves_and_honors_max_concurrency()
    {
        var delays = new List<TimeSpan>();
        var active = 0;
        var maxActive = 0;
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = async (_, _, _, _, _) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, nowActive);
                await Task.Delay(35);
                Interlocked.Decrement(ref active);
                return TomestoneProgressData.Empty;
            },
        };
        var configuration = new Configuration
        {
            TomestoneMaxConcurrency = 2,
            TomestoneRequestIntervalMs = 250,
            BypassTomestonePacingClampDebug = true,
        };
        var processor = CreateProcessor(
            progression,
            configuration: configuration,
            requestDelayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                CreateJob(1, 73, 101, "A", "Tonberry"),
                CreateJob(2, 73, 102, "B", "Tonberry"),
                CreateJob(3, 73, 103, "C", "Tonberry"),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Equal(3, result.ProcessedResults.Count);
        Assert.Equal(2, maxActive);
        var delay = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMilliseconds(250), delay);
    }

    [Fact]
    public async Task Batch_processor_aborts_mid_batch_on_rate_limit_cooldown_and_defers_remaining_leases()
    {
        var cooldownActive = false;
        var progression = new StubTomestoneProgressionService
        {
            OnTryGetRateLimitRemaining = () => cooldownActive
                ? (true, TimeSpan.FromMinutes(30))
                : (false, TimeSpan.Zero),
            OnFetchProgressionDataOutcomeAsync = (name, _, _, _, _) =>
            {
                if (name == "Second")
                {
                    cooldownActive = true;
                    return Task.FromResult(OperationOutcome<TomestoneProgressData>.Failure(
                        transientFailure: true,
                        "Tomestone API request was rate limited."));
                }

                return Task.FromResult(OperationOutcome<TomestoneProgressData>.Success(
                    new TomestoneProgressData([], BossPercentage: 55.0)));
            },
        };
        var configuration = new Configuration
        {
            TomestoneMaxConcurrency = 1,
            TomestoneRequestIntervalMs = 0,
            BypassTomestonePacingClampDebug = true,
        };
        var processor = CreateProcessor(progression, configuration: configuration);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                CreateJob(3001, 73, 101, "First", "Tonberry"),
                CreateJob(3002, 73, 102, "Second", "Tonberry"),
                CreateJob(3003, 73, 103, "Third", "Tonberry"),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(result.HitRateLimitCooldown);
        Assert.False(result.HitApiKeyRejected);
        Assert.Equal(TimeSpan.FromMinutes(30), result.CooldownRemaining);
        Assert.Equal(2, progression.Requests.Count);
        var completed = Assert.Single(result.ProcessedResults);
        Assert.Equal(3001UL, completed.ContentId);

        Assert.Equal(2, result.AbandonedLeases.Count);
        Assert.All(result.AbandonedLeases, abandon =>
        {
            Assert.Equal("worker_rate_limit_deferred", abandon.Reason);
            Assert.Equal("tomestone_rate_limited", abandon.FailureCode);
        });
        Assert.Contains(result.AbandonedLeases, a => a.ContentId == 3002);
        Assert.Contains(result.AbandonedLeases, a => a.ContentId == 3003);
    }

    [Fact]
    public async Task Batch_processor_aborts_mid_batch_when_api_key_is_rejected()
    {
        var apiStatus = new ApiCredentialStatusStore();
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataOutcomeAsync = (name, _, _, _, _) =>
            {
                if (name == "First")
                {
                    apiStatus.ReportTomestone(ApiCredentialValidationState.Rejected, "401 Unauthorized");
                    return Task.FromResult(OperationOutcome<TomestoneProgressData>.Failure(
                        transientFailure: false,
                        "401 Unauthorized"));
                }

                return Task.FromResult(OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty));
            },
        };
        var processor = CreateProcessor(
            progression,
            configuration: new Configuration { TomestoneMaxConcurrency = 1, TomestoneRequestIntervalMs = 0, BypassTomestonePacingClampDebug = true },
            apiStatus: apiStatus);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                CreateJob(4001, 73, 101, "First", "Tonberry"),
                CreateJob(4002, 73, 102, "Second", "Tonberry"),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(result.HitApiKeyRejected);
        Assert.False(result.HitRateLimitCooldown);
        Assert.Single(progression.Requests);
        Assert.Empty(result.ProcessedResults);
        Assert.Equal(2, result.AbandonedLeases.Count);
        Assert.All(result.AbandonedLeases, abandon =>
        {
            Assert.Equal("worker_api_key_rejected", abandon.Reason);
            Assert.Equal("tomestone_api_key_rejected", abandon.FailureCode);
        });
    }

    [Fact]
    public async Task Batch_processor_aborts_after_pacing_delay_if_cooldown_starts_during_delay()
    {
        var cooldownActive = false;
        var progression = new StubTomestoneProgressionService
        {
            OnTryGetRateLimitRemaining = () => cooldownActive
                ? (true, TimeSpan.FromMinutes(10))
                : (false, TimeSpan.Zero),
        };
        var processor = CreateProcessor(
            progression,
            configuration: new Configuration { TomestoneMaxConcurrency = 1, TomestoneRequestIntervalMs = 150, BypassTomestonePacingClampDebug = true },
            requestDelayAsync: (_, _) =>
            {
                cooldownActive = true;
                return Task.CompletedTask;
            });
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                CreateJob(5001, 73, 101, "First", "Tonberry"),
                CreateJob(5002, 73, 102, "Second", "Tonberry"),
                CreateJob(5003, 73, 103, "Third", "Tonberry"),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(result.HitRateLimitCooldown);
        Assert.Single(progression.Requests);
        Assert.Single(result.ProcessedResults);
        Assert.Equal(2, result.AbandonedLeases.Count);
        Assert.Contains(result.AbandonedLeases, a => a.ContentId == 5002 && a.Reason == "worker_rate_limit_deferred");
        Assert.Contains(result.AbandonedLeases, a => a.ContentId == 5003 && a.Reason == "worker_rate_limit_deferred");
    }

    [Fact]
    public async Task Batch_processor_abandons_job_as_transient_when_progression_service_throws()
    {
        var warnings = new List<string>();
        var progression = new ThrowingProgressionService();
        var seams = TomestoneCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            progression,
            new ManualFFLogsTimeProvider(),
            new Configuration { TomestoneMaxConcurrency = 1, TomestoneRequestIntervalMs = 0, BypassTomestonePacingClampDebug = true },
            new ApiCredentialStatusStore(),
            static (_, _) => Task.CompletedTask);
        var processor = new TomestoneBatchProcessor(seams, warnings.Add);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [CreateJob(6001, 73, 101, "Exploding", "Tonberry")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(result.HadTransientFailure);
        Assert.Empty(result.ProcessedResults);
        var abandon = Assert.Single(result.AbandonedLeases);
        Assert.Equal("worker_transient_failure", abandon.Reason);
        Assert.Equal("tomestone_unexpected_error", abandon.FailureCode);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task Batch_processor_queries_split_boss_secondary_encounter_merges_results_and_reuses_profile_cache()
    {
        var requestCount = 0;
        var handler = new SplitBossProfileHandler(() => Interlocked.Increment(ref requestCount));
        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            TomestoneApiKey = "secret-token",
            TomestoneMaxConcurrency = 1,
            TomestoneRequestIntervalMs = 0,
            BypassTomestonePacingClampDebug = true,
        };
        using var apiClient = new TomestoneApiClient(config, handler);
        var service = new TomestoneProgressionService(apiClient, config);
        var seams = TomestoneCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            service,
            new ManualFFLogsTimeProvider(),
            config,
            new ApiCredentialStatusStore(),
            static (_, _) => Task.CompletedTask);
        var processor = new TomestoneBatchProcessor(seams);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                new TomestoneJob
                {
                    ContentId = 7101,
                    ZoneId = 73,
                    DifficultyId = 101,
                    EncounterId = 104,
                    SecondaryEncounterId = 105,
                    Name = "Split Raider",
                    Server = "Tonberry",
                    Region = "JP",
                    LeaseToken = "lease-split-104",
                },
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.False(result.HadTransientFailure);
        Assert.Empty(result.AbandonedLeases);
        Assert.Equal(1, requestCount);

        var submitted = Assert.Single(result.ProcessedResults);
        Assert.Equal(104u, submitted.EncounterId);
        Assert.Equal("lease-split-104", submitted.LeaseToken);
        Assert.Equal([104, 105], submitted.TomestoneQueriedEncounters);
        Assert.Equal(35.2, submitted.BossPercentages[104], 1);
        Assert.Equal(8.7, submitted.BossPercentages[105], 1);
    }

    [Fact]
    public async Task Batch_processor_clamps_concurrency_and_interval_unless_debug_override_is_enabled()
    {
        var delays = new List<TimeSpan>();
        var active = 0;
        var maxActive = 0;
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = async (_, _, _, _, _) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, nowActive);
                await Task.Delay(35);
                Interlocked.Decrement(ref active);
                return TomestoneProgressData.Empty;
            },
        };
        var configuration = new Configuration
        {
            TomestoneMaxConcurrency = 3,
            TomestoneRequestIntervalMs = 50,
            BypassTomestonePacingClampDebug = false,
        };
        var processor = CreateProcessor(
            progression,
            configuration: configuration,
            requestDelayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                CreateJob(7201, 73, 101, "A", "Tonberry"),
                CreateJob(7202, 73, 102, "B", "Tonberry"),
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Equal(2, result.ProcessedResults.Count);
        Assert.Equal(1, maxActive);
        var delay = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), delay);
    }

    [Fact]
    public async Task Batch_processor_spaces_multiple_targets_within_single_job()
    {
        var delays = new List<TimeSpan>();
        var progression = new StubTomestoneProgressionService();
        var configuration = new Configuration
        {
            TomestoneMaxConcurrency = 1,
            TomestoneRequestIntervalMs = 1000,
        };
        var processor = CreateProcessor(
            progression,
            configuration: configuration,
            requestDelayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var job = new TomestoneJob
        {
            ContentId = 7301,
            ZoneId = 73,
            DifficultyId = 101,
            EncounterId = 104,
            SecondaryEncounterId = 105,
            Name = "Split Multi",
            Server = "Tonberry",
            Region = "JP",
            LeaseToken = "lease-split-multi",
        };
        var session = new TomestoneLeaseSession(new UploadUrl("https://upload.example/"), [job]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Single(result.ProcessedResults);
        Assert.Equal(2, progression.Requests.Count);
        var delay = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), delay);
    }

    [Fact]
    public async Task Batch_processor_abandons_split_boss_job_when_secondary_target_triggers_rate_limit_or_failure()
    {
        var rateLimited = false;
        var progression = new StubTomestoneProgressionService
        {
            OnTryGetRateLimitRemaining = () => rateLimited
                ? (true, TimeSpan.FromMinutes(25))
                : (false, TimeSpan.Zero),
            OnFetchProgressionDataOutcomeAsync = (_, _, _, encounterId, _) =>
            {
                if (encounterId == 105)
                {
                    rateLimited = true;
                    return Task.FromResult(OperationOutcome<TomestoneProgressData>.Failure(
                        transientFailure: true,
                        "Tomestone API request was rate limited."));
                }

                return Task.FromResult(OperationOutcome<TomestoneProgressData>.Success(
                    new TomestoneProgressData([], BossPercentage: 40.0)));
            },
        };
        var processor = CreateProcessor(progression);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                new TomestoneJob
                {
                    ContentId = 7201,
                    ZoneId = 73,
                    DifficultyId = 101,
                    EncounterId = 104,
                    SecondaryEncounterId = 105,
                    Name = "Alpha",
                    Server = "Tonberry",
                    Region = "JP",
                    LeaseToken = "lease-split-rate-limited",
                },
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(result.HitRateLimitCooldown);
        Assert.Empty(result.ProcessedResults);
        var abandon = Assert.Single(result.AbandonedLeases);
        Assert.Equal(104u, abandon.EncounterId);
        Assert.Equal("worker_rate_limit_deferred", abandon.Reason);
        Assert.Equal("tomestone_rate_limited", abandon.FailureCode);
        Assert.Equal("lease-split-rate-limited", abandon.LeaseToken);

        var transientFailProgression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataOutcomeAsync = (_, _, _, encounterId, _) => Task.FromResult(
                encounterId == 105
                    ? OperationOutcome<TomestoneProgressData>.Failure(transientFailure: true, "500 Internal Server Error")
                    : OperationOutcome<TomestoneProgressData>.Success(new TomestoneProgressData([], BossPercentage: 40.0))),
        };
        var transientResult = await CreateProcessor(transientFailProgression)
            .ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.True(transientResult.HadTransientFailure);
        Assert.Empty(transientResult.ProcessedResults);
        var transientAbandon = Assert.Single(transientResult.AbandonedLeases);
        Assert.Equal("worker_transient_failure", transientAbandon.Reason);
        Assert.Equal("tomestone_transient_failure", transientAbandon.FailureCode);
    }

    [Fact]
    public async Task Batch_processor_abandons_single_boss_cleared_without_percentiles()
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = static (_, _, _, _, _) => Task.FromResult(TomestoneProgressData.ClearedProgress),
        };
        var processor = CreateProcessor(progression);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [CreateJob(7301, 73, 101, "Cleared Player", "Tonberry")]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.False(result.HadTransientFailure);
        Assert.False(result.HitRateLimitCooldown);
        Assert.False(result.HitApiKeyRejected);
        Assert.Empty(result.ProcessedResults);
        var abandon = Assert.Single(result.AbandonedLeases);
        Assert.Equal(7301UL, abandon.ContentId);
        Assert.Equal(101u, abandon.EncounterId);
        Assert.Equal("tomestone_no_data", abandon.Reason);
        Assert.Equal("tomestone_cleared_without_percentiles", abandon.FailureCode);
        Assert.Equal("Tomestone API reported completed activity without clear rankings.", abandon.Message);
    }

    [Fact]
    public async Task Batch_processor_split_boss_cleared_primary_and_prog_secondary_emits_only_secondary_coverage()
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = static (_, _, _, encounterId, _) => Task.FromResult(
                encounterId == 104
                    ? TomestoneProgressData.ClearedProgress
                    : new TomestoneProgressData([], BossPercentage: 42.5)),
        };
        var processor = CreateProcessor(progression);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                new TomestoneJob
                {
                    ContentId = 7401,
                    ZoneId = 73,
                    DifficultyId = 101,
                    EncounterId = 104,
                    SecondaryEncounterId = 105,
                    Name = "Split Clear Prog",
                    Server = "Tonberry",
                    Region = "JP",
                    LeaseToken = "lease-split-clear-prog",
                },
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.False(result.HadTransientFailure);
        Assert.Empty(result.AbandonedLeases);
        var submitted = Assert.Single(result.ProcessedResults);
        Assert.Equal(104u, submitted.EncounterId);
        Assert.Equal(42.5, submitted.BossPercentages[105], 1);
        Assert.False(submitted.BossPercentages.ContainsKey(104));
        Assert.Equal([105], submitted.TomestoneQueriedEncounters);
    }

    private sealed class SplitBossProfileHandler(Action onRequest) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("""
                {
                  "encounters": {
                    "savage": [
                      {
                        "canonicalName": "lindwurm",
                        "progression": { "displayPercent": "35.2%" }
                      },
                      {
                        "canonicalName": "lindwurm-ii",
                        "progression": { "displayPercent": "8.7%" }
                      }
                    ]
                  }
                }
                """),
            });
        }
    }

    private sealed class ThrowingProgressionService : ITomestoneProgressionService
    {
        public bool IsConfigured => true;
        public DateTime RateLimitCooldownUntilUtc => DateTime.MinValue;
        public bool TryGetRateLimitRemaining(out TimeSpan remaining) { remaining = TimeSpan.Zero; return false; }
        public void ResetRateLimitCooldown() { }

        public Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
            string name,
            string server,
            uint zoneId,
            uint encounterId,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("unexpected exception");

        public IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
            string name,
            string server,
            uint zoneId,
            IReadOnlyCollection<uint> excludeEncounterIds)
            => new Dictionary<uint, TomestoneProgressData>();
    }

    private static TomestoneBatchProcessor CreateProcessor(
        StubTomestoneProgressionService progression,
        Configuration? configuration = null,
        ApiCredentialStatusStore? apiStatus = null,
        Func<TimeSpan, CancellationToken, Task>? requestDelayAsync = null)
    {
        var seams = TomestoneCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            progression,
            new ManualFFLogsTimeProvider(),
            configuration ?? new Configuration { TomestoneMaxConcurrency = 1, TomestoneRequestIntervalMs = 0, BypassTomestonePacingClampDebug = true },
            apiStatus ?? new ApiCredentialStatusStore(),
            requestDelayAsync ?? (static (_, _) => Task.CompletedTask));
        return new TomestoneBatchProcessor(seams);
    }

    private static TomestoneJob CreateJob(ulong contentId, uint zoneId, uint encounterId, string name, string server) => new()
    {
        ContentId = contentId,
        ZoneId = zoneId,
        DifficultyId = 0,
        EncounterId = encounterId,
        Name = name,
        Server = server,
        Region = "JP",
        LeaseToken = $"lease-{contentId}-{encounterId}",
    };

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
}
