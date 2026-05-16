using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal sealed record FFLogsBossPercentEnrichmentResult(
    bool HadTransientFailure,
    bool HitRateLimitCooldown,
    TimeSpan CooldownRemaining);

internal sealed class FFLogsBossPercentEnricher
{
    private const int ReportsToCheckForProgress = 5;

    private readonly FFLogsCollectorSeams _seams;

    public FFLogsBossPercentEnricher(FFLogsCollectorSeams seams)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
    }

    public async Task<FFLogsBossPercentEnrichmentResult> EnrichBossPercentagesAsync(
        IReadOnlyList<ParseJob> jobs,
        IDictionary<ulong, ParseResult> resultsByContentId,
        IReadOnlyDictionary<ulong, FFLogsClient.CharacterFetchedData> chosenDataByContentId,
        int? difficultyId,
        CancellationToken cancellationToken)
    {
        var hadTransientFailure = false;

        var encounterIdsNeeded = new HashSet<uint>();
        foreach (var job in jobs)
        {
            if (job.EncounterId != 0)
            {
                encounterIdsNeeded.Add(job.EncounterId);
            }

            if (job.SecondaryEncounterId.HasValue && job.SecondaryEncounterId.Value != 0)
            {
                encounterIdsNeeded.Add(job.SecondaryEncounterId.Value);
            }
        }

        foreach (var encounterId in encounterIdsNeeded)
        {
            if (_seams.ApiClient.TryGetRateLimitRemaining(out var cooldownRemaining))
            {
                return new FFLogsBossPercentEnrichmentResult(
                    hadTransientFailure,
                    HitRateLimitCooldown: true,
                    cooldownRemaining);
            }

            var cids = jobs
                .Where(j => j.EncounterId == encounterId || j.SecondaryEncounterId == encounterId)
                .Select(j => j.ContentId)
                .Distinct()
                .ToList();

            var codesByCid = new Dictionary<ulong, List<string>>();
            var allCodes = new HashSet<string>();
            foreach (var contentId in cids)
            {
                if (!chosenDataByContentId.TryGetValue(contentId, out var data) || data == null)
                {
                    continue;
                }

                var codes = data.RecentReportCodes
                    .Take(ReportsToCheckForProgress)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .ToList();

                codesByCid[contentId] = codes;
                foreach (var code in codes)
                {
                    allCodes.Add(code);
                }
            }

            if (allCodes.Count == 0)
            {
                continue;
            }

            var bestBossOutcome = await _seams.ApiClient.FetchBestBossPercentByReportAsync(
                allCodes.ToList(),
                (int)encounterId,
                difficultyId,
                cancellationToken);

            if (!bestBossOutcome.Succeeded)
            {
                hadTransientFailure |= bestBossOutcome.TransientFailure;
                var hitCooldown = _seams.ApiClient.TryGetRateLimitRemaining(out var failureCooldownRemaining);
                return new FFLogsBossPercentEnrichmentResult(
                    hadTransientFailure,
                    hitCooldown,
                    failureCooldownRemaining);
            }

            var bestBossByReport = bestBossOutcome.Value ?? [];

            if (_seams.ApiClient.TryGetRateLimitRemaining(out var afterFetchCooldownRemaining))
            {
                return new FFLogsBossPercentEnrichmentResult(
                    hadTransientFailure,
                    HitRateLimitCooldown: true,
                    afterFetchCooldownRemaining);
            }

            foreach (var (contentId, codes) in codesByCid)
            {
                double? best = null;
                foreach (var code in codes)
                {
                    if (!bestBossByReport.TryGetValue(code, out var value))
                    {
                        continue;
                    }

                    best = best.HasValue ? Math.Min(best.Value, value) : value;
                }

                if (best.HasValue && resultsByContentId.TryGetValue(contentId, out var parseResult))
                {
                    parseResult.BossPercentages[(int)encounterId] = best.Value;
                }
            }
        }

        return new FFLogsBossPercentEnrichmentResult(
            hadTransientFailure,
            HitRateLimitCooldown: false,
            CooldownRemaining: TimeSpan.Zero);
    }
}
