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
    public void Capability_families_are_isolated_between_fflogs_and_tomestone() {
        var now = new DateTimeOffset(2026, 5, 10, 0, 20, 0, TimeSpan.Zero);
        var uploadUrl = new UploadUrl("https://upload.example");
        uploadUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities {
                FflogsJobs = new ProtectedEndpointCapabilityGrant {
                    Token = "fflogs-jobs",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
                FflogsResults = new ProtectedEndpointCapabilityGrant {
                    Token = "fflogs-results",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
                FflogsLeasesAbandon = new ProtectedEndpointCapabilityGrant {
                    Token = "fflogs-abandon",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
            },
            null,
            now);

        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.TomestoneJobs, now));
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.TomestoneResults, now));
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.TomestoneLeasesAbandon, now));

        uploadUrl.RequireProtectedCapabilities(ProtectedEndpointCapabilityKind.TomestoneJobs);
        Assert.True(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.TomestoneJobs, now));
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsJobs, now));

        var tomestoneOnlyUrl = new UploadUrl("https://tomestone-only.example");
        tomestoneOnlyUrl.ApplyIngestCapabilities(
            new ProtectedEndpointCapabilities {
                TomestoneJobs = new ProtectedEndpointCapabilityGrant {
                    Token = "ts-jobs",
                    ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
                },
            },
            null,
            now);

        Assert.False(tomestoneOnlyUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsJobs, now));
        Assert.True(tomestoneOnlyUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.TomestoneResults, now));
        Assert.True(tomestoneOnlyUrl.TryGetProtectedCapability(ProtectedEndpointCapabilityKind.TomestoneJobs, out var tsToken, now));
        Assert.Equal("ts-jobs", tsToken);
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

    [Fact]
    public void Recorder_route_not_found_opens_circuit_for_15_minutes_and_recovers() {
        var nowUtc = new DateTime(2026, 5, 10, 5, 0, 0, DateTimeKind.Utc);
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerBreakDurationMinutes = 1,
        };
        var uploadUrl = new UploadUrl("https://upload.example");

        UploadAttemptRecorder.RecordRouteNotFoundFailure(uploadUrl, nowUtc);

        Assert.Equal(1, uploadUrl.FailureCount);
        Assert.True(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc));
        Assert.True(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(14).AddSeconds(59)));
        Assert.False(UploadAttemptRecorder.TryBeginAttempt(uploadUrl, configuration, nowUtc.AddMinutes(14), out _));

        var statusAt5Min = UploadAttemptRecorder.GetCircuitStatus(uploadUrl, configuration, nowUtc.AddMinutes(5));
        Assert.True(statusAt5Min.IsOpen);
        Assert.Equal(1, statusAt5Min.FailureCount);
        Assert.Equal(TimeSpan.FromMinutes(10), statusAt5Min.RemainingBreakDuration);

        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(15)));
        var statusAt15Min = UploadAttemptRecorder.GetCircuitStatus(uploadUrl, configuration, nowUtc.AddMinutes(15));
        Assert.False(statusAt15Min.IsOpen);
        Assert.Equal(TimeSpan.Zero, statusAt15Min.RemainingBreakDuration);
        Assert.True(UploadAttemptRecorder.TryBeginAttempt(uploadUrl, configuration, nowUtc.AddMinutes(15), out var lease));
        lease.Dispose();
    }

    [Fact]
    public void Recorder_route_not_found_resets_cleanly_on_success() {
        var nowUtc = new DateTime(2026, 5, 10, 6, 0, 0, DateTimeKind.Utc);
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerBreakDurationMinutes = 1,
        };
        var uploadUrl = new UploadUrl("https://upload.example");

        UploadAttemptRecorder.RecordRouteNotFoundFailure(uploadUrl, nowUtc);
        Assert.True(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(1)));

        UploadAttemptRecorder.RecordSuccess(uploadUrl);

        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(1)));
        Assert.Equal(TimeSpan.Zero, UploadAttemptRecorder.GetCircuitStatus(uploadUrl, configuration, nowUtc.AddMinutes(1)).RemainingBreakDuration);
    }

    [Fact]
    public void Lease_record_route_not_found_failure_releases_half_open_probe_and_applies_15_minute_cooldown() {
        var startUtc = new DateTime(2026, 5, 10, 7, 0, 0, DateTimeKind.Utc);
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 1,
        };
        var uploadUrl = new UploadUrl("https://upload.example");
        UploadAttemptRecorder.RecordFailure(uploadUrl, startUtc);

        var probeUtc = startUtc.AddMinutes(1);
        Assert.True(UploadAttemptRecorder.TryBeginAttempt(uploadUrl, configuration, probeUtc, out var lease));
        Assert.True(lease.ClaimedHalfOpenProbe);

        lease.RecordRouteNotFoundFailure(probeUtc);

        Assert.True(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, probeUtc.AddMinutes(14)));
        Assert.False(UploadAttemptRecorder.TryBeginAttempt(uploadUrl, configuration, probeUtc.AddMinutes(14), out _));
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, probeUtc.AddMinutes(15)));
        Assert.True(UploadAttemptRecorder.TryBeginAttempt(uploadUrl, configuration, probeUtc.AddMinutes(15), out var retryLease));
        Assert.True(retryLease.ClaimedHalfOpenProbe);
        retryLease.Dispose();
    }

    [Fact]
    public void HasAnyAttemptableEndpoint_checks_enabled_validity_and_open_circuits_without_consuming_half_open_probe() {
        var nowUtc = new DateTime(2026, 5, 10, 8, 0, 0, DateTimeKind.Utc);
        var openUrl = new UploadUrl("https://open.example");
        var disabledUrl = new UploadUrl("https://disabled.example") { IsEnabled = false };
        var insecureUrl = new UploadUrl("http://remote-insecure.example");
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 1,
            UploadUrls = [openUrl, disabledUrl, insecureUrl],
        };
        UploadAttemptRecorder.RecordFailure(openUrl, nowUtc);

        Assert.False(UploadAttemptRecorder.HasAnyAttemptableEndpoint(configuration, IngestRoutes.Players, nowUtc.AddSeconds(30)));

        // At 1 minute, Half-Open becomes eligible and HasAnyAttemptableEndpoint must not consume the probe slot.
        var probeUtc = nowUtc.AddMinutes(1);
        Assert.True(UploadAttemptRecorder.HasAnyAttemptableEndpoint(configuration, IngestRoutes.Players, probeUtc));
        Assert.True(UploadAttemptRecorder.TryBeginAttempt(openUrl, configuration, probeUtc, out var lease));
        Assert.True(lease.ClaimedHalfOpenProbe);
        lease.Dispose();
    }
}
