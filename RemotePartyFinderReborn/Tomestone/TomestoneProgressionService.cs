using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RemotePartyFinderReborn.Tomestone;

internal readonly record struct TomestoneProgressionFetchResult(
    OperationOutcome<TomestoneProgressData> Outcome,
    bool PerformedHttpRequest
);

internal interface ITomestoneProgressionService
{
    bool IsConfigured { get; }
    DateTime RateLimitCooldownUntilUtc { get; }
    bool TryGetRateLimitRemaining(out TimeSpan remaining);
    void ResetRateLimitCooldown();
    Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken);
    async Task<TomestoneProgressionFetchResult> FetchProgressionDataWithTelemetryAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken)
    {
        var outcome = await FetchProgressionDataAsync(
            name,
            server,
            zoneId,
            encounterId,
            cancellationToken).ConfigureAwait(false);
        return new TomestoneProgressionFetchResult(outcome, PerformedHttpRequest: true);
    }
    Task<TomestoneProgressionFetchResult> FetchProgressionDataWithTelemetryAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        TomestoneEncounterParams encounterParams,
        CancellationToken cancellationToken)
        => FetchProgressionDataWithTelemetryAsync(
            name,
            server,
            zoneId,
            encounterId,
            cancellationToken);
    bool TryGetCachedProgressionData(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        out OperationOutcome<TomestoneProgressData> outcome)
    {
        outcome = default;
        return false;
    }
    bool TryGetCachedProgressionData(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        TomestoneEncounterParams encounterParams,
        out OperationOutcome<TomestoneProgressData> outcome)
        => TryGetCachedProgressionData(
            name,
            server,
            zoneId,
            encounterId,
            out outcome);
    IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
        string name,
        string server,
        uint zoneId,
        IReadOnlyCollection<uint> excludeEncounterIds);
}

internal sealed class TomestoneProgressionService : ITomestoneProgressionService
{
    private sealed record CachedProfileEntry(
        DateTime ExpiresAt,
        TomestoneRawResponse Response,
        JToken ParsedRoot);

    private readonly record struct CachedProfileFetchResult(
        OperationOutcome<CachedProfileEntry> Outcome,
        bool PerformedHttpRequest);

    private static readonly object ShapeDiagnosticLock = new();
    private static readonly HashSet<string> LoggedShapeDiagnostics = new(StringComparer.Ordinal);

    private readonly TomestoneApiClient _client;
    private readonly Configuration _configuration;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _warningLog;
    private readonly Func<DateTime> _utcNow;
    private readonly object _profileCacheLock = new();
    private readonly Dictionary<(string Path, string Credential), CachedProfileEntry> _profileCache = new();
    private readonly ConcurrentDictionary<(string Path, string Credential), Lazy<Task<CachedProfileFetchResult>>> _profileRequests = new();

    internal TomestoneProgressionService(
        TomestoneApiClient client,
        Configuration configuration,
        Action<string> debugLog = null,
        Action<string> warningLog = null,
        Func<DateTime> utcNow = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _debugLog = debugLog ?? (static _ => { });
        _warningLog = warningLog ?? (static _ => { });
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    public bool IsConfigured
        => _configuration.EnableTomestoneProgressEnrichment && _configuration.HasTomestoneApiKeyConfigured;

    public DateTime RateLimitCooldownUntilUtc
        => _client.RateLimitCooldownUntilUtc;

    public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        => _client.TryGetRateLimitRemaining(out remaining);

    public void ResetRateLimitCooldown()
        => _client.ResetRateLimitCooldown();

    public IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
        string name,
        string server,
        uint zoneId,
        IReadOnlyCollection<uint> excludeEncounterIds)
    {
        var merged = new Dictionary<uint, TomestoneProgressData>();
        var zoneTargets = TomestoneEncounterMapping.GetProgressionTargetsForZone(zoneId);
        if (zoneTargets.Count == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(server))
        {
            return merged;
        }

        var profilePath = BuildCharacterProfilePath(name, server);
        var key = (Path: profilePath, Credential: _configuration.TomestoneApiKey?.Trim() ?? string.Empty);
        JToken cachedRoot = null;
        var now = _utcNow();
        lock (_profileCacheLock)
        {
            if (_profileCache.TryGetValue(key, out var cached)
                && cached.ExpiresAt > now
                && cached.ParsedRoot != null
                && !cached.Response.ActivityHidden)
            {
                cachedRoot = cached.ParsedRoot;
            }
        }

        if (cachedRoot == null)
        {
            return merged;
        }

        foreach (var (encounterId, targetParams) in zoneTargets)
        {
            if (excludeEncounterIds != null && excludeEncounterIds.Contains(encounterId))
            {
                continue;
            }

            var progress = TomestoneProgressParser.ParseProgress(cachedRoot, targetParams);
            if (progress.IsAuthoritative
                && (progress.Cleared || progress.BossPercentage.HasValue || progress.Phases.Count > 0))
            {
                merged[encounterId] = progress;
            }
        }

        return merged;
    }

    public bool TryGetCachedProgressionData(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        out OperationOutcome<TomestoneProgressData> outcome)
        => TryGetCachedProgressionData(
            name,
            server,
            zoneId,
            encounterId,
            encounterParams: null,
            out outcome);

    public bool TryGetCachedProgressionData(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        TomestoneEncounterParams encounterParams,
        out OperationOutcome<TomestoneProgressData> outcome)
    {
        if (!IsConfigured
            || _client.TryGetRateLimitRemaining(out _)
            || (encounterParams is null && !TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out encounterParams)))
        {
            outcome = default;
            return false;
        }

        var profilePath = BuildCharacterProfilePath(name, server);
        var key = (Path: profilePath, Credential: _configuration.TomestoneApiKey?.Trim() ?? string.Empty);
        var now = _utcNow();
        CachedProfileEntry cachedProfile = null;
        lock (_profileCacheLock)
        {
            if (_profileCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            {
                cachedProfile = cached;
            }
        }

        if (cachedProfile == null)
        {
            outcome = default;
            return false;
        }

        var targetDescription = FormatTomestoneProgressTarget(zoneId, encounterId, encounterParams);
        return TryResolveProfileEntryOutcome(cachedProfile, zoneId, encounterId, encounterParams, targetDescription, out outcome);
    }

    public async Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken)
    {
        var result = await FetchProgressionDataWithTelemetryAsync(
            name,
            server,
            zoneId,
            encounterId,
            cancellationToken).ConfigureAwait(false);
        return result.Outcome;
    }

    public Task<TomestoneProgressionFetchResult> FetchProgressionDataWithTelemetryAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken)
        => FetchProgressionDataWithTelemetryAsync(
            name,
            server,
            zoneId,
            encounterId,
            encounterParams: null,
            cancellationToken);

    public async Task<TomestoneProgressionFetchResult> FetchProgressionDataWithTelemetryAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        TomestoneEncounterParams encounterParams,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            _debugLog("Tomestone progress enrichment is disabled or missing an API key.");
            return new TomestoneProgressionFetchResult(
                OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Skipped),
                PerformedHttpRequest: false);
        }

        if (encounterParams is null
            && !TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out encounterParams))
        {
            _debugLog($"Tomestone progress skipped for unmapped encounter zone={zoneId}, encounter={encounterId}.");
            return new TomestoneProgressionFetchResult(
                OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Skipped),
                PerformedHttpRequest: false);
        }

        if (_client.TryGetRateLimitRemaining(out _))
        {
            return new TomestoneProgressionFetchResult(
                OperationOutcome<TomestoneProgressData>.Failure(
                    transientFailure: true,
                    "Tomestone API rate-limit cooldown is active."),
                PerformedHttpRequest: false);
        }

        var targetDescription = FormatTomestoneProgressTarget(zoneId, encounterId, encounterParams);
        var profilePath = BuildCharacterProfilePath(name, server);
        var profileFetch = await FetchProfileAsync(profilePath, cancellationToken).ConfigureAwait(false);
        var performedHttp = profileFetch.PerformedHttpRequest;
        var profileOutcome = profileFetch.Outcome;
        if (!profileOutcome.Succeeded)
        {
            _debugLog($"Tomestone profile returned no response for {targetDescription}.");
            return new TomestoneProgressionFetchResult(
                OperationOutcome<TomestoneProgressData>.Failure(
                    profileOutcome.TransientFailure,
                    profileOutcome.ErrorMessage ?? "Tomestone profile returned no response."),
                performedHttp);
        }

        var cachedProfile = profileOutcome.Value!;
        if (TryResolveProfileEntryOutcome(cachedProfile, zoneId, encounterId, encounterParams, targetDescription, out var profileResolvedOutcome))
        {
            return new TomestoneProgressionFetchResult(profileResolvedOutcome, performedHttp);
        }

        LogResponseShapeOnce("profile", zoneId, encounterId, targetDescription, cachedProfile.Response.Body, encounterParams);
        _debugLog($"Tomestone profile had no target progress for {targetDescription}; falling back to encounter activity.");

        var activityOutcome = await FetchEncounterActivityAsync(
            name,
            server,
            zoneId,
            encounterId,
            encounterParams,
            targetDescription,
            cancellationToken).ConfigureAwait(false);
        return new TomestoneProgressionFetchResult(activityOutcome, PerformedHttpRequest: true);
    }

    private bool TryResolveProfileEntryOutcome(
        CachedProfileEntry cachedProfile,
        uint zoneId,
        uint encounterId,
        TomestoneEncounterParams encounterParams,
        string targetDescription,
        out OperationOutcome<TomestoneProgressData> outcome)
    {
        var profileResponse = cachedProfile.Response;
        if (profileResponse.ActivityHidden)
        {
            _debugLog($"Tomestone profile activity is hidden for {targetDescription}.");
            outcome = OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            return true;
        }

        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
        {
            _debugLog($"Tomestone profile not found for {targetDescription}.");
            outcome = OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            return true;
        }

        if (profileResponse.StatusCode == HttpStatusCode.NoContent)
        {
            _debugLog($"Tomestone profile had no content for {targetDescription}.");
            outcome = OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            return true;
        }

        if (profileResponse.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            _warningLog($"Tomestone profile failed with {(int)profileResponse.StatusCode} for {targetDescription}.");
            outcome = OperationOutcome<TomestoneProgressData>.Failure(
                IsTransientStatusCode(profileResponse.StatusCode),
                $"Tomestone profile failed with {(int)profileResponse.StatusCode}.");
            return true;
        }

        var profileRoot = cachedProfile.ParsedRoot;
        if (profileRoot == null)
        {
            _warningLog($"Tomestone profile parse failed for {targetDescription}: profile JSON was invalid.");
            outcome = OperationOutcome<TomestoneProgressData>.Failure(
                transientFailure: false,
                "Tomestone profile parse failed.");
            return true;
        }

        var profileProgress = TomestoneProgressParser.ParseProgress(profileRoot, encounterParams);
        if (HasUsefulProgress(profileProgress, encounterParams.ProgressKind))
        {
            _debugLog(
                $"Tomestone profile parsed {profileProgress.Phases.Count} phase(s), boss={profileProgress.BossPercentage?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none"} for {targetDescription}.");
            outcome = OperationOutcome<TomestoneProgressData>.Success(profileProgress);
            return true;
        }

        outcome = default;
        return false;
    }

    private async Task<OperationOutcome<TomestoneProgressData>> FetchEncounterActivityAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        TomestoneEncounterParams encounterParams,
        string targetDescription,
        CancellationToken cancellationToken)
    {
        var activityPath = BuildEncounterActivityPath(name, server, encounterParams);
        var rawOutcome = await _client.GetRawOutcomeAsync(activityPath, cancellationToken).ConfigureAwait(false);
        if (!rawOutcome.Succeeded)
        {
            _debugLog($"Tomestone progress returned no response for {targetDescription}.");
            return OperationOutcome<TomestoneProgressData>.Failure(
                rawOutcome.TransientFailure,
                rawOutcome.ErrorMessage ?? "Tomestone progress returned no response.");
        }

        var response = rawOutcome.Value!;
        if (response.ActivityHidden)
        {
            _debugLog($"Tomestone encounter activity is hidden for {targetDescription}.");
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _debugLog($"Tomestone progress not found for {targetDescription}.");
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            _debugLog($"Tomestone progress had no content for {targetDescription}.");
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
        }

        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            _warningLog($"Tomestone progress failed with {(int)response.StatusCode} for {targetDescription}.");
            return OperationOutcome<TomestoneProgressData>.Failure(
                IsTransientStatusCode(response.StatusCode),
                $"Tomestone progress failed with {(int)response.StatusCode}.");
        }

        try
        {
            var activityRoot = JToken.Parse(response.Body);
            var progress = TomestoneProgressParser.ParseEncounterActivityProgress(activityRoot, encounterParams);
            if (!HasUsefulProgress(progress, encounterParams.ProgressKind))
            {
                LogResponseShapeOnce("activity", zoneId, encounterId, targetDescription, response.Body, encounterParams);
            }

            _debugLog(
                $"Tomestone progress parsed {progress.Phases.Count} phase(s), boss={progress.BossPercentage?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none"} for {targetDescription}.");
            return OperationOutcome<TomestoneProgressData>.Success(progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _warningLog($"Tomestone progress parse failed for {targetDescription}: {ex.GetType().Name}: {ex.Message}");
            return OperationOutcome<TomestoneProgressData>.Failure(
                transientFailure: false,
                $"Tomestone progress parse failed: {ex.Message}");
        }
    }

    private static bool HasUsefulProgress(
        TomestoneProgressData progress,
        TomestoneProgressKind progressKind)
        => progress.Cleared || (progressKind switch
        {
            TomestoneProgressKind.PhaseProgress => progress.Phases.Count > 0,
            TomestoneProgressKind.BossPercentage => progress.BossPercentage.HasValue
                || progress.Phases.Any(static phase => phase.BossPercentage.HasValue),
            _ => false,
        });

    internal int PendingProfileRequestCountForTesting => _profileRequests.Count;

    private async Task<CachedProfileFetchResult> FetchProfileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var key = (Path: path, Credential: _configuration.TomestoneApiKey?.Trim() ?? string.Empty);
        var now = _utcNow();
        lock (_profileCacheLock)
        {
            if (_profileCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            {
                return new CachedProfileFetchResult(
                    OperationOutcome<CachedProfileEntry>.Success(cached),
                    PerformedHttpRequest: false);
            }
        }

        var request = _profileRequests.GetOrAdd(key, k =>
        {
            Lazy<Task<CachedProfileFetchResult>> entry = null;
            entry = new Lazy<Task<CachedProfileFetchResult>>(
                () => FetchAndCacheProfileAsync(k, entry, cancellationToken));
            return entry;
        });

        return await request.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CachedProfileFetchResult> FetchAndCacheProfileAsync(
        (string Path, string Credential) key,
        Lazy<Task<CachedProfileFetchResult>> self,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = _utcNow();
            lock (_profileCacheLock)
            {
                if (_profileCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
                {
                    return new CachedProfileFetchResult(
                        OperationOutcome<CachedProfileEntry>.Success(cached),
                        PerformedHttpRequest: false);
                }
            }

            var outcome = await _client.GetRawOutcomeAsync(key.Path, cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded || outcome.Value is not { } response)
            {
                return new CachedProfileFetchResult(
                    OperationOutcome<CachedProfileEntry>.Failure(
                        outcome.TransientFailure,
                        outcome.ErrorMessage),
                    PerformedHttpRequest: true);
            }

            JToken parsedRoot = null;
            if (response.ActivityHidden
                || response.StatusCode == HttpStatusCode.NotFound
                || response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
            {
                if (!response.ActivityHidden
                    && response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
                    && response.StatusCode != HttpStatusCode.NoContent)
                {
                    try
                    {
                        var root = JToken.Parse(response.Body);
                        // Keep encounter and activity data; drop large cosmetic/gear payloads.
                        if (root is JObject profile && profile["encounters"] is { } encounters)
                        {
                            var trimmedProfile = new JObject
                            {
                                ["encounters"] = encounters,
                            };
                            if (profile["activity"] is { Type: not JTokenType.Null and not JTokenType.Undefined } activity)
                            {
                                trimmedProfile["activity"] = activity;
                            }

                            parsedRoot = trimmedProfile;
                            response = new TomestoneRawResponse
                            {
                                StatusCode = response.StatusCode,
                                Body = trimmedProfile.ToString(),
                            };
                        }
                        else
                        {
                            parsedRoot = root;
                        }
                    }
                    catch (JsonException ex)
                    {
                        return new CachedProfileFetchResult(
                            OperationOutcome<CachedProfileEntry>.Failure(
                                transientFailure: false,
                                $"Tomestone profile parse failed: {ex.Message}"),
                            PerformedHttpRequest: true);
                    }
                }

                var cacheNow = _utcNow();
                var entry = new CachedProfileEntry(cacheNow.AddMinutes(5), response, parsedRoot);
                lock (_profileCacheLock)
                {
                    foreach (var expired in _profileCache.Where(pair => pair.Value.ExpiresAt <= cacheNow).Select(pair => pair.Key).ToArray())
                    {
                        _profileCache.Remove(expired);
                    }

                    if (_profileCache.Count >= 128)
                    {
                        _profileCache.Remove(_profileCache.MinBy(static pair => pair.Value.ExpiresAt).Key);
                    }

                    _profileCache[key] = entry;
                }

                return new CachedProfileFetchResult(
                    OperationOutcome<CachedProfileEntry>.Success(entry),
                    PerformedHttpRequest: true);
            }

            var nonCachedEntry = new CachedProfileEntry(DateTime.MinValue, response, null);
            return new CachedProfileFetchResult(
                OperationOutcome<CachedProfileEntry>.Success(nonCachedEntry),
                PerformedHttpRequest: true);
        }
        finally
        {
            _profileRequests.TryRemove(
                new KeyValuePair<(string Path, string Credential), Lazy<Task<CachedProfileFetchResult>>>(key, self));
        }
    }

    private void LogResponseShapeOnce(
        string endpoint,
        uint zoneId,
        uint encounterId,
        string targetDescription,
        string body,
        TomestoneEncounterParams encounterParams = null)
    {
        var key = $"{endpoint}:{zoneId}:{encounterId}";
        lock (ShapeDiagnosticLock)
        {
            if (!LoggedShapeDiagnostics.Add(key))
            {
                return;
            }
        }

        var resolvedTarget = encounterParams
            ?? (TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var target)
                ? target
                : null);
        var shape = TomestoneProgressParser.DescribeResponseShape(body, resolvedTarget);
        _debugLog($"Tomestone response shape endpoint={endpoint} for {targetDescription}: {shape}.");
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout
            || code == 429
            || code >= 500;
    }

    private static string FormatTomestoneProgressTarget(
        uint zoneId,
        uint encounterId,
        TomestoneEncounterParams encounterParams)
    {
        return
            $"zone={zoneId}, encounter={encounterId}; "
            + $"tomestone expansion={encounterParams.Expansion}, "
            + $"zone={encounterParams.EffectiveZoneSlug}, "
            + $"encounter={encounterParams.Encounter}, "
            + $"kind={encounterParams.ProgressKind}";
    }

    private static string BuildEncounterActivityPath(
        string name,
        string server,
        TomestoneEncounterParams encounterParams)
    {
        static string Encode(string value)
            => Uri.EscapeDataString((value ?? string.Empty).Trim());

        return
            $"character/activity/{Encode(server)}/{Encode(name)}"
            + $"?expansion={Encode(encounterParams.Expansion)}"
            + $"&zone={Encode(encounterParams.EffectiveZoneSlug)}"
            + $"&encounter={Encode(encounterParams.Encounter)}";
    }

    private static string BuildCharacterProfilePath(string name, string server)
    {
        static string Encode(string value)
            => Uri.EscapeDataString((value ?? string.Empty).Trim());

        return $"character/profile/{Encode(server)}/{Encode(name)}";
    }
}
