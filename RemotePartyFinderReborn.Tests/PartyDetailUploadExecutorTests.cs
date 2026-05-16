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
            PartyDetailUploadPolicy.ComputeFingerprint(detail.MemberContentIds, detail.MemberJobs, detail.SlotFlags),
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
