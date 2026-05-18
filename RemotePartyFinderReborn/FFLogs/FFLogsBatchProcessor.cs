using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsBatchProcessor
{
    private readonly FFLogsCollectorSeams _seams;
    private readonly TomestoneProgressEnricher _tomestoneProgressEnricher;

    public FFLogsBatchProcessor(FFLogsCollectorSeams seams)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _tomestoneProgressEnricher = new TomestoneProgressEnricher(_seams);
    }

    public async Task<FFLogsBatchProcessResult> ProcessLeaseSessionAsync(
        FFLogsLeaseSession leaseSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaseSession);

        var results = new List<ParseResult>();
        var jobsByZone = leaseSession.Jobs.GroupBy(j => new { j.ZoneId, j.DifficultyId });

        var hitRateLimitCooldown = false;
        var cooldownRemaining = TimeSpan.Zero;
        var hadTransientFailure = false;

        foreach (var group in jobsByZone)
        {
            var zoneId = group.Key.ZoneId;
            var difficultyId = group.Key.DifficultyId == 0 ? (int?)null : group.Key.DifficultyId;
            var candidateMatcher = new FFLogsCandidateMatcher(group.ToList(), zoneId, group.Key.DifficultyId);
            var jobs = candidateMatcher.Jobs;

            if (_seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining))
            {
                var tomestoneOnlyResults = new Dictionary<ulong, ParseResult>();
                await _tomestoneProgressEnricher.EnrichProgressAsync(
                    jobs,
                    tomestoneOnlyResults,
                    zoneId,
                    cancellationToken);
                results.AddRange(tomestoneOnlyResults.Values);
                hitRateLimitCooldown = true;
                break;
            }

            var candidateQueries = candidateMatcher.BuildCandidateQueries();

            var fetchedOutcome = await _seams.ApiClient.FetchCharacterCandidateDataBatchAsync(
                candidateQueries,
                (int)zoneId,
                difficultyId,
                cancellationToken);

            if (!fetchedOutcome.Succeeded)
            {
                var tomestoneOnlyResults = new Dictionary<ulong, ParseResult>();
                await _tomestoneProgressEnricher.EnrichProgressAsync(
                    jobs,
                    tomestoneOnlyResults,
                    zoneId,
                    cancellationToken);
                results.AddRange(tomestoneOnlyResults.Values);
                hadTransientFailure |= fetchedOutcome.TransientFailure;
                hitRateLimitCooldown = TryCaptureCooldown(out cooldownRemaining);
                break;
            }

            var fetchedByKey = fetchedOutcome.Value ?? [];

            var candidateMatch = candidateMatcher.MatchFetchedCandidates(fetchedByKey);
            var resultsByContentId = candidateMatch.ResultsByContentId;

            await _tomestoneProgressEnricher.EnrichProgressAsync(
                jobs,
                resultsByContentId,
                zoneId,
                cancellationToken);

            if (_seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining))
            {
                results.AddRange(resultsByContentId.Values);
                hitRateLimitCooldown = true;
                break;
            }

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
