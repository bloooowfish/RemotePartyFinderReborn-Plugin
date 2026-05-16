using System;
using System.Collections.Generic;
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

    internal async Task<ProtectedIngestResult> ExecuteAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IngestRoute route,
        ProtectedEndpointCapabilityKind capabilityKind,
        HttpMethod method,
        string? jsonBody,
        bool recordUploadAttempt,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
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
                RecordSuccess(uploadUrl, recordUploadAttempt);
                return new ProtectedIngestResult(response.StatusCode, body, true, ProtectedIngestSkipReason.None, false, false, null);
            }

            var isAuthFailure = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (isAuthFailure)
            {
                uploadUrl.RequireProtectedCapabilities();
                uploadUrl.InvalidateProtectedCapability(capabilityKind);
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                RecordFailure(uploadUrl, nowUtc, recordUploadAttempt);
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
        catch (Exception ex)
        {
            RecordFailure(uploadUrl, GetUtcNow(), recordUploadAttempt);
            return new ProtectedIngestResult(null, ex.Message, false, ProtectedIngestSkipReason.None, false, true, null);
        }
    }

    private static void RecordSuccess(UploadUrl uploadUrl, bool recordUploadAttempt)
    {
        if (recordUploadAttempt)
        {
            UploadAttemptRecorder.RecordSuccess(uploadUrl);
        }
    }

    private static void RecordFailure(UploadUrl uploadUrl, DateTime utcNow, bool recordUploadAttempt)
    {
        if (recordUploadAttempt)
        {
            UploadAttemptRecorder.RecordFailure(uploadUrl, utcNow);
        }
    }

    private DateTime GetUtcNow()
    {
        var now = _utcNow();
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
