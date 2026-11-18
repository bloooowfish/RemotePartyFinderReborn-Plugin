using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneApiClientTests
{
    static TomestoneApiClientTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task GetRawAsync_sends_bearer_token_when_api_key_is_configured()
    {
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}"),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler);

        var response = await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"ok\":true}", response.Body);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetRawOutcomeAsync_returns_non_transient_failure_and_sends_no_http_when_api_key_is_blank(string apiKey)
    {
        var handler = new RecordingHttpMessageHandler();
        using var client = new TomestoneApiClient(new Configuration { TomestoneApiKey = apiKey }, handler);

        var outcome = await client.GetRawOutcomeAsync("/api/test", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.TransientFailure);
        Assert.Equal("Tomestone API key is not configured.", outcome.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/api/leak")]
    [InlineData("http://tomestone.gg/character/1/alpha/graph")]
    [InlineData("https://tomestone.gg/character/123/alpha/activity/fru/graph")]
    [InlineData("/character/123/alpha/activity/fru/graph")]
    [InlineData("../character/1/alpha")]
    [InlineData("//evil.example/api/leak")]
    [InlineData("not a valid uri ://")]
    public async Task GetRawOutcomeAsync_refuses_off_origin_or_invalid_uri_without_sending_bearer_token(string uri)
    {
        var warnings = new List<string>();
        var handler = new RecordingHttpMessageHandler();
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler,
            warningLog: warnings.Add);

        var outcome = await client.GetRawOutcomeAsync(uri, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.TransientFailure);
        Assert.Empty(handler.Requests);
        Assert.Contains(warnings, static message => message.Contains("off-origin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetRawOutcomeAsync_accepts_same_origin_tomestone_api_uri_and_sends_bearer_token()
    {
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"graph":[]}}"""),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler);

        var outcome = await client.GetRawOutcomeAsync(
            "https://tomestone.gg/api/character/profile/Tonberry/Alpha",
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://tomestone.gg/api/character/profile/Tonberry/Alpha", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetRawOutcomeAsync_resets_rejected_status_when_api_key_changes_or_clears()
    {
        var configuration = new Configuration { TomestoneApiKey = "rejected-key" };
        var statusTransitions = new List<(ApiCredentialValidationState State, string Message)>();
        var attempt = 0;
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (_, _) => Task.FromResult(++attempt switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") },
                2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") },
            }),
        };
        using var client = new TomestoneApiClient(
            configuration,
            handler,
            credentialStatusChanged: (state, message) => statusTransitions.Add((state, message)));

        var first = await client.GetRawOutcomeAsync("/api/test", CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(HttpStatusCode.Unauthorized, first.Value!.StatusCode);
        Assert.Equal(ApiCredentialValidationState.Rejected, statusTransitions[^1].State);

        configuration.TomestoneApiKey = "new-valid-key";
        var second = await client.GetRawOutcomeAsync("/api/test", CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(HttpStatusCode.OK, second.Value!.StatusCode);
        Assert.Contains(statusTransitions, static item => item.State == ApiCredentialValidationState.NotChecked);
        Assert.Equal(ApiCredentialValidationState.Accepted, statusTransitions[^1].State);

        configuration.TomestoneApiKey = "another-rejected-key";
        await client.GetRawOutcomeAsync("/api/test", CancellationToken.None);
        Assert.Equal(ApiCredentialValidationState.Rejected, statusTransitions[^1].State);

        configuration.TomestoneApiKey = string.Empty;
        var cleared = await client.GetRawOutcomeAsync("/api/test", CancellationToken.None);
        Assert.False(cleared.Succeeded);
        Assert.Equal(ApiCredentialValidationState.NotChecked, statusTransitions[^1].State);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(403, "cf-mitigated", "Just a moment...")]
    [InlineData(503, "", "<html><title>Attention Required! | Cloudflare</title></html>")]
    [InlineData(200, "", "<html><script src=\"https://www.google.com/recaptcha/api.js\"></script><div class=\"g-recaptcha\"></div></html>")]
    [InlineData(200, "", "<html><div class=\"hcaptcha\"></div></html>")]
    public async Task Official_api_client_aborts_and_enters_cooldown_on_challenge_indicators(
        int statusCode,
        string headerName,
        string body)
    {
        var nowUtc = new DateTime(2026, 5, 12, 1, 0, 0, DateTimeKind.Utc);
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)statusCode)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
                };
                if (!string.IsNullOrEmpty(headerName))
                {
                    response.Headers.TryAddWithoutValidation(headerName, "challenge");
                }

                return Task.FromResult(response);
            },
        };

        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler,
            () => nowUtc,
            static _ => { },
            static _ => { },
            static _ => { });

        var firstResponse = await client.GetRawAsync("/api/characters/Tonberry/Alpha", CancellationToken.None);

        Assert.Null(firstResponse);
        Assert.True(client.TryGetRateLimitRemaining(out _));
        Assert.Single(handler.Requests);

        var secondResponse = await client.GetRawAsync("/api/characters/Tonberry/Alpha", CancellationToken.None);

        Assert.Null(secondResponse);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_response_reports_rejected_key_without_exposing_it()
    {
        (ApiCredentialValidationState State, string Message)? status = null;
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("invalid bearer key"),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "private-key" },
            handler,
            credentialStatusChanged: (state, message) => status = (state, message));

        await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ApiCredentialValidationState.Rejected, status.Value.State);
        Assert.Contains("HTTP 401", status.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", status.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid bearer key", status.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_hidden_activity_does_not_misreport_a_valid_key_as_rejected()
    {
        (ApiCredentialValidationState State, string Message)? status = null;
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":\"Character activity is hidden.\"}"),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "private-key" },
            handler,
            credentialStatusChanged: (state, message) => status = (state, message));

        var result = await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.ActivityHidden);
        Assert.NotNull(status);
        Assert.Equal(ApiCredentialValidationState.Accepted, status.Value.State);
        Assert.Contains("activity is hidden", status.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-key", status.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_response_marks_api_key_accepted()
    {
        (ApiCredentialValidationState State, string Message)? status = null;
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "private-key" },
            new RecordingHttpMessageHandler(),
            credentialStatusChanged: (state, message) => status = (state, message));

        await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(ApiCredentialValidationState.Accepted, status.Value.State);
        Assert.DoesNotContain("private-key", status.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRawAsync_activates_cooldown_on_rate_limit_and_skips_follow_up_request()
    {
        var nowUtc = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var warnings = new List<string>();
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("rate limited"),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler,
            () => nowUtc,
            static _ => { },
            warnings.Add,
            static _ => { });

        var firstResponse = await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.Null(firstResponse);
        Assert.True(client.TryGetRateLimitRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromHours(1), remaining);
        Assert.Equal(nowUtc.AddHours(1), client.RateLimitCooldownUntilUtc);

        var secondResponse = await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.Null(secondResponse);
        Assert.Single(handler.Requests);
        Assert.Equal(2, warnings.Count);

        nowUtc = nowUtc.AddSeconds(59);

        await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.Equal(2, warnings.Count);

        nowUtc = nowUtc.AddSeconds(1);

        await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.Equal(3, warnings.Count);
        Assert.DoesNotContain(warnings, message => message.Contains("secret-token", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, message => message.Contains("Authorization", StringComparison.OrdinalIgnoreCase));

        client.ResetRateLimitCooldown();

        Assert.False(client.TryGetRateLimitRemaining(out _));
        Assert.Equal(DateTime.MinValue, client.RateLimitCooldownUntilUtc);
    }

    [Fact]
    public async Task GetRawAsync_parses_numeric_retry_after_header_into_adaptive_cooldown()
    {
        var nowUtc = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("rate limited"),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", "45");
                return Task.FromResult(response);
            },
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler,
            () => nowUtc,
            static _ => { },
            static _ => { },
            static _ => { });

        var result = await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.Null(result);
        Assert.True(client.TryGetRateLimitRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromSeconds(45), remaining);
        Assert.Equal(nowUtc.AddSeconds(45), client.RateLimitCooldownUntilUtc);
    }

    [Fact]
    public async Task GetRawAsync_parses_http_date_retry_after_header_and_clamps_to_max_cooldown()
    {
        var nowUtc = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("rate limited"),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", nowUtc.AddHours(3).ToString("R"));
                return Task.FromResult(response);
            },
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler,
            () => nowUtc,
            static _ => { },
            static _ => { },
            static _ => { });

        var result = await client.GetRawAsync("/api/test", CancellationToken.None);

        Assert.Null(result);
        Assert.True(client.TryGetRateLimitRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromHours(1), remaining);
    }

    [Fact]
    public async Task GetRawAsync_refuses_3xx_redirect_responses_without_following_location()
    {
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Content = new StringContent("redirecting"),
                };
                response.Headers.Location = new Uri("https://evil.example/redirect");
                return Task.FromResult(response);
            },
        };
        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler);

        var result = await client.GetRawOutcomeAsync("/api/test", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.TransientFailure);
        Assert.Contains("redirect", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Default_constructor_configures_twenty_second_timeout_and_disables_auto_redirect()
    {
        using var client = new TomestoneApiClient(new Configuration());
        var httpClientField = typeof(TomestoneApiClient)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(static f => f.FieldType == typeof(HttpClient));

        Assert.NotNull(httpClientField);
        var httpClient = Assert.IsType<HttpClient>(httpClientField!.GetValue(client));
        Assert.Equal(TimeSpan.FromSeconds(20), httpClient.Timeout);
        Assert.Equal(PluginHttpClientFactory.MaxResponseContentBufferSizeBytes, httpClient.MaxResponseContentBufferSize);

        var handlerField = typeof(HttpMessageInvoker)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(static f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
        Assert.NotNull(handlerField);
        var innerHandler = Assert.IsType<SocketsHttpHandler>(handlerField!.GetValue(httpClient));
        Assert.False(innerHandler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromSeconds(5), innerHandler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), innerHandler.PooledConnectionLifetime);
        Assert.Equal(DecompressionMethods.All, innerHandler.AutomaticDecompression);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSendAsync { get; set; }
            = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return OnSendAsync(request, cancellationToken);
        }
    }
}
