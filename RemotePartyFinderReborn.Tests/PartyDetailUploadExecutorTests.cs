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
