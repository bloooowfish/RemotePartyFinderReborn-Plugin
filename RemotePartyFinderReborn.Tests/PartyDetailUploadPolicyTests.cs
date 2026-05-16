using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyDetailUploadPolicyTests {
    [Theory]
    [InlineData(-1, 5000)]
    [InlineData(0, 5000)]
    [InlineData(100, 1000)]
    [InlineData(4500, 4500)]
    [InlineData(60000, 30000)]
    public void Upload_timeout_is_bounded(int configuredMs, int expectedMs) {
        Assert.Equal(expectedMs, PartyDetailUploadPolicy.NormalizeDetailUploadTimeoutMs(configuredMs));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(8, 4)]
    public void Upload_concurrency_is_bounded(int configured, int expected) {
        Assert.Equal(expected, PartyDetailUploadPolicy.NormalizeDetailUploadConcurrency(configured));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(64, 16)]
    public void Detail_batch_size_is_bounded(int configured, int expected) {
        Assert.Equal(expected, PartyDetailUploadPolicy.NormalizeDetailUploadBatchSize(configured));
    }

    [Theory]
    [InlineData(null, 1, 2100)]
    [InlineData(0, 1, 100)]
    [InlineData(12, 1, 12100)]
    [InlineData(60, 1, 30100)]
    public void Next_attempt_uses_retry_after_when_available(int? retryAfterSeconds, int attemptCount, int expectedDelayMs) {
        var now = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        var next = PartyDetailUploadPolicy.ComputeNextAttemptAtUtc(
            now,
            attemptCount,
            retryAfterSeconds,
            static (_, _) => 100
        );

        Assert.Equal(now.AddMilliseconds(expectedDelayMs), next);
    }

    [Fact]
    public async Task Rate_limited_detail_upload_uses_retry_after_for_next_attempt() {
        var now = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                Content = new StringContent("too many requests"),
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "12");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0);
        var detail = CreateDetail();

        collector.EnqueuePendingDetailForTesting(detail, now);
        await collector.ProcessNextDueUploadForTestingAsync();

        var pending = collector.GetPendingDetailForTesting(detail.ListingId).GetValueOrDefault();
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(now.AddSeconds(12), pending.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Multiple_rate_limited_targets_use_longest_retry_after_for_next_attempt() {
        var now = new DateTime(2026, 5, 10, 1, 30, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(CreateRateLimitedResponse(5)));
        handler.Enqueue((_, _) => Task.FromResult(CreateRateLimitedResponse(12)));
        using var httpClient = new HttpClient(handler);
        var configuration = new Configuration {
            DetailUploadRetryCount = 2,
            DetailUploadTimeoutMs = 1000,
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(
                new UploadUrl("https://upload-a.example"),
                new UploadUrl("https://upload-b.example")
            ),
        };
        var collector = new PartyDetailCollector(
            new PartyDetailCaptureState(),
            configuration,
            httpClient,
            () => now,
            static (_, _) => 0
        );
        var detail = CreateDetail();

        collector.EnqueuePendingDetailForTesting(detail, now);
        await collector.ProcessNextDueUploadForTestingAsync();

        var pending = collector.GetPendingDetailForTesting(detail.ListingId).GetValueOrDefault();
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(now.AddSeconds(12), pending.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Timed_out_detail_upload_is_retryable_and_uses_normal_backoff() {
        var now = new DateTime(2026, 5, 10, 2, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, cancellationToken) => {
            Assert.True(cancellationToken.CanBeCanceled);
            throw new OperationCanceledException(cancellationToken);
        });
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0);
        var detail = CreateDetail();

        collector.EnqueuePendingDetailForTesting(detail, now);
        await collector.ProcessNextDueUploadForTestingAsync();

        var pending = collector.GetPendingDetailForTesting(detail.ListingId).GetValueOrDefault();
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(now.AddSeconds(2), pending.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Non_rate_limited_failure_uses_normal_exponential_backoff() {
        var now = new DateTime(2026, 5, 10, 3, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("server error"),
        }));
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0);
        var detail = CreateDetail();

        collector.EnqueuePendingDetailForTesting(detail, now);
        await collector.ProcessNextDueUploadForTestingAsync();

        var pending = collector.GetPendingDetailForTesting(detail.ListingId).GetValueOrDefault();
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(now.AddSeconds(2), pending.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Bounded_concurrency_starts_two_due_uploads_before_either_response_completes() {
        var now = new DateTime(2026, 5, 10, 4, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        var firstResponse = handler.EnqueueControlledResponse();
        var secondResponse = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, maxConcurrency: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(2);

        Assert.Equal(2, handler.MaxConcurrentRequests);
        Assert.Equal(2, collector.ActiveUploadWorkerCountForTesting);
        Assert.Equal(2, collector.InFlightDetailCountForTesting);

        firstResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        secondResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.PendingQueueCount == 0 && collector.ActiveUploadWorkerCountForTesting == 0);
    }

    [Fact]
    public async Task Same_listing_is_not_uploaded_concurrently_when_newer_payload_is_queued() {
        var now = new DateTime(2026, 5, 10, 4, 30, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        var firstResponse = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, maxConcurrency: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U, slotFlag: "0x1"), now);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(1);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U, slotFlag: "0x2"), now);
        collector.PumpDueUploadsForTesting();

        Assert.Equal(1, handler.StartedCount);
        Assert.Equal(1, collector.ActiveUploadWorkerCountForTesting);
        Assert.Equal(1, collector.InFlightDetailCountForTesting);

        firstResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.ActiveUploadWorkerCountForTesting == 0);
    }

    [Fact]
    public async Task Old_in_flight_success_does_not_remove_newer_pending_payload() {
        var now = new DateTime(2026, 5, 10, 5, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        var firstResponse = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, maxConcurrency: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U, slotFlag: "0x1"), now);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(1);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U, slotFlag: "0x2"), now);
        firstResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.ActiveUploadWorkerCountForTesting == 0);

        Assert.Equal(1, collector.PendingQueueCount);
    }

    [Fact]
    public async Task Pump_refills_after_success_when_capacity_is_available() {
        var now = new DateTime(2026, 5, 10, 5, 30, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        var firstResponse = handler.EnqueueControlledResponse();
        var secondResponse = handler.EnqueueControlledResponse();
        var thirdResponse = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, maxConcurrency: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now.AddSeconds(-2));
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now.AddSeconds(-1));
        collector.EnqueuePendingDetailForTesting(CreateDetail(9003U), now);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(2);

        firstResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.ActiveUploadWorkerCountForTesting == 1);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(3);

        Assert.Equal(2, collector.ActiveUploadWorkerCountForTesting);
        Assert.Equal(2, handler.MaxConcurrentRequests);

        secondResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        thirdResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.PendingQueueCount == 0 && collector.ActiveUploadWorkerCountForTesting == 0);
    }

    [Fact]
    public async Task Pump_refills_after_failure_when_capacity_is_available() {
        var now = new DateTime(2026, 5, 10, 6, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        var firstResponse = handler.EnqueueControlledResponse();
        var secondResponse = handler.EnqueueControlledResponse();
        var thirdResponse = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, maxConcurrency: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now.AddSeconds(-2));
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now.AddSeconds(-1));
        collector.EnqueuePendingDetailForTesting(CreateDetail(9003U), now);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(2);

        firstResponse.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("server error"),
        });
        await WaitUntilAsync(() => collector.ActiveUploadWorkerCountForTesting == 1);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(3);

        Assert.Equal(2, collector.ActiveUploadWorkerCountForTesting);
        Assert.Equal(2, handler.MaxConcurrentRequests);

        secondResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        thirdResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.ActiveUploadWorkerCountForTesting == 0);
        Assert.Equal(1, collector.PendingQueueCount);
    }

    [Fact]
    public async Task Concurrent_failures_record_each_target_failure() {
        var now = new DateTime(2026, 5, 10, 6, 30, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        var firstResponse = handler.EnqueueControlledResponse();
        var secondResponse = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var uploadUrl = new UploadUrl("https://upload.example");
        var collector = CreateCollector(
            httpClient,
            () => now,
            static (_, _) => 0,
            maxConcurrency: 2,
            uploadUrls: ImmutableList.Create(uploadUrl)
        );

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        collector.PumpDueUploadsForTesting();
        await handler.WaitForStartedCountAsync(2);

        firstResponse.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("server error"),
        });
        secondResponse.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("server error"),
        });
        await WaitUntilAsync(() => collector.ActiveUploadWorkerCountForTesting == 0);

        Assert.Equal(2, uploadUrl.FailureCount);
    }

    [Fact]
    public async Task Batch_upload_posts_detail_batch_endpoint_and_removes_applied_items() {
        var now = new DateTime(2026, 5, 10, 7, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(async (request, cancellationToken) => {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/contribute/detail/batch", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var items = JArray.Parse(body);
            Assert.Equal([9001U, 9002U], items.Select(item => item.Value<uint>("listing_id")).ToArray());

            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {"status":"ok","requested":2,"accepted":2,"applied":2,"missing":0,"failed":0,"results":[
                        {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1},
                        {"listing_id":9002,"status":"ok","matched_count":1,"modified_count":1}
                    ]}
                    """),
            };
        });
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, batchSize: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(2, collector.LastSuccessfulUploadAckVersion);
    }

    [Theory]
    [InlineData("""{"status":"ok","requested":2,"accepted":2,"applied":2,"missing":0,"failed":0,"results":[]}""")]
    [InlineData("""{"status":"ok","requested":2""")]
    public async Task Batch_response_without_item_results_retries_sent_items(string responseBody) {
        var now = new DateTime(2026, 5, 10, 7, 15, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(responseBody),
        }));
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, batchSize: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        var first = collector.GetPendingDetailForTesting(9001U).GetValueOrDefault();
        var second = collector.GetPendingDetailForTesting(9002U).GetValueOrDefault();
        Assert.Equal(2, collector.PendingQueueCount);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(1, second.AttemptCount);
        Assert.Equal(now.AddSeconds(2), first.NextAttemptAtUtc);
        Assert.Equal(now.AddSeconds(2), second.NextAttemptAtUtc);
        Assert.Equal(0, collector.LastSuccessfulUploadAckVersion);
    }

    [Fact]
    public async Task Mixed_batch_results_remove_terminal_items_and_retry_failed_items() {
        var now = new DateTime(2026, 5, 10, 7, 30, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"partial","requested":3,"accepted":3,"applied":1,"missing":1,"failed":1,"results":[
                    {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1},
                    {"listing_id":9002,"status":"missing","matched_count":0,"modified_count":0},
                    {"listing_id":9003,"status":"failed","matched_count":0,"modified_count":0}
                ]}
                """),
        }));
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, batchSize: 3);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9003U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.Equal(1, collector.PendingQueueCount);
        Assert.Null(collector.GetPendingDetailForTesting(9001U));
        Assert.Null(collector.GetPendingDetailForTesting(9002U));
        var failed = collector.GetPendingDetailForTesting(9003U).GetValueOrDefault();
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(now.AddSeconds(2), failed.NextAttemptAtUtc);
        Assert.Equal(1, collector.LastSuccessfulUploadAckVersion);
        Assert.Equal(1, collector.LastTerminalUploadAckVersion);
    }

    [Fact]
    public async Task Invalid_and_forbidden_batch_results_are_terminal_without_retry() {
        var now = new DateTime(2026, 5, 10, 8, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""
                {"status":"ok","requested":2,"accepted":0,"applied":0,"missing":0,"failed":0,"results":[
                    {"listing_id":9001,"status":"invalid","matched_count":0,"modified_count":0,"message":"too many members in request"},
                    {"listing_id":9002,"status":"forbidden","matched_count":0,"modified_count":0,"message":"capability resource mismatch"}
                ]}
                """),
        }));
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, batchSize: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(0, collector.LastSuccessfulUploadAckVersion);
        Assert.Equal(2, collector.LastTerminalUploadAckVersion);
    }

    [Fact]
    public async Task Batch_not_supported_falls_back_to_single_upload_without_recording_failure() {
        var now = new DateTime(2026, 5, 10, 8, 30, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((request, _) => {
            Assert.Equal("/contribute/detail/batch", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) {
                Content = new StringContent("not found"),
            });
        });
        handler.Enqueue(async (request, cancellationToken) => {
            Assert.Equal("/contribute/detail", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal(9001U, JObject.Parse(body).Value<uint>("listing_id"));
            return CreateAppliedResponse();
        });
        var uploadUrl = new UploadUrl("https://upload.example");
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(
            httpClient,
            () => now,
            static (_, _) => 0,
            batchSize: 1,
            uploadUrls: ImmutableList.Create(uploadUrl)
        );

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.True(uploadUrl.DetailBatchUnsupported);
        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(2, handler.StartedCount);
    }

    [Fact]
    public async Task Protected_batch_fallback_uses_single_detail_capability_header() {
        var now = new DateTime(2026, 5, 10, 8, 45, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((request, _) => {
            Assert.Equal("/contribute/detail/batch", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) {
                Content = new StringContent("method not allowed"),
            });
        });
        handler.Enqueue((request, _) => {
            Assert.Equal("/contribute/detail", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.TryGetValues("X-RPF-Capability", out var values));
            Assert.Equal("detail-token-9001", Assert.Single(values));
            return Task.FromResult(CreateAppliedResponse());
        });
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.MarkDetailCapabilitiesRequired();
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "detail-token-9001",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
                },
            ],
            new DateTimeOffset(now)
        );
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(
            httpClient,
            () => now,
            static (_, _) => 0,
            batchSize: 1,
            uploadUrls: ImmutableList.Create(uploadUrl)
        );

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.True(uploadUrl.DetailBatchUnsupported);
        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(2, handler.StartedCount);
    }

    [Fact]
    public async Task Protected_batch_includes_body_capability_tokens_and_skips_missing_tokens() {
        var now = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(async (request, cancellationToken) => {
            Assert.False(request.Headers.Contains("X-RPF-Capability"));
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var items = JArray.Parse(body);
            var item = Assert.Single(items);
            Assert.Equal(9001U, item.Value<uint>("listing_id"));
            Assert.Equal("detail-token-9001", item.Value<string>("capability_token"));

            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                        {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
                    ]}
                    """),
            };
        });
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.MarkDetailCapabilitiesRequired();
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "detail-token-9001",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
                },
            ],
            new DateTimeOffset(now)
        );
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(
            httpClient,
            () => now,
            static (_, _) => 0,
            batchSize: 2,
            uploadUrls: ImmutableList.Create(uploadUrl)
        );

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.Null(collector.GetPendingDetailForTesting(9001U));
        var skipped = collector.GetPendingDetailForTesting(9002U).GetValueOrDefault();
        Assert.Equal(0, skipped.AttemptCount);
        Assert.Equal(now, skipped.NextAttemptAtUtc);
        Assert.Equal(1, collector.PendingQueueCount);
        Assert.Equal(1, handler.StartedCount);
    }

    [Fact]
    public void Queue_debug_state_reports_capability_deferred_items() {
        var now = new DateTime(2026, 5, 10, 9, 10, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.MarkDetailCapabilitiesRequired();
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(
            httpClient,
            () => now,
            static (_, _) => 0,
            uploadUrls: ImmutableList.Create(uploadUrl)
        );

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now, PartyDetailRequestOwner.Scanner);

        var state = collector.GetUploadQueueDebugState();
        Assert.Equal(1, state.PendingCount);
        Assert.Equal(1, state.DueCount);
        Assert.Equal(0, state.EligibleNowCount);
        Assert.Equal(1, state.CapabilityDeferredCount);
        Assert.Equal(0, state.CircuitBlockedCount);
    }

    [Fact]
    public async Task Manual_capability_deferred_detail_is_dropped_instead_of_persisting_pending_queue() {
        var now = new DateTime(2026, 5, 10, 9, 15, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.MarkDetailCapabilitiesRequired();
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(
            httpClient,
            () => now,
            static (_, _) => 0,
            uploadUrls: ImmutableList.Create(uploadUrl)
        );

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now, PartyDetailRequestOwner.Manual);

        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(0, handler.StartedCount);
    }

    [Fact]
    public async Task Manual_retryable_failure_is_dropped_instead_of_scheduled_for_retry() {
        var now = new DateTime(2026, 5, 10, 9, 17, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("server error"),
        }));
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now, PartyDetailRequestOwner.Manual);

        await collector.ProcessNextDueUploadForTestingAsync();

        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(1, handler.StartedCount);
    }

    [Fact]
    public async Task Manual_retry_reports_in_flight_state_when_pending_count_is_unchanged() {
        var now = new DateTime(2026, 5, 10, 9, 20, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        var response = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now.AddSeconds(10));

        var result = collector.TriggerPendingDetailUploadNowWithState();

        Assert.Equal(1, result.TriggeredCount);
        Assert.Equal(1, result.QueueState.PendingCount);
        Assert.Equal(1, result.QueueState.InFlightCount);
        Assert.Equal(1, result.QueueState.ActiveWorkerCount);

        response.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.PendingQueueCount == 0 && collector.ActiveUploadWorkerCountForTesting == 0);
    }

    [Fact]
    public async Task Batch_transport_failure_schedules_retry_after_for_every_sent_item() {
        var now = new DateTime(2026, 5, 10, 9, 30, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(CreateRateLimitedResponse(12)));
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0, batchSize: 2);

        collector.EnqueuePendingDetailForTesting(CreateDetail(9001U), now);
        collector.EnqueuePendingDetailForTesting(CreateDetail(9002U), now);
        await collector.ProcessNextDueUploadForTestingAsync();

        var first = collector.GetPendingDetailForTesting(9001U).GetValueOrDefault();
        var second = collector.GetPendingDetailForTesting(9002U).GetValueOrDefault();
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(1, second.AttemptCount);
        Assert.Equal(now.AddSeconds(12), first.NextAttemptAtUtc);
        Assert.Equal(now.AddSeconds(12), second.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Manual_retry_makes_pending_detail_due_and_resets_attempt_count() {
        var now = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(CreateRateLimitedResponse(12)));
        var manualResponse = handler.EnqueueControlledResponse();
        using var httpClient = new HttpClient(handler);
        var collector = CreateCollector(httpClient, () => now, static (_, _) => 0);
        var detail = CreateDetail();

        collector.EnqueuePendingDetailForTesting(detail, now);
        await collector.ProcessNextDueUploadForTestingAsync();

        var pendingAfterFailure = collector.GetPendingDetailForTesting(detail.ListingId).GetValueOrDefault();
        Assert.Equal(1, pendingAfterFailure.AttemptCount);
        Assert.Equal(now.AddSeconds(12), pendingAfterFailure.NextAttemptAtUtc);

        now = now.AddSeconds(1);
        var triggered = collector.TriggerPendingDetailUploadNow();
        await handler.WaitForStartedCountAsync(2);

        var pendingAfterManualTrigger = collector.GetPendingDetailForTesting(detail.ListingId).GetValueOrDefault();
        Assert.Equal(1, triggered);
        Assert.Equal(0, pendingAfterManualTrigger.AttemptCount);
        Assert.Equal(now, pendingAfterManualTrigger.NextAttemptAtUtc);
        Assert.Equal(1, collector.InFlightDetailCountForTesting);

        manualResponse.SetResult(CreateBatchAppliedResponseForRequestAsync);
        await WaitUntilAsync(() => collector.PendingQueueCount == 0 && collector.ActiveUploadWorkerCountForTesting == 0);

        Assert.Equal(0, collector.PendingQueueCount);
        Assert.Equal(2, handler.StartedCount);
    }

    private static PartyDetailCollector CreateCollector(
        HttpClient httpClient,
        Func<DateTime> utcNow,
        Func<int, int, int> nextJitterMs,
        int maxConcurrency = 1,
        int batchSize = 1,
        ImmutableList<UploadUrl>? uploadUrls = null
    ) {
        var uploadUrl = new UploadUrl("https://upload.example");
        var configuration = new Configuration {
            DetailUploadRetryCount = 2,
            DetailUploadTimeoutMs = 1000,
            DetailUploadMaxConcurrency = maxConcurrency,
            DetailUploadBatchSize = batchSize,
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = uploadUrls ?? ImmutableList.Create(uploadUrl),
        };

        return new PartyDetailCollector(
            new PartyDetailCaptureState(),
            configuration,
            httpClient,
            utcNow,
            nextJitterMs
        );
    }

    private static UploadablePartyDetail CreateDetail(uint listingId = 9001U, string slotFlag = "0x0000000000000001") {
        return new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            MemberContentIds = [44UL],
            MemberJobs = [19],
            SlotFlags = [slotFlag],
        };
    }

    private static HttpResponseMessage CreateAppliedResponse() {
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""{"status":"ok","matched_count":1,"modified_count":1}"""),
        };
    }

    private static HttpResponseMessage CreateBatchAppliedResponse(uint listingId) {
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent($$"""
                {"status":"ok","requested":1,"accepted":1,"applied":1,"missing":0,"failed":0,"results":[
                    {"listing_id":{{listingId}},"status":"ok","matched_count":1,"modified_count":1}
                ]}
                """),
        };
    }

    private static async Task<HttpResponseMessage> CreateBatchAppliedResponseForRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var item = Assert.Single(JArray.Parse(body));
        return CreateBatchAppliedResponse(item.Value<uint>("listing_id"));
    }

    private static HttpResponseMessage CreateRateLimitedResponse(int retryAfterSeconds) {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
            Content = new StringContent("too many requests"),
        };
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.ToString());
        return response;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
        private readonly object _lock = new();
        private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRequests;

        internal int StartedCount { get; private set; }
        internal int MaxConcurrentRequests { get; private set; }

        internal void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) {
            lock (_lock) {
                _responses.Enqueue(response);
            }
        }

        internal ControlledResponse EnqueueControlledResponse() {
            var response = new ControlledResponse();
            Enqueue(response.WaitAsync);
            return response;
        }

        internal async Task WaitForStartedCountAsync(int expected) {
            while (true) {
                Task waitTask;
                lock (_lock) {
                    if (StartedCount >= expected) {
                        return;
                    }

                    waitTask = _started.Task;
                }

                await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            lock (_lock) {
                StartedCount++;
                _activeRequests++;
                MaxConcurrentRequests = Math.Max(MaxConcurrentRequests, _activeRequests);
                _started.TrySetResult();
                _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try {
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response;
                lock (_lock) {
                    response = _responses.Dequeue();
                }

                return await response(request, cancellationToken);
            } finally {
                lock (_lock) {
                    _activeRequests--;
                }
            }
        }
    }

    private sealed class ControlledResponse {
        private readonly TaskCompletionSource<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task<HttpResponseMessage> WaitAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var response = await _response.Task.WaitAsync(cancellationToken);
            return await response(request, cancellationToken);
        }

        internal void SetResult(HttpResponseMessage response) {
            _response.SetResult((_, _) => Task.FromResult(response));
        }

        internal void SetResult(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) {
            _response.SetResult(response);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) {
            await Task.Delay(10, timeout.Token);
        }
    }
}
