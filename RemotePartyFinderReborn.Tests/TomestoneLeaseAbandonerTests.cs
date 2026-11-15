using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneLeaseAbandonerTests
{
    static TomestoneLeaseAbandonerTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Abandon_leases_posts_snake_case_payload_with_tomestone_defaults_and_deduplicates()
    {
        string? capturedPayload = null;
        HttpRequestMessage? capturedRequest = null;
        var warnings = new List<string>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                capturedRequest = request;
                capturedPayload = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","submitted":1,"released":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                };
            },
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider());
        var abandoner = new TomestoneLeaseAbandoner(seams);
        var uploadUrl = new UploadUrl("https://upload.example/");
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(uploadUrl),
        };

        var job = new TomestoneJob
        {
            ContentId = 7001,
            ZoneId = 73,
            DifficultyId = 0,
            EncounterId = 101,
            LeaseToken = "lease-7001",
        };
        var entry = TomestoneLeaseAbandoner.CreateJobAbandonment(
            job,
            "worker_rate_limit_deferred",
            "tomestone_rate_limited",
            "Deferred due to rate limit.");
        var duplicate = TomestoneLeaseAbandoner.CreateJobAbandonment(
            job,
            "worker_rate_limit_deferred",
            "tomestone_rate_limited",
            "Duplicate.");
        var missingToken = TomestoneLeaseAbandoner.CreateJobAbandonment(
            new TomestoneJob { ContentId = 7002, ZoneId = 73, EncounterId = 102, LeaseToken = " " },
            "worker_skipped",
            "tomestone_skipped",
            "No token");

        await abandoner.AbandonLeasesAsync(
            configuration,
            uploadUrl,
            [entry, duplicate, missingToken],
            CancellationToken.None,
            warnings.Add,
            static _ => { });

        Assert.NotNull(capturedRequest);
        Assert.Equal("/contribute/tomestone/leases/abandon", capturedRequest!.RequestUri!.AbsolutePath);
        Assert.Single(warnings);

        var array = JArray.Parse(capturedPayload!);
        var payloadObj = Assert.IsType<JObject>(Assert.Single(array));
        Assert.Equal(7001UL, payloadObj["content_id"]!.Value<ulong>());
        Assert.Equal(73u, payloadObj["zone_id"]!.Value<uint>());
        Assert.Equal(101u, payloadObj["encounter_id"]!.Value<uint>());
        Assert.Equal("lease-7001", payloadObj["lease_token"]!.Value<string>());
        Assert.Equal("worker_rate_limit_deferred", payloadObj["reason"]!.Value<string>());
        Assert.Equal("dalamud_plugin_worker", payloadObj["processor_kind"]!.Value<string>());
        Assert.Equal("tomestone_api", payloadObj["source_kind"]!.Value<string>());
        Assert.Equal("tomestone_rate_limited", payloadObj["failure_code"]!.Value<string>());
        Assert.Equal("tomestone_rate_limited:73:0:101", payloadObj["failure_fingerprint"]!.Value<string>());
    }

    [Fact]
    public async Task Abandon_result_leases_sets_shutdown_reason_and_ignores_404_gracefully()
    {
        string? capturedPayload = null;
        var debugLogs = new List<string>();
        var warnings = new List<string>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                capturedPayload = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };
        var seams = TomestoneCollector.CreateSeams(
            sender,
            new StubTomestoneProgressionService(),
            new ManualFFLogsTimeProvider());
        var abandoner = new TomestoneLeaseAbandoner(seams);
        var uploadUrl = new UploadUrl("https://upload.example/");
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(uploadUrl),
        };

        await abandoner.AbandonResultLeasesAsync(
            configuration,
            uploadUrl,
            [
                new TomestoneResult
                {
                    ContentId = 8001,
                    ZoneId = 65,
                    DifficultyId = 0,
                    EncounterId = 1079,
                    LeaseToken = "shutdown-lease",
                },
            ],
            "worker_shutdown",
            "worker_shutdown",
            CancellationToken.None,
            warnings.Add,
            debugLogs.Add);

        Assert.Empty(warnings);
        Assert.Single(debugLogs);
        var payloadObj = Assert.IsType<JObject>(Assert.Single(JArray.Parse(capturedPayload!)));
        Assert.Equal("worker_shutdown", payloadObj["reason"]!.Value<string>());
        Assert.Equal("worker_shutdown", payloadObj["failure_code"]!.Value<string>());
        Assert.Equal("worker_shutdown:65:0:1079", payloadObj["failure_fingerprint"]!.Value<string>());
    }
}
