using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RemotePartyFinderReborn.Tomestone;

namespace RemotePartyFinderReborn;

internal sealed class TomestoneProgressEnricher
{
    private readonly FFLogsCollectorSeams _seams;

    public TomestoneProgressEnricher(FFLogsCollectorSeams seams)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
    }

    public async Task<bool> EnrichClearsFallbackAsync(
        IReadOnlyList<ParseJob> jobs,
        IDictionary<ulong, ParseResult> resultsByContentId,
        CancellationToken cancellationToken)
    {
        var tomestoneClient = _seams.TomestoneApiClient;
        if (!tomestoneClient.IsFallbackConfigured)
        {
            return false;
        }

        var missingJobs = jobs
            .Where(job => NeedsClearsFallback(job, resultsByContentId))
            .ToList();
        if (missingJobs.Count == 0)
        {
            return false;
        }

        var requestInterval = GetTomestoneRequestInterval();
        var maxConcurrency = GetTomestoneMaxConcurrency();
        var hadTransientFailure = false;

        for (var offset = 0; offset < missingJobs.Count; offset += maxConcurrency)
        {
            if (tomestoneClient.TryGetRateLimitRemaining(out _))
            {
                return hadTransientFailure;
            }

            if (offset > 0 && requestInterval > TimeSpan.Zero)
            {
                await _seams.TomestoneRequestDelayAsync(requestInterval, cancellationToken).ConfigureAwait(false);
            }

            var count = Math.Min(maxConcurrency, missingJobs.Count - offset);
            var tasks = missingJobs
                .Skip(offset)
                .Take(count)
                .Select(job => FetchFallbackJobAsync(tomestoneClient, job, resultsByContentId, cancellationToken))
                .ToArray();
            var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var (job, outcome) in outcomes)
            {
                if (!outcome.Succeeded)
                {
                    hadTransientFailure |= outcome.TransientFailure;
                    continue;
                }

                if (outcome.Value is { } fallbackResult)
                {
                    MergeFallbackParseResult(job, fallbackResult, resultsByContentId);
                }
            }
        }

        return hadTransientFailure;
    }

    public async Task<bool> EnrichProgressAsync(
        IReadOnlyList<ParseJob> jobs,
        IDictionary<ulong, ParseResult> resultsByContentId,
        uint zoneId,
        CancellationToken cancellationToken)
    {
        var tomestoneClient = _seams.TomestoneApiClient;
        if (!tomestoneClient.IsConfigured)
        {
            return false;
        }

        var requestInterval = GetTomestoneRequestInterval();
        var maxConcurrency = GetTomestoneMaxConcurrency();
        var hadTransientFailure = false;
        var requests = BuildRequests(jobs, resultsByContentId, zoneId).ToList();

        for (var offset = 0; offset < requests.Count; offset += maxConcurrency)
        {
            if (tomestoneClient.TryGetRateLimitRemaining(out _))
            {
                return hadTransientFailure;
            }

            if (offset > 0 && requestInterval > TimeSpan.Zero)
            {
                await _seams.TomestoneRequestDelayAsync(requestInterval, cancellationToken).ConfigureAwait(false);
            }

            var count = Math.Min(maxConcurrency, requests.Count - offset);
            var fetchTasks = requests
                .Skip(offset)
                .Take(count)
                .Select(request => FetchProgressAsync(
                    tomestoneClient,
                    request,
                    cancellationToken))
                .ToArray();
            var fetchResults = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
            foreach (var fetchResult in fetchResults)
            {
                hadTransientFailure |= fetchResult.HadTransientFailure;
                ApplyTomestoneProgress(fetchResult, resultsByContentId);
                if (fetchResult.Succeeded)
                {
                    ApplySiblingSavageProgressFromCache(
                        tomestoneClient,
                        fetchResult.Request,
                        resultsByContentId);
                }
            }
        }

        return hadTransientFailure;
    }

    private static bool NeedsClearsFallback(
        ParseJob job,
        IDictionary<ulong, ParseResult> resultsByContentId)
    {
        if (!resultsByContentId.TryGetValue(job.ContentId, out var current))
        {
            return !string.IsNullOrWhiteSpace(job.Server);
        }

        if (current.IsHidden)
        {
            return false;
        }

        return current.Encounters.Count == 0
            && current.ClearCounts.Count == 0
            && current.PhaseProgress.Count == 0
            && current.BossPercentages.Count == 0
            && !string.Equals(current.SourceKind, "tomestone_api", StringComparison.Ordinal);
    }

    private static async Task<(ParseJob Job, OperationOutcome<ParseResult> Outcome)> FetchFallbackJobAsync(
        ITomestoneApiClient tomestoneClient,
        ParseJob job,
        IDictionary<ulong, ParseResult> resultsByContentId,
        CancellationToken cancellationToken)
    {
        resultsByContentId.TryGetValue(job.ContentId, out var existing);
        var lookupServer = ResolveLookupServer(job, existing);
        if (string.IsNullOrWhiteSpace(lookupServer))
        {
            return (job, OperationOutcome<ParseResult>.Success(null));
        }

        try
        {
            var outcome = await tomestoneClient
                .FetchFallbackParseResultAsync(job, lookupServer, cancellationToken)
                .ConfigureAwait(false);
            return (job, outcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (job, OperationOutcome<ParseResult>.Failure(true, ex.Message));
        }
    }

    private static void MergeFallbackParseResult(
        ParseJob job,
        ParseResult fallbackResult,
        IDictionary<ulong, ParseResult> resultsByContentId)
    {
        if (!resultsByContentId.TryGetValue(job.ContentId, out var current))
        {
            resultsByContentId[job.ContentId] = fallbackResult;
            return;
        }

        if (current.IsHidden)
        {
            return;
        }

        foreach (var pair in fallbackResult.Encounters)
        {
            if (!current.Encounters.ContainsKey(pair.Key))
            {
                current.Encounters[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in fallbackResult.ClearCounts)
        {
            if (!current.ClearCounts.ContainsKey(pair.Key))
            {
                current.ClearCounts[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in fallbackResult.BossPercentages)
        {
            if (!current.BossPercentages.ContainsKey(pair.Key))
            {
                current.BossPercentages[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in fallbackResult.PhaseProgress)
        {
            if (!current.PhaseProgress.ContainsKey(pair.Key))
            {
                current.PhaseProgress[pair.Key] = pair.Value.ToList();
            }
        }

        foreach (var queriedEncounter in fallbackResult.TomestoneQueriedEncounters)
        {
            if (!current.TomestoneQueriedEncounters.Contains(queriedEncounter))
            {
                current.TomestoneQueriedEncounters.Add(queriedEncounter);
            }
        }

        if (string.IsNullOrWhiteSpace(current.SourceKind))
        {
            current.SourceKind = fallbackResult.SourceKind;
        }

        if (string.IsNullOrWhiteSpace(current.MatchedServer))
        {
            current.MatchedServer = fallbackResult.MatchedServer;
        }
    }

    private static void ApplySiblingSavageProgressFromCache(
        ITomestoneApiClient tomestoneClient,
        TomestoneRequest request,
        IDictionary<ulong, ParseResult> resultsByContentId)
    {
        if (request.ProgressKind != TomestoneProgressKind.BossPercentage)
        {
            return;
        }

        if (!resultsByContentId.TryGetValue(request.Job.ContentId, out var parseResult) || parseResult == null)
        {
            return;
        }

        var exclude = new HashSet<uint>(parseResult.TomestoneQueriedEncounters.Select(static id => (uint)id))
        {
            request.EncounterId,
        };

        var siblingProgressByEncounter = tomestoneClient.TryGetCachedZoneSiblingProgress(
            request.Job.Name,
            request.LookupServer,
            request.ZoneId,
            exclude);
        if (siblingProgressByEncounter == null || siblingProgressByEncounter.Count == 0)
        {
            return;
        }

        foreach (var (siblingEncounterId, siblingProgress) in siblingProgressByEncounter)
        {
            if (!siblingProgress.IsAuthoritative)
            {
                continue;
            }

            if (!TomestoneEncounterMapping.TryGetProgressionTarget(
                    request.ZoneId,
                    siblingEncounterId,
                    out var siblingParams)
                || siblingParams.ProgressKind != TomestoneProgressKind.BossPercentage)
            {
                continue;
            }

            var siblingKey = (int)siblingEncounterId;
            var hasExistingClear =
                (parseResult.Encounters.TryGetValue(siblingKey, out var pct) && pct > 0.0)
                || (parseResult.ClearCounts.TryGetValue(siblingKey, out var count) && count > 0);
            if (hasExistingClear)
            {
                continue;
            }

            ApplyTomestoneProgressValues(parseResult, siblingKey, siblingParams.ProgressKind, siblingProgress);
        }
    }

    private static void ApplyTomestoneProgress(
        TomestoneFetchResult fetchResult,
        IDictionary<ulong, ParseResult> resultsByContentId)
    {
        if (!fetchResult.Succeeded)
        {
            return;
        }

        var parseResult = GetExistingMergeTarget(fetchResult.Request, resultsByContentId);
        if (parseResult == null)
        {
            if (resultsByContentId.TryGetValue(fetchResult.Request.Job.ContentId, out var current)
                && current.IsHidden)
            {
                return;
            }

            parseResult = CreateTomestoneProgressOnlyResult(fetchResult.Request);
            resultsByContentId[fetchResult.Request.Job.ContentId] = parseResult;
        }

        ApplyTomestoneProgressValues(
            parseResult,
            (int)fetchResult.Request.EncounterId,
            fetchResult.Request.ProgressKind,
            fetchResult.Progress);
    }

    private static void ApplyTomestoneProgressValues(
        ParseResult parseResult,
        int encounterId,
        TomestoneProgressKind progressKind,
        TomestoneProgressData progress)
    {
        if (!parseResult.TomestoneQueriedEncounters.Contains(encounterId))
        {
            parseResult.TomestoneQueriedEncounters.Add(encounterId);
        }

        parseResult.PhaseProgress.Remove(encounterId);
        parseResult.BossPercentages.Remove(encounterId);
        if (progress.Cleared)
        {
            return;
        }

        switch (progressKind)
        {
            case TomestoneProgressKind.PhaseProgress:
                if (progress.Phases.Count > 0)
                {
                    parseResult.PhaseProgress[encounterId] = progress.Phases.ToList();
                }

                break;
            case TomestoneProgressKind.BossPercentage:
                if (TryGetBossPercentage(progress, out var bossPercentage))
                {
                    parseResult.BossPercentages[encounterId] = bossPercentage;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(progressKind), progressKind, null);
        }
    }

    private static ParseResult GetExistingMergeTarget(
        TomestoneRequest request,
        IDictionary<ulong, ParseResult> resultsByContentId)
    {
        if (!resultsByContentId.TryGetValue(request.Job.ContentId, out var current))
        {
            return null;
        }

        if (!current.IsHidden || string.Equals(current.SourceKind, "tomestone_api", StringComparison.Ordinal))
        {
            return current;
        }

        return null;
    }

    private static ParseResult CreateTomestoneProgressOnlyResult(TomestoneRequest request)
        => new()
        {
            ContentId = request.Job.ContentId,
            ZoneId = request.Job.ZoneId,
            DifficultyId = request.Job.DifficultyId,
            IsEstimated = request.Job.CandidateServers?.Count > 0,
            MatchedServer = request.LookupServer,
            SourceKind = "tomestone_api",
            LeaseToken = request.Job.LeaseToken,
        };

    private static bool TryGetBossPercentage(TomestoneProgressData progress, out double bossPercentage)
    {
        if (progress.BossPercentage.HasValue)
        {
            bossPercentage = progress.BossPercentage.Value;
            return true;
        }

        var phaseBossPercentage = progress.Phases
            .Where(static phase => phase.BossPercentage.HasValue)
            .Select(static phase => phase.BossPercentage!.Value)
            .DefaultIfEmpty(double.NaN)
            .Min();
        if (!double.IsNaN(phaseBossPercentage))
        {
            bossPercentage = phaseBossPercentage;
            return true;
        }

        bossPercentage = 0;
        return false;
    }

    private TimeSpan GetTomestoneRequestInterval()
    {
        var configuredMs = _seams.Configuration?.TomestoneRequestIntervalMs ?? 0;
        var clampedMs = Configuration.NormalizeTomestoneRequestIntervalMs(configuredMs);
        return TimeSpan.FromMilliseconds(clampedMs);
    }

    private int GetTomestoneMaxConcurrency()
    {
        var configured = _seams.Configuration?.TomestoneMaxConcurrency ?? 1;
        return Configuration.NormalizeTomestoneMaxConcurrency(configured);
    }

    private static IEnumerable<TomestoneRequest> BuildRequests(
        IReadOnlyList<ParseJob> jobs,
        IDictionary<ulong, ParseResult> resultsByContentId,
        uint zoneId)
    {
        foreach (var job in jobs)
        {
            resultsByContentId.TryGetValue(job.ContentId, out var parseResult);
            if (parseResult?.IsHidden == true
                && !string.Equals(parseResult.SourceKind, "tomestone_api", StringComparison.Ordinal))
            {
                continue;
            }

            var lookupServer = ResolveLookupServer(job, parseResult);
            if (string.IsNullOrWhiteSpace(lookupServer))
            {
                continue;
            }

            foreach (var target in GetMappedTomestoneEncounterTargets(zoneId, job))
            {
                if (parseResult?.TomestoneQueriedEncounters.Contains((int)target.EncounterId) == true)
                {
                    continue;
                }

                yield return new TomestoneRequest(
                    job,
                    lookupServer,
                    target.ZoneId,
                    target.EncounterId,
                    target.ProgressKind);
            }
        }
    }

    private static string ResolveLookupServer(ParseJob job, ParseResult parseResult)
    {
        if (!string.IsNullOrWhiteSpace(parseResult?.MatchedServer))
        {
            return parseResult.MatchedServer.Trim();
        }

        return job.Server?.Trim() ?? string.Empty;
    }

    private static async Task<TomestoneFetchResult> FetchProgressAsync(
        ITomestoneApiClient tomestoneClient,
        TomestoneRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await tomestoneClient.FetchProgressionDataAsync(
                request.Job.Name,
                request.LookupServer,
                request.ZoneId,
                request.EncounterId,
                cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                return new TomestoneFetchResult(request, TomestoneProgressData.Empty, false, outcome.TransientFailure);
            }

            var progress = outcome.Value ?? TomestoneProgressData.Skipped;
            return new TomestoneFetchResult(request, progress, progress.IsAuthoritative, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new TomestoneFetchResult(request, TomestoneProgressData.Empty, false, true);
        }
    }

    private static IEnumerable<(uint ZoneId, uint EncounterId, TomestoneProgressKind ProgressKind)> GetMappedTomestoneEncounterTargets(uint zoneId, ParseJob job)
    {
        var seen = new HashSet<(uint ZoneId, uint EncounterId)>();
        foreach (var encounterId in EnumerateEncounterIds(job))
        {
            if (TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var encounterParams))
            {
                var target = (zoneId, encounterId);
                if (seen.Add(target))
                {
                    yield return (target.Item1, target.Item2, encounterParams.ProgressKind);
                }
            }
            else if (TomestoneEncounterMapping.TryGetProgressionTargetByEncounterId(
                         encounterId,
                         out var mappedZoneId,
                         out encounterParams))
            {
                var target = (mappedZoneId, encounterId);
                if (seen.Add(target))
                {
                    yield return (target.Item1, target.Item2, encounterParams.ProgressKind);
                }
            }
        }
    }

    private static IEnumerable<uint> EnumerateEncounterIds(ParseJob job)
    {
        if (job.EncounterId != 0)
        {
            yield return job.EncounterId;
        }

        if (job.SecondaryEncounterId.HasValue && job.SecondaryEncounterId.Value != 0)
        {
            yield return job.SecondaryEncounterId.Value;
        }
    }

    private sealed record TomestoneRequest(
        ParseJob Job,
        string LookupServer,
        uint ZoneId,
        uint EncounterId,
        TomestoneProgressKind ProgressKind);

    private sealed record TomestoneFetchResult(
        TomestoneRequest Request,
        TomestoneProgressData Progress,
        bool Succeeded,
        bool HadTransientFailure);
}
