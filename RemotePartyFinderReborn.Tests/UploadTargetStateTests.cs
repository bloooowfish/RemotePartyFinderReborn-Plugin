using Xunit;
using Newtonsoft.Json.Linq;

namespace RemotePartyFinderReborn.Tests;

public sealed class UploadTargetStateTests {
    [Fact]
    public void Protected_capability_expires_and_defers_after_prune() {
        var now = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities {
                FflogsJobs = new ProtectedEndpointCapabilityGrant {
                    Token = "jobs-token",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
            },
            null,
            now);

        Assert.True(uploadUrl.TryGetProtectedCapability(
            ProtectedEndpointCapabilityKind.FflogsJobs,
            out var token,
            now.AddMinutes(4)));
        Assert.Equal("jobs-token", token);

        Assert.True(uploadUrl.ShouldDeferProtectedRequest(
            ProtectedEndpointCapabilityKind.FflogsJobs,
            now.AddMinutes(5)));
        Assert.False(uploadUrl.TryGetProtectedCapability(
            ProtectedEndpointCapabilityKind.FflogsJobs,
            out _,
            now.AddMinutes(5)));
    }

    [Fact]
    public void Protected_capability_invalidation_removes_cached_token_and_defers() {
        var now = new DateTimeOffset(2026, 5, 10, 0, 15, 0, TimeSpan.Zero);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities {
                FflogsResults = new ProtectedEndpointCapabilityGrant {
                    Token = "results-token",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
            },
            null,
            now);

        uploadUrl.InvalidateProtectedCapability(ProtectedEndpointCapabilityKind.FflogsResults);

        Assert.True(uploadUrl.ShouldDeferProtectedRequest(
            ProtectedEndpointCapabilityKind.FflogsResults,
            now));
        Assert.False(uploadUrl.TryGetProtectedCapability(
            ProtectedEndpointCapabilityKind.FflogsResults,
            out _,
            now));
    }

    [Fact]
    public void Detail_capability_expires_and_defers_after_prune() {
        var now = new DateTimeOffset(2026, 5, 10, 0, 30, 0, TimeSpan.Zero);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "detail-token",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
            ],
            now);

        Assert.True(uploadUrl.TryGetDetailCapability(9001U, out var token, now.AddMinutes(4)));
        Assert.Equal("detail-token", token);

        Assert.True(uploadUrl.ShouldDeferDetailRequest(9001U, now.AddMinutes(5)));
        Assert.False(uploadUrl.TryGetDetailCapability(9001U, out _, now.AddMinutes(5)));
    }

    [Fact]
    public void Detail_capability_invalidation_removes_cached_token_and_defers() {
        var now = new DateTimeOffset(2026, 5, 10, 0, 45, 0, TimeSpan.Zero);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "detail-token",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
            ],
            now);

        uploadUrl.InvalidateDetailCapability(9001U);

        Assert.True(uploadUrl.ShouldDeferDetailRequest(9001U, now));
        Assert.False(uploadUrl.TryGetDetailCapability(9001U, out _, now));
    }

    [Fact]
    public void Recorder_resets_failure_state_on_success() {
        var failedAtUtc = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        UploadAttemptRecorder.RecordFailure(uploadUrl, failedAtUtc);
        UploadAttemptRecorder.RecordFailure(uploadUrl, failedAtUtc);
        UploadAttemptRecorder.RecordFailure(uploadUrl, failedAtUtc);

        UploadAttemptRecorder.RecordSuccess(uploadUrl);

        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.Equal(DateTime.MinValue, uploadUrl.LastFailureTime);
    }

    [Fact]
    public void Recorder_records_failure_time_from_supplied_clock() {
        var nowUtc = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");

        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);

        Assert.Equal(1, uploadUrl.FailureCount);
        Assert.Equal(nowUtc, uploadUrl.LastFailureTime);
    }

    [Fact]
    public void Runtime_failure_state_is_not_serialized() {
        var nowUtc = new DateTime(2026, 5, 10, 1, 30, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);

        var serialized = JObject.FromObject(uploadUrl);

        Assert.False(serialized.ContainsKey("FailureCount"));
        Assert.False(serialized.ContainsKey("LastFailureTime"));
        Assert.Equal("https://upload.example", serialized.Value<string>("Url"));
    }

    [Fact]
    public void Recorder_reports_open_circuit_until_break_duration_expires() {
        var nowUtc = new DateTime(2026, 5, 10, 2, 0, 0, DateTimeKind.Utc);
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerBreakDurationMinutes = 5,
        };
        var uploadUrl = new UploadUrl("https://upload.example");
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);

        Assert.True(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(4)));
        Assert.False(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(5)));
    }

    [Fact]
    public void Recorder_reports_circuit_status_and_remaining_break_duration() {
        var nowUtc = new DateTime(2026, 5, 10, 3, 0, 0, DateTimeKind.Utc);
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationMinutes = 5,
        };
        var uploadUrl = new UploadUrl("https://upload.example");
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);

        var status = UploadAttemptRecorder.GetCircuitStatus(uploadUrl, configuration, nowUtc.AddMinutes(2));

        Assert.Equal(2, status.FailureCount);
        Assert.True(status.IsOpen);
        Assert.Equal(TimeSpan.FromMinutes(3), status.RemainingBreakDuration);
    }

    [Fact]
    public void Detail_batch_unsupported_state_survives_failure_reset_and_capability_updates() {
        var now = new DateTimeOffset(2026, 5, 10, 4, 0, 0, TimeSpan.Zero);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.MarkDetailBatchUnsupported();
        UploadAttemptRecorder.RecordFailure(uploadUrl, now.UtcDateTime);

        UploadAttemptRecorder.RecordSuccess(uploadUrl);
        uploadUrl.ApplyIngestCapabilities(
            null,
            [
                new ListingDetailCapability {
                    ListingId = 9001U,
                    Token = "detail-token",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
            ],
            now);

        Assert.True(uploadUrl.DetailBatchUnsupported);
        Assert.Equal(0, uploadUrl.FailureCount);
    }
}
