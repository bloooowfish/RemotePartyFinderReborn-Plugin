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

    public async Task<bool> EnrichProgressAsync(
        IReadOnlyList<ParseJob> jobs,
        IReadOnlyDictionary<ulong, ParseResult> resultsByContentId,
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
                await _seams.TomestoneRequestDelayAsync(requestInterval, cancellationToken);
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
            var fetchResults = await Task.WhenAll(fetchTasks);
            foreach (var fetchResult in fetchResults)
            {
                hadTransientFailure |= fetchResult.HadTransientFailure;
                ApplyTomestoneProgress(fetchResult);
            }
        }

        return hadTransientFailure;
    }

    private static void ApplyTomestoneProgress(TomestoneFetchResult fetchResult)
    {
        switch (fetchResult.Request.ProgressKind)
        {
            case TomestoneProgressKind.PhaseProgress:
                if (fetchResult.Progress.Phases.Count > 0)
                {
                    fetchResult.Request.ParseResult.PhaseProgress[(int)fetchResult.Request.EncounterId] =
                        fetchResult.Progress.Phases.ToList();
                }

                break;
            case TomestoneProgressKind.BossPercentage:
                if (TryGetBossPercentage(fetchResult.Progress, out var bossPercentage))
                {
                    fetchResult.Request.ParseResult.BossPercentages[(int)fetchResult.Request.EncounterId] =
                        bossPercentage;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fetchResult), fetchResult.Request.ProgressKind, null);
        }
    }

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
        IReadOnlyDictionary<ulong, ParseResult> resultsByContentId,
        uint zoneId)
    {
        foreach (var job in jobs)
        {
            if (!resultsByContentId.TryGetValue(job.ContentId, out var parseResult) || parseResult.IsHidden)
            {
                continue;
            }

            foreach (var target in GetMappedTomestoneEncounterTargets(zoneId, job))
            {
                yield return new TomestoneRequest(
                    job,
                    parseResult,
                    target.ZoneId,
                    target.EncounterId,
                    target.ProgressKind);
            }
        }
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
                request.ParseResult.MatchedServer,
                request.ZoneId,
                request.EncounterId,
                cancellationToken);
            if (!outcome.Succeeded)
            {
                return new TomestoneFetchResult(request, TomestoneProgressData.Empty, outcome.TransientFailure);
            }

            return new TomestoneFetchResult(request, outcome.Value ?? TomestoneProgressData.Empty, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new TomestoneFetchResult(request, TomestoneProgressData.Empty, true);
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
        ParseResult ParseResult,
        uint ZoneId,
        uint EncounterId,
        TomestoneProgressKind ProgressKind);

    private sealed record TomestoneFetchResult(
        TomestoneRequest Request,
        TomestoneProgressData Progress,
        bool HadTransientFailure);
}
