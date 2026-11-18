using System;
using System.Net;
using System.Net.Http;

namespace RemotePartyFinderReborn;

internal static class PluginHttpClientFactory {
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan IngestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ExternalApiTimeout = TimeSpan.FromSeconds(20);
    internal const long MaxResponseContentBufferSizeBytes = 8 * 1024 * 1024;

    internal static SocketsHttpHandler CreateSocketsHandler(bool allowAutoRedirect = true) {
        return new SocketsHttpHandler {
            PooledConnectionLifetime = PooledConnectionLifetime,
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowAutoRedirect,
        };
    }

    internal static HttpClient CreateIngestClient(TimeSpan? timeout = null) {
        return CreateClient(
            CreateSocketsHandler(allowAutoRedirect: true),
            disposeHandler: true,
            timeout ?? IngestTimeout);
    }

    internal static HttpClient CreateExternalApiClient(bool allowAutoRedirect = true, TimeSpan? timeout = null) {
        return CreateClient(
            CreateSocketsHandler(allowAutoRedirect),
            disposeHandler: true,
            timeout ?? ExternalApiTimeout);
    }

    internal static HttpClient CreateClient(
        HttpMessageHandler handler,
        bool disposeHandler = true,
        TimeSpan? timeout = null) {
        ArgumentNullException.ThrowIfNull(handler);
        return new HttpClient(handler, disposeHandler) {
            Timeout = timeout ?? ExternalApiTimeout,
            MaxResponseContentBufferSize = MaxResponseContentBufferSizeBytes,
        };
    }
}
