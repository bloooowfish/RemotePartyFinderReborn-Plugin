using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed record ProtectedIngestResult(
    HttpStatusCode? StatusCode,
    string Body,
    bool Success,
    ProtectedIngestSkipReason SkipReason,
    bool AuthFailure,
    bool TransientFailure,
    int? RetryAfterSeconds)
{
    public bool Deferred
        => SkipReason != ProtectedIngestSkipReason.None;

    public string FailureBody
        => string.IsNullOrWhiteSpace(Body)
            ? "protected ingest request failed."
            : Body;
}

internal enum ProtectedIngestSkipReason
{
    None,
    EndpointUnavailable,
    CapabilityDeferred,
    CircuitOpen,
}

internal sealed class ProtectedIngestEndpointExecutor
{
    private readonly IIngestHttpSender _httpSender;
    private readonly Func<DateTime> _utcNow;

    internal ProtectedIngestEndpointExecutor(IIngestHttpSender httpSender, Func<DateTime> utcNow)
    {
        _httpSender = httpSender ?? throw new ArgumentNullException(nameof(httpSender));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>
    /// Selects the first enabled, valid, non-open-circuit target using a non-claiming circuit peek.
    /// Intended for background flush/abandon paths that do not claim a Half-Open probe.
    /// </summary>
    internal static UploadUrl? SelectAvailableUploadUrl(
        Configuration configuration,
        DateTime utcNow,
        Action<string> warningLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(warningLog);

        foreach (var candidate in configuration.GetUploadUrlsSnapshot().Where(static url => url.IsEnabled))
        {
            if (UploadAttemptRecorder.PeekIsCircuitOpen(candidate, configuration, utcNow))
            {
                continue;
            }

            if (!IngestEndpointResolver.IsValidUploadUrl(candidate, warningLog))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Validates target URL and capability readiness before claiming a Half-Open probe lease.
    /// </summary>
    internal static bool TrySelectTargetForProtectedAttempt(
        Configuration configuration,
        ProtectedEndpointCapabilityKind capabilityKind,
        DateTime utcNow,
        Action<string> warningLog,
        out UploadUrl? uploadUrl,
        out UploadAttemptLease? lease,
        out bool capabilityDeferred)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(warningLog);

        var nowOffset = new DateTimeOffset(NormalizeUtc(utcNow));
        foreach (var candidate in configuration.GetUploadUrlsSnapshot().Where(static url => url.IsEnabled))
        {
            if (!IngestEndpointResolver.IsValidUploadUrl(candidate, warningLog))
            {
                continue;
            }

            if (UploadAttemptRecorder.PeekIsCircuitOpen(candidate, configuration, utcNow))
            {
                continue;
            }

            if (candidate.ShouldDeferProtectedRequest(capabilityKind, nowOffset))
            {
                uploadUrl = candidate;
                lease = null;
                capabilityDeferred = true;
                return true;
            }

            if (!UploadAttemptRecorder.TryBeginAttempt(candidate, configuration, utcNow, out lease))
            {
                continue;
            }

            uploadUrl = candidate;
            capabilityDeferred = false;
            return true;
        }

        uploadUrl = null;
        lease = null;
        capabilityDeferred = false;
        return false;
    }

    internal static bool TryBuildWorkerHeaders(
        IFFLogsWorkerRegionProvider? workerRegionProvider,
        string processorKindHeader,
        string processorKindValue,
        string jobModeHeader,
        string jobModeValue,
        string workerRegionHeader,
        string logPrefix,
        Action<string> debugLog,
        out IReadOnlyDictionary<string, string>? extraHeaders)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [processorKindHeader] = processorKindValue,
            [jobModeHeader] = jobModeValue,
        };

        if (workerRegionProvider == null)
        {
            extraHeaders = headers;
            return true;
        }

        if (workerRegionProvider is IFFLogsWorkerRegionSnapshotState { HasObservedSnapshot: false })
        {
            extraHeaders = null;
            return false;
        }

        if (!workerRegionProvider.TryGetWorkerRegion(out var region))
        {
            debugLog($"{logPrefix}: worker region unavailable; skipping regional jobs poll.");
            extraHeaders = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            debugLog($"{logPrefix}: worker region was blank; skipping regional jobs poll.");
            extraHeaders = null;
            return false;
        }

        headers[workerRegionHeader] = region.Trim();
        extraHeaders = headers;
        return true;
    }

    internal Task<ProtectedIngestResult> ExecuteAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IngestRoute route,
        ProtectedEndpointCapabilityKind capabilityKind,
        HttpMethod method,
        string? jsonBody,
        bool recordUploadAttempt,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
        => ExecuteAsync(
            configuration,
            uploadUrl,
            route,
            capabilityKind,
            method,
            jsonBody,
            recordUploadAttempt,
            cancellationToken,
            extraHeaders,
            attemptLease: null);

    internal async Task<ProtectedIngestResult> ExecuteAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IngestRoute route,
        ProtectedEndpointCapabilityKind capabilityKind,
        HttpMethod method,
        string? jsonBody,
        bool recordUploadAttempt,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders,
        UploadAttemptLease? attemptLease)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(method);

        if (!IngestEndpointResolver.TryBuildEndpoint(uploadUrl, route, out var endpoint))
        {
            return new ProtectedIngestResult(null, string.Empty, false, ProtectedIngestSkipReason.EndpointUnavailable, false, false, null);
        }

        var nowUtc = GetUtcNow();
        var now = new DateTimeOffset(nowUtc);
        if (uploadUrl.ShouldDeferProtectedRequest(capabilityKind, now))
        {
            return new ProtectedIngestResult(null, string.Empty, false, ProtectedIngestSkipReason.CapabilityDeferred, false, false, null);
        }

        UploadAttemptLease? ownedLease = null;
        if (recordUploadAttempt && attemptLease is null)
        {
            if (!UploadAttemptRecorder.TryBeginAttempt(uploadUrl, configuration, nowUtc, out ownedLease))
            {
                return new ProtectedIngestResult(null, string.Empty, false, ProtectedIngestSkipReason.CircuitOpen, false, false, null);
            }
        }

        var activeLease = attemptLease ?? ownedLease;
        using (ownedLease)
        {
            var capabilityToken = uploadUrl.TryGetProtectedCapability(capabilityKind, out var cachedCapability, now)
                ? cachedCapability
                : null;

            try
            {
                using var request = CreateRequest(configuration, endpoint, method, jsonBody, capabilityToken, extraHeaders);
                using var response = await _httpSender.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    RecordSuccess(uploadUrl, recordUploadAttempt, activeLease);
                    return new ProtectedIngestResult(response.StatusCode, body, true, ProtectedIngestSkipReason.None, false, false, null);
                }

                var isAuthFailure = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
                if (isAuthFailure)
                {
                    uploadUrl.RequireProtectedCapabilities(capabilityKind);
                    uploadUrl.InvalidateProtectedCapability(capabilityKind);
                }
                else if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    RecordFailure(uploadUrl, nowUtc, recordUploadAttempt, activeLease);
                }

                var retryAfterSeconds = (int)response.StatusCode == 429
                    ? IngestRequestFactory.ReadRetryAfterSeconds(response)
                    : null;

                var transientFailure = !isAuthFailure && response.StatusCode != HttpStatusCode.NotFound;
                return new ProtectedIngestResult(
                    response.StatusCode,
                    body,
                    false,
                    ProtectedIngestSkipReason.None,
                    isAuthFailure,
                    transientFailure,
                    retryAfterSeconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordFailure(uploadUrl, GetUtcNow(), recordUploadAttempt, activeLease);
                return new ProtectedIngestResult(null, ex.Message, false, ProtectedIngestSkipReason.None, false, true, null);
            }
        }
    }

    private static void RecordSuccess(UploadUrl uploadUrl, bool recordUploadAttempt, UploadAttemptLease? activeLease)
    {
        if (!recordUploadAttempt)
        {
            return;
        }

        if (activeLease is not null)
        {
            activeLease.RecordSuccess();
        }
        else
        {
            UploadAttemptRecorder.RecordSuccess(uploadUrl);
        }
    }

    private static void RecordFailure(UploadUrl uploadUrl, DateTime utcNow, bool recordUploadAttempt, UploadAttemptLease? activeLease)
    {
        if (!recordUploadAttempt)
        {
            return;
        }

        if (activeLease is not null)
        {
            activeLease.RecordFailure(utcNow);
        }
        else
        {
            UploadAttemptRecorder.RecordFailure(uploadUrl, utcNow);
        }
    }

    private DateTime GetUtcNow() => NormalizeUtc(_utcNow());

    private static DateTime NormalizeUtc(DateTime now)
    {
        if (now == DateTime.MinValue || now == DateTime.MaxValue)
        {
            return DateTime.SpecifyKind(now, DateTimeKind.Utc);
        }

        return now.Kind switch
        {
            DateTimeKind.Utc => now,
            DateTimeKind.Local => now.ToUniversalTime(),
            _ => DateTime.SpecifyKind(now, DateTimeKind.Utc),
        };
    }

    private static HttpRequestMessage CreateRequest(
        Configuration configuration,
        IngestEndpoint endpoint,
        HttpMethod method,
        string? jsonBody,
        string? capabilityToken,
        IReadOnlyDictionary<string, string>? extraHeaders)
    {
        if (method == HttpMethod.Get)
        {
            return IngestRequestFactory.CreateGetRequest(configuration, endpoint, capabilityToken, extraHeaders);
        }

        if (method == HttpMethod.Post)
        {
            return IngestRequestFactory.CreatePostJsonRequest(configuration, endpoint, jsonBody ?? string.Empty, capabilityToken, extraHeaders);
        }

        throw new NotSupportedException($"Unsupported protected ingest method: {method.Method}");
    }
}
