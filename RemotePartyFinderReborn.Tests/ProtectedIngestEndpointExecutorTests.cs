using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class ProtectedIngestEndpointExecutorTests {
    [Fact]
    public async Task Protected_executor_attaches_cached_capability_token() {
        var now = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = static (request, _) => {
                Assert.True(request.Headers.TryGetValues("X-RPF-Capability", out var values));
                Assert.Equal("jobs-token", Assert.Single(values));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            },
        };
        var uploadUrl = new UploadUrl("https://secure.example/");
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities {
                FflogsJobs = new ProtectedEndpointCapabilityGrant {
                    Token = "jobs-token",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
                },
            },
            null,
            new DateTimeOffset(now));
        var executor = new ProtectedIngestEndpointExecutor(sender, () => now);

        var result = await executor.ExecuteAsync(
            CreateConfiguration(uploadUrl),
            uploadUrl,
            IngestRoutes.FflogsJobs,
            ProtectedEndpointCapabilityKind.FflogsJobs,
            HttpMethod.Get,
            jsonBody: null,
            recordUploadAttempt: true,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Single(sender.Requests);
    }

    [Fact]
    public async Task Protected_executor_marks_capability_required_on_auth_failure() {
        var now = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc);
        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) {
                Content = new StringContent("forbidden"),
            }),
        };
        var uploadUrl = new UploadUrl("https://secure.example/");
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities {
                FflogsResults = new ProtectedEndpointCapabilityGrant {
                    Token = "results-token",
                    ExpiresAt = new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeSeconds(),
                },
            },
            null,
            new DateTimeOffset(now));
        var executor = new ProtectedIngestEndpointExecutor(sender, () => now);

        var result = await executor.ExecuteAsync(
            CreateConfiguration(uploadUrl),
            uploadUrl,
            IngestRoutes.FflogsResults,
            ProtectedEndpointCapabilityKind.FflogsResults,
            HttpMethod.Post,
            "[]",
            recordUploadAttempt: false,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.AuthFailure);
        Assert.False(result.TransientFailure);
        Assert.True(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsResults, new DateTimeOffset(now)));
        Assert.False(uploadUrl.TryGetProtectedCapability(ProtectedEndpointCapabilityKind.FflogsResults, out _, new DateTimeOffset(now)));
    }

    [Fact]
    public async Task Protected_executor_reports_retry_after_on_rate_limit() {
        var now = new DateTime(2026, 5, 10, 2, 0, 0, DateTimeKind.Utc);
        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = static (_, _) => {
                var response = new HttpResponseMessage((HttpStatusCode)429) {
                    Content = new StringContent("rate_limited"),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", "12");
                return Task.FromResult(response);
            },
        };
        var uploadUrl = new UploadUrl("https://secure.example/");
        var executor = new ProtectedIngestEndpointExecutor(sender, () => now);

        var result = await executor.ExecuteAsync(
            CreateConfiguration(uploadUrl),
            uploadUrl,
            IngestRoutes.FflogsResults,
            ProtectedEndpointCapabilityKind.FflogsResults,
            HttpMethod.Post,
            "[]",
            recordUploadAttempt: true,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.TransientFailure);
        Assert.Equal(12, result.RetryAfterSeconds);
        Assert.Equal(1, uploadUrl.FailureCount);
        Assert.Equal(now, uploadUrl.LastFailureTime);
    }

    [Fact]
    public async Task Protected_executor_defers_when_capability_required_but_missing() {
        var now = new DateTime(2026, 5, 10, 3, 0, 0, DateTimeKind.Utc);
        var sender = new StubFFLogsIngestHttpSender();
        var uploadUrl = new UploadUrl("https://secure.example/");
        uploadUrl.RequireProtectedCapabilities();
        var executor = new ProtectedIngestEndpointExecutor(sender, () => now);

        var result = await executor.ExecuteAsync(
            CreateConfiguration(uploadUrl),
            uploadUrl,
            IngestRoutes.FflogsJobs,
            ProtectedEndpointCapabilityKind.FflogsJobs,
            HttpMethod.Get,
            jsonBody: null,
            recordUploadAttempt: true,
            CancellationToken.None);

        Assert.True(result.Deferred);
        Assert.Equal(ProtectedIngestSkipReason.CapabilityDeferred, result.SkipReason);
        Assert.False(result.TransientFailure);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task Protected_executor_can_skip_upload_attempt_recording_for_auxiliary_routes() {
        var now = new DateTime(2026, 5, 10, 4, 0, 0, DateTimeKind.Utc);
        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
                Content = new StringContent("boom"),
            }),
        };
        var uploadUrl = new UploadUrl("https://secure.example/");
        var executor = new ProtectedIngestEndpointExecutor(sender, () => now);

        var result = await executor.ExecuteAsync(
            CreateConfiguration(uploadUrl),
            uploadUrl,
            IngestRoutes.FflogsLeasesAbandon,
            ProtectedEndpointCapabilityKind.FflogsLeasesAbandon,
            HttpMethod.Post,
            "[]",
            recordUploadAttempt: false,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.TransientFailure);
        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.Equal(DateTime.MinValue, uploadUrl.LastFailureTime);
    }

    private static Configuration CreateConfiguration(UploadUrl uploadUrl) {
        return new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };
    }
}
