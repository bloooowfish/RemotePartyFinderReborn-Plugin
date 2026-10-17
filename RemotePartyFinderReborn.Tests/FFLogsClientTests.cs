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

    [Fact]
    public async Task OAuth_rejection_is_reported_without_exposing_credentials()
    {
        var statuses = new List<(ApiCredentialValidationState State, string Message)>();
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("invalid client secret"),
            }),
        };
        using var client = new FFLogsClient(
            new Configuration { FFLogsClientId = "client-id", FFLogsClientSecret = "private-secret" },
            handler,
            credentialStatusChanged: (state, message) => statuses.Add((state, message)));

        await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        var status = Assert.Single(statuses);
        Assert.Equal(ApiCredentialValidationState.Rejected, status.State);
        Assert.Contains("HTTP 401", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid client secret", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_OAuth_exchange_marks_credentials_accepted()
    {
        var statuses = new List<(ApiCredentialValidationState State, string Message)>();
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}"),
            }),
        };
        using var client = new FFLogsClient(
            new Configuration { FFLogsClientId = "client-id", FFLogsClientSecret = "private-secret" },
            handler,
            credentialStatusChanged: (state, message) => statuses.Add((state, message)));

        await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        var status = Assert.Single(statuses);
        Assert.Equal(ApiCredentialValidationState.Accepted, status.State);
        Assert.DoesNotContain("private-secret", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_credentials_discards_cached_token_and_revalidates()
    {
        var configuration = new Configuration
        {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "first-secret",
        };
        var statuses = new List<(ApiCredentialValidationState State, string Message)>();
        var oauthRequestCount = 0;
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.Ordinal))
                {
                    oauthRequestCount++;
                    return Task.FromResult(oauthRequestCount == 1
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}"),
                        }
                        : new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{}}"),
                });
            },
        };
        using var client = new FFLogsClient(
            configuration,
            handler,
            credentialStatusChanged: (state, message) => statuses.Add((state, message)));

        await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);
        configuration.FFLogsClientSecret = "second-secret";
        await client.QueryAsync("query { reportData { reports } }", CancellationToken.None);

        Assert.Equal(2, oauthRequestCount);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(ApiCredentialValidationState.Accepted, statuses[0].State);
        Assert.Equal(ApiCredentialValidationState.Rejected, statuses[1].State);
        Assert.DoesNotContain("second-secret", statuses[1].Message, StringComparison.Ordinal);
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
