using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace RemotePartyFinderReborn;

internal static class IngestRequestFactory {
    private const string SignatureVersion = "v1";
    private const string DefaultSharedSecret = "rpf-reborn-public-ingest-v1";
    private static readonly object ConfigLock = new();

    public static HttpRequestMessage CreateGetRequest(
        Configuration configuration,
        IngestEndpoint endpoint,
        string capabilityToken = null,
        IReadOnlyDictionary<string, string> extraHeaders = null
    ) {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
        AddIngestHeaders(configuration, request, endpoint.Route.Path, capabilityToken);
        AddExtraHeaders(request, extraHeaders);
        return request;
    }

    public static HttpRequestMessage CreatePostJsonRequest(
        Configuration configuration,
        IngestEndpoint endpoint,
        string jsonBody,
        string capabilityToken = null,
        IReadOnlyDictionary<string, string> extraHeaders = null
    ) {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        AddIngestHeaders(configuration, request, endpoint.Route.Path, capabilityToken);
        AddExtraHeaders(request, extraHeaders);
        return request;
    }

    public static int? ReadRetryAfterSeconds(HttpResponseMessage response) {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) {
            return null;
        }

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) {
            return null;
        }

        if (int.TryParse(raw, out var seconds) && seconds >= 0) {
            return seconds;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var retryAt)) {
            var remaining = retryAt - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds <= 0) {
                return 0;
            }

            return (int)Math.Ceiling(remaining.TotalSeconds);
        }

        return null;
    }

    private static void AddIngestHeaders(
        Configuration configuration,
        HttpRequestMessage request,
        string routePath,
        string capabilityToken
    ) {
        var clientId = GetOrCreateClientId(configuration);
        var secret = string.IsNullOrWhiteSpace(configuration.IngestSharedSecret)
            ? DefaultSharedSecret
            : configuration.IngestSharedSecret.Trim();

        var normalizedPath = NormalizePath(routePath, request.RequestUri?.AbsolutePath);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var signaturePayload = string.Concat(
            request.Method.Method,
            "\n",
            normalizedPath,
            "\n",
            timestamp,
            "\n",
            nonce,
            "\n",
            clientId
        );

        var signature = ComputeSignature(secret, signaturePayload);

        request.Headers.TryAddWithoutValidation("X-RPF-Client-Id", clientId);
        request.Headers.TryAddWithoutValidation("X-RPF-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-RPF-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-RPF-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-RPF-Signature-Version", SignatureVersion);
        if (!string.IsNullOrWhiteSpace(capabilityToken)) {
            request.Headers.TryAddWithoutValidation("X-RPF-Capability", capabilityToken.Trim());
        }
    }

    private static void AddExtraHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> extraHeaders
    ) {
        if (extraHeaders == null) {
            return;
        }

        foreach (var (name, value) in extraHeaders) {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value.Trim());
        }
    }

    private static string GetOrCreateClientId(Configuration configuration) {
        lock (ConfigLock) {
            if (Guid.TryParse(configuration.IngestClientId, out _)) {
                return configuration.IngestClientId;
            }

            configuration.IngestClientId = Guid.NewGuid().ToString("N");
            configuration.Save();
            return configuration.IngestClientId;
        }
    }

    private static string ComputeSignature(string secret, string payload) {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(bytes);
    }

    private static string NormalizePath(string configuredPath, string requestPath) {
        var path = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath.Trim()
            : (requestPath ?? "/");

        if (!path.StartsWith('/')) {
            path = "/" + path;
        }

        return path;
    }
}
