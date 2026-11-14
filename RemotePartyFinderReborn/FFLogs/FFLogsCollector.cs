using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;

namespace RemotePartyFinderReborn;

internal interface IFFLogsApiClient
{
    bool TryGetRateLimitRemaining(out TimeSpan remaining);
    DateTime RateLimitCooldownUntilUtc { get; }
    void ResetRateLimitCooldown();
    Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
        List<FFLogsClient.CandidateCharacterQuery> queries,
        int zoneId,
        int? difficultyId,
        CancellationToken cancellationToken);
}

internal interface IFFLogsTimeProvider
{
    DateTime UtcNow { get; }
}

internal interface ITomestoneApiClient
{
    bool IsConfigured { get; }
    bool IsFallbackConfigured => false;
    bool TryGetRateLimitRemaining(out TimeSpan remaining);
    DateTime RateLimitCooldownUntilUtc { get; }
    void ResetRateLimitCooldown();
    Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken);

    Task<OperationOutcome<ParseResult>> FetchFallbackParseResultAsync(
        ParseJob job,
        string lookupServer,
        CancellationToken cancellationToken)
        => Task.FromResult(OperationOutcome<ParseResult>.Success(null));

    IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
        string name,
        string server,
        uint zoneId,
        IReadOnlyCollection<uint> excludeEncounterIds)
        => new Dictionary<uint, TomestoneProgressData>();
}

internal sealed record FFLogsCollectorSeams(
    IIngestHttpSender IngestHttpSender,
    IFFLogsApiClient ApiClient,
    IFFLogsTimeProvider TimeProvider,
    ITomestoneApiClient TomestoneApiClient,
    Configuration Configuration,
    Func<TimeSpan, CancellationToken, Task> TomestoneRequestDelayAsync);

public class FFLogsCollector : IDisposable
{
    private sealed class HttpClientIngestHttpSender(HttpClient httpClient) : IIngestHttpSender
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => httpClient.SendAsync(request, cancellationToken);
    }

    private sealed class FFLogsApiClientAdapter(FFLogsClient client) : IFFLogsApiClient
    {
        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
            => client.TryGetRateLimitRemaining(out remaining);

        public DateTime RateLimitCooldownUntilUtc
            => client.RateLimitCooldownUntilUtc;

        public void ResetRateLimitCooldown()
            => client.ResetRateLimitCooldown();

        public Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
            List<FFLogsClient.CandidateCharacterQuery> queries,
            int zoneId,
            int? difficultyId,
            CancellationToken cancellationToken)
            => client.FetchCharacterCandidateDataBatchOutcomeAsync(
                queries,
                zoneId,
                difficultyId,
                cancellationToken);
    }

    private sealed class SystemFFLogsTimeProvider : IFFLogsTimeProvider
    {
        public DateTime UtcNow
            => DateTime.UtcNow;
    }

    private sealed class NoOpTomestoneApiClient : ITomestoneApiClient
    {
        public bool IsConfigured => false;

        public DateTime RateLimitCooldownUntilUtc => DateTime.MinValue;

        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        public void ResetRateLimitCooldown()
        {
        }

        public Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
            string name,
            string server,
            uint zoneId,
            uint encounterId,
            CancellationToken cancellationToken)
            => Task.FromResult(OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty));
    }

    internal sealed class TomestoneApiClientAdapter(
        TomestoneApiClient client,
        Configuration configuration,
        Action<string> debugLog,
        Action<string> warningLog,
        ITomestoneWebFallbackClient webFallbackClient = null) : ITomestoneApiClient
    {
        private sealed record CachedProfileEntry(
            DateTime ExpiresAt,
            TomestoneRawResponse Response,
            JToken ParsedRoot);

        private sealed record CachedGraphEntry(
            DateTime ExpiresAt,
            TomestoneProgressData Progress);

        private static readonly Uri TomestoneSiteOrigin = new("https://tomestone.gg", UriKind.Absolute);
        private static readonly object ShapeDiagnosticLock = new();
        private static readonly HashSet<string> LoggedShapeDiagnostics = new(StringComparer.Ordinal);
        private readonly object ProfileCacheLock = new();
        private readonly Dictionary<(string Path, string Credential), CachedProfileEntry> ProfileCache = new();
        private readonly Dictionary<string, CachedGraphEntry> GraphCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Path, string Credential), Lazy<Task<OperationOutcome<CachedProfileEntry>>>> ProfileRequests = new();

        private bool HasApiKey => !string.IsNullOrWhiteSpace(configuration.TomestoneApiKey);
        private bool HasWebFallback => webFallbackClient != null && configuration.EnableTomestoneWebFallback;

        public bool IsConfigured
            => configuration.EnableTomestoneProgressEnrichment && (HasApiKey || HasWebFallback);

        public bool IsFallbackConfigured
            => HasApiKey || HasWebFallback;

        public DateTime RateLimitCooldownUntilUtc
            => client.RateLimitCooldownUntilUtc > (webFallbackClient?.CooldownUntilUtc ?? DateTime.MinValue)
                ? client.RateLimitCooldownUntilUtc
                : (webFallbackClient?.CooldownUntilUtc ?? DateTime.MinValue);

        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        {
            if (HasApiKey && client.TryGetRateLimitRemaining(out remaining))
            {
                if (!HasWebFallback || webFallbackClient.TryGetCooldownRemaining(out _))
                {
                    return true;
                }
            }

            if (HasWebFallback && webFallbackClient.TryGetCooldownRemaining(out remaining))
            {
                if (!HasApiKey)
                {
                    return true;
                }
            }

            remaining = TimeSpan.Zero;
            return false;
        }

        public void ResetRateLimitCooldown()
        {
            client.ResetRateLimitCooldown();
            webFallbackClient?.ResetCooldown();
        }

        public IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
            string name,
            string server,
            uint zoneId,
            IReadOnlyCollection<uint> excludeEncounterIds)
        {
            var merged = new Dictionary<uint, TomestoneProgressData>();
            var zoneTargets = TomestoneEncounterMapping.GetProgressionTargetsForZone(zoneId);
            if (zoneTargets.Count > 0 && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(server))
            {
                var profilePath = BuildCharacterProfilePath(name, server);
                var key = (Path: profilePath, Credential: configuration.TomestoneApiKey?.Trim() ?? string.Empty);
                JToken cachedRoot = null;
                lock (ProfileCacheLock)
                {
                    if (ProfileCache.TryGetValue(key, out var cached)
                        && cached.ExpiresAt > DateTime.UtcNow
                        && cached.ParsedRoot != null
                        && !cached.Response.ActivityHidden)
                    {
                        cachedRoot = cached.ParsedRoot;
                    }
                }

                if (cachedRoot != null)
                {
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
                }
            }

            if (HasWebFallback)
            {
                var webSiblings = webFallbackClient.TryGetCachedZoneSiblingProgress(
                    name,
                    server,
                    zoneId,
                    excludeEncounterIds);
                foreach (var pair in webSiblings)
                {
                    if (!merged.ContainsKey(pair.Key))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            return merged;
        }

        public async Task<OperationOutcome<ParseResult>> FetchFallbackParseResultAsync(
            ParseJob job,
            string lookupServer,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(job);
            if (!IsFallbackConfigured)
            {
                return OperationOutcome<ParseResult>.Success(null);
            }

            var effectiveServer = !string.IsNullOrWhiteSpace(lookupServer)
                ? lookupServer.Trim()
                : job.Server?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(job.Name) || string.IsNullOrWhiteSpace(effectiveServer))
            {
                return OperationOutcome<ParseResult>.Success(null);
            }

            OperationOutcome<ParseResult> tier1Outcome = null;
            ParseResult tier1ProgressOnly = null;
            if (HasApiKey && !client.TryGetRateLimitRemaining(out _))
            {
                var profilePath = BuildCharacterProfilePath(job.Name, effectiveServer);
                var profileOutcome = await FetchProfileAsync(profilePath, cancellationToken).ConfigureAwait(false);
                if (profileOutcome.Succeeded && profileOutcome.Value is { } cachedProfile)
                {
                    if (cachedProfile.Response.ActivityHidden)
                    {
                        return OperationOutcome<ParseResult>.Success(null);
                    }

                    if (cachedProfile.ParsedRoot is { } root)
                    {
                        var targets = TomestoneEncounterMapping.ResolveJobTargets(job)
                            .Select(static item => (item.EncounterId, item.EncounterParams))
                            .ToArray();
                        var parsed = TomestoneClearsParser.ParseClearsOrProgressIntoResult(
                            root,
                            job,
                            targets,
                            fallbackSourceKind: "fflogs_web_tomestone_web",
                            matchedServer: effectiveServer);
                        if (parsed != null && (parsed.Encounters.Count > 0 || parsed.ClearCounts.Count > 0))
                        {
                            return OperationOutcome<ParseResult>.Success(parsed);
                        }

                        tier1ProgressOnly = parsed;
                    }
                }
                else if (!profileOutcome.Succeeded)
                {
                    tier1Outcome = OperationOutcome<ParseResult>.Failure(
                        profileOutcome.TransientFailure,
                        profileOutcome.ErrorMessage ?? "Tomestone profile fallback failed.");
                }
            }

            if (HasWebFallback)
            {
                var webOutcome = await webFallbackClient
                    .FetchFallbackParseResultAsync(job, effectiveServer, cancellationToken)
                    .ConfigureAwait(false);
                if (webOutcome.Succeeded)
                {
                    return webOutcome.Value != null
                        ? webOutcome
                        : OperationOutcome<ParseResult>.Success(tier1ProgressOnly);
                }

                return tier1ProgressOnly != null
                    ? OperationOutcome<ParseResult>.Success(tier1ProgressOnly)
                    : webOutcome;
            }

            if (tier1ProgressOnly != null)
            {
                return OperationOutcome<ParseResult>.Success(tier1ProgressOnly);
            }

            return tier1Outcome ?? OperationOutcome<ParseResult>.Success(null);
        }

        public async Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
            string name,
            string server,
            uint zoneId,
            uint encounterId,
            CancellationToken cancellationToken)
        {
            if (!IsConfigured)
            {
                debugLog("Tomestone progress enrichment is disabled or unconfigured.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Skipped);
            }

            if (!TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var encounterParams))
            {
                debugLog($"Tomestone progress skipped for unmapped encounter zone={zoneId}, encounter={encounterId}.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Skipped);
            }

            var targetDescription = FormatTomestoneProgressTarget(zoneId, encounterId, encounterParams);
            OperationOutcome<TomestoneProgressData> tier1Failure = null;
            var tier1Attempted = HasApiKey && !client.TryGetRateLimitRemaining(out _);

            if (tier1Attempted)
            {
                var profilePath = BuildCharacterProfilePath(name, server);
                var profileOutcome = await FetchProfileAsync(profilePath, cancellationToken).ConfigureAwait(false);
                if (!profileOutcome.Succeeded)
                {
                    debugLog($"Tomestone profile returned no response for {targetDescription}.");
                    tier1Failure = OperationOutcome<TomestoneProgressData>.Failure(
                        profileOutcome.TransientFailure,
                        profileOutcome.ErrorMessage ?? "Tomestone profile returned no response.");
                }
                else
                {
                    var cachedProfile = profileOutcome.Value!;
                    var profileResponse = cachedProfile.Response;
                    if (profileResponse.ActivityHidden)
                    {
                        debugLog($"Tomestone profile activity is hidden for {targetDescription}.");
                        return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
                    }

                    if (profileResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        debugLog($"Tomestone profile not found for {targetDescription}.");
                        if (!HasWebFallback)
                        {
                            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
                        }
                    }
                    else if (profileResponse.StatusCode == HttpStatusCode.NoContent)
                    {
                        debugLog($"Tomestone profile had no content for {targetDescription}.");
                        if (!HasWebFallback)
                        {
                            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
                        }
                    }
                    else if (profileResponse.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                    {
                        warningLog($"Tomestone profile failed with {(int)profileResponse.StatusCode} for {targetDescription}.");
                        tier1Failure = OperationOutcome<TomestoneProgressData>.Failure(
                            IsTransientStatusCode(profileResponse.StatusCode),
                            $"Tomestone profile failed with {(int)profileResponse.StatusCode}.");
                    }
                    else
                    {
                        TomestoneProgressData profileProgress;
                        var profileRoot = cachedProfile.ParsedRoot;
                        if (profileRoot == null)
                        {
                            warningLog($"Tomestone profile parse failed for {targetDescription}: profile JSON was invalid.");
                            return OperationOutcome<TomestoneProgressData>.Failure(
                                transientFailure: false,
                                "Tomestone profile parse failed.");
                        }

                        profileProgress = TomestoneProgressParser.ParseProgress(profileRoot, encounterParams);

                        if (!profileProgress.Cleared
                            && encounterParams.ProgressKind == TomestoneProgressKind.PhaseProgress
                            && profileProgress.Phases.Count == 0
                            && TomestoneProgressParser.ExtractProgressionGraphUrl(profileRoot) is { } profileGraphUrl)
                        {
                            var graphOutcome = await FetchGraphCachedAsync(profileGraphUrl, cancellationToken).ConfigureAwait(false);
                            if (graphOutcome.Succeeded && graphOutcome.Value is { Phases.Count: > 0 } graphProgress)
                            {
                                profileProgress = TomestoneProgressParser.MergeProgressData(profileProgress, graphProgress);
                            }
                        }

                        if (HasUsefulProgress(profileProgress, encounterParams.ProgressKind))
                        {
                            debugLog(
                                $"Tomestone profile parsed {profileProgress.Phases.Count} phase(s), boss={profileProgress.BossPercentage?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none"} for {targetDescription}.");
                            return OperationOutcome<TomestoneProgressData>.Success(profileProgress);
                        }

                        LogResponseShapeOnce("profile", zoneId, encounterId, targetDescription, profileResponse.Body);
                        debugLog($"Tomestone profile had no target progress for {targetDescription}; falling back to encounter activity.");

                        var activityOutcome = await FetchTier1ActivityAsync(
                            name,
                            server,
                            zoneId,
                            encounterId,
                            encounterParams,
                            targetDescription,
                            cancellationToken).ConfigureAwait(false);
                        if (activityOutcome.Succeeded)
                        {
                            var activityProgress = activityOutcome.Value!;
                            if (HasUsefulProgress(activityProgress, encounterParams.ProgressKind) || !HasWebFallback)
                            {
                                return activityOutcome;
                            }
                        }
                        else if (!HasWebFallback)
                        {
                            return activityOutcome;
                        }
                        else
                        {
                            tier1Failure = activityOutcome;
                        }
                    }
                }
            }

            if (HasWebFallback)
            {
                debugLog($"Escalating Tomestone progression lookup for {targetDescription} to Tier 2 web fallback.");
                var webOutcome = await webFallbackClient
                    .FetchProgressionAsync(name, server, zoneId, encounterParams, cancellationToken)
                    .ConfigureAwait(false);
                if (webOutcome.Succeeded)
                {
                    if (HasUsefulProgress(webOutcome.Value!, encounterParams.ProgressKind) || tier1Failure == null)
                    {
                        return webOutcome;
                    }
                }
                else
                {
                    return webOutcome;
                }
            }

            return tier1Failure ?? OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
        }

        private async Task<OperationOutcome<TomestoneProgressData>> FetchTier1ActivityAsync(
            string name,
            string server,
            uint zoneId,
            uint encounterId,
            TomestoneEncounterParams encounterParams,
            string targetDescription,
            CancellationToken cancellationToken)
        {
            var activityPath = BuildEncounterActivityPath(name, server, encounterParams);
            var rawOutcome = await client.GetRawOutcomeAsync(activityPath, cancellationToken).ConfigureAwait(false);
            if (!rawOutcome.Succeeded)
            {
                debugLog($"Tomestone progress returned no response for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Failure(
                    rawOutcome.TransientFailure,
                    rawOutcome.ErrorMessage ?? "Tomestone progress returned no response.");
            }

            var response = rawOutcome.Value!;
            if (response.ActivityHidden)
            {
                debugLog($"Tomestone encounter activity is hidden for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                debugLog($"Tomestone progress not found for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                debugLog($"Tomestone progress had no content for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                warningLog($"Tomestone progress failed with {(int)response.StatusCode} for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Failure(
                    IsTransientStatusCode(response.StatusCode),
                    $"Tomestone progress failed with {(int)response.StatusCode}.");
            }

            try
            {
                var activityRoot = JToken.Parse(response.Body);
                var progress = TomestoneProgressParser.ParseEncounterActivityProgress(activityRoot, encounterParams);
                if (!progress.Cleared
                    && encounterParams.ProgressKind == TomestoneProgressKind.PhaseProgress
                    && progress.Phases.Count == 0
                    && TomestoneProgressParser.ExtractProgressionGraphUrl(activityRoot) is { } activityGraphUrl)
                {
                    var graphOutcome = await FetchGraphCachedAsync(activityGraphUrl, cancellationToken).ConfigureAwait(false);
                    if (graphOutcome.Succeeded && graphOutcome.Value is { Phases.Count: > 0 } graphProgress)
                    {
                        progress = TomestoneProgressParser.MergeProgressData(progress, graphProgress);
                    }
                }

                if (!HasUsefulProgress(progress, encounterParams.ProgressKind))
                {
                    LogResponseShapeOnce("activity", zoneId, encounterId, targetDescription, response.Body);
                }

                debugLog(
                    $"Tomestone progress parsed {progress.Phases.Count} phase(s), boss={progress.BossPercentage?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none"} for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Success(progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warningLog($"Tomestone progress parse failed for {targetDescription}: {ex.GetType().Name}: {ex.Message}");
                return OperationOutcome<TomestoneProgressData>.Failure(
                    transientFailure: false,
                    $"Tomestone progress parse failed: {ex.Message}");
            }
        }

        private async Task<OperationOutcome<TomestoneProgressData>> FetchGraphCachedAsync(
            string graphUrl,
            CancellationToken cancellationToken)
        {
            lock (ProfileCacheLock)
            {
                if (GraphCache.TryGetValue(graphUrl, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                {
                    return OperationOutcome<TomestoneProgressData>.Success(cached.Progress);
                }
            }

            if (!TryResolveSameOriginSiteUrl(graphUrl, out var resolvedUrl))
            {
                warningLog($"Ignoring invalid or cross-origin Tomestone progressionGraphUrl: {graphUrl}");
                return OperationOutcome<TomestoneProgressData>.Success(null);
            }

            var rawOutcome = await client.GetRawOutcomeAsync(resolvedUrl, cancellationToken).ConfigureAwait(false);
            if (!rawOutcome.Succeeded)
            {
                return OperationOutcome<TomestoneProgressData>.Failure(
                    rawOutcome.TransientFailure,
                    rawOutcome.ErrorMessage);
            }

            var response = rawOutcome.Value!;
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || response.StatusCode == HttpStatusCode.NoContent)
            {
                return OperationOutcome<TomestoneProgressData>.Success(null);
            }

            try
            {
                var graphRoot = JToken.Parse(response.Body);
                var parsed = TomestoneProgressParser.ParseGraphProgress(graphRoot);
                lock (ProfileCacheLock)
                {
                    var now = DateTime.UtcNow;
                    foreach (var expired in GraphCache.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                    {
                        GraphCache.Remove(expired);
                    }

                    if (GraphCache.Count >= 256)
                    {
                        GraphCache.Remove(GraphCache.MinBy(static pair => pair.Value.ExpiresAt).Key);
                    }

                    GraphCache[graphUrl] = new CachedGraphEntry(now.AddMinutes(5), parsed);
                }

                return OperationOutcome<TomestoneProgressData>.Success(parsed);
            }
            catch (JsonException)
            {
                return OperationOutcome<TomestoneProgressData>.Success(null);
            }
        }

        private static bool TryResolveSameOriginSiteUrl(string candidate, out string resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var trimmed = candidate.Trim();
            if (trimmed.StartsWith('/'))
            {
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    return false;
                }

                if (Uri.TryCreate(TomestoneSiteOrigin, trimmed, out var relativeResolved))
                {
                    resolved = relativeResolved.ToString();
                    return true;
                }

                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (string.Equals(parsed.Scheme, TomestoneSiteOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Host, TomestoneSiteOrigin.Host, StringComparison.OrdinalIgnoreCase)
                && parsed.Port == TomestoneSiteOrigin.Port)
            {
                resolved = parsed.ToString();
                return true;
            }

            return false;
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

        private async Task<OperationOutcome<CachedProfileEntry>> FetchProfileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var key = (Path: path, Credential: configuration.TomestoneApiKey?.Trim() ?? string.Empty);
            var request = ProfileRequests.GetOrAdd(key, _ => new Lazy<Task<OperationOutcome<CachedProfileEntry>>>(
                () => FetchAndCacheProfileAsync(key, cancellationToken)));
            try
            {
                return await request.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (request.IsValueCreated && request.Value.IsCompleted)
                {
                    ProfileRequests.TryRemove(new KeyValuePair<(string Path, string Credential), Lazy<Task<OperationOutcome<CachedProfileEntry>>>>(key, request));
                }
            }
        }

        private async Task<OperationOutcome<CachedProfileEntry>> FetchAndCacheProfileAsync(
            (string Path, string Credential) key,
            CancellationToken cancellationToken)
        {
            lock (ProfileCacheLock)
            {
                if (ProfileCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                {
                    return OperationOutcome<CachedProfileEntry>.Success(cached);
                }
            }

            var outcome = await client.GetRawOutcomeAsync(key.Path, cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded || outcome.Value is not { } response)
            {
                return OperationOutcome<CachedProfileEntry>.Failure(
                    outcome.TransientFailure,
                    outcome.ErrorMessage);
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

                            if (profile["progressionGraphUrl"] is { Type: not JTokenType.Null and not JTokenType.Undefined } graphUrl)
                            {
                                trimmedProfile["progressionGraphUrl"] = graphUrl;
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
                        return OperationOutcome<CachedProfileEntry>.Failure(
                            transientFailure: false,
                            $"Tomestone profile parse failed: {ex.Message}");
                    }
                }

                var entry = new CachedProfileEntry(DateTime.UtcNow.AddMinutes(5), response, parsedRoot);
                lock (ProfileCacheLock)
                {
                    var now = DateTime.UtcNow;
                    foreach (var expired in ProfileCache.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                    {
                        ProfileCache.Remove(expired);
                    }

                    if (ProfileCache.Count >= 128)
                    {
                        ProfileCache.Remove(ProfileCache.MinBy(static pair => pair.Value.ExpiresAt).Key);
                    }

                    ProfileCache[key] = entry;
                }

                return OperationOutcome<CachedProfileEntry>.Success(entry);
            }

            var nonCachedEntry = new CachedProfileEntry(DateTime.MinValue, response, null);
            return OperationOutcome<CachedProfileEntry>.Success(nonCachedEntry);
        }

        private void LogResponseShapeOnce(
            string endpoint,
            uint zoneId,
            uint encounterId,
            string targetDescription,
            string body)
        {
            var key = $"{endpoint}:{zoneId}:{encounterId}";
            lock (ShapeDiagnosticLock)
            {
                if (!LoggedShapeDiagnostics.Add(key))
                {
                    return;
                }
            }

            var shape = TomestoneProgressParser.DescribeResponseShape(
                body,
                TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var target)
                    ? target
                    : null);
            debugLog($"Tomestone response shape endpoint={endpoint} for {targetDescription}: {shape}.");
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
                $"fflogs zone={zoneId}, encounter={encounterId}; "
                + $"tomestone expansion={encounterParams.Expansion}, "
                + $"zone={encounterParams.Category}, "
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
                + $"&zone={Encode(encounterParams.Category)}"
                + $"&encounter={Encode(encounterParams.Encounter)}";
        }

        private static string BuildCharacterProfilePath(string name, string server)
        {
            static string Encode(string value)
                => Uri.EscapeDataString((value ?? string.Empty).Trim());

            return $"character/profile/{Encode(server)}/{Encode(name)}";
        }
    }

    private Configuration Configuration { get; set; }
    private Action<string> InfoLog { get; set; } = static _ => { };
    private Action<string> WarningLog { get; set; } = static _ => { };
    private Action<string> ErrorLog { get; set; } = static _ => { };
    private Action<string> DebugLog { get; set; } = static _ => { };
    private FFLogsCollectorSeams Seams { get; set; }
    private FFLogsSubmitBuffer SubmitBuffer { get; set; }
    private FFLogsWorkerPolicy WorkerPolicy { get; set; }
    private FFLogsResultSubmitter ResultSubmitter { get; set; }
    private FFLogsLeaseAbandoner LeaseAbandoner { get; set; }
    private FFLogsJobLeaseClient JobLeaseClient { get; set; }
    private FFLogsBatchProcessor BatchProcessor { get; set; }
    private FFLogsBackgroundWorker BackgroundWorker { get; set; }
    private IReadOnlyList<IDisposable> OwnedDisposables { get; set; } = [];
    private System.Threading.CancellationTokenSource Cts { get; set; } = new();
    private Task _workerTask = Task.CompletedTask;

    internal static FFLogsCollectorSeams CreateSeams(
        IIngestHttpSender ingestHttpSender,
        IFFLogsApiClient apiClient,
        IFFLogsTimeProvider timeProvider,
        ITomestoneApiClient tomestoneApiClient = null,
        Configuration configuration = null,
        Func<TimeSpan, CancellationToken, Task> tomestoneRequestDelayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(ingestHttpSender);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new FFLogsCollectorSeams(
            ingestHttpSender,
            apiClient,
            timeProvider,
            tomestoneApiClient ?? new NoOpTomestoneApiClient(),
            configuration ?? new Configuration(),
            tomestoneRequestDelayAsync ?? Task.Delay);
    }

    internal static FFLogsCollector CreateForTesting(
        Configuration configuration,
        FFLogsCollectorSeams seams,
        FFLogsSubmitBuffer submitBuffer,
        FFLogsWorkerPolicy workerPolicy,
        FFLogsJobLeaseClient jobLeaseClient = null,
        FFLogsResultSubmitter resultSubmitter = null,
        FFLogsBatchProcessor batchProcessor = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(submitBuffer);
        ArgumentNullException.ThrowIfNull(workerPolicy);

        var collector = new FFLogsCollector();
        var leaseAbandoner = new FFLogsLeaseAbandoner(seams);
        collector.Initialize(
            configuration,
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            seams,
            submitBuffer,
            workerPolicy,
            resultSubmitter ?? new FFLogsResultSubmitter(seams, submitBuffer, leaseAbandoner),
            leaseAbandoner,
            jobLeaseClient ?? new FFLogsJobLeaseClient(seams),
            batchProcessor ?? new FFLogsBatchProcessor(seams),
            [],
            startWorker: false);
        return collector;
    }

    internal Task RunWorkerLoopForTestingAsync()
        => BackgroundWorker.RunAsync(Cts.Token);

    private FFLogsCollector()
    {
    }

    public FFLogsCollector(Plugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var httpClient = new HttpClient();
        var ffLogsClient = new FFLogsClient(plugin.Configuration, plugin.ApiCredentialStatus.ReportFFLogs);
        var webFallbackClient = new TomestoneWebFallbackClient(
            plugin.Configuration,
            message => Plugin.Log.Debug(message),
            message => Plugin.Log.Warning(message));
        var workerRegionProvider = new DalamudFFLogsWorkerRegionProvider(
            plugin.Framework,
            plugin.ObjectTable,
            message => Plugin.Log.Warning(message));
        var seams = CreateSeams(
            new HttpClientIngestHttpSender(httpClient),
            new FFLogsApiClientAdapter(ffLogsClient),
            new SystemFFLogsTimeProvider(),
            new TomestoneApiClientAdapter(
                plugin.TomestoneApiClient,
                plugin.Configuration,
                message => Plugin.Log.Debug(message),
                message => Plugin.Log.Warning(message),
                webFallbackClient),
            plugin.Configuration);
        var submitBuffer = new FFLogsSubmitBuffer();
        var leaseAbandoner = new FFLogsLeaseAbandoner(seams);
        Initialize(
            plugin.Configuration,
            message => Plugin.Log.Info(message),
            message => Plugin.Log.Warning(message),
            message => Plugin.Log.Error(message),
            message => Plugin.Log.Debug(message),
            seams,
            submitBuffer,
            new FFLogsWorkerPolicy(plugin.Configuration, message => Plugin.Log.Warning(message), seams.TimeProvider),
            new FFLogsResultSubmitter(seams, submitBuffer, leaseAbandoner),
            leaseAbandoner,
            new FFLogsJobLeaseClient(
                seams,
                workerRegionProvider),
            new FFLogsBatchProcessor(seams),
            [webFallbackClient, ffLogsClient, httpClient, workerRegionProvider],
            startWorker: true);
    }

    public void Dispose()
    {
        Cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Ignore cancellation/timeout during shutdown before tearing down HTTP clients.
        }

        foreach (var disposable in OwnedDisposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                WarningLog($"FFLogsCollector: disposable cleanup failed: {ex.Message}");
            }
        }

        Cts.Dispose();
    }

    private void Initialize(
        Configuration configuration,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<string> debugLog,
        FFLogsCollectorSeams seams,
        FFLogsSubmitBuffer submitBuffer,
        FFLogsWorkerPolicy workerPolicy,
        FFLogsResultSubmitter resultSubmitter,
        FFLogsLeaseAbandoner leaseAbandoner,
        FFLogsJobLeaseClient jobLeaseClient,
        FFLogsBatchProcessor batchProcessor,
        IReadOnlyList<IDisposable> ownedDisposables,
        bool startWorker)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        InfoLog = infoLog ?? throw new ArgumentNullException(nameof(infoLog));
        WarningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        ErrorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
        DebugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        Seams = seams ?? throw new ArgumentNullException(nameof(seams));
        SubmitBuffer = submitBuffer ?? throw new ArgumentNullException(nameof(submitBuffer));
        WorkerPolicy = workerPolicy ?? throw new ArgumentNullException(nameof(workerPolicy));
        ResultSubmitter = resultSubmitter ?? throw new ArgumentNullException(nameof(resultSubmitter));
        LeaseAbandoner = leaseAbandoner ?? throw new ArgumentNullException(nameof(leaseAbandoner));
        JobLeaseClient = jobLeaseClient ?? throw new ArgumentNullException(nameof(jobLeaseClient));
        BatchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
        OwnedDisposables = ownedDisposables ?? throw new ArgumentNullException(nameof(ownedDisposables));
        BackgroundWorker = new FFLogsBackgroundWorker(
            Configuration,
            Seams.ApiClient,
            WorkerPolicy,
            JobLeaseClient,
            BatchProcessor,
            ResultSubmitter,
            LeaseAbandoner,
            InfoLog,
            WarningLog,
            ErrorLog,
            DebugLog);
        if (startWorker)
        {
            StartWorker();
        }
    }

    private void StartWorker()
    {
        _workerTask = Task.Run(() => BackgroundWorker.RunAsync(Cts.Token));
    }

    public bool TryGetRateLimitCooldownRemaining(out TimeSpan remaining)
        => Seams.ApiClient.TryGetRateLimitRemaining(out remaining);

    public DateTime RateLimitCooldownUntilUtc
        => Seams.ApiClient.RateLimitCooldownUntilUtc;

    public void ResetRateLimitCooldown()
        => Seams.ApiClient.ResetRateLimitCooldown();
}
