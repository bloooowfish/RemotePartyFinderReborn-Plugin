using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsBackgroundWorkerTests
{
    static FFLogsBackgroundWorkerTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Background_worker_uses_worker_policy_to_choose_idle_delay()
    {
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerIdleDelayMs = 250,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 5, 0, 0, DateTimeKind.Utc),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            timeProvider);
        var delays = new List<int>();
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                throw new TaskCanceledException();
            });
        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new FixedAttemptJobLeaseClient(
                seams,
                new FFLogsJobLeaseAttempt(
                    new FFLogsLeaseSession(configuration.UploadUrls[0], []),
                    false)),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { });

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal([1000], delays);
    }

    [Fact]
    public async Task Background_worker_applies_backoff_after_transient_iteration_failure()
    {
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 5000,
            FFLogsWorkerMaxBackoffDelayMs = 12000,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 6, 0, 0, DateTimeKind.Utc),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            timeProvider);
        var delays = new List<int>();
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                throw new TaskCanceledException();
            });
        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new FixedAttemptJobLeaseClient(
                seams,
                new FFLogsJobLeaseAttempt(
                    new FFLogsLeaseSession(configuration.UploadUrls[0], []),
                    true)),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { });

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal([5000], delays);
        Assert.Equal(5000, workerPolicy.LastBackoffDelayMs);
    }

    [Fact]
    public async Task Backoff_on_transient_candidate_fetch_without_top_level_error()
    {
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 5000,
            FFLogsWorkerMaxBackoffDelayMs = 12000,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (_, _, _, _) =>
                Task.FromException<Dictionary<string, FFLogsClient.CharacterFetchedData>>(
                    new HttpRequestException("transient candidate lookup")),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            apiClient,
            new ManualFFLogsTimeProvider());
        var delays = new List<int>();
        var errors = new List<string>();
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            seams.TimeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                throw new TaskCanceledException();
            });
        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new FixedAttemptJobLeaseClient(
                seams,
                new FFLogsJobLeaseAttempt(
                    new FFLogsLeaseSession(configuration.UploadUrls[0], [CreateJob()]),
                    false)),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            errors.Add,
            static _ => { });

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal([5000], delays);
        Assert.Equal(5000, workerPolicy.LastBackoffDelayMs);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Background_worker_uses_base_delay_when_no_session_is_acquired()
    {
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 4000,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 6, 30, 0, DateTimeKind.Utc),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            timeProvider);
        var delays = new List<int>();
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                throw new TaskCanceledException();
            });
        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new FixedAttemptJobLeaseClient(
                seams,
                new FFLogsJobLeaseAttempt(null, false)),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { });

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal([4000], delays);
    }

    [Fact]
    public async Task Background_worker_uses_base_delay_when_batch_processor_hits_rate_limit_cooldown()
    {
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 3500,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var cooldownChecks = 0;
        var apiClient = new StubFFLogsApiClient
        {
            OnTryGetRateLimitRemaining = () =>
            {
                cooldownChecks++;
                return cooldownChecks >= 2
                    ? (true, TimeSpan.FromMinutes(3))
                    : (false, TimeSpan.Zero);
            },
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 6, 45, 0, DateTimeKind.Utc),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            apiClient,
            timeProvider);
        var delays = new List<int>();
        var warnings = new List<string>();
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            warnings.Add,
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                throw new TaskCanceledException();
            });
        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new FixedAttemptJobLeaseClient(
                seams,
                new FFLogsJobLeaseAttempt(
                    new FFLogsLeaseSession(configuration.UploadUrls[0], [CreateJob()]),
                    false)),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            warnings.Add,
            static _ => { },
            static _ => { });

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal([3500], delays);
        Assert.Contains(warnings, message => message.Contains("FFLogs cooldown active", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Background_worker_applies_backoff_after_top_level_exception()
    {
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 4500,
            FFLogsWorkerMaxBackoffDelayMs = 12000,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 7, 15, 0, DateTimeKind.Utc),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            timeProvider);
        var delays = new List<int>();
        var errors = new List<string>();
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                throw new TaskCanceledException();
            });
        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new ThrowingJobLeaseClient(seams),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            errors.Add,
            static _ => { });

        await Assert.ThrowsAsync<TaskCanceledException>(() => worker.RunAsync(CancellationToken.None));

        Assert.Equal([4500], delays);
        Assert.Equal(4500, workerPolicy.LastBackoffDelayMs);
        Assert.Contains(errors, message => message.Contains("FFLogsCollector Loop Error", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_remains_the_plugin_facing_facade_after_worker_extraction()
    {
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var apiClient = new StubFFLogsApiClient
        {
            RateLimitCooldownUntilUtc = new DateTime(2026, 4, 15, 7, 30, 0, DateTimeKind.Utc),
            OnTryGetRateLimitRemaining = static () => (true, TimeSpan.FromMinutes(9)),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender
            {
                OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            },
            apiClient,
            new ManualFFLogsTimeProvider
            {
                UtcNow = new DateTime(2026, 4, 15, 7, 0, 0, DateTimeKind.Utc),
            });
        var workerPolicy = new FFLogsWorkerPolicy(configuration, _ => { }, seams.TimeProvider);

        using var collector = FFLogsCollector.CreateForTesting(
            configuration,
            seams,
            new FFLogsSubmitBuffer(),
            workerPolicy);

        Assert.True(collector.TryGetRateLimitCooldownRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromMinutes(9), remaining);
        Assert.Equal(apiClient.RateLimitCooldownUntilUtc, collector.RateLimitCooldownUntilUtc);

        collector.ResetRateLimitCooldown();

        Assert.Equal(DateTime.MinValue, apiClient.RateLimitCooldownUntilUtc);
    }

    [Fact]
    public async Task Background_worker_skips_lease_polling_when_plugin_is_not_operational()
    {
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 4200,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 8, 0, 0, DateTimeKind.Utc),
        };
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            timeProvider);
        var delays = new List<int>();
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                throw new TaskCanceledException();
            });
        var leaseClient = new CountingJobLeaseClient(seams);
        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            leaseClient,
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            isPluginOperational: static () => false);

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal([4200], delays);
        Assert.Equal(0, leaseClient.AcquireCalls);
    }

    [Fact]
    public async Task Background_worker_abandons_explicit_and_unprocessed_leases_before_early_return_on_deferred_submit()
    {
        var uploadUrl = new UploadUrl("https://upload.example/");
        // Require protected capabilities so FflogsResults is capability-deferred -> ShouldUseBaseDelayBeforeNextPoll = true,
        // while FflogsLeasesAbandon has a valid grant so abandon requests still execute.
        uploadUrl.RequireProtectedCapabilities(ProtectedEndpointCapabilityKind.FflogsResults);
        uploadUrl.ApplyProtectedCapabilities(new ProtectedEndpointCapabilities
        {
            FflogsLeasesAbandon = new ProtectedEndpointCapabilityGrant
            {
                Token = "abandon-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            },
        });

        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 4000,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(uploadUrl),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 8, 30, 0, DateTimeKind.Utc),
        };
        var abandonedItems = new List<AbandonFflogsLease>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, ct) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith(IngestRoutes.FflogsLeasesAbandon.Path, StringComparison.Ordinal))
                {
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    abandonedItems.AddRange(JsonConvert.DeserializeObject<List<AbandonFflogsLease>>(body)!);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"submitted":1,"released":1,"rejected":0}"""),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var apiCalls = 0;
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = (queries, _, _, _) =>
            {
                apiCalls++;
                if (apiCalls == 1)
                {
                    var output = new Dictionary<string, FFLogsClient.CharacterFetchedData>();
                    foreach (var query in queries)
                    {
                        output[query.Key] = new FFLogsClient.CharacterFetchedData
                        {
                            Exists = true,
                            Hidden = false,
                            Parses = [new FFLogsClient.CharacterEncounterParse { EncounterId = 9001, Percentile = 75.0, ClearCount = 1 }],
                        };
                    }

                    return Task.FromResult(output);
                }

                return Task.FromException<Dictionary<string, FFLogsClient.CharacterFetchedData>>(
                    new HttpRequestException("transient fflogs error"));
            },
        };

        var seams = FFLogsCollector.CreateSeams(sender, apiClient, timeProvider);
        var delays = new List<int>();
        AbandonFflogsLease[]? abandonedBeforeDelay = null;
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                abandonedBeforeDelay = abandonedItems.ToArray();
                throw new TaskCanceledException();
            });

        var explicitMissingJob = new ParseJob
        {
            ContentId = 1001,
            Name = "",
            ZoneId = 111,
            DifficultyId = 2,
            EncounterId = 9001,
            LeaseToken = "lease-explicit-missing",
        };
        var processedJob = new ParseJob
        {
            ContentId = 1002,
            Name = "Processed Player",
            Server = "TestServer",
            Region = "NA",
            ZoneId = 111,
            DifficultyId = 2,
            EncounterId = 9001,
            LeaseToken = "lease-processed",
        };
        var unprocessedJob = new ParseJob
        {
            ContentId = 1003,
            Name = "Unprocessed Player",
            Server = "TestServer",
            Region = "NA",
            ZoneId = 112,
            DifficultyId = 2,
            EncounterId = 9002,
            LeaseToken = "lease-unprocessed",
        };

        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new FixedAttemptJobLeaseClient(
                seams,
                new FFLogsJobLeaseAttempt(
                    new FFLogsLeaseSession(uploadUrl, [explicitMissingJob, processedJob, unprocessedJob]),
                    false)),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { });

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal([4000], delays);
        Assert.NotNull(abandonedBeforeDelay);
        Assert.Contains(abandonedBeforeDelay!, x => x.ContentId == 1001 && x.LeaseToken == "lease-explicit-missing" && x.Reason == "fflogs_missing_identity");
        Assert.Contains(abandonedBeforeDelay!, x => x.ContentId == 1003 && x.LeaseToken == "lease-unprocessed" && x.Reason == "worker_transient_failure");
        Assert.DoesNotContain(abandonedBeforeDelay!, x => x.ContentId == 1002);
        Assert.Contains(abandonedItems, x => x.ContentId == 1002 && x.LeaseToken == "lease-processed" && x.Reason == "worker_shutdown");
    }

    [Fact]
    public async Task Background_worker_abandons_in_flight_leased_jobs_on_shutdown_cancellation()
    {
        var uploadUrl = new UploadUrl("https://upload.example/");
        var configuration = new Configuration
        {
            EnableFFLogsWorker = true,
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 4000,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(uploadUrl),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc),
        };
        var abandonedItems = new List<AbandonFflogsLease>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(IngestRoutes.FflogsLeasesAbandon.Path, StringComparison.Ordinal))
                {
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    abandonedItems.AddRange(JsonConvert.DeserializeObject<List<AbandonFflogsLease>>(body)!);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"submitted":2,"released":2,"rejected":0}"""),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        using var cts = new CancellationTokenSource();
        var apiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = (_, _, _, ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<Dictionary<string, FFLogsClient.CharacterFetchedData>>(ct);
            },
        };

        var seams = FFLogsCollector.CreateSeams(sender, apiClient, timeProvider);
        var workerPolicy = new FFLogsWorkerPolicy(configuration, _ => { }, timeProvider);
        var job1 = new ParseJob
        {
            ContentId = 2001,
            Name = "InFlight One",
            Server = "Tonberry",
            Region = "JP",
            ZoneId = 111,
            DifficultyId = 2,
            EncounterId = 9001,
            LeaseToken = "lease-inflight-1",
        };
        var job2 = new ParseJob
        {
            ContentId = 2002,
            Name = "InFlight Two",
            Server = "Tonberry",
            Region = "JP",
            ZoneId = 111,
            DifficultyId = 2,
            EncounterId = 9001,
            LeaseToken = "lease-inflight-2",
        };

        var worker = new FFLogsBackgroundWorker(
            configuration,
            seams.ApiClient,
            workerPolicy,
            new FixedAttemptJobLeaseClient(
                seams,
                new FFLogsJobLeaseAttempt(
                    new FFLogsLeaseSession(uploadUrl, [job1, job2]),
                    false)),
            new FFLogsBatchProcessor(seams),
            new FFLogsResultSubmitter(seams, new FFLogsSubmitBuffer()),
            new FFLogsLeaseAbandoner(seams),
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { });

        await worker.RunAsync(cts.Token);

        Assert.Equal(2, abandonedItems.Count);
        Assert.All(abandonedItems, item => Assert.Equal("worker_shutdown", item.Reason));
        Assert.Contains(abandonedItems, item => item.ContentId == 2001 && item.LeaseToken == "lease-inflight-1");
        Assert.Contains(abandonedItems, item => item.ContentId == 2002 && item.LeaseToken == "lease-inflight-2");
    }

    private static ParseJob CreateJob()
        => new()
        {
            ContentId = 1234,
            Name = "Example Player",
            Server = "TestServer",
            Region = "NA",
            ZoneId = 111,
            DifficultyId = 2,
            EncounterId = 9001,
            LeaseToken = "lease-token",
        };

    private sealed class FixedAttemptJobLeaseClient(
        FFLogsCollectorSeams seams,
        FFLogsJobLeaseAttempt attempt) : FFLogsJobLeaseClient(seams)
    {
        public override Task<FFLogsJobLeaseAttempt> TryAcquireSessionAsync(
            Configuration configuration,
            CancellationToken cancellationToken,
            Action<string>? warningLog = null,
            Action<string>? debugLog = null)
            => Task.FromResult(attempt);
    }

    private sealed class ThrowingJobLeaseClient(FFLogsCollectorSeams seams) : FFLogsJobLeaseClient(seams)
    {
        public override Task<FFLogsJobLeaseAttempt> TryAcquireSessionAsync(
            Configuration configuration,
            CancellationToken cancellationToken,
            Action<string>? warningLog = null,
            Action<string>? debugLog = null)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CountingJobLeaseClient(FFLogsCollectorSeams seams) : FFLogsJobLeaseClient(seams)
    {
        public int AcquireCalls { get; private set; }

        public override Task<FFLogsJobLeaseAttempt> TryAcquireSessionAsync(
            Configuration configuration,
            CancellationToken cancellationToken,
            Action<string>? warningLog = null,
            Action<string>? debugLog = null)
        {
            AcquireCalls++;
            return Task.FromResult(new FFLogsJobLeaseAttempt(null, false));
        }
    }
}
