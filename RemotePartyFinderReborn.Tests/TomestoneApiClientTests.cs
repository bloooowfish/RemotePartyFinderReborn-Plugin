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

        var response = await client.GetRawAsync("/test", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"ok\":true}", response.Body);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetRawAsync_does_not_send_authorization_when_api_key_is_blank()
    {
        var handler = new RecordingHttpMessageHandler();
        using var client = new TomestoneApiClient(new Configuration(), handler);

        await client.GetRawAsync("/test", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Headers.Authorization);
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

        var firstResponse = await client.GetRawAsync("/test", CancellationToken.None);

        Assert.Null(firstResponse);
        Assert.True(client.TryGetRateLimitRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromHours(1), remaining);
        Assert.Equal(nowUtc.AddHours(1), client.RateLimitCooldownUntilUtc);

        var secondResponse = await client.GetRawAsync("/test", CancellationToken.None);

        Assert.Null(secondResponse);
        Assert.Single(handler.Requests);
        Assert.Equal(2, warnings.Count);

        nowUtc = nowUtc.AddSeconds(59);

        await client.GetRawAsync("/test", CancellationToken.None);

        Assert.Equal(2, warnings.Count);

        nowUtc = nowUtc.AddSeconds(1);

        await client.GetRawAsync("/test", CancellationToken.None);

        Assert.Equal(3, warnings.Count);
        Assert.DoesNotContain(warnings, message => message.Contains("secret-token", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, message => message.Contains("Authorization", StringComparison.OrdinalIgnoreCase));

        client.ResetRateLimitCooldown();

        Assert.False(client.TryGetRateLimitRemaining(out _));
        Assert.Equal(DateTime.MinValue, client.RateLimitCooldownUntilUtc);
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
