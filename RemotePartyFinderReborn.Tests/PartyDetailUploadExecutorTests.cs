using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyDetailUploadExecutorTests {
    [Fact]
    public async Task UploadBatchToAllTargetsAsync_returns_applied_outcome_for_successful_batch_item() {
        var now = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((request, _) => {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/contribute/detail/batch", request.RequestUri!.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                        {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                    ]}
                    """),
            });
        });
        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(
            CreateConfiguration(),
            httpClient,
            () => now,
            _ => { },
            _ => { },
            _ => { }
        );
        var pending = CreatePendingDetail(CreateDetail(), now);

        var outcomes = await executor.UploadBatchToAllTargetsAsync([pending]);

        Assert.Equal(1, handler.StartedCount);
        var outcome = Assert.Contains(9001U, outcomes);
        Assert.Equal(DetailUploadResult.Applied, outcome.Result);
    }

    [Fact]
    public async Task UploadBatchToAllTargetsAsync_retains_detail_capability_after_http_200_ok() {
        var now = new DateTime(2026, 5, 10, 11, 5, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "detail-token-9001",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(3)).ToUnixTimeSeconds(),
                },
            ],
            new DateTimeOffset(now)
        );
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                ]}
                """),
        }));
        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(
            CreateConfiguration(ImmutableList.Create(uploadUrl)),
            httpClient,
            () => now,
            _ => { },
            _ => { },
            _ => { }
        );

        var outcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);

        Assert.Equal(DetailUploadResult.Applied, outcomes[9001U].Result);
        Assert.True(uploadUrl.TryGetDetailCapability(9001U, out var retained, new DateTimeOffset(now)));
        Assert.Equal("detail-token-9001", retained);
    }

    [Fact]
    public async Task UploadBatchToAllTargetsAsync_marks_capabilities_required_and_defers_only_forbidden_items() {
        var now = new DateTime(2026, 5, 10, 11, 10, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "detail-token-9001",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(3)).ToUnixTimeSeconds(),
                },
                new ListingDetailCapability {
                    ListingId = 9002U,
                    Token = "stale-token-9002",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(3)).ToUnixTimeSeconds(),
                },
            ],
            new DateTimeOffset(now)
        );
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"partial","requested":2,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1},
                    {"listing_id":9002,"status":"forbidden","matched_count":0,"modified_count":0,"message":"capability resource mismatch"}
                ]}
                """),
        }));
        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(
            CreateConfiguration(ImmutableList.Create(uploadUrl)),
            httpClient,
            () => now,
            _ => { },
            _ => { },
            _ => { }
        );

        var outcomes = await executor.UploadBatchToAllTargetsAsync([
            CreatePendingDetail(CreateDetail(9001U), now),
            CreatePendingDetail(CreateDetail(9002U), now),
        ]);

        Assert.Equal(DetailUploadResult.Applied, outcomes[9001U].Result);
        Assert.Equal(DetailUploadResult.Deferred, outcomes[9002U].Result);
        Assert.True(uploadUrl.TryGetDetailCapability(9001U, out var retained9001, new DateTimeOffset(now)));
        Assert.Equal("detail-token-9001", retained9001);
        Assert.False(uploadUrl.TryGetDetailCapability(9002U, out _, new DateTimeOffset(now)));
        Assert.True(uploadUrl.ShouldDeferDetailRequest(9002U, new DateTimeOffset(now)));
        Assert.Equal(0, uploadUrl.FailureCount);
    }

    [Fact]
    public async Task UploadBatchToAllTargetsAsync_all_forbidden_batch_keeps_failure_count_honest() {
        var now = new DateTime(2026, 5, 10, 11, 15, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        UploadAttemptRecorder.RecordFailure(uploadUrl, now.AddSeconds(-5));
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"ok","requested":1,"accepted":0,"applied":0,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"forbidden","matched_count":0,"modified_count":0,"message":"missing capability token"}
                ]}
                """),
        }));
        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(
            CreateConfiguration(ImmutableList.Create(uploadUrl)),
            httpClient,
            () => now,
            _ => { },
            _ => { },
            _ => { }
        );

        var outcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);

        Assert.Equal(DetailUploadResult.Deferred, outcomes[9001U].Result);
        Assert.Equal(1, uploadUrl.FailureCount);
        Assert.True(uploadUrl.ShouldDeferDetailRequest(9001U, new DateTimeOffset(now)));
    }

    [Fact]
    public async Task UploadSingleFallback_retains_capability_on_200_and_defers_on_403() {
        var now = new DateTime(2026, 5, 10, 11, 20, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example") {
            DetailBatchUnsupported = true,
        };
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "tok-9001",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(3)).ToUnixTimeSeconds(),
                },
                new ListingDetailCapability {
                    ListingId = 9002U,
                    Token = "tok-9002",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(3)).ToUnixTimeSeconds(),
                },
            ],
            new DateTimeOffset(now)
        );
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""{"status":"ok","matched_count":1,"modified_count":1}"""),
        }));
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) {
            Content = new StringContent("forbidden"),
        }));
        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(
            CreateConfiguration(ImmutableList.Create(uploadUrl)),
            httpClient,
            () => now,
            _ => { },
            _ => { },
            _ => { }
        );

        var outcomes = await executor.UploadBatchToAllTargetsAsync([
            CreatePendingDetail(CreateDetail(9001U), now),
            CreatePendingDetail(CreateDetail(9002U), now),
        ]);

        Assert.Equal(DetailUploadResult.Applied, outcomes[9001U].Result);
        Assert.Equal(DetailUploadResult.Deferred, outcomes[9002U].Result);
        Assert.True(uploadUrl.TryGetDetailCapability(9001U, out _, new DateTimeOffset(now)));
        Assert.False(uploadUrl.TryGetDetailCapability(9002U, out _, new DateTimeOffset(now)));
        Assert.Equal(0, uploadUrl.FailureCount);
    }

    [Fact]
    public async Task HalfOpen_probe_with_detail_batch_unsupported_does_not_self_deadlock() {
        var now = new DateTime(2026, 5, 10, 11, 25, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example") {
            DetailBatchUnsupported = true,
        };
        var config = new Configuration {
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 1,
            DetailUploadTimeoutMs = 1000,
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(uploadUrl),
        };
        UploadAttemptRecorder.RecordFailure(uploadUrl, now.AddMinutes(-1));

        var handler = new StubHttpMessageHandler();
        handler.Enqueue((request, _) => {
            Assert.Equal("/contribute/detail", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"status":"ok","matched_count":1,"modified_count":1}"""),
            });
        });
        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(config, httpClient, () => now, _ => { }, _ => { }, _ => { });

        var outcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);

        Assert.Equal(1, handler.StartedCount);
        Assert.Equal(DetailUploadResult.Applied, outcomes[9001U].Result);
        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, config, now));
    }

    [Fact]
    public async Task HalfOpen_probe_releases_slot_on_batch_403_and_all_forbidden_200_and_deferred() {
        var now = new DateTime(2026, 5, 10, 11, 30, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        var config = new Configuration {
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 1,
            DetailUploadTimeoutMs = 1000,
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(uploadUrl),
        };

        var handler = new StubHttpMessageHandler();
        // 1. All-forbidden 200 OK
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"ok","requested":1,"accepted":0,"applied":0,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"forbidden","matched_count":0,"modified_count":0,"message":"missing capability token"}
                ]}
                """),
        }));
        // 2. HTTP 403
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) {
            Content = new StringContent("forbidden"),
        }));
        // 3. Subsequent probe with capability token succeeds
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                ]}
                """),
        }));

        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(config, httpClient, () => now, _ => { }, _ => { }, _ => { });

        UploadAttemptRecorder.RecordFailure(uploadUrl, now.AddMinutes(-1));
        var firstOutcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);
        Assert.Equal(DetailUploadResult.Deferred, firstOutcomes[9001U].Result);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, config, now));

        // Grant token so pre-send deferral doesn't skip, then hit HTTP 403
        uploadUrl.ApplyIngestCapabilities(null, [
            new ListingDetailCapability {
                ListingId = 9001U,
                Token = "tok-1",
                ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
            },
        ], new DateTimeOffset(now));
        var secondOutcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);
        Assert.Equal(DetailUploadResult.Deferred, secondOutcomes[9001U].Result);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, config, now));

        // Pre-send deferral (missing token) must also NOT claim or leak Half-Open probe
        var skippedOutcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);
        Assert.Equal(DetailUploadResult.Deferred, skippedOutcomes[9001U].Result);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, config, now));

        // Finally grant fresh token and verify Half-Open probe runs and resets circuit
        uploadUrl.ApplyIngestCapabilities(null, [
            new ListingDetailCapability {
                ListingId = 9001U,
                Token = "tok-2",
                ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
            },
        ], new DateTimeOffset(now));
        var finalOutcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);
        Assert.Equal(DetailUploadResult.Applied, finalOutcomes[9001U].Result);
        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.Equal(3, handler.StartedCount);
    }

    [Fact]
    public async Task UploadBatchToAllTargetsAsync_returns_circuit_blocked_when_all_targets_open_or_unavailable() {
        var now = new DateTime(2026, 5, 10, 11, 35, 0, DateTimeKind.Utc);
        var openTarget = new UploadUrl("https://open.example");
        var invalidTarget = new UploadUrl("http://remote-insecure.example");
        var config = new Configuration {
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 1,
            DetailUploadTimeoutMs = 1000,
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(openTarget, invalidTarget),
        };
        UploadAttemptRecorder.RecordFailure(openTarget, now);

        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var executor = new PartyDetailUploadExecutor(config, httpClient, () => now, _ => { }, _ => { }, _ => { });

        var outcomes = await executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(CreateDetail(9001U), now)]);

        Assert.Equal(0, handler.StartedCount);
        Assert.Equal(DetailUploadResult.CircuitBlocked, outcomes[9001U].Result);
    }

    [Fact]
    public async Task MultiUrl_applied_and_deferred_does_not_drop_deferred_target_and_skips_applied_target_on_retry() {
        var now = new DateTime(2026, 5, 10, 11, 40, 0, DateTimeKind.Utc);
        var targetA = new UploadUrl("https://target-a.example");
        var targetB = new UploadUrl("https://target-b.example");
        targetB.MarkDetailCapabilitiesRequired();

        var requestedHosts = new List<string>();
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((request, _) => {
            requestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                        {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                    ]}
                    """),
            });
        });
        handler.Enqueue((request, _) => {
            requestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                        {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                    ]}
                    """),
            });
        });

        using var httpClient = new HttpClient(handler);
        var config = CreateConfiguration(ImmutableList.Create(targetA, targetB));
        var collector = new PartyDetailCollector(
            new PartyDetailCaptureState(),
            config,
            httpClient,
            () => now,
            static (_, _) => 0
        );
        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now, PartyDetailRequestOwner.Manual);

        // First run: Target A applies, Target B is capability-deferred.
        await collector.ProcessNextDueUploadForTestingAsync();
        var pending = collector.GetPendingDetailForTesting(9001U);
        Assert.NotNull(pending);
        Assert.Equal(1, pending!.Value.DeferredCount);
        Assert.Equal(["target-a.example"], requestedHosts);

        // Grant capability on Target B and retry at +4s -> Target A must be skipped, only Target B hit!
        targetB.ApplyIngestCapabilities(null, [
            new ListingDetailCapability {
                ListingId = 9001U,
                Token = "tok-b",
                ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
            },
        ], new DateTimeOffset(now.AddSeconds(4)));
        now = now.AddSeconds(4);

        await collector.ProcessNextDueUploadForTestingAsync();
        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(["target-a.example", "target-b.example"], requestedHosts);
        Assert.Equal(1, collector.LastSuccessfulUploadAckVersion);
    }

    [Fact]
    public async Task MultiUrl_manual_deferred_plus_retryable_failure_preserves_manual_entry() {
        var now = new DateTime(2026, 5, 10, 11, 45, 0, DateTimeKind.Utc);
        var targetA = new UploadUrl("https://target-a.example");
        targetA.MarkDetailCapabilitiesRequired();
        var targetB = new UploadUrl("https://target-b.example");

        var handler = new StubHttpMessageHandler();
        // Round 1: Target B returns 500, Target A is capability-deferred.
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        // Round 2: Target A gets capability & applies; Target B also applies.
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                ]}
                """),
        }));
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                ]}
                """),
        }));

        using var httpClient = new HttpClient(handler);
        var config = CreateConfiguration(ImmutableList.Create(targetA, targetB));
        var collector = new PartyDetailCollector(
            new PartyDetailCaptureState(),
            config,
            httpClient,
            () => now,
            static (_, _) => 0
        );
        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now, PartyDetailRequestOwner.Manual);

        await collector.ProcessNextDueUploadForTestingAsync();
        var pending = collector.GetPendingDetailForTesting(9001U);
        Assert.NotNull(pending);
        Assert.Equal(1, pending!.Value.DeferredCount);
        Assert.Equal(0, pending.Value.AttemptCount);

        targetA.ApplyIngestCapabilities(null, [
            new ListingDetailCapability {
                ListingId = 9001U,
                Token = "tok-a",
                ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
            },
        ], new DateTimeOffset(now.AddSeconds(4)));
        now = now.AddSeconds(4);

        await collector.ProcessNextDueUploadForTestingAsync();
        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(1, collector.LastSuccessfulUploadAckVersion);
    }

    [Fact]
    public async Task MultiUrl_terminal_rejected_or_listing_missing_does_not_abort_healthy_pending_target() {
        var now = new DateTime(2026, 5, 10, 11, 50, 0, DateTimeKind.Utc);
        var targetA = new UploadUrl("https://target-a.example");
        var targetB = new UploadUrl("https://target-b.example");

        var requestedHosts = new List<string>();
        var handler = new StubHttpMessageHandler();
        // Round 1: Target A fails with 500, Target B returns terminal invalid.
        handler.Enqueue((request, _) => {
            requestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        handler.Enqueue((request, _) => {
            requestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {"status":"ok","requested":1,"accepted":0,"applied":0,"missing":0,"failed":0,"results":[
                        {"listing_id":9001,"status":"invalid","matched_count":0,"modified_count":0,"message":"invalid on secondary"}
                    ]}
                    """),
            });
        });
        // Round 2: Target B must be skipped; Target A retries and applies.
        handler.Enqueue((request, _) => {
            requestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                        {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                    ]}
                    """),
            });
        });

        using var httpClient = new HttpClient(handler);
        var config = CreateConfiguration(ImmutableList.Create(targetA, targetB));
        config.DetailUploadRetryCount = 2;
        var collector = new PartyDetailCollector(
            new PartyDetailCaptureState(),
            config,
            httpClient,
            () => now,
            static (_, _) => 0
        );
        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now, PartyDetailRequestOwner.Scanner);

        await collector.ProcessNextDueUploadForTestingAsync();
        var pending = collector.GetPendingDetailForTesting(9001U);
        Assert.NotNull(pending);
        Assert.Equal(1, pending!.Value.AttemptCount);
        Assert.Equal(0, collector.LastTerminalUploadAckVersion);

        now = now.AddSeconds(2);
        await collector.ProcessNextDueUploadForTestingAsync();
        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(1, collector.LastSuccessfulUploadAckVersion);
        Assert.Equal(["target-a.example", "target-b.example", "target-a.example"], requestedHosts);

        // Also verify ListingMissing + RetryableFailure keeps Manual detail alive rather than dropping it.
        var manualHandler = new StubHttpMessageHandler();
        manualHandler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        manualHandler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"partial","requested":1,"accepted":1,"applied":0,"missing":1,"failed":0,"results":[
                    {"listing_id":9002,"status":"missing","matched_count":0,"modified_count":0}
                ]}
                """),
        }));
        using var manualHttpClient = new HttpClient(manualHandler);
        var manualConfig = CreateConfiguration(ImmutableList.Create(
            new UploadUrl("https://primary.example"),
            new UploadUrl("https://secondary.example")));
        var manualCollector = new PartyDetailCollector(
            new PartyDetailCaptureState(),
            manualConfig,
            manualHttpClient,
            () => now,
            static (_, _) => 0
        );
        manualCollector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now, PartyDetailRequestOwner.Manual);

        await manualCollector.ProcessNextDueUploadForTestingAsync();
        var manualPending = manualCollector.GetPendingDetailForTesting(9002U);
        Assert.NotNull(manualPending);
        Assert.Equal(1, manualPending!.Value.DeferredCount);
        Assert.Equal(0, manualPending.Value.AttemptCount);
    }

    private static Configuration CreateConfiguration(ImmutableList<UploadUrl>? uploadUrls = null) {
        return new Configuration {
            DetailUploadTimeoutMs = 1000,
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = uploadUrls ?? ImmutableList.Create(new UploadUrl("https://upload.example")),
        };
    }

    private static PendingDetailEntry CreatePendingDetail(UploadablePartyDetail detail, DateTime now) {
        return new PendingDetailEntry(
            detail,
            PartyDetailUploadPolicy.ComputeFingerprint(detail),
            PartyDetailRequestOwner.Manual,
            now,
            0,
            now
        );
    }

    private static UploadablePartyDetail CreateDetail(uint listingId = 9001U) {
        return new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 77,
            MemberContentIds = [44UL],
            MemberJobs = [19],
            SlotFlags = ["0x0000000000000001"],
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        internal int StartedCount { get; private set; }

        internal void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) {
            _responses.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            StartedCount++;
            return _responses.Dequeue()(request, cancellationToken);
        }
    }
}
