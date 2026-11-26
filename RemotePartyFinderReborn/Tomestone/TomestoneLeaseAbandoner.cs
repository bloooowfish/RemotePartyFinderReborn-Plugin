using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn.Tomestone;

internal sealed class TomestoneLeaseAbandoner
{
    private readonly TomestoneCollectorSeams _seams;
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;

    public TomestoneLeaseAbandoner(TomestoneCollectorSeams seams)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _protectedExecutor = new ProtectedIngestEndpointExecutor(
            seams.IngestHttpSender,
            () => seams.TimeProvider.UtcNow);
    }

    public async Task AbandonLeasesAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IEnumerable<AbandonTomestoneLease> abandonments,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(abandonments);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(debugLog);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<AbandonTomestoneLease>();
        foreach (var entry in abandonments)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.LeaseToken))
            {
                continue;
            }

            var key = $"{entry.ContentId}:{entry.ZoneId}:{entry.DifficultyId}:{entry.EncounterId}";
            if (!seen.Add(key))
            {
                continue;
            }

            batch.Add(new AbandonTomestoneLease
            {
                ContentId = entry.ContentId,
                ZoneId = entry.ZoneId,
                DifficultyId = entry.DifficultyId,
                EncounterId = entry.EncounterId,
                LeaseToken = entry.LeaseToken,
                Reason = entry.Reason ?? string.Empty,
                ProcessorKind = string.IsNullOrWhiteSpace(entry.ProcessorKind)
                    ? TomestoneJobLeaseClient.ProcessorKindValue
                    : entry.ProcessorKind,
                SourceKind = string.IsNullOrWhiteSpace(entry.SourceKind)
                    ? TomestoneJobLeaseClient.SourceKindValue
                    : entry.SourceKind,
                FailureCode = entry.FailureCode ?? string.Empty,
                FailureFingerprint = entry.FailureFingerprint ?? string.Empty,
                Message = entry.Message ?? string.Empty,
            });
        }

        await SendAbandonBatchAsync(
            configuration,
            uploadUrl,
            batch,
            cancellationToken,
            warningLog,
            debugLog).ConfigureAwait(false);
    }

    public Task AbandonOversizedResultLeaseAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        TomestoneResult oversizedResult,
        string reason,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(oversizedResult);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(debugLog);

        if (string.IsNullOrWhiteSpace(oversizedResult.LeaseToken))
        {
            return Task.CompletedTask;
        }

        return AbandonLeasesAsync(
            configuration,
            uploadUrl,
            [
                new AbandonTomestoneLease
                {
                    ContentId = oversizedResult.ContentId,
                    ZoneId = oversizedResult.ZoneId,
                    DifficultyId = oversizedResult.DifficultyId,
                    EncounterId = oversizedResult.EncounterId,
                    LeaseToken = oversizedResult.LeaseToken,
                    Reason = reason,
                    ProcessorKind = TomestoneJobLeaseClient.ProcessorKindValue,
                    SourceKind = TomestoneJobLeaseClient.SourceKindValue,
                    FailureCode = "payload_too_large",
                    FailureFingerprint = BuildFailureFingerprint(
                        "payload_too_large",
                        oversizedResult.ZoneId,
                        oversizedResult.DifficultyId,
                        oversizedResult.EncounterId),
                    Message = "Tomestone result payload exceeded server size limit (HTTP 413).",
                },
            ],
            cancellationToken,
            warningLog,
            debugLog);
    }

    public Task AbandonResultLeasesAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IEnumerable<TomestoneResult> unsubmittedResults,
        string reason,
        string failureCode,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog)
    {
        ArgumentNullException.ThrowIfNull(unsubmittedResults);

        var abandonments = new List<AbandonTomestoneLease>();
        foreach (var result in unsubmittedResults)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.LeaseToken))
            {
                continue;
            }

            abandonments.Add(new AbandonTomestoneLease
            {
                ContentId = result.ContentId,
                ZoneId = result.ZoneId,
                DifficultyId = result.DifficultyId,
                EncounterId = result.EncounterId,
                LeaseToken = result.LeaseToken,
                Reason = reason,
                ProcessorKind = TomestoneJobLeaseClient.ProcessorKindValue,
                SourceKind = TomestoneJobLeaseClient.SourceKindValue,
                FailureCode = failureCode ?? string.Empty,
                FailureFingerprint = BuildFailureFingerprint(
                    failureCode,
                    result.ZoneId,
                    result.DifficultyId,
                    result.EncounterId),
                Message = $"Abandoning Tomestone result lease: {reason}.",
            });
        }

        return AbandonLeasesAsync(
            configuration,
            uploadUrl,
            abandonments,
            cancellationToken,
            warningLog,
            debugLog);
    }

    public async Task AbandonUnprocessedLeasesAsync(
        Configuration configuration,
        TomestoneLeaseSession leaseSession,
        IEnumerable<TomestoneJob> leasedJobs,
        IEnumerable<TomestoneResult> processedResults,
        string reason,
        string failureCode,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog,
        IEnumerable<AbandonTomestoneLease> alreadyAbandonedLeases = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(leaseSession);
        ArgumentNullException.ThrowIfNull(leasedJobs);
        ArgumentNullException.ThrowIfNull(processedResults);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(debugLog);

        var excludedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in processedResults)
        {
            if (result != null)
            {
                excludedKeys.Add($"{result.ContentId}:{result.ZoneId}:{result.DifficultyId}:{result.EncounterId}");
            }
        }

        if (alreadyAbandonedLeases is not null)
        {
            foreach (var abandoned in alreadyAbandonedLeases)
            {
                if (abandoned != null)
                {
                    excludedKeys.Add($"{abandoned.ContentId}:{abandoned.ZoneId}:{abandoned.DifficultyId}:{abandoned.EncounterId}");
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<AbandonTomestoneLease>();
        foreach (var job in leasedJobs)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.LeaseToken))
            {
                continue;
            }

            var key = $"{job.ContentId}:{job.ZoneId}:{job.DifficultyId}:{job.EncounterId}";
            if (excludedKeys.Contains(key) || !seen.Add(key))
            {
                continue;
            }

            batch.Add(CreateJobAbandonment(
                job,
                reason,
                failureCode,
                $"Abandoning unprocessed Tomestone lease: {reason}."));
        }

        await SendAbandonBatchAsync(
            configuration,
            leaseSession.UploadUrl,
            batch,
            cancellationToken,
            warningLog,
            debugLog).ConfigureAwait(false);
    }

    internal static AbandonTomestoneLease CreateJobAbandonment(
        TomestoneJob job,
        string reason,
        string failureCode,
        string message)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new AbandonTomestoneLease
        {
            ContentId = job.ContentId,
            ZoneId = job.ZoneId,
            DifficultyId = job.DifficultyId,
            EncounterId = job.EncounterId,
            LeaseToken = job.LeaseToken ?? string.Empty,
            Reason = reason ?? string.Empty,
            ProcessorKind = TomestoneJobLeaseClient.ProcessorKindValue,
            SourceKind = TomestoneJobLeaseClient.SourceKindValue,
            FailureCode = failureCode ?? string.Empty,
            FailureFingerprint = BuildFailureFingerprint(
                failureCode,
                job.ZoneId,
                job.DifficultyId,
                job.EncounterId),
            Message = message ?? string.Empty,
        };
    }

    private static string BuildFailureFingerprint(
        string failureCode,
        uint zoneId,
        int difficultyId,
        uint encounterId)
    {
        var code = string.IsNullOrWhiteSpace(failureCode) ? "unknown" : failureCode.Trim();
        return $"{code}:{zoneId}:{difficultyId}:{encounterId}";
    }

    private async Task SendAbandonBatchAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IReadOnlyList<AbandonTomestoneLease> abandonBatch,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog)
    {
        if (abandonBatch.Count == 0 || uploadUrl == null)
        {
            return;
        }

        var jsonContent = JsonConvert.SerializeObject(abandonBatch);
        var result = await _protectedExecutor.ExecuteAsync(
            configuration,
            uploadUrl,
            IngestRoutes.TomestoneLeasesAbandon,
            ProtectedEndpointCapabilityKind.TomestoneLeasesAbandon,
            HttpMethod.Post,
            jsonContent,
            recordUploadAttempt: false,
            cancellationToken).ConfigureAwait(false);

        if (result.Deferred)
        {
            return;
        }

        if (result.Success)
        {
            if (TryParseLeaseAbandonResponse(result.Body, out var parsed))
            {
                warningLog(
                    $"TomestoneCollector: released abandoned leases {parsed.Released}/{parsed.Submitted} (rejected={parsed.Rejected}).");
            }
            else
            {
                warningLog(
                    $"TomestoneCollector: released abandoned leases request succeeded (submitted={abandonBatch.Count}).");
            }

            return;
        }

        if (result.StatusCode == HttpStatusCode.NotFound)
        {
            debugLog(
                "TomestoneCollector: lease abandon endpoint is unavailable on this server version.");
            return;
        }

        if (!result.StatusCode.HasValue)
        {
            debugLog($"TomestoneCollector: lease abandon request error: {result.FailureBody}");
            return;
        }

        warningLog(
            $"TomestoneCollector: failed to release abandoned leases ({result.StatusCode}) body={result.Body}");
    }

    private static bool TryParseLeaseAbandonResponse(string content, out ContributeTomestoneLeaseAbandonResponse parsed)
    {
        parsed = null;
        return IngestJson.TryDeserializeObject(content, out parsed);
    }
}
