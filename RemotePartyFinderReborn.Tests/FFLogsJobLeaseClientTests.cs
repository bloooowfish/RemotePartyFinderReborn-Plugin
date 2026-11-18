using System.Net;
using System.Net.Http;
using System.Text;
using System.Collections.Immutable;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsJobLeaseClientTests
{
    static FFLogsJobLeaseClientTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Job_lease_client_skips_circuit_open_endpoints_when_selecting_a_session()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"content_id\":1001,\"zone_id\":77,\"difficulty_id\":5,\"partition\":1,\"lease_token\":\"lease-a\",\"name\":\"Player One\",\"home_world_id\":1}]",
                        Encoding.UTF8,
                        "application/json"),
                })
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams);

        var openCircuit = new UploadUrl("https://circuit-open.example/");
        UploadAttemptRecorder.RecordFailure(openCircuit, timeProvider.UtcNow);
        UploadAttemptRecorder.RecordFailure(openCircuit, timeProvider.UtcNow);
        UploadAttemptRecorder.RecordFailure(openCircuit, timeProvider.UtcNow);
        var healthy = new UploadUrl("https://healthy.example/");
        var configuration = new Configuration
        {
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerBreakDurationMinutes = 1,
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [openCircuit, healthy],
        };

        var result = await leaseClient.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.NotNull(result.Session);
        Assert.Same(healthy, result.Session!.UploadUrl);
        Assert.False(result.HadTransientFailure);
        Assert.Single(sender.Requests);
        Assert.StartsWith("https://healthy.example/", sender.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Job_lease_client_warns_when_skipping_invalid_upload_url()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                })
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams);
        var invalid = new UploadUrl("http://remote.example/");
        var healthy = new UploadUrl("https://healthy.example/");
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [invalid, healthy],
        };
        var warnings = new List<string>();

        var result = await leaseClient.TryAcquireSessionAsync(
            configuration,
            CancellationToken.None,
            warningLog: warnings.Add);

        Assert.NotNull(result.Session);
        Assert.Same(healthy, result.Session!.UploadUrl);
        Assert.Contains(
            warnings,
            message => message.Contains("skipped upload target", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Remote HTTP upload URLs are blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_lease_client_invalidates_jobs_capability_on_auth_failure()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
                })
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams);
        var uploadUrl = new UploadUrl("https://secure.example/");
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities
            {
                FflogsJobs = new ProtectedEndpointCapabilityGrant
                {
                    Token = "jobs-token",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                },
            },
            null);
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };

        var result = await leaseClient.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.NotNull(result.Session);
        Assert.Same(uploadUrl, result.Session!.UploadUrl);
        Assert.Empty(result.Session.Jobs);
        Assert.False(result.HadTransientFailure);
        Assert.True(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsJobs));
    }

    [Fact]
    public async Task Job_lease_client_defers_protected_jobs_endpoint_without_transient_failure()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender();
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams);
        var uploadUrl = new UploadUrl("https://secure.example/");
        uploadUrl.RequireProtectedCapabilities(ProtectedEndpointCapabilityKind.FflogsJobs);
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };

        var result = await leaseClient.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.NotNull(result.Session);
        Assert.Same(uploadUrl, result.Session!.UploadUrl);
        Assert.Empty(result.Session.Jobs);
        Assert.True(result.Session.UseBaseDelayWhenNoWork);
        Assert.False(result.HadTransientFailure);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task Job_lease_client_treats_not_found_jobs_endpoint_as_quiet_no_work()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
                })
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams);
        var uploadUrl = new UploadUrl("https://old-server.example/");
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };

        var result = await leaseClient.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.NotNull(result.Session);
        Assert.Same(uploadUrl, result.Session!.UploadUrl);
        Assert.Empty(result.Session.Jobs);
        Assert.False(result.HadTransientFailure);
        Assert.Equal(0, uploadUrl.FailureCount);
    }

    [Fact]
    public async Task Job_lease_client_sends_worker_region_header_when_provider_resolves_region()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                })
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams, new FixedWorkerRegionProvider("JP"));
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [new UploadUrl("https://upload.example/")],
        };

        await leaseClient.TryAcquireSessionAsync(configuration, CancellationToken.None);

        var request = Assert.Single(sender.Requests);
        Assert.Equal(["JP"], request.Headers.GetValues("X-RPF-FFLogs-Worker-Region").ToArray());
    }

    [Fact]
    public async Task Job_lease_client_skips_poll_when_configured_region_provider_cannot_resolve_region()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender();
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams, new MissingWorkerRegionProvider());
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [new UploadUrl("https://upload.example/")],
        };

        var result = await leaseClient.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.Null(result.Session);
        Assert.False(result.HadTransientFailure);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task Job_lease_client_silently_skips_poll_before_worker_region_snapshot()
    {
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) };
        var sender = new StubFFLogsIngestHttpSender();
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
        var leaseClient = new FFLogsJobLeaseClient(seams, new PendingWorkerRegionProvider());
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [new UploadUrl("https://upload.example/")],
        };
        var debugLogs = new List<string>();

        var result = await leaseClient.TryAcquireSessionAsync(
            configuration,
            CancellationToken.None,
            debugLog: debugLogs.Add);

        Assert.Null(result.Session);
        Assert.False(result.HadTransientFailure);
        Assert.Empty(sender.Requests);
        Assert.Empty(debugLogs);
    }

    [Fact]
    public async Task AcquireLeaseAsync_releases_half_open_probe_on_missing_headers_deferral_401_404_and_cancellation() {
        var now = new DateTime(2026, 5, 10, 12, 30, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://lease-probe.example");
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 1,
            IngestClientId = string.Empty,
            UploadUrls = ImmutableList.Create(uploadUrl),
        };
        UploadAttemptRecorder.RecordFailure(uploadUrl, now);
        var probeNow = now.AddMinutes(1);

        var responses = new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>();
        var sender = new LeaseTestIngestHttpSender((req, ct) => responses.Dequeue()(req, ct));
        var seams = new FFLogsCollectorSeams(sender, new LeaseTestFFLogsApiClient(), new LeaseTestFFLogsTimeProvider(() => probeNow));
        var client = new FFLogsJobLeaseClient(seams);

        // 1) Missing worker headers must not claim/leak the Half-Open probe slot.
        var missingHeaderClient = new FFLogsJobLeaseClient(seams, new MissingWorkerRegionProvider());
        var noHeaders = await missingHeaderClient.TryAcquireSessionAsync(configuration, CancellationToken.None);
        Assert.Null(noHeaders.Session);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, probeNow));

        // 2) Pre-send capability deferral must not claim/leak the Half-Open probe slot.
        configuration.IngestClientId = Guid.NewGuid().ToString("N");
        uploadUrl.RequireProtectedCapabilities(ProtectedEndpointCapabilityKind.FflogsJobs);
        var deferred = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);
        Assert.NotNull(deferred.Session);
        Assert.False(deferred.Session!.HasJobs);
        Assert.True(deferred.Session.UseBaseDelayWhenNoWork);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, probeNow));

        // Reset to separate targets for 401 vs 404/OCE/Recovery (401 intentionally sets capability-required deferral).
        var authUrl = new UploadUrl("https://lease-probe-auth.example");
        configuration.UploadUrls = ImmutableList.Create(authUrl);
        UploadAttemptRecorder.RecordFailure(authUrl, now);

        // 3) 401 non-failure response releases Half-Open probe slot.
        responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var res401 = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);
        Assert.NotNull(res401.Session);
        Assert.False(res401.Session!.HasJobs);
        Assert.False(res401.HadTransientFailure);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(authUrl, configuration, probeNow));

        var activeUrl = new UploadUrl("https://lease-probe-active.example");
        configuration.UploadUrls = ImmutableList.Create(activeUrl);
        UploadAttemptRecorder.RecordFailure(activeUrl, now);

        // 4) 404 non-failure response releases Half-Open probe slot.
        responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var res404 = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);
        Assert.NotNull(res404.Session);
        Assert.False(res404.Session!.HasJobs);
        Assert.False(res404.HadTransientFailure);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(activeUrl, configuration, probeNow));

        // 5) Caller OperationCanceledException releases Half-Open probe slot so subsequent healthy attempt succeeds.
        using var cts = new CancellationTokenSource();
        responses.Enqueue((_, _) => {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.TryAcquireSessionAsync(configuration, cts.Token));
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(activeUrl, configuration, probeNow));

        // 6) Final healthy probe settles circuit to closed.
        responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("[]"),
        }));
        var resOk = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);
        Assert.False(resOk.HadTransientFailure);
        Assert.Equal(0, activeUrl.FailureCount);
        Assert.Empty(responses);
    }

    private sealed class LeaseTestIngestHttpSender(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler
    ) : IIngestHttpSender {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class LeaseTestFFLogsApiClient : IFFLogsApiClient {
        public DateTime RateLimitCooldownUntilUtc => DateTime.MinValue;
        public bool TryGetRateLimitRemaining(out TimeSpan remaining) {
            remaining = TimeSpan.Zero;
            return false;
        }
        public void ResetRateLimitCooldown() { }
        public Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
            List<FFLogsClient.CandidateCharacterQuery> queries,
            int zoneId,
            int? difficultyId,
            CancellationToken cancellationToken
        ) => Task.FromResult(OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>.Success(new Dictionary<string, FFLogsClient.CharacterFetchedData>()));
    }

    private sealed class LeaseTestFFLogsTimeProvider(Func<DateTime> utcNow) : IFFLogsTimeProvider {
        public DateTime UtcNow => utcNow();
    }

    [Fact]
    public async Task Collector_continues_results_submission_through_the_lease_session_owner()
    {
        var configuration = new Configuration
        {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerIdleDelayMs = 250,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(
                new UploadUrl("http://127.0.0.1:8000"),
                new UploadUrl("http://127.0.0.1:9000")),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 1, 0, 0, DateTimeKind.Utc),
        };
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\",\"submitted\":1,\"accepted\":1,\"updated\":0,\"rejected\":0}"),
                }),
        };
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
                                Percentile = 99.1,
                            },
                        ],
                    },
                });
            },
        };
        var seams = FFLogsCollector.CreateSeams(sender, apiClient, timeProvider);
        var workerPolicy = new FFLogsWorkerPolicy(
            configuration,
            _ => { },
            timeProvider,
            (delayMs, _) => throw new TaskCanceledException());
        var sessionOwner = configuration.UploadUrls[1];
        var fixedJobLeaseClient = new FixedSessionJobLeaseClient(
            seams,
            new FFLogsLeaseSession(sessionOwner, [CreateCollectorJob()]));

        using var collector = FFLogsCollector.CreateForTesting(
            configuration,
            seams,
            new FFLogsSubmitBuffer(),
            workerPolicy,
            fixedJobLeaseClient);

        await collector.RunWorkerLoopForTestingAsync();

        var resultsRequest = Assert.Single(sender.Requests);
        Assert.StartsWith("http://127.0.0.1:9000/contribute/fflogs/results", resultsRequest.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collector_uses_base_delay_for_quiet_jobs_endpoint_deferral()
    {
        var configuration = new Configuration
        {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            IngestClientId = Guid.NewGuid().ToString("N"),
            FFLogsWorkerBaseDelayMs = 4321,
            FFLogsWorkerIdleDelayMs = 9999,
            FFLogsWorkerJitterMs = 0,
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000")),
        };
        var timeProvider = new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 4, 15, 2, 0, 0, DateTimeKind.Utc),
        };
        var sender = new StubFFLogsIngestHttpSender();
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), timeProvider);
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
        var deferredSession = new FFLogsLeaseSession(configuration.UploadUrls[0], [], useBaseDelayWhenNoWork: true);
        var leaseClient = new FixedSessionJobLeaseClient(seams, deferredSession);

        using var collector = FFLogsCollector.CreateForTesting(
            configuration,
            seams,
            new FFLogsSubmitBuffer(),
            workerPolicy,
            leaseClient);

        await collector.RunWorkerLoopForTestingAsync();

        Assert.Equal([4321], delays);
        Assert.Empty(sender.Requests);
    }

    private static ParseJob CreateCollectorJob()
    {
        return new ParseJob
        {
            ContentId = 7001,
            Name = "Collector Test",
            Server = "Alpha",
            Region = "JP",
            ZoneId = 77,
            DifficultyId = 5,
            EncounterId = 88,
            LeaseToken = "lease-owner",
        };
    }

    private sealed class FixedSessionJobLeaseClient(
        FFLogsCollectorSeams seams,
        FFLogsLeaseSession session) : FFLogsJobLeaseClient(seams)
    {
        private bool _leased;

        public override Task<FFLogsJobLeaseAttempt> TryAcquireSessionAsync(
            Configuration configuration,
            CancellationToken cancellationToken,
            Action<string>? warningLog = null,
            Action<string>? debugLog = null)
        {
            if (_leased)
            {
                return Task.FromResult(new FFLogsJobLeaseAttempt(
                    new FFLogsLeaseSession(
                        session.UploadUrl,
                        [],
                        useBaseDelayWhenNoWork: session.UseBaseDelayWhenNoWork),
                    false));
            }

            _leased = true;
            return Task.FromResult(new FFLogsJobLeaseAttempt(session, false));
        }
    }

    private sealed class FixedWorkerRegionProvider(string region) : IFFLogsWorkerRegionProvider
    {
        public bool TryGetWorkerRegion(out string value)
        {
            value = region;
            return true;
        }
    }

    private sealed class MissingWorkerRegionProvider : IFFLogsWorkerRegionProvider
    {
        public bool TryGetWorkerRegion(out string value)
        {
            value = string.Empty;
            return false;
        }
    }

    private sealed class PendingWorkerRegionProvider : IFFLogsWorkerRegionProvider, IFFLogsWorkerRegionSnapshotState
    {
        public bool HasObservedSnapshot => false;

        public bool TryGetWorkerRegion(out string value)
        {
            throw new InvalidOperationException("Worker region should not be read before the first main-thread snapshot.");
        }
    }
}
