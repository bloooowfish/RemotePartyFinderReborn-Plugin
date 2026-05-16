using System.Net;
using System.Net.Http;
using System.Threading;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsClientTests
{
    static FFLogsClientTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task QueryAsync_activates_cooldown_on_rate_limit_and_skips_follow_up_request()
    {
        var nowUtc = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        var warnings = new List<string>();
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("rate limited"),
                });
            },
        };
        using var client = new FFLogsClient(
            new Configuration
            {
                FFLogsClientId = "client-id",
                FFLogsClientSecret = "client-secret",
            },
            handler,
            () => nowUtc,
            static _ => { },
            warnings.Add,
            static _ => { },
            static (_, _) => { });

        var firstResponse = await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        Assert.Null(firstResponse);
        Assert.True(client.TryGetRateLimitRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromHours(1), remaining);
        Assert.Equal(nowUtc.AddHours(1), client.RateLimitCooldownUntilUtc);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(warnings);

        var secondResponse = await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        Assert.Null(secondResponse);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, warnings.Count);

        nowUtc = nowUtc.AddSeconds(59);
        await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, warnings.Count);

        nowUtc = nowUtc.AddSeconds(1);
        await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public async Task QueryAsync_activates_cooldown_on_oauth_rate_limit_and_skips_follow_up_request()
    {
        var nowUtc = new DateTime(2026, 5, 12, 14, 0, 0, DateTimeKind.Utc);
        var warnings = new List<string>();
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("token rate limited"),
            }),
        };
        using var client = new FFLogsClient(
            new Configuration
            {
                FFLogsClientId = "client-id",
                FFLogsClientSecret = "client-secret",
            },
            handler,
            () => nowUtc,
            static _ => { },
            warnings.Add,
            static _ => { },
            static (_, _) => { });

        var firstResponse = await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        Assert.Null(firstResponse);
        Assert.True(client.TryGetRateLimitRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromHours(1), remaining);
        Assert.Equal(nowUtc.AddHours(1), client.RateLimitCooldownUntilUtc);
        Assert.Single(handler.Requests);
        Assert.Single(warnings);

        var secondResponse = await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        Assert.Null(secondResponse);
        Assert.Single(handler.Requests);
        Assert.Equal(2, warnings.Count);
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
