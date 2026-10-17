using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn.Tomestone;

public sealed class TomestoneApiClient : IDisposable
{
    private static readonly Uri DefaultBaseUri = new("https://tomestone.gg/api/");
    private static readonly TimeSpan RateLimitCooldownDuration = TimeSpan.FromHours(1);

    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _infoLog;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _debugLog;
    private readonly Action<ApiCredentialValidationState, string> _credentialStatusChanged;
    private readonly RateLimitCooldownState _rateLimitCooldown = new();

    public TomestoneApiClient(Configuration configuration)
        : this(configuration, null)
    {
    }

    internal TomestoneApiClient(
        Configuration configuration,
        Action<ApiCredentialValidationState, string> credentialStatusChanged)
        : this(
            configuration,
            new HttpClient(),
            static () => DateTime.UtcNow,
            message => Plugin.Log.Info(message),
            message => Plugin.Log.Warning(message),
            message => Plugin.Log.Debug(message),
            credentialStatusChanged)
    {
    }

    internal TomestoneApiClient(
        Configuration configuration,
        HttpMessageHandler messageHandler,
        Func<DateTime> utcNow = null,
        Action<string> infoLog = null,
        Action<string> warningLog = null,
        Action<string> debugLog = null,
        Action<ApiCredentialValidationState, string> credentialStatusChanged = null)
        : this(
            configuration,
            new HttpClient(messageHandler),
            utcNow ?? (static () => DateTime.UtcNow),
            infoLog ?? (static _ => { }),
            warningLog ?? (static _ => { }),
            debugLog ?? (static _ => { }),
            credentialStatusChanged)
    {
    }

    private TomestoneApiClient(
        Configuration configuration,
        HttpClient httpClient,
        Func<DateTime> utcNow,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> debugLog,
        Action<ApiCredentialValidationState, string> credentialStatusChanged)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _infoLog = infoLog ?? throw new ArgumentNullException(nameof(infoLog));
        _warningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _credentialStatusChanged = credentialStatusChanged;
    }

    public DateTime RateLimitCooldownUntilUtc
        => _rateLimitCooldown.CooldownUntilUtc;

    public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        => _rateLimitCooldown.TryGetRemaining(_utcNow(), out remaining);

    public void ResetRateLimitCooldown()
    {
        _rateLimitCooldown.Reset();
        _infoLog("Tomestone API rate-limit cooldown was reset manually.");
    }

    public async Task<TomestoneRawResponse> GetRawAsync(string path, CancellationToken cancellationToken)
    {
        var outcome = await GetRawOutcomeAsync(path, cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? outcome.Value : null;
    }

    internal async Task<OperationOutcome<TomestoneRawResponse>> GetRawOutcomeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (TryGetRateLimitRemaining(out var cooldownRemaining))
        {
            LogCooldownSkipIfNeeded(cooldownRemaining);
            return OperationOutcome<TomestoneRawResponse>.Failure(
                transientFailure: true,
                "Tomestone API rate-limit cooldown is active.");
        }

        var requestUri = ResolveUri(path);
        using var request = CreateAuthenticatedGet(requestUri);

        try
        {
            _debugLog($"Tomestone API GET {requestUri}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _debugLog($"Tomestone API GET {requestUri} returned {(int)response.StatusCode} with {body.Length} byte(s).");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                ReportCredentialStatus(
                    ApiCredentialValidationState.RequestFailed,
                    "Tomestone rate-limited the request; API key validity was not confirmed.");
                ActivateRateLimitCooldown($"GET {requestUri}");
                return OperationOutcome<TomestoneRawResponse>.Failure(
                    transientFailure: true,
                    "Tomestone API request was rate limited.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var activityHidden = response.StatusCode == HttpStatusCode.Forbidden
                    && body.Contains("hidden", StringComparison.OrdinalIgnoreCase);
                var credentialState = activityHidden
                    ? ApiCredentialValidationState.Accepted
                    : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? ApiCredentialValidationState.Rejected
                        : response.StatusCode == HttpStatusCode.NotFound
                            ? ApiCredentialValidationState.NotChecked
                            : ApiCredentialValidationState.RequestFailed;
                var credentialMessage = activityHidden
                    ? "Tomestone accepted the API key, but this character's activity is hidden."
                    : credentialState == ApiCredentialValidationState.Rejected
                        ? $"Tomestone rejected the API key (HTTP {statusCode})."
                        : response.StatusCode == HttpStatusCode.NotFound
                            ? "Tomestone returned no matching record (HTTP 404); API key validity was not confirmed."
                            : $"Tomestone request failed (HTTP {statusCode}); API key validity was not confirmed.";
                ReportCredentialStatus(credentialState, credentialMessage);
                if (!activityHidden)
                {
                    _warningLog(
                        $"Tomestone API GET {requestUri} failed with {(int)response.StatusCode} ({response.StatusCode}); body length {body.Length} byte(s).");
                }
            }
            else
            {
                var configuredApiKey = !string.IsNullOrWhiteSpace(_configuration.TomestoneApiKey);
                ReportCredentialStatus(
                    configuredApiKey ? ApiCredentialValidationState.Accepted : ApiCredentialValidationState.NotChecked,
                    configuredApiKey
                        ? $"Tomestone accepted the request (HTTP {(int)response.StatusCode})."
                        : "Tomestone request succeeded without an API key; key validity was not checked.");
            }

            return OperationOutcome<TomestoneRawResponse>.Success(new TomestoneRawResponse
            {
                StatusCode = response.StatusCode,
                Body = body,
                ActivityHidden = response.StatusCode == HttpStatusCode.Forbidden
                    && body.Contains("hidden", StringComparison.OrdinalIgnoreCase),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportCredentialStatus(ApiCredentialValidationState.RequestFailed, "Could not reach Tomestone to check the API key.");
            _warningLog($"Tomestone API GET {requestUri} failed: {ex.GetType().Name}: {ex.Message}");
            return OperationOutcome<TomestoneRawResponse>.Failure(
                transientFailure: true,
                $"Tomestone API GET failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private void ActivateRateLimitCooldown(string source)
    {
        _rateLimitCooldown.Activate(_utcNow(), RateLimitCooldownDuration);

        _warningLog(
            $"Tomestone API rate limited at {source}. Pausing Tomestone requests until {RateLimitCooldownUntilUtc:O} (1 hour lockout).");
    }

    private void ReportCredentialStatus(ApiCredentialValidationState state, string message)
        => _credentialStatusChanged?.Invoke(state, message);

    private void LogCooldownSkipIfNeeded(TimeSpan remaining)
    {
        if (!_rateLimitCooldown.ShouldLogSkip(_utcNow(), TimeSpan.FromMinutes(1)))
        {
            return;
        }

        var minutesRemaining = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        _warningLog($"Tomestone cooldown active. Skipping Tomestone API request for about {minutesRemaining} minute(s).");
    }

    private HttpRequestMessage CreateAuthenticatedGet(Uri requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var apiKey = _configuration.TomestoneApiKey?.Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    private static Uri ResolveUri(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        var normalizedPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.TrimStart('/');
        return new Uri(DefaultBaseUri, normalizedPath);
    }
}
