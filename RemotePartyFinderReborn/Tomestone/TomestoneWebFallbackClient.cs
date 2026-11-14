using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RemotePartyFinderReborn.Tomestone;

internal interface ITomestoneWebFallbackClient : IDisposable
{
    DateTime CooldownUntilUtc { get; }
    bool TryGetCooldownRemaining(out TimeSpan remaining);
    void ResetCooldown();

    Task<OperationOutcome<TomestoneProgressData>> FetchProgressionAsync(
        string name,
        string server,
        uint zoneId,
        TomestoneEncounterParams target,
        CancellationToken cancellationToken);

    Task<OperationOutcome<ParseResult>> FetchFallbackParseResultAsync(
        ParseJob job,
        string lookupServer,
        CancellationToken cancellationToken);

    IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
        string name,
        string server,
        uint zoneId,
        IReadOnlyCollection<uint> excludeEncounterIds);
}

/// <summary>
/// Tier 2 direct web fallback (<c>tomestone.gg</c> autocomplete, Inertia JSON / <c>#app[data-page]</c>,
/// <c>/clears</c>, <c>/activity</c>, and same-origin <c>progressionGraphUrl</c>) with automatic escalation
/// to Tier 3 <see cref="ObscuraCdpClient"/> when direct HTTP fails to extract Inertia/JSON data,
/// or directly via <c>obscura</c> when <see cref="Configuration.PreferTomestoneObscura"/> is enabled.
/// Aborts immediately and enters cooldown on HTTP 403/429 or any anti-bot/CAPTCHA indicator.
/// </summary>
internal sealed class TomestoneWebFallbackClient : ITomestoneWebFallbackClient
{
    private const string DefaultOriginUrl = "https://tomestone.gg";
    private const int MaxHrefCacheEntries = 512;
    private const int MaxProfileCacheEntries = 128;
    private const int MaxGraphCacheEntries = 256;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HrefCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProfileCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GraphCacheTtl = TimeSpan.FromMinutes(5);

    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Uri _originUri;
    private readonly ITomestoneCdpHydrator _cdpHydrator;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _warningLog;
    private readonly RateLimitCooldownState _cooldown = new();
    private readonly SemaphoreSlim _pacingLock = new(1, 1);

    private readonly object _stateLock = new();
    private readonly Dictionary<(string Name, string Server), (DateTime ExpiresAt, string Href)> _hrefCache = new();
    private readonly Dictionary<string, (DateTime ExpiresAt, JToken Content)> _profileCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _notFoundProfileCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime ExpiresAt, TomestoneProgressData Progress)> _graphCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<OperationOutcome<JToken>>>> _inFlightProfiles = new(StringComparer.Ordinal);

    private DateTime _lastRequestStartedUtc = DateTime.MinValue;
    private string _cachedInertiaVersion;
    private bool _disposed;

    public TomestoneWebFallbackClient(
        Configuration configuration,
        Action<string> debugLog = null,
        Action<string> warningLog = null)
        : this(
            configuration,
            CreateDefaultHttpClient(),
            new ObscuraCdpClient(debugLog, warningLog, bearerTokenProvider: () => configuration?.TomestoneObscuraCdpToken),
            static () => DateTime.UtcNow,
            Task.Delay,
            debugLog,
            warningLog,
            DefaultOriginUrl)
    {
    }

    internal TomestoneWebFallbackClient(
        Configuration configuration,
        HttpMessageHandler messageHandler,
        ITomestoneCdpHydrator cdpHydrator = null,
        Func<DateTime> utcNow = null,
        Func<TimeSpan, CancellationToken, Task> delayAsync = null,
        Action<string> debugLog = null,
        Action<string> warningLog = null,
        string originUrl = DefaultOriginUrl)
        : this(
            configuration,
            CreateHttpClientWithTimeout(messageHandler),
            cdpHydrator ?? new ObscuraCdpClient(debugLog, warningLog, bearerTokenProvider: () => configuration?.TomestoneObscuraCdpToken),
            utcNow ?? (static () => DateTime.UtcNow),
            delayAsync ?? Task.Delay,
            debugLog,
            warningLog,
            originUrl)
    {
    }

    private TomestoneWebFallbackClient(
        Configuration configuration,
        HttpClient httpClient,
        ITomestoneCdpHydrator cdpHydrator,
        Func<DateTime> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<string> debugLog,
        Action<string> warningLog,
        string originUrl)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cdpHydrator = cdpHydrator ?? throw new ArgumentNullException(nameof(cdpHydrator));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _debugLog = debugLog ?? (static _ => { });
        _warningLog = warningLog ?? (static _ => { });

        var normalizedOrigin = string.IsNullOrWhiteSpace(originUrl)
            ? DefaultOriginUrl
            : originUrl.Trim().TrimEnd('/');
        _originUri = new Uri(normalizedOrigin, UriKind.Absolute);
    }

    public DateTime CooldownUntilUtc => _cooldown.CooldownUntilUtc;

    public bool TryGetCooldownRemaining(out TimeSpan remaining)
        => _cooldown.TryGetRemaining(_utcNow(), out remaining);

    public void ResetCooldown()
    {
        _cooldown.Reset();
    }

    public IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
        string name,
        string server,
        uint zoneId,
        IReadOnlyCollection<uint> excludeEncounterIds)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(server))
        {
            return new Dictionary<uint, TomestoneProgressData>();
        }

        var zoneTargets = TomestoneEncounterMapping.GetProgressionTargetsForZone(zoneId);
        if (zoneTargets.Count == 0)
        {
            return new Dictionary<uint, TomestoneProgressData>();
        }

        var now = _utcNow();
        JToken cachedProfile = null;
        lock (_stateLock)
        {
            var hrefKey = (name.Trim().ToLowerInvariant(), server.Trim().ToLowerInvariant());
            if (_hrefCache.TryGetValue(hrefKey, out var hrefEntry) && hrefEntry.ExpiresAt > now)
            {
                if (_profileCache.TryGetValue(hrefEntry.Href, out var profileEntry) && profileEntry.ExpiresAt > now)
                {
                    cachedProfile = profileEntry.Content;
                }
            }
        }

        if (cachedProfile is null)
        {
            return new Dictionary<uint, TomestoneProgressData>();
        }

        var result = new Dictionary<uint, TomestoneProgressData>();
        foreach (var (encounterId, targetParams) in zoneTargets)
        {
            if (excludeEncounterIds != null && excludeEncounterIds.Contains(encounterId))
            {
                continue;
            }

            var progress = TomestoneProgressParser.ParseProgress(cachedProfile, targetParams);
            if (progress.IsAuthoritative && (progress.Cleared || progress.BossPercentage.HasValue || progress.Phases.Count > 0))
            {
                result[encounterId] = progress;
            }
        }

        return result;
    }

    public async Task<OperationOutcome<TomestoneProgressData>> FetchProgressionAsync(
        string name,
        string server,
        uint zoneId,
        TomestoneEncounterParams target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (TryGetCooldownRemaining(out var cooldownRemaining))
        {
            LogCooldownSkipIfNeeded(cooldownRemaining);
            return OperationOutcome<TomestoneProgressData>.Failure(
                transientFailure: true,
                "Tomestone web fallback cooldown is active.");
        }

        var hrefOutcome = await ResolveCharacterHrefAsync(name, server, null, cancellationToken).ConfigureAwait(false);
        if (!hrefOutcome.Succeeded)
        {
            return OperationOutcome<TomestoneProgressData>.Failure(
                hrefOutcome.TransientFailure,
                hrefOutcome.ErrorMessage ?? "Tomestone character lookup failed.");
        }

        var characterHref = hrefOutcome.Value;
        if (string.IsNullOrEmpty(characterHref))
        {
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
        }

        var profileOutcome = await FetchCachedProfilePageContentAsync(characterHref, cancellationToken).ConfigureAwait(false);
        if (!profileOutcome.Succeeded)
        {
            return OperationOutcome<TomestoneProgressData>.Failure(
                profileOutcome.TransientFailure,
                profileOutcome.ErrorMessage ?? "Tomestone web profile fetch failed.");
        }

        var profileContent = profileOutcome.Value;
        if (profileContent is null)
        {
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
        }

        var progress = TomestoneProgressParser.ParseProgress(profileContent, target);
        if (progress.Cleared)
        {
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.ClearedProgress);
        }

        if (target.ProgressKind == TomestoneProgressKind.PhaseProgress && progress.Phases.Count == 0)
        {
            var profileGraphUrl = TomestoneProgressParser.ExtractProgressionGraphUrl(profileContent);
            if (!string.IsNullOrWhiteSpace(profileGraphUrl))
            {
                var graphOutcome = await FetchGraphCachedAsync(profileGraphUrl, cancellationToken).ConfigureAwait(false);
                if (!graphOutcome.Succeeded)
                {
                    return OperationOutcome<TomestoneProgressData>.Failure(
                        graphOutcome.TransientFailure,
                        graphOutcome.ErrorMessage);
                }

                if (graphOutcome.Value is { Phases.Count: > 0 } graphProgress)
                {
                    progress = new TomestoneProgressData(
                        graphProgress.Phases,
                        progress.BossPercentage ?? graphProgress.BossPercentage);
                }
            }
        }

        if (HasUsefulProgress(progress, target.ProgressKind))
        {
            return OperationOutcome<TomestoneProgressData>.Success(progress);
        }

        var activityPath = BuildActivityPath(characterHref, target);
        var activityOutcome = await FetchPageContentAsync(activityPath, cancellationToken).ConfigureAwait(false);
        if (!activityOutcome.Succeeded)
        {
            return OperationOutcome<TomestoneProgressData>.Failure(
                activityOutcome.TransientFailure,
                activityOutcome.ErrorMessage ?? "Tomestone web activity fetch failed.");
        }

        var activityContent = activityOutcome.Value;
        if (activityContent is null)
        {
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
        }

        var activityTargetProgress = TomestoneProgressParser.ParseProgress(activityContent, target);
        if (activityTargetProgress.Cleared)
        {
            return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.ClearedProgress);
        }

        progress = TomestoneProgressParser.MergeProgressData(progress, activityTargetProgress);

        if (target.ProgressKind == TomestoneProgressKind.PhaseProgress && progress.Phases.Count == 0)
        {
            var activityGraphUrl = TomestoneProgressParser.ExtractProgressionGraphUrl(activityContent);
            if (!string.IsNullOrWhiteSpace(activityGraphUrl))
            {
                var graphOutcome = await FetchGraphCachedAsync(activityGraphUrl, cancellationToken).ConfigureAwait(false);
                if (!graphOutcome.Succeeded)
                {
                    return OperationOutcome<TomestoneProgressData>.Failure(
                        graphOutcome.TransientFailure,
                        graphOutcome.ErrorMessage);
                }

                if (graphOutcome.Value is { Phases.Count: > 0 } graphProgress)
                {
                    progress = TomestoneProgressParser.MergeProgressData(progress, graphProgress);
                }
            }
        }

        if (!HasUsefulProgress(progress, target.ProgressKind))
        {
            var activityRowsProgress = TomestoneProgressParser.ParseEncounterActivityProgress(activityContent, target);
            progress = TomestoneProgressParser.MergeProgressData(progress, activityRowsProgress);
        }

        return OperationOutcome<TomestoneProgressData>.Success(progress);
    }

    public async Task<OperationOutcome<ParseResult>> FetchFallbackParseResultAsync(
        ParseJob job,
        string lookupServer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (TryGetCooldownRemaining(out var cooldownRemaining))
        {
            LogCooldownSkipIfNeeded(cooldownRemaining);
            return OperationOutcome<ParseResult>.Failure(
                transientFailure: true,
                "Tomestone web fallback cooldown is active.");
        }

        var effectiveServer = !string.IsNullOrWhiteSpace(lookupServer)
            ? lookupServer.Trim()
            : job.Server?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(job.Name) || string.IsNullOrWhiteSpace(effectiveServer))
        {
            return OperationOutcome<ParseResult>.Success(null);
        }

        var hrefOutcome = await ResolveCharacterHrefAsync(
            job.Name,
            effectiveServer,
            job.CandidateServers,
            cancellationToken).ConfigureAwait(false);
        if (!hrefOutcome.Succeeded)
        {
            return OperationOutcome<ParseResult>.Failure(
                hrefOutcome.TransientFailure,
                hrefOutcome.ErrorMessage ?? "Tomestone character lookup failed.");
        }

        var characterHref = hrefOutcome.Value;
        if (string.IsNullOrEmpty(characterHref))
        {
            return OperationOutcome<ParseResult>.Success(null);
        }

        var category = TomestoneEncounterMapping.InferClearsCategory(job);
        var clearsPath = BuildClearsPath(characterHref, category);
        var targets = TomestoneEncounterMapping.ResolveJobTargets(job);
        var targetTuples = targets
            .Select(static item => (item.EncounterId, item.EncounterParams))
            .ToArray();

        ParseResult fallbackResult = null;
        var clearsOutcome = await FetchPageContentAsync(clearsPath, cancellationToken).ConfigureAwait(false);
        if (!clearsOutcome.Succeeded)
        {
            return OperationOutcome<ParseResult>.Failure(
                clearsOutcome.TransientFailure,
                clearsOutcome.ErrorMessage ?? "Tomestone web clears fetch failed.");
        }

        if (clearsOutcome.Value is { } clearsContent)
        {
            fallbackResult = TomestoneClearsParser.ParseClearsOrProgressIntoResult(
                clearsContent,
                job,
                targetTuples,
                fallbackSourceKind: "tomestone_web",
                matchedServer: effectiveServer);
        }

        if (_configuration.EnableTomestoneProgressEnrichment && targetTuples.Length > 0)
        {
            foreach (var (zoneId, encounterId, encounterParams) in targets)
            {
                var encounterKey = (int)encounterId;
                var alreadyCleared = fallbackResult != null
                    && ((fallbackResult.Encounters.TryGetValue(encounterKey, out var pct) && pct > 0.0)
                        || (fallbackResult.ClearCounts.TryGetValue(encounterKey, out var count) && count > 0));
                if (alreadyCleared)
                {
                    continue;
                }

                var alreadyHasProgress = fallbackResult != null
                    && ((fallbackResult.PhaseProgress.TryGetValue(encounterKey, out var phases) && phases.Count > 0)
                        || fallbackResult.BossPercentages.ContainsKey(encounterKey));
                if (alreadyHasProgress)
                {
                    continue;
                }

                var progressOutcome = await FetchProgressionAsync(
                    job.Name,
                    effectiveServer,
                    zoneId,
                    encounterParams,
                    cancellationToken).ConfigureAwait(false);
                if (!progressOutcome.Succeeded)
                {
                    if (TryGetCooldownRemaining(out _))
                    {
                        return OperationOutcome<ParseResult>.Failure(
                            progressOutcome.TransientFailure,
                            progressOutcome.ErrorMessage);
                    }

                    continue;
                }

                var progress = progressOutcome.Value;
                if (progress is { IsAuthoritative: true })
                {
                    fallbackResult ??= new ParseResult
                    {
                        ContentId = job.ContentId,
                        ZoneId = job.ZoneId,
                        DifficultyId = job.DifficultyId,
                        IsHidden = false,
                        IsEstimated = job.CandidateServers is { Count: > 0 },
                        MatchedServer = effectiveServer,
                        SourceKind = "tomestone_api",
                        LeaseToken = job.LeaseToken ?? string.Empty,
                    };

                    TomestoneClearsParser.ApplyProgress(
                        fallbackResult,
                        encounterKey,
                        encounterParams.ProgressKind,
                        progress);
                }
            }
        }

        return OperationOutcome<ParseResult>.Success(fallbackResult);
    }

    private async Task<OperationOutcome<string>> ResolveCharacterHrefAsync(
        string name,
        string server,
        IReadOnlyCollection<ParseJobCandidateServer> candidateServers,
        CancellationToken cancellationToken)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        var trimmedServer = server?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedName))
        {
            return OperationOutcome<string>.Success(null);
        }

        var cacheKey = (trimmedName.ToLowerInvariant(), trimmedServer.ToLowerInvariant());
        var now = _utcNow();
        lock (_stateLock)
        {
            if (_hrefCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
            {
                return OperationOutcome<string>.Success(cached.Href);
            }
        }

        var path = BuildAutocompletePath(trimmedName);
        var jsonOutcome = await FetchJsonWithOptionalObscuraAsync(
            path,
            noContentIsNull: true,
            "Tomestone autocomplete",
            cancellationToken).ConfigureAwait(false);
        if (!jsonOutcome.Succeeded)
        {
            return OperationOutcome<string>.Failure(
                jsonOutcome.TransientFailure,
                jsonOutcome.ErrorMessage);
        }

        if (string.IsNullOrEmpty(jsonOutcome.Value))
        {
            return OperationOutcome<string>.Success(null);
        }

        string matchedHref;
        try
        {
            matchedHref = SelectAutocompleteHref(trimmedName, trimmedServer, candidateServers, jsonOutcome.Value);
        }
        catch (JsonException ex)
        {
            return OperationOutcome<string>.Failure(
                transientFailure: false,
                $"Tomestone autocomplete returned invalid JSON: {ex.Message}");
        }

        if (string.IsNullOrEmpty(matchedHref))
        {
            return OperationOutcome<string>.Success(null);
        }

        var normalizedHref = NormalizeCharacterHref(matchedHref);
        if (string.IsNullOrEmpty(normalizedHref))
        {
            return OperationOutcome<string>.Failure(
                transientFailure: false,
                $"Tomestone autocomplete returned non-character href '{matchedHref}'.");
        }

        lock (_stateLock)
        {
            var expireNow = _utcNow();
            PruneExpired(_hrefCache, expireNow);
            if (_hrefCache.Count >= MaxHrefCacheEntries)
            {
                _hrefCache.Remove(_hrefCache.MinBy(static p => p.Value.ExpiresAt).Key);
            }

            _hrefCache[cacheKey] = (expireNow.Add(HrefCacheTtl), normalizedHref);
        }

        return OperationOutcome<string>.Success(normalizedHref);
    }

    private async Task<OperationOutcome<JToken>> FetchCachedProfilePageContentAsync(
        string characterHref,
        CancellationToken cancellationToken)
    {
        var now = _utcNow();
        lock (_stateLock)
        {
            if (_notFoundProfileCache.TryGetValue(characterHref, out var notFoundUntil) && notFoundUntil > now)
            {
                return OperationOutcome<JToken>.Success(null);
            }

            if (_profileCache.TryGetValue(characterHref, out var cached) && cached.ExpiresAt > now)
            {
                return OperationOutcome<JToken>.Success(cached.Content);
            }
        }

        var lazyTask = _inFlightProfiles.GetOrAdd(
            characterHref,
            key => new Lazy<Task<OperationOutcome<JToken>>>(() => FetchAndCacheProfilePageAsync(key, cancellationToken)));
        try
        {
            return await lazyTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lazyTask.IsValueCreated && lazyTask.Value.IsCompleted)
            {
                _inFlightProfiles.TryRemove(
                    new KeyValuePair<string, Lazy<Task<OperationOutcome<JToken>>>>(characterHref, lazyTask));
            }
        }
    }

    private async Task<OperationOutcome<JToken>> FetchAndCacheProfilePageAsync(
        string characterHref,
        CancellationToken cancellationToken)
    {
        var outcome = await FetchPageContentAsync(characterHref, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return outcome;
        }

        var now = _utcNow();
        lock (_stateLock)
        {
            if (outcome.Value is null)
            {
                PruneExpiredDates(_notFoundProfileCache, now);
                _notFoundProfileCache[characterHref] = now.Add(ProfileCacheTtl);
                return outcome;
            }

            PruneExpired(_profileCache, now);
            if (_profileCache.Count >= MaxProfileCacheEntries)
            {
                _profileCache.Remove(_profileCache.MinBy(static p => p.Value.ExpiresAt).Key);
            }

            _profileCache[characterHref] = (now.Add(ProfileCacheTtl), outcome.Value);
        }

        return outcome;
    }

    private async Task<OperationOutcome<JToken>> FetchPageContentAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (ShouldPreferObscura(out _))
        {
            return await HydrateWithObscuraCdpAsync(relativePath, applyPacing: true, cancellationToken).ConfigureAwait(false);
        }

        string knownVersion;
        lock (_stateLock)
        {
            knownVersion = _cachedInertiaVersion;
        }

        if (!string.IsNullOrEmpty(knownVersion))
        {
            var inertiaOutcome = await SendInertiaGetAsync(relativePath, knownVersion, cancellationToken).ConfigureAwait(false);
            if (!inertiaOutcome.Succeeded)
            {
                return OperationOutcome<JToken>.Failure(inertiaOutcome.TransientFailure, inertiaOutcome.ErrorMessage);
            }

            var inertiaResponse = inertiaOutcome.Value;
            if (inertiaResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return OperationOutcome<JToken>.Success(null);
            }

            if (inertiaResponse.StatusCode == HttpStatusCode.Conflict)
            {
                var headerVersion = inertiaResponse.InertiaVersionHeader;
                if (!string.IsNullOrEmpty(headerVersion) && !string.Equals(headerVersion, knownVersion, StringComparison.Ordinal))
                {
                    CacheInertiaVersion(headerVersion);
                    var retryOutcome = await SendInertiaGetAsync(relativePath, headerVersion, cancellationToken).ConfigureAwait(false);
                    if (!retryOutcome.Succeeded)
                    {
                        return OperationOutcome<JToken>.Failure(retryOutcome.TransientFailure, retryOutcome.ErrorMessage);
                    }

                    var retryResponse = retryOutcome.Value;
                    if (retryResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        return OperationOutcome<JToken>.Success(null);
                    }

                    if (retryResponse.IsSuccessStatusCode)
                    {
                        var parsed = ParseInertiaOrHtmlResponse(relativePath, retryResponse.Body);
                        if (parsed.Succeeded || !CanUseObscuraCdp(out _))
                        {
                            return parsed;
                        }

                        return await HydrateWithObscuraCdpAsync(relativePath, applyPacing: false, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else if (inertiaResponse.IsSuccessStatusCode)
            {
                var parsed = ParseInertiaOrHtmlResponse(relativePath, inertiaResponse.Body);
                if (parsed.Succeeded || !CanUseObscuraCdp(out _))
                {
                    return parsed;
                }

                return await HydrateWithObscuraCdpAsync(relativePath, applyPacing: false, cancellationToken).ConfigureAwait(false);
            }
            else if (inertiaResponse.StatusCode != HttpStatusCode.Conflict)
            {
                return OperationOutcome<JToken>.Failure(
                    IsTransientStatus(inertiaResponse.StatusCode),
                    $"Tomestone Inertia request failed with HTTP {(int)inertiaResponse.StatusCode}.");
            }
        }

        var bootstrapOutcome = await SendWebRequestAsync(
            relativePath,
            static request =>
            {
                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            },
            cancellationToken).ConfigureAwait(false);
        if (!bootstrapOutcome.Succeeded)
        {
            return OperationOutcome<JToken>.Failure(bootstrapOutcome.TransientFailure, bootstrapOutcome.ErrorMessage);
        }

        var bootstrap = bootstrapOutcome.Value;
        if (bootstrap.StatusCode == HttpStatusCode.NotFound)
        {
            return OperationOutcome<JToken>.Success(null);
        }

        if (!bootstrap.IsSuccessStatusCode)
        {
            return OperationOutcome<JToken>.Failure(
                IsTransientStatus(bootstrap.StatusCode),
                $"Tomestone web page request returned HTTP {(int)bootstrap.StatusCode}.");
        }

        var embeddedDataPage = TomestoneProgressParser.ExtractDataPageJsonFromHtml(bootstrap.Body);
        if (!string.IsNullOrEmpty(embeddedDataPage))
        {
            CacheInertiaVersion(TomestoneProgressParser.ExtractInertiaVersion(embeddedDataPage));
            return UnwrapJsonPayload(embeddedDataPage);
        }

        var extractedVersion = TomestoneProgressParser.ExtractInertiaVersion(bootstrap.Body);
        if (!string.IsNullOrEmpty(extractedVersion))
        {
            CacheInertiaVersion(extractedVersion);
            var jsonOutcome = await SendInertiaGetAsync(relativePath, extractedVersion, cancellationToken).ConfigureAwait(false);
            if (!jsonOutcome.Succeeded)
            {
                return OperationOutcome<JToken>.Failure(jsonOutcome.TransientFailure, jsonOutcome.ErrorMessage);
            }

            var jsonResponse = jsonOutcome.Value;
            if (jsonResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return OperationOutcome<JToken>.Success(null);
            }

            if (!jsonResponse.IsSuccessStatusCode)
            {
                return OperationOutcome<JToken>.Failure(
                    IsTransientStatus(jsonResponse.StatusCode),
                    $"Tomestone Inertia XHR failed with HTTP {(int)jsonResponse.StatusCode}.");
            }

            var parsed = ParseInertiaOrHtmlResponse(relativePath, jsonResponse.Body);
            if (parsed.Succeeded || !CanUseObscuraCdp(out _))
            {
                return parsed;
            }

            return await HydrateWithObscuraCdpAsync(relativePath, applyPacing: false, cancellationToken).ConfigureAwait(false);
        }

        var trimmedBody = bootstrap.Body?.TrimStart() ?? string.Empty;
        if (trimmedBody.StartsWith('{'))
        {
            CacheInertiaVersion(TomestoneProgressParser.ExtractInertiaVersion(bootstrap.Body));
            return UnwrapJsonPayload(bootstrap.Body);
        }

        if (CanUseObscuraCdp(out _))
        {
            return await HydrateWithObscuraCdpAsync(relativePath, applyPacing: false, cancellationToken).ConfigureAwait(false);
        }

        if (TomestoneProgressParser.LooksLikeUnhydratedSpaShell(bootstrap.Body))
        {
            return OperationOutcome<JToken>.Failure(
                transientFailure: false,
                $"Tomestone returned an unhydrated SPA shell at {relativePath} without #app[data-page] and Obscura CDP URL is blank.");
        }

        return OperationOutcome<JToken>.Failure(
            transientFailure: false,
            $"Tomestone page at {relativePath} did not expose Inertia data-page JSON or version.");
    }

    private bool CanUseObscuraCdp(out string cdpUrl)
    {
        cdpUrl = _configuration.TomestoneObscuraCdpUrl?.Trim();
        return !string.IsNullOrEmpty(cdpUrl);
    }

    private bool ShouldPreferObscura(out string cdpUrl)
    {
        cdpUrl = _configuration.TomestoneObscuraCdpUrl?.Trim();
        return _configuration.PreferTomestoneObscura
            && ObscuraCdpClient.TryValidateEndpointUri(cdpUrl, out _);
    }

    private async Task<OperationOutcome<JToken>> HydrateWithObscuraCdpAsync(
        string relativePath,
        bool applyPacing,
        CancellationToken cancellationToken)
    {
        if (!CanUseObscuraCdp(out var cdpUrl))
        {
            return OperationOutcome<JToken>.Failure(
                transientFailure: false,
                $"Tomestone returned an unhydrated SPA shell at {relativePath} without #app[data-page] and Obscura CDP URL is blank.");
        }

        if (!TryResolveSameOriginUrl(relativePath, out var targetUri))
        {
            return OperationOutcome<JToken>.Failure(
                transientFailure: false,
                $"Invalid relative URL for Tier 3 CDP hydration: {relativePath}");
        }

        if (applyPacing)
        {
            if (TryGetCooldownRemaining(out var cooldownRemaining))
            {
                LogCooldownSkipIfNeeded(cooldownRemaining);
                return OperationOutcome<JToken>.Failure(
                    transientFailure: true,
                    "Tomestone web fallback cooldown is active.");
            }

            await WaitForPacingSlotAsync(cancellationToken).ConfigureAwait(false);
            if (TryGetCooldownRemaining(out cooldownRemaining))
            {
                LogCooldownSkipIfNeeded(cooldownRemaining);
                return OperationOutcome<JToken>.Failure(
                    transientFailure: true,
                    "Tomestone web fallback cooldown is active.");
            }
        }

        try
        {
            var hydratedJson = await _cdpHydrator.HydrateUrlAsync(
                cdpUrl,
                targetUri.ToString(),
                cancellationToken).ConfigureAwait(false);

            var postHydrationChallenge = TomestoneChallengeDetector.DetectTextChallenge(hydratedJson);
            if (postHydrationChallenge != null)
            {
                ActivateChallengeCooldown(targetUri.ToString(), postHydrationChallenge);
                return OperationOutcome<JToken>.Failure(
                    transientFailure: false,
                    $"Tomestone Tier 3 CDP detected anti-bot challenge ({postHydrationChallenge.Label}); cooldown active.");
            }

            CacheInertiaVersion(TomestoneProgressParser.ExtractInertiaVersion(hydratedJson));
            return UnwrapJsonPayload(hydratedJson);
        }
        catch (TomestoneChallengeException ex)
        {
            ActivateChallengeCooldown(targetUri.ToString(), ex.Challenge);
            return OperationOutcome<JToken>.Failure(
                transientFailure: false,
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _warningLog($"Tomestone Tier 3 CDP hydration failed for {targetUri}: {ex.Message}");
            return OperationOutcome<JToken>.Failure(
                transientFailure: true,
                $"Tomestone Tier 3 CDP hydration failed: {ex.Message}");
        }
    }

    private async Task<OperationOutcome<string>> FetchJsonWithOptionalObscuraAsync(
        string pathAndQuery,
        bool noContentIsNull,
        string requestLabel,
        CancellationToken cancellationToken)
    {
        if (ShouldPreferObscura(out var preferredCdpUrl))
        {
            return await FetchJsonViaObscuraCdpAsync(pathAndQuery, preferredCdpUrl, applyPacing: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var responseOutcome = await SendWebRequestAsync(
            pathAndQuery,
            static request =>
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            },
            cancellationToken).ConfigureAwait(false);
        if (!responseOutcome.Succeeded)
        {
            return OperationOutcome<string>.Failure(
                responseOutcome.TransientFailure,
                responseOutcome.ErrorMessage);
        }

        var response = responseOutcome.Value;
        if (response.StatusCode == HttpStatusCode.NotFound
            || (noContentIsNull && response.StatusCode == HttpStatusCode.NoContent))
        {
            return OperationOutcome<string>.Success(null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return OperationOutcome<string>.Failure(
                IsTransientStatus(response.StatusCode),
                $"{requestLabel} returned HTTP {(int)response.StatusCode}.");
        }

        if (IsValidJsonDocument(response.Body))
        {
            return OperationOutcome<string>.Success(response.Body);
        }

        if (CanUseObscuraCdp(out var fallbackCdpUrl))
        {
            return await FetchJsonViaObscuraCdpAsync(pathAndQuery, fallbackCdpUrl, applyPacing: false, cancellationToken)
                .ConfigureAwait(false);
        }

        return OperationOutcome<string>.Success(response.Body);
    }

    private async Task<OperationOutcome<string>> FetchJsonViaObscuraCdpAsync(
        string pathAndQuery,
        string cdpUrl,
        bool applyPacing,
        CancellationToken cancellationToken)
    {
        if (!TryResolveSameOriginUrl(pathAndQuery, out var targetUri))
        {
            return OperationOutcome<string>.Failure(
                transientFailure: false,
                $"Invalid relative URL for Tier 3 CDP JSON fetch: {pathAndQuery}");
        }

        if (applyPacing)
        {
            if (TryGetCooldownRemaining(out var cooldownRemaining))
            {
                LogCooldownSkipIfNeeded(cooldownRemaining);
                return OperationOutcome<string>.Failure(
                    transientFailure: true,
                    "Tomestone web fallback cooldown is active.");
            }

            await WaitForPacingSlotAsync(cancellationToken).ConfigureAwait(false);
            if (TryGetCooldownRemaining(out cooldownRemaining))
            {
                LogCooldownSkipIfNeeded(cooldownRemaining);
                return OperationOutcome<string>.Failure(
                    transientFailure: true,
                    "Tomestone web fallback cooldown is active.");
            }
        }

        try
        {
            var json = await _cdpHydrator.FetchJsonUrlAsync(
                cdpUrl,
                targetUri.ToString(),
                cancellationToken).ConfigureAwait(false);

            var challenge = TomestoneChallengeDetector.DetectTextChallenge(json);
            if (challenge != null)
            {
                ActivateChallengeCooldown(targetUri.ToString(), challenge);
                return OperationOutcome<string>.Failure(
                    transientFailure: false,
                    $"Tomestone Tier 3 CDP detected anti-bot challenge ({challenge.Label}); cooldown active.");
            }

            return OperationOutcome<string>.Success(json);
        }
        catch (TomestoneChallengeException ex)
        {
            ActivateChallengeCooldown(targetUri.ToString(), ex.Challenge);
            return OperationOutcome<string>.Failure(
                transientFailure: false,
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _warningLog($"Tomestone Tier 3 CDP JSON fetch failed for {targetUri}: {ex.Message}");
            return OperationOutcome<string>.Failure(
                transientFailure: true,
                $"Tomestone Tier 3 CDP JSON fetch failed: {ex.Message}");
        }
    }

    private static bool IsValidJsonDocument(string candidate)
    {
        var trimmed = candidate?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')))
        {
            return false;
        }

        try
        {
            _ = JToken.Parse(trimmed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<OperationOutcome<TomestoneProgressData>> FetchGraphCachedAsync(
        string graphUrl,
        CancellationToken cancellationToken)
    {
        var now = _utcNow();
        lock (_stateLock)
        {
            if (_graphCache.TryGetValue(graphUrl, out var cached) && cached.ExpiresAt > now)
            {
                return OperationOutcome<TomestoneProgressData>.Success(cached.Progress);
            }
        }

        if (!TryResolveSameOriginUrl(graphUrl, out var resolvedUri))
        {
            _warningLog($"Ignoring cross-origin or invalid Tomestone progressionGraphUrl: {graphUrl}");
            return OperationOutcome<TomestoneProgressData>.Success(null);
        }

        var jsonOutcome = await FetchJsonWithOptionalObscuraAsync(
            resolvedUri.PathAndQuery,
            noContentIsNull: false,
            "Tomestone progression graph request",
            cancellationToken).ConfigureAwait(false);
        if (!jsonOutcome.Succeeded)
        {
            return OperationOutcome<TomestoneProgressData>.Failure(
                jsonOutcome.TransientFailure,
                jsonOutcome.ErrorMessage);
        }

        if (string.IsNullOrEmpty(jsonOutcome.Value))
        {
            return OperationOutcome<TomestoneProgressData>.Success(null);
        }

        try
        {
            var token = JToken.Parse(jsonOutcome.Value);
            var parsed = TomestoneProgressParser.ParseGraphProgress(token);
            lock (_stateLock)
            {
                var cacheNow = _utcNow();
                PruneExpired(_graphCache, cacheNow);
                if (_graphCache.Count >= MaxGraphCacheEntries)
                {
                    _graphCache.Remove(_graphCache.MinBy(static p => p.Value.ExpiresAt).Key);
                }

                _graphCache[graphUrl] = (cacheNow.Add(GraphCacheTtl), parsed);
            }

            return OperationOutcome<TomestoneProgressData>.Success(parsed);
        }
        catch (JsonException ex)
        {
            return OperationOutcome<TomestoneProgressData>.Failure(
                transientFailure: false,
                $"Tomestone progressionGraphUrl returned invalid JSON: {ex.Message}");
        }
    }

    private Task<OperationOutcome<WebHttpResponse>> SendInertiaGetAsync(
        string relativePath,
        string version,
        CancellationToken cancellationToken)
    {
        return SendWebRequestAsync(
            relativePath,
            request =>
            {
                request.Headers.TryAddWithoutValidation("X-Inertia", "true");
                request.Headers.TryAddWithoutValidation("X-Inertia-Version", version);
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Accept", "text/html, application/xhtml+xml");
            },
            cancellationToken);
    }

    private async Task<OperationOutcome<WebHttpResponse>> SendWebRequestAsync(
        string pathAndQuery,
        Action<HttpRequestMessage> configureHeaders,
        CancellationToken cancellationToken)
    {
        if (TryGetCooldownRemaining(out var cooldownRemaining))
        {
            LogCooldownSkipIfNeeded(cooldownRemaining);
            return OperationOutcome<WebHttpResponse>.Failure(
                transientFailure: true,
                "Tomestone web fallback cooldown is active.");
        }

        if (!TryResolveSameOriginUrl(pathAndQuery, out var targetUri))
        {
            return OperationOutcome<WebHttpResponse>.Failure(
                transientFailure: false,
                $"Invalid same-origin Tomestone web path '{pathAndQuery}'.");
        }

        await WaitForPacingSlotAsync(cancellationToken).ConfigureAwait(false);

        if (TryGetCooldownRemaining(out cooldownRemaining))
        {
            LogCooldownSkipIfNeeded(cooldownRemaining);
            return OperationOutcome<WebHttpResponse>.Failure(
                transientFailure: true,
                "Tomestone web fallback cooldown is active.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);
        configureHeaders?.Invoke(request);

        try
        {
            _debugLog($"Tomestone Web GET {targetUri}");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = response.Content != null
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : string.Empty;

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                _warningLog($"Tomestone Web GET {targetUri} attempted redirect ({(int)response.StatusCode}), which is disabled.");
                return OperationOutcome<WebHttpResponse>.Failure(
                    transientFailure: false,
                    $"Tomestone Web request attempted redirect ({(int)response.StatusCode}).");
            }

            var challenge = TomestoneChallengeDetector.DetectWebHttpChallenge(
                response.StatusCode,
                response.Headers,
                body,
                _utcNow());
            if (challenge != null)
            {
                ActivateChallengeCooldown(targetUri.ToString(), challenge);
                return OperationOutcome<WebHttpResponse>.Failure(
                    transientFailure: false,
                    $"Tomestone Web aborted on anti-bot challenge ({challenge.Label}); cooldown active.");
            }

            string inertiaVersionHeader = null;
            if (response.Headers.TryGetValues("X-Inertia-Version", out var versionValues))
            {
                inertiaVersionHeader = versionValues.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
            }

            return OperationOutcome<WebHttpResponse>.Success(new WebHttpResponse(
                response.StatusCode,
                response.IsSuccessStatusCode,
                body,
                inertiaVersionHeader));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _warningLog($"Tomestone Web GET {targetUri} failed: {ex.GetType().Name}: {ex.Message}");
            return OperationOutcome<WebHttpResponse>.Failure(
                transientFailure: true,
                $"Tomestone Web GET failed: {ex.Message}");
        }
    }

    private async Task WaitForPacingSlotAsync(CancellationToken cancellationToken)
    {
        await _pacingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var intervalMs = Configuration.NormalizeTomestoneRequestIntervalMs(_configuration.TomestoneRequestIntervalMs);
            if (intervalMs > 0 && _lastRequestStartedUtc != DateTime.MinValue)
            {
                var elapsed = _utcNow() - _lastRequestStartedUtc;
                var required = TimeSpan.FromMilliseconds(intervalMs);
                if (elapsed < required)
                {
                    await _delayAsync(required - elapsed, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastRequestStartedUtc = _utcNow();
        }
        finally
        {
            _pacingLock.Release();
        }
    }

    private OperationOutcome<JToken> ParseInertiaOrHtmlResponse(string relativePath, string body)
    {
        var embeddedDataPage = TomestoneProgressParser.ExtractDataPageJsonFromHtml(body);
        if (!string.IsNullOrEmpty(embeddedDataPage))
        {
            CacheInertiaVersion(TomestoneProgressParser.ExtractInertiaVersion(embeddedDataPage));
            return UnwrapJsonPayload(embeddedDataPage);
        }

        if (TomestoneProgressParser.LooksLikeUnhydratedSpaShell(body))
        {
            return OperationOutcome<JToken>.Failure(
                transientFailure: false,
                $"Tomestone Inertia response returned HTML shell instead of JSON for {relativePath}.");
        }

        CacheInertiaVersion(TomestoneProgressParser.ExtractInertiaVersion(body));
        return UnwrapJsonPayload(body);
    }

    private static OperationOutcome<JToken> UnwrapJsonPayload(string json)
    {
        try
        {
            return OperationOutcome<JToken>.Success(TomestoneProgressParser.UnwrapCharacterPageContent(json));
        }
        catch (JsonException ex)
        {
            return OperationOutcome<JToken>.Failure(
                transientFailure: false,
                $"Failed to parse Tomestone web JSON payload: {ex.Message}");
        }
    }

    private void CacheInertiaVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        lock (_stateLock)
        {
            _cachedInertiaVersion = version.Trim();
        }
    }

    private void ActivateChallengeCooldown(string source, TomestoneChallengeInfo challenge)
    {
        var duration = TomestoneChallengeDetector.EffectiveCooldownDuration(
            challenge,
            TomestoneChallengeDetector.DefaultChallengeCooldown);
        _cooldown.Activate(_utcNow(), duration);
        _warningLog(
            $"Tomestone Web anti-bot challenge ({challenge.Label}) at {source}. Aborting fallback and entering cooldown until {CooldownUntilUtc:O} ({Math.Ceiling(duration.TotalSeconds)}s).");
    }

    private void LogCooldownSkipIfNeeded(TimeSpan remaining)
    {
        if (!_cooldown.ShouldLogSkip(_utcNow(), TimeSpan.FromMinutes(1)))
        {
            return;
        }

        var minutesRemaining = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        _warningLog($"Tomestone Web cooldown active. Skipping web fallback for about {minutesRemaining} minute(s).");
    }

    private bool TryResolveSameOriginUrl(string candidate, out Uri resolved)
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

            return Uri.TryCreate(_originUri, trimmed, out resolved);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var sameOrigin = string.Equals(parsed.Scheme, _originUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parsed.Host, _originUri.Host, StringComparison.OrdinalIgnoreCase)
            && parsed.Port == _originUri.Port;
        if (!sameOrigin)
        {
            return false;
        }

        resolved = parsed;
        return true;
    }

    private static string SelectAutocompleteHref(
        string name,
        string server,
        IReadOnlyCollection<ParseJobCandidateServer> candidateServers,
        string body)
    {
        var root = JObject.Parse(body);
        if (root["characters"] is not JArray charactersArray)
        {
            return null;
        }

        var matchingName = new List<(string ServerName, string Href)>();
        foreach (var entry in charactersArray.OfType<JObject>())
        {
            var href = entry["href"]?.Value<string>()?.Trim();
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var item = entry["item"] as JObject;
            var itemName = item?["name"]?.Value<string>()?.Trim() ?? string.Empty;
            var itemTitle = item?["title"]?.Value<string>()?.Trim() ?? string.Empty;
            if (!StringsEqualIgnoreCase(name, itemName) && !StringsEqualIgnoreCase(name, itemTitle))
            {
                continue;
            }

            var itemServer = item?["serverName"]?.Value<string>()?.Trim() ?? string.Empty;
            matchingName.Add((itemServer, href));
        }

        if (matchingName.Count == 0)
        {
            return null;
        }

        foreach (var candidate in matchingName)
        {
            if (ServerMatches(server, candidateServers, candidate.ServerName))
            {
                return candidate.Href;
            }
        }

        return matchingName.Count == 1 ? matchingName[0].Href : null;
    }

    private static bool ServerMatches(
        string primaryServer,
        IReadOnlyCollection<ParseJobCandidateServer> candidateServers,
        string itemServerName)
    {
        if (string.IsNullOrWhiteSpace(itemServerName))
        {
            return false;
        }

        if (StringsEqualIgnoreCase(primaryServer, itemServerName))
        {
            return true;
        }

        return candidateServers != null
            && candidateServers.Any(candidate => StringsEqualIgnoreCase(candidate?.Server, itemServerName));
    }

    private static bool StringsEqualIgnoreCase(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCharacterHref(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var trimmed = href.Trim();
        var queryIdx = trimmed.IndexOf('?');
        if (queryIdx >= 0)
        {
            trimmed = trimmed[..queryIdx];
        }

        trimmed = trimmed.TrimEnd('/');
        return trimmed.StartsWith("/character/", StringComparison.Ordinal)
            ? trimmed
            : null;
    }

    private static string BuildAutocompletePath(string name)
        => $"/search/autocomplete?term={Uri.EscapeDataString(name.Trim())}&type=&scope=";

    private static string BuildClearsPath(string characterHref, string category)
        => $"{characterHref}/clears?encounterCategory={Uri.EscapeDataString(category)}&include=character";

    private static string BuildActivityPath(string characterHref, TomestoneEncounterParams target)
        => $"{characterHref}/activity?category={Uri.EscapeDataString(target.EffectiveActivityCategory)}"
            + $"&encounter={Uri.EscapeDataString(target.Encounter)}"
            + $"&expansion={Uri.EscapeDataString(target.Expansion)}"
            + $"&zone={Uri.EscapeDataString(target.Category)}";

    private static bool HasUsefulProgress(TomestoneProgressData progress, TomestoneProgressKind progressKind)
        => progress != null && (progress.Cleared || (progressKind switch
        {
            TomestoneProgressKind.PhaseProgress => progress.Phases.Count > 0,
            TomestoneProgressKind.BossPercentage => progress.BossPercentage.HasValue
                || progress.Phases.Any(static p => p.BossPercentage.HasValue),
            _ => false,
        }));

    private static bool IsTransientStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout || code == 429 || code >= 500;
    }

    private static void PruneExpired<TKey, TVal>(Dictionary<TKey, (DateTime ExpiresAt, TVal)> cache, DateTime now)
    {
        var expired = cache.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray();
        foreach (var key in expired)
        {
            cache.Remove(key);
        }
    }

    private static void PruneExpiredDates(Dictionary<string, DateTime> cache, DateTime now)
    {
        var expired = cache.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray();
        foreach (var key in expired)
        {
            cache.Remove(key);
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = DefaultTimeout,
        };
    }

    private static HttpClient CreateHttpClientWithTimeout(HttpMessageHandler messageHandler)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        return new HttpClient(messageHandler)
        {
            Timeout = DefaultTimeout,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _pacingLock.Dispose();
    }

    private sealed record WebHttpResponse(
        HttpStatusCode StatusCode,
        bool IsSuccessStatusCode,
        string Body,
        string InertiaVersionHeader);
}
