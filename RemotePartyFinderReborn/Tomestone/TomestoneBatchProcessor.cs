using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn.Tomestone;

internal sealed record TomestoneBatchProcessResult(
    List<TomestoneResult> ProcessedResults,
    List<AbandonTomestoneLease> AbandonedLeases,
    bool HadTransientFailure,
    bool HitRateLimitCooldown,
    bool HitApiKeyRejected,
    TimeSpan CooldownRemaining);

internal sealed class TomestoneBatchProcessor
{
    private sealed record QueryItem(
        TomestoneJob Job,
        IReadOnlyList<(uint ZoneId, uint EncounterId, TomestoneEncounterParams EncounterParams)> Targets);

    private sealed record SingleJobOutcome(
        TomestoneResult Result,
        AbandonTomestoneLease Abandonment,
        bool TransientFailure,
        bool HitRateLimitCooldown,
        bool HitApiKeyRejected);

    private readonly TomestoneCollectorSeams _seams;
    private readonly Action<string> _warningLog;

    public TomestoneBatchProcessor(
        TomestoneCollectorSeams seams,
        Action<string> warningLog = null)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _warningLog = warningLog ?? (static _ => { });
    }

    public async Task<TomestoneBatchProcessResult> ProcessLeaseSessionAsync(
        TomestoneLeaseSession leaseSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaseSession);

        var processedResults = new List<TomestoneResult>();
        var abandonedLeases = new List<AbandonTomestoneLease>();
        var jobs = leaseSession.Jobs;
        if (jobs.Count == 0)
        {
            return new TomestoneBatchProcessResult(
                processedResults,
                abandonedLeases,
                HadTransientFailure: false,
                HitRateLimitCooldown: false,
                HitApiKeyRejected: false,
                CooldownRemaining: TimeSpan.Zero);
        }

        var maxConcurrency = _seams.Configuration.GetEffectiveTomestoneMaxConcurrency();
        var requestIntervalMs = _seams.Configuration.GetEffectiveTomestoneRequestIntervalMs();

        var hadTransientFailure = false;
        var hitRateLimitCooldown = false;
        var hitApiKeyRejected = false;
        var cooldownRemaining = TimeSpan.Zero;
        var hasExecutedAnyQuery = false;

        var index = 0;
        while (index < jobs.Count)
        {
            if (TryAbortForWorkerState(out hitRateLimitCooldown, out hitApiKeyRejected, out cooldownRemaining))
            {
                AppendRemainingAbandonments(jobs, index, abandonedLeases, hitRateLimitCooldown, hitApiKeyRejected);
                break;
            }

            var wave = new List<QueryItem>(maxConcurrency);
            while (wave.Count < maxConcurrency && index < jobs.Count)
            {
                var job = jobs[index++];
                if (string.IsNullOrWhiteSpace(job.Name) || string.IsNullOrWhiteSpace(job.Server))
                {
                    abandonedLeases.Add(TomestoneLeaseAbandoner.CreateJobAbandonment(
                        job,
                        "worker_non_transient_failure",
                        "tomestone_missing_identity",
                        "Leased Tomestone job is missing character name or server."));
                    continue;
                }

                var targets = TomestoneEncounterMapping.ResolveJobTargets(job);
                if (targets.Count == 0)
                {
                    abandonedLeases.Add(TomestoneLeaseAbandoner.CreateJobAbandonment(
                        job,
                        "worker_unmapped_encounter",
                        "tomestone_unmapped_encounter",
                        $"Encounter zone={job.ZoneId}, encounter={job.EncounterId} is not mapped for Tomestone."));
                    continue;
                }

                wave.Add(new QueryItem(job, targets));
            }

            if (wave.Count == 0)
            {
                continue;
            }

            if (hasExecutedAnyQuery && requestIntervalMs > 0)
            {
                await _seams.RequestDelayAsync(TimeSpan.FromMilliseconds(requestIntervalMs), cancellationToken)
                    .ConfigureAwait(false);

                if (TryAbortForWorkerState(out hitRateLimitCooldown, out hitApiKeyRejected, out cooldownRemaining))
                {
                    AppendWaveAndRemainingAbandonments(
                        wave,
                        jobs,
                        index,
                        abandonedLeases,
                        hitRateLimitCooldown,
                        hitApiKeyRejected);
                    break;
                }
            }

            hasExecutedAnyQuery = true;
            var waveOutcomes = await Task.WhenAll(wave.Select(item => ExecuteJobAsync(item, requestIntervalMs, cancellationToken)))
                .ConfigureAwait(false);

            foreach (var outcome in waveOutcomes)
            {
                if (outcome.Result != null)
                {
                    processedResults.Add(outcome.Result);
                }

                if (outcome.Abandonment != null)
                {
                    abandonedLeases.Add(outcome.Abandonment);
                }

                hadTransientFailure |= outcome.TransientFailure;
                hitRateLimitCooldown |= outcome.HitRateLimitCooldown;
                hitApiKeyRejected |= outcome.HitApiKeyRejected;
            }

            if (hitRateLimitCooldown || hitApiKeyRejected)
            {
                if (hitRateLimitCooldown)
                {
                    _seams.ProgressionService.TryGetRateLimitRemaining(out cooldownRemaining);
                }

                AppendRemainingAbandonments(jobs, index, abandonedLeases, hitRateLimitCooldown, hitApiKeyRejected);
                break;
            }
        }

        return new TomestoneBatchProcessResult(
            processedResults,
            abandonedLeases,
            hadTransientFailure,
            hitRateLimitCooldown,
            hitApiKeyRejected,
            cooldownRemaining);
    }

    private async Task<SingleJobOutcome> ExecuteJobAsync(
        QueryItem item,
        int requestIntervalMs,
        CancellationToken cancellationToken)
    {
        var job = item.Job;
        var targetResults = new List<(uint EncounterId, TomestoneProgressData Data)>(item.Targets.Count);

        for (var i = 0; i < item.Targets.Count; i++)
        {
            if (i > 0)
            {
                if (TryAbortForWorkerState(out var preRateLimited, out var preRejected, out _))
                {
                    return CreateDeferredOutcome(job, preRateLimited, preRejected);
                }

                if (requestIntervalMs > 0)
                {
                    await _seams.RequestDelayAsync(TimeSpan.FromMilliseconds(requestIntervalMs), cancellationToken)
                        .ConfigureAwait(false);

                    if (TryAbortForWorkerState(out var postDelayRateLimited, out var postDelayRejected, out _))
                    {
                        return CreateDeferredOutcome(job, postDelayRateLimited, postDelayRejected);
                    }
                }
            }

            var target = item.Targets[i];
            OperationOutcome<TomestoneProgressData> outcome;
            try
            {
                outcome = await _seams.ProgressionService.FetchProgressionDataAsync(
                    job.Name.Trim(),
                    job.Server.Trim(),
                    target.ZoneId,
                    target.EncounterId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _warningLog(
                    $"TomestoneCollector: progression fetch failed for content_id={job.ContentId}, encounter_id={target.EncounterId}: {ex.Message}");
                return new SingleJobOutcome(
                    Result: null,
                    Abandonment: TomestoneLeaseAbandoner.CreateJobAbandonment(
                        job,
                        "worker_transient_failure",
                        "tomestone_unexpected_error",
                        ex.Message),
                    TransientFailure: true,
                    HitRateLimitCooldown: false,
                    HitApiKeyRejected: false);
            }

            if (!outcome.Succeeded)
            {
                if (_seams.ProgressionService.TryGetRateLimitRemaining(out _))
                {
                    return new SingleJobOutcome(
                        Result: null,
                        Abandonment: TomestoneLeaseAbandoner.CreateJobAbandonment(
                            job,
                            "worker_rate_limit_deferred",
                            "tomestone_rate_limited",
                            outcome.ErrorMessage ?? "Tomestone API rate-limit cooldown became active."),
                        TransientFailure: false,
                        HitRateLimitCooldown: true,
                        HitApiKeyRejected: false);
                }

                if (_seams.ApiCredentialStatus.Tomestone.State == ApiCredentialValidationState.Rejected)
                {
                    return new SingleJobOutcome(
                        Result: null,
                        Abandonment: TomestoneLeaseAbandoner.CreateJobAbandonment(
                            job,
                            "worker_api_key_rejected",
                            "tomestone_api_key_rejected",
                            outcome.ErrorMessage ?? "Tomestone API key was rejected."),
                        TransientFailure: false,
                        HitRateLimitCooldown: false,
                        HitApiKeyRejected: true);
                }

                return new SingleJobOutcome(
                    Result: null,
                    Abandonment: TomestoneLeaseAbandoner.CreateJobAbandonment(
                        job,
                        outcome.TransientFailure ? "worker_transient_failure" : "worker_non_transient_failure",
                        outcome.TransientFailure ? "tomestone_transient_failure" : "tomestone_non_transient_failure",
                        outcome.ErrorMessage ?? "Tomestone progression fetch failed."),
                    TransientFailure: outcome.TransientFailure,
                    HitRateLimitCooldown: false,
                    HitApiKeyRejected: false);
            }

            var data = outcome.Value ?? TomestoneProgressData.Skipped;
            if (!data.IsAuthoritative)
            {
                return new SingleJobOutcome(
                    Result: null,
                    Abandonment: TomestoneLeaseAbandoner.CreateJobAbandonment(
                        job,
                        "worker_skipped",
                        "tomestone_skipped",
                        "Tomestone progression query returned a non-authoritative skipped result."),
                    TransientFailure: false,
                    HitRateLimitCooldown: false,
                    HitApiKeyRejected: false);
            }

            targetResults.Add((target.EncounterId, data));
        }

        var result = BuildTomestoneResult(job, targetResults);

        return new SingleJobOutcome(
            Result: result,
            Abandonment: null,
            TransientFailure: false,
            HitRateLimitCooldown: false,
            HitApiKeyRejected: false);
    }

    private static SingleJobOutcome CreateDeferredOutcome(
        TomestoneJob job,
        bool hitRateLimitCooldown,
        bool hitApiKeyRejected)
    {
        return new SingleJobOutcome(
            Result: null,
            Abandonment: CreateDeferredAbandonment(job, hitRateLimitCooldown, hitApiKeyRejected),
            TransientFailure: false,
            HitRateLimitCooldown: hitRateLimitCooldown,
            HitApiKeyRejected: hitApiKeyRejected);
    }

    private static TomestoneResult BuildTomestoneResult(
        TomestoneJob job,
        IReadOnlyList<(uint EncounterId, TomestoneProgressData Data)> targetResults)
    {
        var bossPercentages = new Dictionary<int, double>();
        var phaseProgress = new Dictionary<int, List<PhaseProgress>>();
        var queriedEncounters = new List<int>(targetResults.Count);

        foreach (var (encounterId, data) in targetResults)
        {
            var encounterIdKey = (int)encounterId;
            if (!queriedEncounters.Contains(encounterIdKey))
            {
                queriedEncounters.Add(encounterIdKey);
            }

            if (data.Cleared)
            {
                continue;
            }

            if (data.BossPercentage.HasValue)
            {
                bossPercentages[encounterIdKey] = data.BossPercentage.Value;
            }

            if (data.Phases is { Count: > 0 })
            {
                phaseProgress[encounterIdKey] = data.Phases.ToList();
            }
        }

        return new TomestoneResult
        {
            ContentId = job.ContentId,
            ZoneId = job.ZoneId,
            DifficultyId = job.DifficultyId,
            EncounterId = job.EncounterId,
            BossPercentages = bossPercentages,
            PhaseProgress = phaseProgress,
            TomestoneQueriedEncounters = queriedEncounters,
            MatchedServer = job.Server?.Trim() ?? string.Empty,
            SourceKind = TomestoneJobLeaseClient.SourceKindValue,
            LeaseToken = job.LeaseToken ?? string.Empty,
        };
    }

    private bool TryAbortForWorkerState(
        out bool hitRateLimitCooldown,
        out bool hitApiKeyRejected,
        out TimeSpan cooldownRemaining)
    {
        if (_seams.ProgressionService.TryGetRateLimitRemaining(out cooldownRemaining))
        {
            hitRateLimitCooldown = true;
            hitApiKeyRejected = false;
            return true;
        }

        if (_seams.ApiCredentialStatus.Tomestone.State == ApiCredentialValidationState.Rejected)
        {
            hitRateLimitCooldown = false;
            hitApiKeyRejected = true;
            cooldownRemaining = TimeSpan.Zero;
            return true;
        }

        hitRateLimitCooldown = false;
        hitApiKeyRejected = false;
        cooldownRemaining = TimeSpan.Zero;
        return false;
    }

    private static void AppendWaveAndRemainingAbandonments(
        IReadOnlyList<QueryItem> wave,
        IReadOnlyList<TomestoneJob> jobs,
        int nextIndex,
        List<AbandonTomestoneLease> abandonedLeases,
        bool hitRateLimitCooldown,
        bool hitApiKeyRejected)
    {
        foreach (var item in wave)
        {
            abandonedLeases.Add(CreateDeferredAbandonment(item.Job, hitRateLimitCooldown, hitApiKeyRejected));
        }

        AppendRemainingAbandonments(jobs, nextIndex, abandonedLeases, hitRateLimitCooldown, hitApiKeyRejected);
    }

    private static void AppendRemainingAbandonments(
        IReadOnlyList<TomestoneJob> jobs,
        int startIndex,
        List<AbandonTomestoneLease> abandonedLeases,
        bool hitRateLimitCooldown,
        bool hitApiKeyRejected)
    {
        for (var i = startIndex; i < jobs.Count; i++)
        {
            abandonedLeases.Add(CreateDeferredAbandonment(jobs[i], hitRateLimitCooldown, hitApiKeyRejected));
        }
    }

    private static AbandonTomestoneLease CreateDeferredAbandonment(
        TomestoneJob job,
        bool hitRateLimitCooldown,
        bool hitApiKeyRejected)
    {
        if (hitApiKeyRejected && !hitRateLimitCooldown)
        {
            return TomestoneLeaseAbandoner.CreateJobAbandonment(
                job,
                "worker_api_key_rejected",
                "tomestone_api_key_rejected",
                "Deferred unqueried Tomestone lease because the API key was rejected.");
        }

        return TomestoneLeaseAbandoner.CreateJobAbandonment(
            job,
            "worker_rate_limit_deferred",
            "tomestone_rate_limited",
            "Deferred unqueried Tomestone lease due to active Tomestone rate-limit cooldown.");
    }
}
