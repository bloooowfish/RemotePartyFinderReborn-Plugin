using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsLeaseAbandoner
{
    private readonly FFLogsCollectorSeams _seams;
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;

    public FFLogsLeaseAbandoner(FFLogsCollectorSeams seams)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _protectedExecutor = new ProtectedIngestEndpointExecutor(
            seams.IngestHttpSender,
            () => seams.TimeProvider.UtcNow);
    }

    public async Task AbandonUnprocessedLeasesAsync(
        Configuration configuration,
        FFLogsLeaseSession leaseSession,
        IEnumerable<ParseJob> leasedJobs,
        IEnumerable<ParseResult> processedResults,
        string reason,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(leaseSession);
        ArgumentNullException.ThrowIfNull(leasedJobs);
        ArgumentNullException.ThrowIfNull(processedResults);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(debugLog);

        var abandonBatch = BuildAbandonLeaseBatch(leasedJobs, processedResults, reason);
        if (abandonBatch.Count == 0)
        {
            return;
        }

        var jsonContent = JsonConvert.SerializeObject(abandonBatch);
        var result = await _protectedExecutor.ExecuteAsync(
            configuration,
            leaseSession.UploadUrl,
            IngestRoutes.FflogsLeasesAbandon,
            ProtectedEndpointCapabilityKind.FflogsLeasesAbandon,
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
                    $"FFLogsCollector: released abandoned leases {parsed.Released}/{parsed.Submitted} (rejected={parsed.Rejected}).");
            }
            else
            {
                warningLog(
                    $"FFLogsCollector: released abandoned leases request succeeded (submitted={abandonBatch.Count}).");
            }

            return;
        }

        if (result.StatusCode == HttpStatusCode.NotFound)
        {
            debugLog(
                "FFLogsCollector: lease abandon endpoint is unavailable on this server version.");
            return;
        }

        if (!result.StatusCode.HasValue)
        {
            debugLog($"FFLogsCollector: lease abandon request error: {result.FailureBody}");
            return;
        }

        warningLog(
            $"FFLogsCollector: failed to release abandoned leases ({result.StatusCode}) body={result.Body}");
    }

    private static string ParseJobKey(ParseJob job)
        => $"{job.ContentId}:{job.ZoneId}:{job.DifficultyId}";

    private static List<AbandonFflogsLease> BuildAbandonLeaseBatch(
        IEnumerable<ParseJob> leasedJobs,
        IEnumerable<ParseResult> processedResults,
        string reason)
    {
        var processedKeys = new HashSet<string>(
            processedResults.Select(FFLogsSubmitBuffer.GetParseResultKey),
            StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<AbandonFflogsLease>();

        foreach (var job in leasedJobs)
        {
            if (string.IsNullOrWhiteSpace(job.LeaseToken))
            {
                continue;
            }

            var key = ParseJobKey(job);
            if (processedKeys.Contains(key) || !seen.Add(key))
            {
                continue;
            }

            batch.Add(new AbandonFflogsLease
            {
                ContentId = job.ContentId,
                ZoneId = job.ZoneId,
                DifficultyId = job.DifficultyId,
                LeaseToken = job.LeaseToken,
                Reason = reason,
            });
        }

        return batch;
    }

    private static bool TryParseLeaseAbandonResponse(string content, out ContributeFflogsLeaseAbandonResponse parsed)
    {
        parsed = null;
        return IngestJson.TryDeserializeObject(content, out parsed);
    }

}
