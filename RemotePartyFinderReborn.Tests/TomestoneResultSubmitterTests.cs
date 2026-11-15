using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneResultSubmitterTests
{
    static TomestoneResultSubmitterTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Result_submitter_posts_snake_case_payload_with_tomestone_api_source_kind()
    {
        string? capturedPayload = null;
        HttpRequestMessage? capturedRequest = null;
        var infoLogs = new List<string>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                capturedRequest = request;
                capturedPayload = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","submitted":1,"accepted":1,"updated":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                };
            },
        };
        var seams = TomestoneCollector.CreateSeams(sender, new StubTomestoneProgressionService(), new ManualFFLogsTimeProvider());
        var submitBuffer = new TomestoneSubmitBuffer();
        var submitter = new TomestoneResultSubmitter(seams, submitBuffer);
        var session = new TomestoneLeaseSession(new UploadUrl("https://upload.example/"), []);

        var result = CreateResult(contentId: 4001, zoneId: 65, encounterId: 1079);
        result.SourceKind = string.Empty;
        result.PhaseProgress[1079] =
        [
            new PhaseProgress
            {
                PhaseKey = "p2",
                PhaseName = "Diamond Dust",
                Order = 2,
                BossPercentage = 52.4,
                DisplayText = "P2 52.4%",
                Source = "tomestone_api",
            },
        ];

        var attempt = await submitter.TrySubmitResultsAsync(
            CreateConfiguration(session.UploadUrl),
            session,
            [result],
            CancellationToken.None,
            infoLogs.Add,
            static _ => { },
            static _ => { });

        Assert.False(attempt.HadTransientFailure);
        Assert.False(attempt.ShouldUseBaseDelayBeforeNextPoll);
        Assert.False(submitBuffer.HasPendingResults);
        Assert.NotNull(capturedRequest);
        Assert.Equal("/contribute/tomestone/results", capturedRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("Uploaded Tomestone results", Assert.Single(infoLogs), StringComparison.Ordinal);

        var serialized = Assert.IsType<JObject>(Assert.Single(JArray.Parse(capturedPayload!)));
        Assert.Equal(4001UL, serialized["content_id"]!.Value<ulong>());
        Assert.Equal(65u, serialized["zone_id"]!.Value<uint>());
        Assert.Equal(1079u, serialized["encounter_id"]!.Value<uint>());
        Assert.Equal("tomestone_api", serialized["source_kind"]!.Value<string>());
        Assert.Equal("lease-4001", serialized["lease_token"]!.Value<string>());
        Assert.Equal(1079, serialized["tomestone_queried_encounters"]![0]!.Value<int>());
        Assert.IsType<JObject>(serialized["encounters"]);
        Assert.IsType<JObject>(serialized["clear_counts"]);
        var phase = Assert.IsType<JObject>(Assert.Single(Assert.IsType<JArray>(serialized["phase_progress"]!["1079"]!)));
        Assert.Equal("p2", phase["phase_key"]!.Value<string>());
    }

    [Fact]
    public async Task Result_submitter_splits_413_batches_and_abandons_single_oversized_item()
    {
        var requests = new List<(string Path, string Body)>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                var body = await request.Content!.ReadAsStringAsync();
                requests.Add((request.RequestUri!.AbsolutePath, body));

                if (request.RequestUri.AbsolutePath == "/contribute/tomestone/results")
                {
                    var items = JArray.Parse(body);
                    if (items.Count > 1 || items[0]["content_id"]!.Value<ulong>() == 5002)
                    {
                        return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"ok","submitted":1,"accepted":1,"updated":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","submitted":1,"released":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                };
            },
        };
        var seams = TomestoneCollector.CreateSeams(sender, new StubTomestoneProgressionService(), new ManualFFLogsTimeProvider());
        var submitBuffer = new TomestoneSubmitBuffer();
        var submitter = new TomestoneResultSubmitter(seams, submitBuffer);
        var session = new TomestoneLeaseSession(new UploadUrl("https://upload.example/"), []);
        var errors = new List<string>();

        var attempt = await submitter.TrySubmitResultsAsync(
            CreateConfiguration(session.UploadUrl),
            session,
            [
                CreateResult(contentId: 5001, zoneId: 73, encounterId: 101),
                CreateResult(contentId: 5002, zoneId: 73, encounterId: 102),
                CreateResult(contentId: 5003, zoneId: 73, encounterId: 103),
            ],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            errors.Add);

        Assert.False(attempt.HadTransientFailure);
        Assert.False(submitBuffer.HasPendingResults);
        Assert.Contains(errors, e => e.Contains("5002", StringComparison.Ordinal));
        var abandonReq = Assert.Single(requests, r => r.Path == "/contribute/tomestone/leases/abandon");
        var abandonPayload = Assert.IsType<JObject>(Assert.Single(JArray.Parse(abandonReq.Body)));
        Assert.Equal(5002UL, abandonPayload["content_id"]!.Value<ulong>());
        Assert.Equal("payload_too_large", abandonPayload["reason"]!.Value<string>());
        Assert.Equal("payload_too_large", abandonPayload["failure_code"]!.Value<string>());
    }

    [Fact]
    public async Task Result_submitter_requeues_on_transient_or_auth_failure_and_invalidates_only_tomestone_capability()
    {
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("unauthorized"),
            }),
        };
        var seams = TomestoneCollector.CreateSeams(sender, new StubTomestoneProgressionService(), new ManualFFLogsTimeProvider());
        var submitBuffer = new TomestoneSubmitBuffer();
        var submitter = new TomestoneResultSubmitter(seams, submitBuffer);
        var uploadUrl = new UploadUrl("https://secure.example/");
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities
            {
                TomestoneResults = new ProtectedEndpointCapabilityGrant
                {
                    Token = "ts-results",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                },
            },
            null);
        var session = new TomestoneLeaseSession(uploadUrl, []);

        var attempt = await submitter.TrySubmitResultsAsync(
            CreateConfiguration(uploadUrl),
            session,
            [CreateResult(contentId: 6001, zoneId: 73, encounterId: 101)],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        Assert.False(attempt.HadTransientFailure);
        Assert.True(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.TomestoneResults));
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsResults));
        Assert.Equal(1, submitBuffer.PendingCount);
    }

    private static Configuration CreateConfiguration(UploadUrl uploadUrl) => new()
    {
        IngestClientId = Guid.NewGuid().ToString("N"),
        UploadUrls = ImmutableList.Create(uploadUrl),
    };

    private static TomestoneResult CreateResult(ulong contentId, uint zoneId, uint encounterId) => new()
    {
        ContentId = contentId,
        ZoneId = zoneId,
        DifficultyId = 0,
        EncounterId = encounterId,
        BossPercentages = new Dictionary<int, double> { [(int)encounterId] = 25.0 },
        TomestoneQueriedEncounters = [(int)encounterId],
        MatchedServer = "Tonberry",
        SourceKind = "tomestone_api",
        LeaseToken = $"lease-{contentId}",
    };
}
