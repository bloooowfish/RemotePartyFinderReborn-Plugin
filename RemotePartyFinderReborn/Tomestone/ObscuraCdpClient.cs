using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RemotePartyFinderReborn.Tomestone;

internal interface ITomestoneCdpHydrator
{
    Task<string> HydrateUrlAsync(string endpointUrl, string targetUrl, CancellationToken cancellationToken);
    Task<string> FetchJsonUrlAsync(string endpointUrl, string targetUrl, CancellationToken cancellationToken);
}

internal enum CdpExtractionMode
{
    InertiaDataPage,
    JsonPayload,
}

/// <summary>
/// Tier 3 CDP WebSocket hydrator for <c>obscura</c> / Chromium remote debugging endpoints.
/// Uses standard CDP commands (<c>Target.createTarget</c>, <c>Target.attachToTarget</c>,
/// <c>Page.navigate</c>, <c>Runtime.evaluate</c>) and aborts immediately on any anti-bot/CAPTCHA DOM marker.
/// Never launches processes with <c>--stealth</c> and never bypasses or waits out challenges.
/// </summary>
internal sealed class ObscuraCdpClient : ITomestoneCdpHydrator
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultNavigationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CloseTargetTimeout = TimeSpan.FromSeconds(3);

    private const string HydrationProbeExpression = """
        (() => {
            const title = (document.title || '').toLowerCase();
            const headHtml = (document.head && document.head.innerHTML ? document.head.innerHTML : '').toLowerCase();
            const bodyText = (document.body && document.body.innerText ? document.body.innerText.slice(0, 8192) : '').toLowerCase();
            const hasTurnstile = Boolean(document.querySelector('.cf-turnstile, [data-cf-turnstile], iframe[src*="challenges.cloudflare.com"]'));
            const hasRecaptcha = Boolean(document.querySelector('.g-recaptcha, iframe[src*="recaptcha"]'));
            const hasHCaptcha = Boolean(document.querySelector('.h-captcha, iframe[src*="hcaptcha.com"]'));
            let challenge = '';
            if (hasTurnstile || bodyText.includes('cf-turnstile') || headHtml.includes('cf-turnstile')) {
                challenge = 'cf-turnstile';
            } else if (hasRecaptcha || bodyText.includes('g-recaptcha') || headHtml.includes('g-recaptcha')) {
                challenge = 'g-recaptcha';
            } else if (hasHCaptcha || bodyText.includes('hcaptcha') || headHtml.includes('hcaptcha')) {
                challenge = 'hcaptcha';
            } else if (headHtml.includes('cf-mitigated') || bodyText.includes('cf-mitigated')) {
                challenge = 'cf-mitigated';
            } else if (title.includes('just a moment...') || bodyText.includes('just a moment...')) {
                challenge = 'just a moment...';
            }
            const appEl = document.querySelector('#app');
            const dataPage = appEl ? (appEl.getAttribute('data-page') || '') : '';
            const preEl = document.querySelector('pre');
            const preText = preEl ? (preEl.innerText || preEl.textContent || '') : '';
            const rawBodyText = document.body && document.body.innerText ? document.body.innerText : '';
            return JSON.stringify({ challenge, dataPage, preText: preText.slice(0, 524288), rawBodyText: rawBodyText.slice(0, 524288) });
        })()
        """;

    private readonly TimeSpan _navigationTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _warningLog;
    private readonly Func<string> _bearerTokenProvider;

    public ObscuraCdpClient(
        Action<string> debugLog = null,
        Action<string> warningLog = null,
        TimeSpan? navigationTimeout = null,
        TimeSpan? pollInterval = null,
        Func<string> bearerTokenProvider = null)
    {
        _debugLog = debugLog ?? (static _ => { });
        _warningLog = warningLog ?? (static _ => { });
        _navigationTimeout = navigationTimeout ?? DefaultNavigationTimeout;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _bearerTokenProvider = bearerTokenProvider;
    }

    public static bool TryValidateEndpointUri(string endpointUrl, out Uri uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public Task<string> HydrateUrlAsync(
        string endpointUrl,
        string targetUrl,
        CancellationToken cancellationToken)
        => ExtractUrlAsync(endpointUrl, targetUrl, CdpExtractionMode.InertiaDataPage, cancellationToken);

    public Task<string> FetchJsonUrlAsync(
        string endpointUrl,
        string targetUrl,
        CancellationToken cancellationToken)
        => ExtractUrlAsync(endpointUrl, targetUrl, CdpExtractionMode.JsonPayload, cancellationToken);

    private async Task<string> ExtractUrlAsync(
        string endpointUrl,
        string targetUrl,
        CdpExtractionMode mode,
        CancellationToken cancellationToken)
    {
        if (!TryValidateEndpointUri(endpointUrl, out var endpointUri))
        {
            throw new InvalidOperationException("Tomestone obscura CDP URL must be an absolute ws:// or wss:// WebSocket URI.");
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (!string.Equals(targetUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Target URL must be an absolute http:// or https:// URI.", nameof(targetUrl));
        }

        var bearerToken = _bearerTokenProvider?.Invoke()?.Trim();
        using var ws = await ConnectWebSocketAsync(endpointUri, bearerToken, cancellationToken).ConfigureAwait(false);

        var nextId = 0;
        string targetId = null;

        try
        {
            var createResult = await SendCdpCommandAsync(
                ws,
                Interlocked.Increment(ref nextId),
                "Target.createTarget",
                new JObject { ["url"] = "about:blank" },
                sessionId: null,
                cancellationToken).ConfigureAwait(false);
            targetId = createResult["targetId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new InvalidOperationException("CDP Target.createTarget did not return a targetId.");
            }

            var attachResult = await SendCdpCommandAsync(
                ws,
                Interlocked.Increment(ref nextId),
                "Target.attachToTarget",
                new JObject
                {
                    ["targetId"] = targetId,
                    ["flatten"] = true,
                },
                sessionId: null,
                cancellationToken).ConfigureAwait(false);
            var sessionId = attachResult["sessionId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidOperationException("CDP Target.attachToTarget did not return a sessionId.");
            }

            await SendCdpCommandAsync(
                ws,
                Interlocked.Increment(ref nextId),
                "Page.enable",
                new JObject(),
                sessionId,
                cancellationToken).ConfigureAwait(false);

            await SendCdpCommandAsync(
                ws,
                Interlocked.Increment(ref nextId),
                "Page.navigate",
                new JObject { ["url"] = targetUri.ToString() },
                sessionId,
                cancellationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.Add(_navigationTimeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var evalResult = await SendCdpCommandAsync(
                    ws,
                    Interlocked.Increment(ref nextId),
                    "Runtime.evaluate",
                    new JObject
                    {
                        ["expression"] = HydrationProbeExpression,
                        ["returnByValue"] = true,
                    },
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

                var probeJson = evalResult["result"]?["value"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(probeJson))
                {
                    var probe = JObject.Parse(probeJson);
                    var challengeMarker = probe["challenge"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(challengeMarker))
                    {
                        var detected = TomestoneChallengeDetector.DetectTextChallenge(challengeMarker)
                            ?? new TomestoneChallengeInfo(TomestoneChallengeKind.CloudflareJustAMoment, challengeMarker);
                        _warningLog($"Tomestone Tier 3 CDP detected challenge ({detected.Label}) at {targetUri}; aborting immediately.");
                        throw new TomestoneChallengeException(
                            detected,
                            $"Tomestone Tier 3 CDP aborted on anti-bot challenge ({detected.Label}).");
                    }

                    var extractedPayload = ExtractPayloadFromProbe(probe, mode);
                    if (!string.IsNullOrEmpty(extractedPayload))
                    {
                        _debugLog($"Tomestone Tier 3 CDP extracted {mode} payload for {targetUri}.");
                        return extractedPayload;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    var expectedLabel = mode == CdpExtractionMode.JsonPayload ? "JSON payload" : "#app[data-page]";
                    throw new TimeoutException($"Tomestone Tier 3 CDP timed out waiting for {expectedLabel} at {targetUri}.");
                }

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await CloseTargetSafelyAsync(ws, targetId, () => Interlocked.Increment(ref nextId)).ConfigureAwait(false);
            await CloseWebSocketSafelyAsync(ws).ConfigureAwait(false);
        }
    }

    private static string ExtractPayloadFromProbe(JObject probe, CdpExtractionMode mode)
    {
        if (mode == CdpExtractionMode.InertiaDataPage)
        {
            var dataPage = probe["dataPage"]?.Value<string>()?.Trim();
            if (!string.IsNullOrEmpty(dataPage) && dataPage.StartsWith('{'))
            {
                return dataPage;
            }
        }

        var preJson = TryExtractValidJson(probe["preText"]?.Value<string>());
        if (preJson is not null)
        {
            return preJson;
        }

        var bodyJson = TryExtractValidJson(probe["rawBodyText"]?.Value<string>());
        if (bodyJson is not null)
        {
            return bodyJson;
        }

        if (mode == CdpExtractionMode.JsonPayload)
        {
            var dataPageFallback = probe["dataPage"]?.Value<string>()?.Trim();
            if (TryExtractValidJson(dataPageFallback) is { } validDataPage)
            {
                return validDataPage;
            }
        }

        return null;
    }

    private static string TryExtractValidJson(string raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed) || (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')))
        {
            return null;
        }

        try
        {
            _ = JToken.Parse(trimmed);
            return trimmed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClientWebSocket CreateConfiguredWebSocket(string bearerToken)
    {
        var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        }

        return ws;
    }

    private static async Task<ClientWebSocket> ConnectWebSocketAsync(
        Uri endpointUri,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        var hasExplicitPath = endpointUri.AbsolutePath.Length > 1;
        if (hasExplicitPath)
        {
            var directWs = CreateConfiguredWebSocket(bearerToken);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(DefaultConnectTimeout);
                await directWs.ConnectAsync(endpointUri, connectCts.Token).ConfigureAwait(false);
                return directWs;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                directWs.Dispose();
                var discovered = await TryDiscoverWebSocketDebuggerUrlAsync(endpointUri, bearerToken, cancellationToken)
                    .ConfigureAwait(false);
                if (discovered is null || Uri.Compare(discovered, endpointUri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0)
                {
                    throw;
                }

                var discoveredWs = CreateConfiguredWebSocket(bearerToken);
                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(DefaultConnectTimeout);
                    await discoveredWs.ConnectAsync(discovered, connectCts.Token).ConfigureAwait(false);
                    return discoveredWs;
                }
                catch
                {
                    discoveredWs.Dispose();
                    throw;
                }
            }
        }

        var resolved = await TryDiscoverWebSocketDebuggerUrlAsync(endpointUri, bearerToken, cancellationToken)
            .ConfigureAwait(false)
            ?? new UriBuilder(endpointUri) { Path = "/devtools/browser" }.Uri;
        var ws = CreateConfiguredWebSocket(bearerToken);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(DefaultConnectTimeout);
            await ws.ConnectAsync(resolved, connectCts.Token).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static async Task<Uri> TryDiscoverWebSocketDebuggerUrlAsync(
        Uri endpointUri,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var versionUri = new UriBuilder(endpointUri)
            {
                Scheme = string.Equals(endpointUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
                Path = "/json/version",
                Query = string.Empty,
            }.Uri;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
            using var response = await http.GetAsync(versionUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JObject.Parse(json);
            var rawDebuggerUrl = parsed["webSocketDebuggerUrl"]?.Value<string>()?.Trim();
            return RewriteWebSocketDebuggerUrl(endpointUri, rawDebuggerUrl);
        }
        catch
        {
            return null;
        }
    }

    internal static Uri RewriteWebSocketDebuggerUrl(Uri endpointUri, string discoveredWsUrl)
    {
        if (string.IsNullOrWhiteSpace(discoveredWsUrl))
        {
            return null;
        }

        if (discoveredWsUrl.StartsWith('/'))
        {
            return new UriBuilder(endpointUri) { Path = discoveredWsUrl, Query = string.Empty }.Uri;
        }

        if (!Uri.TryCreate(discoveredWsUrl, UriKind.Absolute, out var parsed)
            || (!string.Equals(parsed.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsed.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new UriBuilder(endpointUri)
        {
            Path = parsed.AbsolutePath,
            Query = parsed.Query.TrimStart('?'),
        }.Uri;
    }

    private async Task<JToken> SendCdpCommandAsync(
        ClientWebSocket ws,
        int id,
        string method,
        JObject parameters,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var payload = new JObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JObject(),
        };
        if (!string.IsNullOrEmpty(sessionId))
        {
            payload["sessionId"] = sessionId;
        }

        var bytes = Encoding.UTF8.GetBytes(payload.ToString());
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            var rawMessage = await ReceiveTextMessageAsync(ws, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                continue;
            }

            var message = JObject.Parse(rawMessage);
            var responseIdToken = message["id"];
            if (responseIdToken == null || responseIdToken.Type != JTokenType.Integer)
            {
                continue;
            }

            if (responseIdToken.Value<int>() != id)
            {
                continue;
            }

            if (message["error"] is JObject errorObj)
            {
                var errorMessage = errorObj["message"]?.Value<string>() ?? "Unknown CDP error";
                throw new InvalidOperationException($"CDP {method} failed: {errorMessage}");
            }

            return message["result"] ?? new JObject();
        }
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await ws.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("CDP WebSocket closed before response was received.");
                }

                if (result.Count > 0)
                {
                    if (stream.Length + result.Count > MaxMessageBytes)
                    {
                        throw new InvalidOperationException("CDP WebSocket message exceeded maximum size.");
                    }

                    stream.Write(buffer, 0, result.Count);
                }

                if (result.EndOfMessage)
                {
                    return result.MessageType == WebSocketMessageType.Text
                        ? Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length)
                        : string.Empty;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task CloseTargetSafelyAsync(ClientWebSocket ws, string targetId, Func<int> nextIdFactory)
    {
        if (string.IsNullOrWhiteSpace(targetId) || ws.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            using var closeCts = new CancellationTokenSource(CloseTargetTimeout);
            await SendCdpCommandAsync(
                ws,
                nextIdFactory(),
                "Target.closeTarget",
                new JObject { ["targetId"] = targetId },
                sessionId: null,
                closeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _debugLog($"Tomestone Tier 3 CDP Target.closeTarget cleanup failed: {ex.Message}");
        }
    }

    private static async Task CloseWebSocketSafelyAsync(ClientWebSocket ws)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Ignore WebSocket teardown errors in finally block.
            }
        }
    }
}
