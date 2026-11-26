using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneBackgroundWorkerTests
{
    static TomestoneBackgroundWorkerTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Pre_lease_gate_sends_zero_jobs_requests_when_plugin_is_not_operational()
    {
        var sender = new StubFFLogsIngestHttpSender();
        var worker = CreateWorker(
            CreateOperationalConfiguration(),
            sender,
            new StubTomestoneProgressionService(),
            new ApiCredentialStatusStore(),
            isPluginOperational: static () => false,
            out var delays);

        await worker.RunSingleIterationForTestingAsync(0, CancellationToken.None);

        Assert.Empty(sender.Requests);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Pre_lease_gate_sends_zero_jobs_requests_when_tomestone_contributor_is_disabled()
    {
        var configuration = CreateOperationalConfiguration();
        configuration.EnableTomestoneProgressEnrichment = false;
        var sender = new StubFFLogsIngestHttpSender();
        var worker = CreateWorker(
            configuration,
            sender,
            new StubTomestoneProgressionService(),
            new ApiCredentialStatusStore(),
            isPluginOperational: static () => true,
            out var delays);

        await worker.RunSingleIterationForTestingAsync(0, CancellationToken.None);

        Assert.Empty(sender.Requests);
        Assert.Single(delays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Pre_lease_gate_sends_zero_jobs_requests_when_api_key_is_not_configured(string apiKey)
    {
        var configuration = CreateOperationalConfiguration();
        configuration.TomestoneApiKey = apiKey;
        var sender = new StubFFLogsIngestHttpSender();
        var worker = CreateWorker(
            configuration,
            sender,
            new StubTomestoneProgressionService(),
            new ApiCredentialStatusStore(),
            isPluginOperational: static () => true,
            out var delays);

        await worker.RunSingleIterationForTestingAsync(0, CancellationToken.None);

        Assert.Empty(sender.Requests);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Pre_lease_gate_sends_zero_jobs_requests_when_api_key_is_rejected()
    {
        var apiStatus = new ApiCredentialStatusStore();
        apiStatus.ReportTomestone(ApiCredentialValidationState.Rejected, "401 Unauthorized");
        var sender = new StubFFLogsIngestHttpSender();
        var worker = CreateWorker(
            CreateOperationalConfiguration(),
            sender,
            new StubTomestoneProgressionService(),
            apiStatus,
            isPluginOperational: static () => true,
            out var delays);

        await worker.RunSingleIterationForTestingAsync(0, CancellationToken.None);

        Assert.Empty(sender.Requests);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Pre_lease_gate_sends_zero_jobs_requests_when_rate_limit_cooldown_is_active()
    {
        var warnings = new List<string>();
        var sender = new StubFFLogsIngestHttpSender();
        var progression = new StubTomestoneProgressionService
        {
            OnTryGetRateLimitRemaining = static () => (true, TimeSpan.FromMinutes(15)),
        };
        var worker = CreateWorker(
            CreateOperationalConfiguration(),
            sender,
            progression,
            new ApiCredentialStatusStore(),
            isPluginOperational: static () => true,
            out var delays,
            warningLog: warnings.Add);

        await worker.RunSingleIterationForTestingAsync(0, CancellationToken.None);

        Assert.Empty(sender.Requests);
        Assert.Single(delays);
        Assert.Contains(warnings, static w => w.Contains("cooldown active", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pre_lease_gate_still_flushes_pending_results_without_leasing_jobs()
    {
        var configuration = CreateOperationalConfiguration();
        configuration.EnableTomestoneProgressEnrichment = false;
        var requestedPaths = new List<string>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = (request, _) =>
            {
                requestedPaths.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","submitted":1,"accepted":1,"updated":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                });
            },
        };
        var submitBuffer = new TomestoneSubmitBuffer();
        submitBuffer.QueueSubmitResults([
            new TomestoneResult
            {
                ContentId = 9001,
                ZoneId = 73,
                DifficultyId = 0,
                EncounterId = 101,
                TomestoneQueriedEncounters = [101],
                SourceKind = "tomestone_api",
                LeaseToken = "buffered-lease",
            },
        ]);
        var worker = CreateWorker(
            configuration,
            sender,
            new StubTomestoneProgressionService(),
            new ApiCredentialStatusStore(),
            isPluginOperational: static () => true,
            out _,
            submitBuffer: submitBuffer);

        await worker.RunSingleIterationForTestingAsync(0, CancellationToken.None);

        Assert.DoesNotContain("/contribute/tomestone/jobs", requestedPaths);
        Assert.Equal(["/contribute/tomestone/results"], requestedPaths);
        Assert.False(submitBuffer.HasPendingResults);
    }

    [Fact]
    public async Task Worker_executes_lease_query_submit_and_mid_batch_rate_limit_abandon_end_to_end()
    {
        var requests = new List<(string Method, string Path, string Body)>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content != null ? await request.Content.ReadAsStringAsync() : string.Empty;
                requests.Add((request.Method.Method, path, body));

                if (path == "/contribute/tomestone/jobs")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            [
                              {
                                "content_id": 1001,
                                "name": "Alpha",
                                "server": "Tonberry",
                                "region": "JP",
                                "zone_id": 73,
                                "difficulty_id": 0,
                                "encounter_id": 101,
                                "lease_token": "lease-1001"
                              },
                              {
                                "content_id": 1002,
                                "name": "Beta",
                                "server": "Tonberry",
                                "region": "JP",
                                "zone_id": 73,
                                "difficulty_id": 0,
                                "encounter_id": 102,
                                "lease_token": "lease-1002"
                              },
                              {
                                "content_id": 1003,
                                "name": "Gamma",
                                "server": "Tonberry",
                                "region": "JP",
                                "zone_id": 73,
                                "difficulty_id": 0,
                                "encounter_id": 103,
                                "lease_token": "lease-1003"
                              }
                            ]
                            """,
                            Encoding.UTF8,
                            "application/json"),
                    };
                }

                if (path == "/contribute/tomestone/results")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"ok","submitted":1,"accepted":1,"updated":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","submitted":2,"released":2,"rejected":0}""", Encoding.UTF8, "application/json"),
                };
            },
        };

        var cooldownActive = false;
        var progression = new StubTomestoneProgressionService
        {
            OnTryGetRateLimitRemaining = () => cooldownActive
                ? (true, TimeSpan.FromMinutes(20))
                : (false, TimeSpan.Zero),
            OnFetchProgressionDataOutcomeAsync = (name, _, _, _, _) =>
            {
                if (name == "Beta")
                {
                    cooldownActive = true;
                    return Task.FromResult(OperationOutcome<TomestoneProgressData>.Failure(
                        transientFailure: true,
                        "Tomestone API request was rate limited."));
                }

                return Task.FromResult(OperationOutcome<TomestoneProgressData>.Success(
                    new TomestoneProgressData([], BossPercentage: 14.5)));
            },
        };

        var worker = CreateWorker(
            CreateOperationalConfiguration(),
            sender,
            progression,
            new ApiCredentialStatusStore(),
            isPluginOperational: static () => true,
            out _);

        await worker.RunSingleIterationForTestingAsync(0, CancellationToken.None);

        Assert.Collection(
            requests,
            req => Assert.Equal(("GET", "/contribute/tomestone/jobs"), (req.Method, req.Path)),
            req =>
            {
                Assert.Equal(("POST", "/contribute/tomestone/results"), (req.Method, req.Path));
                var resultObj = Assert.IsType<JObject>(Assert.Single(JArray.Parse(req.Body)));
                Assert.Equal(1001UL, resultObj["content_id"]!.Value<ulong>());
                Assert.Equal(101u, resultObj["encounter_id"]!.Value<uint>());
                Assert.Equal("tomestone_api", resultObj["source_kind"]!.Value<string>());
                Assert.Equal(14.5, resultObj["boss_percentages"]!["101"]!.Value<double>(), 1);
            },
            req =>
            {
                Assert.Equal(("POST", "/contribute/tomestone/leases/abandon"), (req.Method, req.Path));
                var abandoned = JArray.Parse(req.Body);
                Assert.Equal(2, abandoned.Count);
                Assert.All(abandoned, token =>
                {
                    Assert.Equal("worker_rate_limit_deferred", token["reason"]!.Value<string>());
                    Assert.Equal("tomestone_rate_limited", token["failure_code"]!.Value<string>());
                    Assert.Equal("dalamud_plugin_worker", token["processor_kind"]!.Value<string>());
                    Assert.Equal("tomestone_api", token["source_kind"]!.Value<string>());
                });
            });
    }

    [Fact]
    public async Task Collector_shutdown_abandons_unsubmitted_results_with_worker_shutdown()
    {
        var requests = new List<(string Path, string Body)>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content != null ? await request.Content.ReadAsStringAsync() : string.Empty;
                requests.Add((path, body));

                if (path == "/contribute/tomestone/results")
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("server_down"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","submitted":1,"released":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                };
            },
        };
        var configuration = CreateOperationalConfiguration();
        configuration.EnableTomestoneProgressEnrichment = false;
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 5, 12, 4, 0, 0, DateTimeKind.Utc),
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            timeProvider,
            configuration,
            new ApiCredentialStatusStore(),
            static (_, _) => Task.CompletedTask);
        var submitBuffer = new TomestoneSubmitBuffer();
        submitBuffer.QueueSubmitResults([
            new TomestoneResult
            {
                ContentId = 9501,
                ZoneId = 65,
                DifficultyId = 0,
                EncounterId = 1079,
                TomestoneQueriedEncounters = [1079],
                SourceKind = "tomestone_api",
                LeaseToken = "shutdown-lease-9501",
            },
        ]);
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            static _ => { },
            timeProvider,
            static (_, _) => throw new TaskCanceledException());

        using var collector = TomestoneCollector.CreateForTesting(
            configuration,
            seams,
            submitBuffer,
            workerPolicy,
            isPluginOperational: static () => true);

        await collector.RunWorkerLoopForTestingAsync();

        Assert.False(submitBuffer.HasPendingResults);
        var abandonReq = Assert.Single(requests, r => r.Path == "/contribute/tomestone/leases/abandon");
        var payloadObj = Assert.IsType<JObject>(Assert.Single(JArray.Parse(abandonReq.Body)));
        Assert.Equal(9501UL, payloadObj["content_id"]!.Value<ulong>());
        Assert.Equal("shutdown-lease-9501", payloadObj["lease_token"]!.Value<string>());
        Assert.Equal("worker_shutdown", payloadObj["reason"]!.Value<string>());
        Assert.Equal("worker_shutdown", payloadObj["failure_code"]!.Value<string>());
    }

    [Fact]
    public async Task Worker_shutdown_mid_batch_abandons_in_flight_leased_jobs_with_worker_shutdown()
    {
        var requests = new List<(string Path, string Body)>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, ct) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
                requests.Add((path, body));

                if (path == "/contribute/tomestone/jobs")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            [
                              {
                                "content_id": 3001,
                                "name": "Alpha",
                                "server": "Tonberry",
                                "region": "JP",
                                "zone_id": 73,
                                "difficulty_id": 0,
                                "encounter_id": 101,
                                "lease_token": "lease-3001"
                              },
                              {
                                "content_id": 3002,
                                "name": "Beta",
                                "server": "Tonberry",
                                "region": "JP",
                                "zone_id": 73,
                                "difficulty_id": 0,
                                "encounter_id": 102,
                                "lease_token": "lease-3002"
                              }
                            ]
                            """,
                            Encoding.UTF8,
                            "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","submitted":2,"released":2,"rejected":0}""", Encoding.UTF8, "application/json"),
                };
            },
        };

        using var cts = new CancellationTokenSource();
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataOutcomeAsync = (_, _, _, _, _) =>
            {
                cts.Cancel();
                return Task.FromCanceled<OperationOutcome<TomestoneProgressData>>(cts.Token);
            },
        };

        var configuration = CreateOperationalConfiguration();
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 5, 12, 4, 30, 0, DateTimeKind.Utc),
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            progression,
            timeProvider,
            configuration,
            new ApiCredentialStatusStore(),
            static (_, _) => Task.CompletedTask);
        var workerPolicy = new FFLogsWorkerPolicy(configuration, static _ => { }, timeProvider);
        var submitBuffer = new TomestoneSubmitBuffer();
        var abandoner = new TomestoneLeaseAbandoner(seams);
        var worker = new TomestoneBackgroundWorker(
            configuration,
            progression,
            new ApiCredentialStatusStore(),
            timeProvider,
            workerPolicy,
            new TomestoneJobLeaseClient(seams),
            new TomestoneBatchProcessor(seams),
            new TomestoneResultSubmitter(seams, submitBuffer, abandoner),
            abandoner,
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            static () => true);

        await worker.RunAsync(cts.Token);

        var abandonReq = Assert.Single(requests, r => r.Path == "/contribute/tomestone/leases/abandon");
        var abandoned = JArray.Parse(abandonReq.Body);
        Assert.Equal(2, abandoned.Count);
        Assert.Contains(abandoned, t => t["content_id"]!.Value<ulong>() == 3001UL && t["lease_token"]!.Value<string>() == "lease-3001");
        Assert.Contains(abandoned, t => t["content_id"]!.Value<ulong>() == 3002UL && t["lease_token"]!.Value<string>() == "lease-3002");
        Assert.All(abandoned, token =>
        {
            Assert.Equal("worker_shutdown", token["reason"]!.Value<string>());
            Assert.Equal("worker_shutdown", token["failure_code"]!.Value<string>());
        });
    }

    private static Configuration CreateOperationalConfiguration() => new()
    {
        IngestClientId = Guid.NewGuid().ToString("N"),
        EnableTomestoneProgressEnrichment = true,
        TomestoneApiKey = "valid-api-key",
        TomestoneMaxConcurrency = 1,
        TomestoneRequestIntervalMs = 0,
        BypassTomestonePacingClampDebug = true,
        FFLogsWorkerBaseDelayMs = 250,
        FFLogsWorkerIdleDelayMs = 250,
        FFLogsWorkerMaxBackoffDelayMs = 1000,
        FFLogsWorkerJitterMs = 0,
        UploadUrls = ImmutableList.Create(new UploadUrl("https://upload.example/")),
    };

    private static TomestoneBackgroundWorker CreateWorker(
        Configuration configuration,
        StubFFLogsIngestHttpSender sender,
        StubTomestoneProgressionService progression,
        ApiCredentialStatusStore apiStatus,
        Func<bool> isPluginOperational,
        out List<int> recordedDelays,
        TomestoneSubmitBuffer? submitBuffer = null,
        Action<string>? warningLog = null)
    {
        var delays = new List<int>();
        recordedDelays = delays;
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 5, 12, 3, 0, 0, DateTimeKind.Utc),
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            progression,
            timeProvider,
            configuration,
            apiStatus,
            static (_, _) => Task.CompletedTask);
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            static _ => { },
            timeProvider,
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                return Task.CompletedTask;
            });
        var buffer = submitBuffer ?? new TomestoneSubmitBuffer();
        var abandoner = new TomestoneLeaseAbandoner(seams);
        return new TomestoneBackgroundWorker(
            configuration,
            progression,
            apiStatus,
            timeProvider,
            workerPolicy,
            new TomestoneJobLeaseClient(seams),
            new TomestoneBatchProcessor(seams, warningLog ?? (static _ => { })),
            new TomestoneResultSubmitter(seams, buffer, abandoner),
            abandoner,
            static _ => { },
            warningLog ?? (static _ => { }),
            static _ => { },
            static _ => { },
            isPluginOperational);
    }
}
