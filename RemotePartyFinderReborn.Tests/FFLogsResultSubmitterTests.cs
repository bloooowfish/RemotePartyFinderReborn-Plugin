using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsResultSubmitterTests
{
    static FFLogsResultSubmitterTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Result_submitter_requeues_the_entire_batch_on_submit_failure()
    {
        var configuration = CreateConfiguration();
        var submitBuffer = new FFLogsSubmitBuffer();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "application/json"),
            }),
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var session = new FFLogsLeaseSession(new UploadUrl("https://session-owner.example/"), []);

        var attempt = await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 1001), CreateResult(contentId: 1002)],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        var requeuedBatch = submitBuffer.BuildSubmitBatch([]);

        Assert.True(attempt.HadTransientFailure);
        Assert.False(attempt.ShouldUseBaseDelayBeforeNextPoll);
        Assert.Equal(2, requeuedBatch.Count);
        Assert.Contains(requeuedBatch, result => result.ContentId == 1001UL);
        Assert.Contains(requeuedBatch, result => result.ContentId == 1002UL);
    }

    [Fact]
    public async Task Result_submitter_invalidates_results_capability_on_auth_failure()
    {
        var configuration = CreateConfiguration();
        var submitBuffer = new FFLogsSubmitBuffer();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("unauthorized", Encoding.UTF8, "application/json"),
            }),
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var uploadUrl = CreateUploadUrlWithResultsCapability("https://secure.example/");
        var session = new FFLogsLeaseSession(uploadUrl, []);

        var attempt = await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 2001)],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        Assert.False(attempt.HadTransientFailure);
        Assert.True(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsResults));
        Assert.False(uploadUrl.TryGetProtectedCapability(ProtectedEndpointCapabilityKind.FflogsResults, out _));
        Assert.Single(submitBuffer.BuildSubmitBatch([]));
    }

    [Fact]
    public async Task Result_submitter_treats_not_found_as_transient_submit_failure()
    {
        var configuration = CreateConfiguration();
        var submitBuffer = new FFLogsSubmitBuffer();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not_found", Encoding.UTF8, "application/json"),
            }),
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var session = new FFLogsLeaseSession(new UploadUrl("https://session-owner.example/"), []);

        var attempt = await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 2501)],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        Assert.True(attempt.HadTransientFailure);
        Assert.False(attempt.ShouldUseBaseDelayBeforeNextPoll);
        Assert.Single(submitBuffer.BuildSubmitBatch([]));
    }

    [Fact]
    public async Task Result_submitter_failure_does_not_increment_upload_target_circuit()
    {
        var configuration = CreateConfiguration();
        var submitBuffer = new FFLogsSubmitBuffer();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "application/json"),
            }),
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider
        {
            UtcNow = new DateTime(2026, 5, 10, 5, 0, 0, DateTimeKind.Utc),
        });
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var uploadUrl = new UploadUrl("https://session-owner.example/");
        var session = new FFLogsLeaseSession(uploadUrl, []);

        await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 2751)],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.Equal(DateTime.MinValue, uploadUrl.LastFailureTime);
    }

    [Fact]
    public async Task Result_submitter_uses_the_existing_lease_session_endpoint_instead_of_reselecting()
    {
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(
                new UploadUrl("https://reselected.example/"),
                new UploadUrl("https://session-owner.example/")),
        };
        var submitBuffer = new FFLogsSubmitBuffer();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\",\"submitted\":1,\"accepted\":1,\"updated\":0,\"rejected\":0}", Encoding.UTF8, "application/json"),
            }),
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var sessionOwner = configuration.UploadUrls[1];
        var session = new FFLogsLeaseSession(sessionOwner, []);

        await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 3001)],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        var request = Assert.Single(sender.Requests);
        Assert.StartsWith("https://session-owner.example/contribute/fflogs/results", request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_submitter_merges_fresh_results_with_pending_submit_buffer_entries()
    {
        var configuration = CreateConfiguration();
        var submitBuffer = new FFLogsSubmitBuffer();
        submitBuffer.QueueSubmitResults([CreateResult(contentId: 4001, matchedServer: "Pending")]);
        string payload = string.Empty;
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                payload = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\",\"submitted\":2,\"accepted\":2,\"updated\":0,\"rejected\":0}", Encoding.UTF8, "application/json"),
                };
            },
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var session = new FFLogsLeaseSession(new UploadUrl("https://session-owner.example/"), []);

        await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 4002, matchedServer: "Fresh")],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        var submitted = JsonConvert.DeserializeObject<List<ParseResult>>(payload);

        Assert.Equal(2, submitted!.Count);
        Assert.Contains(submitted, result => result.ContentId == 4001UL && result.MatchedServer == "Pending");
        Assert.Contains(submitted, result => result.ContentId == 4002UL && result.MatchedServer == "Fresh");
        Assert.Empty(submitBuffer.BuildSubmitBatch([]));
    }

    [Fact]
    public async Task Result_submitter_does_not_include_partition_in_payload()
    {
        var configuration = CreateConfiguration();
        var submitBuffer = new FFLogsSubmitBuffer();
        string payload = string.Empty;
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, _) =>
            {
                payload = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\",\"submitted\":1,\"accepted\":1,\"updated\":0,\"rejected\":0}", Encoding.UTF8, "application/json"),
                };
            },
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var session = new FFLogsLeaseSession(new UploadUrl("https://session-owner.example/"), []);

        await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 4501)],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        Assert.DoesNotContain("\"partition\"", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_result_serializes_phase_progress_with_snake_case_nested_fields()
    {
        var result = CreateResult(contentId: 4601);
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

        var payload = JObject.Parse(JsonConvert.SerializeObject(result));

        var phaseProgress = Assert.IsType<JObject>(payload["phase_progress"]);
        var encounterProgress = Assert.IsType<JArray>(phaseProgress["1079"]);
        var phase = Assert.IsType<JObject>(Assert.Single(encounterProgress));
        Assert.Equal("p2", phase["phase_key"]!.Value<string>());
        Assert.Equal("Diamond Dust", phase["phase_name"]!.Value<string>());
        Assert.Equal(2, phase["order"]!.Value<int>());
        Assert.Equal(52.4, phase["boss_percentage"]!.Value<double>());
        Assert.Equal("P2 52.4%", phase["display_text"]!.Value<string>());
        Assert.Equal("tomestone_api", phase["source"]!.Value<string>());
        Assert.Null(phase["PhaseKey"]);
        Assert.Null(phase["bossPercentage"]);
    }

    [Fact]
    public async Task Result_submitter_maps_429_to_worker_level_transient_failure_signal()
    {
        var configuration = CreateConfiguration();
        var submitBuffer = new FFLogsSubmitBuffer();
        var warnings = new List<string>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("rate_limited", Encoding.UTF8, "application/json"),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", "12");
                return Task.FromResult(response);
            },
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var session = new FFLogsLeaseSession(new UploadUrl("https://session-owner.example/"), []);

        var attempt = await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateResult(contentId: 5001)],
            CancellationToken.None,
            static _ => { },
            warnings.Add,
            static _ => { });

        Assert.True(attempt.HadTransientFailure);
        Assert.Contains(warnings, message => message.Contains("results endpoint rate limited", StringComparison.Ordinal));
        Assert.Single(submitBuffer.BuildSubmitBatch([]));
    }

    private static Configuration CreateConfiguration()
        => new()
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(
                new UploadUrl("https://configured.example/"),
                new UploadUrl("https://session-owner.example/")),
        };

    private static UploadUrl CreateUploadUrlWithResultsCapability(string url)
    {
        var uploadUrl = new UploadUrl(url);
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities
            {
                FflogsResults = new ProtectedEndpointCapabilityGrant
                {
                    Token = "results-token",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                },
            },
            null);
        return uploadUrl;
    }

    private static ParseResult CreateResult(ulong contentId, string matchedServer = "Tonberry")
        => new()
        {
            ContentId = contentId,
            ZoneId = 88,
            DifficultyId = 5,
            MatchedServer = matchedServer,
            LeaseToken = $"lease-{contentId}",
        };
}
