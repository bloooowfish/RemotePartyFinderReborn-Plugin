using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsBatchProcessor
{
    private readonly FFLogsCollectorSeams _seams;

    public FFLogsBatchProcessor(FFLogsCollectorSeams seams)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
    }

    public async Task<FFLogsBatchProcessResult> ProcessLeaseSessionAsync(
        FFLogsLeaseSession leaseSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaseSession);

        var results = new List<ParseResult>();
        var abandonedLeases = new List<AbandonFflogsLease>();
        var jobsByZone = leaseSession.Jobs.GroupBy(j => new { j.ZoneId, j.DifficultyId });

        var hitRateLimitCooldown = false;
        var cooldownRemaining = TimeSpan.Zero;
        var hadTransientFailure = false;

        foreach (var group in jobsByZone)
        {
            var zoneId = group.Key.ZoneId;
            var difficultyId = group.Key.DifficultyId == 0 ? (int?)null : group.Key.DifficultyId;
            var candidateMatcher = new FFLogsCandidateMatcher(group.ToList(), zoneId, group.Key.DifficultyId);

            foreach (var missingJob in candidateMatcher.MissingIdentityJobs)
            {
                if (!string.IsNullOrWhiteSpace(missingJob.LeaseToken))
                {
                    abandonedLeases.Add(FFLogsLeaseAbandoner.CreateJobAbandonment(missingJob, "fflogs_missing_identity"));
                }
            }

            var candidateQueries = candidateMatcher.BuildCandidateQueries();
            if (candidateQueries.Count == 0)
            {
                continue;
            }

            if (_seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining))
            {
                hitRateLimitCooldown = true;
                break;
            }

            var fetchedOutcome = await _seams.ApiClient.FetchCharacterCandidateDataBatchAsync(
                candidateQueries,
                (int)zoneId,
                difficultyId,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!fetchedOutcome.Succeeded)
            {
                hadTransientFailure |= fetchedOutcome.TransientFailure;
                hitRateLimitCooldown = TryCaptureCooldown(out cooldownRemaining);
                break;
            }

            var fetchedByKey = fetchedOutcome.Value ?? [];

            var candidateMatch = candidateMatcher.MatchFetchedCandidates(fetchedByKey);
            var resultsByContentId = candidateMatch.ResultsByContentId;
            foreach (var notFoundJob in candidateMatch.NotFoundCandidateJobs)
            {
                if (!string.IsNullOrWhiteSpace(notFoundJob.LeaseToken))
                {
                    abandonedLeases.Add(FFLogsLeaseAbandoner.CreateJobAbandonment(notFoundJob, "fflogs_not_found"));
                }
            }

            if (_seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining))
            {
                results.AddRange(resultsByContentId.Values);
                hitRateLimitCooldown = true;
                break;
            }

            results.AddRange(resultsByContentId.Values);
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return new FFLogsBatchProcessResult(
            ProcessedResults: results,
            HadTransientFailure: hadTransientFailure,
            HitRateLimitCooldown: hitRateLimitCooldown,
            CooldownRemaining: cooldownRemaining,
            ShouldAbandonRemainingLeases: hitRateLimitCooldown,
            AbandonedLeases: abandonedLeases);
    }

    private bool TryCaptureCooldown(out TimeSpan cooldownRemaining)
        => _seams.ApiClient.TryGetRateLimitRemaining(out cooldownRemaining);
}
