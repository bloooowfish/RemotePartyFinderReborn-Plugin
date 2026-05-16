using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsBatchProcessor
{
    private readonly FFLogsCollectorSeams _seams;
    private readonly FFLogsBossPercentEnricher _bossPercentEnricher;
    private readonly TomestoneProgressEnricher _tomestoneProgressEnricher;

    public FFLogsBatchProcessor(FFLogsCollectorSeams seams)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _bossPercentEnricher = new FFLogsBossPercentEnricher(_seams);
        _tomestoneProgressEnricher = new TomestoneProgressEnricher(_seams);
    }

    public async Task<FFLogsBatchProcessResult> ProcessLeaseSessionAsync(
        FFLogsLeaseSession leaseSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaseSession);

        var results = new List<ParseResult>();
        var jobsByZone = leaseSession.Jobs.GroupBy(j => new { j.ZoneId, j.DifficultyId });

        const int recentReportsLimit = 10;
        var hitRateLimitCooldown = false;
        var cooldownRemaining = TimeSpan.Zero;
        var hadTransientFailure = false;

        foreach (var group in jobsByZone)
        {
            if (_seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining))
            {
                hitRateLimitCooldown = true;
                break;
            }

            var zoneId = group.Key.ZoneId;
            var difficultyId = group.Key.DifficultyId == 0 ? (int?)null : group.Key.DifficultyId;

            var candidateMatcher = new FFLogsCandidateMatcher(group.ToList(), zoneId, group.Key.DifficultyId);
            var jobs = candidateMatcher.Jobs;
            var candidateQueries = candidateMatcher.BuildCandidateQueries();

            var fetchedOutcome = await _seams.ApiClient.FetchCharacterCandidateDataBatchAsync(
                candidateQueries,
                (int)zoneId,
                difficultyId,
                recentReportsLimit,
                cancellationToken);

            if (!fetchedOutcome.Succeeded)
            {
                hadTransientFailure |= fetchedOutcome.TransientFailure;
                hitRateLimitCooldown = TryCaptureCooldown(out cooldownRemaining);
                break;
            }

            var fetchedByKey = fetchedOutcome.Value ?? [];

            if (_seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining))
            {
                hitRateLimitCooldown = true;
                break;
            }

            var candidateMatch = candidateMatcher.MatchFetchedCandidates(fetchedByKey);
            var resultsByContentId = candidateMatch.ResultsByContentId;
            var chosenDataByCid = candidateMatch.ChosenDataByContentId;

            var nonHiddenJobs = jobs
                .Where(j => resultsByContentId.TryGetValue(j.ContentId, out var result) && !result.IsHidden)
                .ToList();

            var bossPercentEnrichmentResult = await _bossPercentEnricher.EnrichBossPercentagesAsync(
                nonHiddenJobs,
                resultsByContentId,
                chosenDataByCid,
                difficultyId,
                cancellationToken);
            hadTransientFailure |= bossPercentEnrichmentResult.HadTransientFailure;
            hitRateLimitCooldown = bossPercentEnrichmentResult.HitRateLimitCooldown;
            cooldownRemaining = bossPercentEnrichmentResult.CooldownRemaining;

            if (hitRateLimitCooldown)
            {
                break;
            }

            var tomestoneHadTransientFailure = await _tomestoneProgressEnricher.EnrichProgressAsync(
                jobs,
                resultsByContentId,
                zoneId,
                cancellationToken);
            hadTransientFailure |= tomestoneHadTransientFailure;

            results.AddRange(resultsByContentId.Values);
            await Task.Delay(1000, cancellationToken);
        }

        return new FFLogsBatchProcessResult(
            ProcessedResults: results,
            HadTransientFailure: hadTransientFailure,
            HitRateLimitCooldown: hitRateLimitCooldown,
            CooldownRemaining: cooldownRemaining,
            ShouldAbandonRemainingLeases: hitRateLimitCooldown);
    }

    private bool TryCaptureCooldown(out TimeSpan cooldownRemaining)
        => _seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining);

}
