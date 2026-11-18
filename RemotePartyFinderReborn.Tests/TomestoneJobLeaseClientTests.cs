using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneJobLeaseClientTests
{
    static TomestoneJobLeaseClientTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Lease_client_sends_tomestone_worker_headers_and_deserializes_jobs()
    {
        HttpRequestMessage? capturedRequest = null;
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        [
                          {
                            "content_id": 123456,
                            "name": "Alpha Omega",
                            "server": "Tonberry",
                            "region": "JP",
                            "zone_id": 65,
                            "difficulty_id": 0,
                            "encounter_id": 1079,
                            "lease_token": "ts-lease-1"
                          },
                          {
                            "content_id": 123457,
                            "name": "Lindwurm Runner",
                            "server": "Tonberry",
                            "region": "JP",
                            "zone_id": 73,
                            "difficulty_id": 101,
                            "encounter_id": 104,
                            "secondary_encounter_id": 105,
                            "lease_token": "ts-lease-2"
                          }
                        ]
                        """,
                        Encoding.UTF8,
                        "application/json"),
                });
            },
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc) });
        var regionProvider = new StaticWorkerRegionProvider { HasObservedSnapshot = true, Region = "JP" };
        var client = new TomestoneJobLeaseClient(seams, regionProvider);
        var configuration = CreateConfiguration("https://upload.example/");

        var attempt = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.False(attempt.HadTransientFailure);
        Assert.NotNull(attempt.Session);
        Assert.True(attempt.Session.HasJobs);
        Assert.Equal(2, attempt.Session.Jobs.Count);
        var job = attempt.Session.Jobs[0];
        Assert.Equal(123456UL, job.ContentId);
        Assert.Equal("Alpha Omega", job.Name);
        Assert.Equal("Tonberry", job.Server);
        Assert.Equal(65u, job.ZoneId);
        Assert.Equal(1079u, job.EncounterId);
        Assert.Null(job.SecondaryEncounterId);
        Assert.Equal("ts-lease-1", job.LeaseToken);

        var splitJob = attempt.Session.Jobs[1];
        Assert.Equal(123457UL, splitJob.ContentId);
        Assert.Equal(73u, splitJob.ZoneId);
        Assert.Equal(104u, splitJob.EncounterId);
        Assert.Equal(105u, splitJob.SecondaryEncounterId);
        Assert.Equal("ts-lease-2", splitJob.LeaseToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal("/contribute/tomestone/jobs", capturedRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("dalamud_plugin_worker", Assert.Single(capturedRequest.Headers.GetValues("X-RPF-Tomestone-Processor-Kind")));
        Assert.Equal("exact-only", Assert.Single(capturedRequest.Headers.GetValues("X-RPF-Tomestone-Job-Mode")));
        Assert.Equal("JP", Assert.Single(capturedRequest.Headers.GetValues("X-RPF-Tomestone-Worker-Region")));
        Assert.True(capturedRequest.Headers.Contains("X-RPF-Signature"));
        Assert.Equal("v2", Assert.Single(capturedRequest.Headers.GetValues("X-RPF-Signature-Version")));
        Assert.Equal("upload.example", Assert.Single(capturedRequest.Headers.GetValues("X-RPF-Target-Host")));
    }

    [Fact]
    public async Task Lease_client_deserializes_server_manifest_tomestone_routing_metadata()
    {
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    [
                      {
                        "content_id": 555555,
                        "name": "Future Raider",
                        "server": "Tonberry",
                        "region": "JP",
                        "zone_id": 999,
                        "difficulty_id": 101,
                        "encounter_id": 9901,
                        "secondary_encounter_id": 9902,
                        "zone_encounters": [9900, 9901, 9902],
                        "tomestone_expansion": "dawntrail",
                        "tomestone_category": "raids",
                        "tomestone_clears_category": "savage",
                        "tomestone_zone_slug": "aac-future-savage",
                        "tomestone_encounter_slug": "future-p1",
                        "tomestone_encounter_names": ["future-p1"],
                        "tomestone_secondary_encounter_slug": "future-p2",
                        "tomestone_secondary_encounter_names": ["future-p2"],
                        "tomestone_progress_kind": "boss_percentage",
                        "lease_token": "ts-future-1"
                      }
                    ]
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }),
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc) });
        var client = new TomestoneJobLeaseClient(seams);

        var attempt = await client.TryAcquireSessionAsync(
            CreateConfiguration("https://upload.example/"),
            CancellationToken.None);

        var job = Assert.Single(attempt.Session!.Jobs);
        Assert.Equal([9900u, 9901u, 9902u], job.ZoneEncounters);
        Assert.Equal("dawntrail", job.TomestoneExpansion);
        Assert.Equal("raids", job.TomestoneCategory);
        Assert.Equal("savage", job.TomestoneClearsCategory);
        Assert.Equal("aac-future-savage", job.TomestoneZoneSlug);
        Assert.Equal("future-p1", job.TomestoneEncounterSlug);
        Assert.Equal(["future-p1"], job.TomestoneEncounterNames);
        Assert.Equal("future-p2", job.TomestoneSecondaryEncounterSlug);
        Assert.Equal(["future-p2"], job.TomestoneSecondaryEncounterNames);
        Assert.Equal("boss_percentage", job.TomestoneProgressKind);
    }

    [Theory]
    [InlineData(false, "JP")]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public async Task Lease_client_skips_http_when_worker_region_is_unobserved_or_unavailable(
        bool hasObservedSnapshot,
        string region)
    {
        var sender = new StubFFLogsIngestHttpSender();
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider());
        var regionProvider = new StaticWorkerRegionProvider
        {
            HasObservedSnapshot = hasObservedSnapshot,
            Region = region,
        };
        var client = new TomestoneJobLeaseClient(seams, regionProvider);

        var attempt = await client.TryAcquireSessionAsync(
            CreateConfiguration("https://upload.example/"),
            CancellationToken.None);

        Assert.Null(attempt.Session);
        Assert.False(attempt.HadTransientFailure);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task Lease_client_skips_http_when_tomestone_capability_is_required_and_missing()
    {
        var sender = new StubFFLogsIngestHttpSender();
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider { UtcNow = new DateTime(2026, 5, 12, 1, 0, 0, DateTimeKind.Utc) });
        var client = new TomestoneJobLeaseClient(seams);
        var configuration = CreateConfiguration("https://secure.example/");
        configuration.UploadUrls[0].RequireProtectedCapabilities(ProtectedEndpointCapabilityKind.TomestoneJobs);

        var attempt = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.NotNull(attempt.Session);
        Assert.False(attempt.Session.HasJobs);
        Assert.True(attempt.Session.UseBaseDelayWhenNoWork);
        Assert.False(attempt.HadTransientFailure);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task Lease_client_403_requires_only_tomestone_capabilities_and_does_not_open_fflogs()
    {
        var now = new DateTime(2026, 5, 12, 2, 0, 0, DateTimeKind.Utc);
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden"),
            }),
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider { UtcNow = now });
        var client = new TomestoneJobLeaseClient(seams);
        var configuration = CreateConfiguration("https://secure.example/");

        var attempt = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);

        Assert.NotNull(attempt.Session);
        Assert.False(attempt.HadTransientFailure);
        var uploadUrl = configuration.UploadUrls[0];
        Assert.True(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.TomestoneJobs, new DateTimeOffset(now)));
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsJobs, new DateTimeOffset(now)));
    }

    [Fact]
    public async Task Lease_client_treats_404_as_non_transient_empty_session_and_500_as_transient()
    {
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider());
        var client = new TomestoneJobLeaseClient(seams);
        var configuration = CreateConfiguration("https://upload.example/");

        var notFoundAttempt = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);
        Assert.NotNull(notFoundAttempt.Session);
        Assert.False(notFoundAttempt.Session.HasJobs);
        Assert.False(notFoundAttempt.HadTransientFailure);

        sender.OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var serverErrorAttempt = await client.TryAcquireSessionAsync(configuration, CancellationToken.None);
        Assert.NotNull(serverErrorAttempt.Session);
        Assert.True(serverErrorAttempt.HadTransientFailure);
    }

    private static Configuration CreateConfiguration(string url) => new()
    {
        IngestClientId = Guid.NewGuid().ToString("N"),
        UploadUrls = ImmutableList.Create(new UploadUrl(url)),
    };

    private sealed class StaticWorkerRegionProvider : IFFLogsWorkerRegionProvider, IFFLogsWorkerRegionSnapshotState
    {
        public bool HasObservedSnapshot { get; init; }
        public string Region { get; init; } = string.Empty;

        public bool TryGetWorkerRegion(out string region)
        {
            region = Region;
            return !string.IsNullOrWhiteSpace(region);
        }
    }
}
