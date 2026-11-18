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
        Action<string> debugLog,
        IEnumerable<AbandonFflogsLease> alreadyAbandonedLeases = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(leaseSession);
        ArgumentNullException.ThrowIfNull(leasedJobs);
        ArgumentNullException.ThrowIfNull(processedResults);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(debugLog);

        var abandonBatch = BuildAbandonLeaseBatch(leasedJobs, processedResults, reason, alreadyAbandonedLeases);
        await SendAbandonBatchAsync(
            configuration,
            leaseSession.UploadUrl,
            abandonBatch,
            cancellationToken,
            warningLog,
            debugLog).ConfigureAwait(false);
    }

    public async Task AbandonExplicitLeasesAsync(
        Configuration configuration,
        FFLogsLeaseSession leaseSession,
        IReadOnlyList<AbandonFflogsLease> abandonBatch,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(leaseSession);
        ArgumentNullException.ThrowIfNull(abandonBatch);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(debugLog);

        if (abandonBatch.Count == 0)
        {
            return;
        }

        await SendAbandonBatchAsync(
            configuration,
            leaseSession.UploadUrl,
            abandonBatch,
            cancellationToken,
            warningLog,
            debugLog).ConfigureAwait(false);
    }

    internal static AbandonFflogsLease CreateJobAbandonment(ParseJob job, string reason)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(reason);

        return new AbandonFflogsLease
        {
            ContentId = job.ContentId,
            ZoneId = job.ZoneId,
            DifficultyId = job.DifficultyId,
            LeaseToken = job.LeaseToken,
            Reason = reason,
            ProcessorKind = FFLogsJobLeaseClient.ProcessorKindValue,
        };
    }

    public async Task AbandonOversizedResultLeaseAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        ParseResult oversizedResult,
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
            return;
        }

        await SendAbandonBatchAsync(
            configuration,
            uploadUrl,
            [
                new AbandonFflogsLease
                {
                    ContentId = oversizedResult.ContentId,
                    ZoneId = oversizedResult.ZoneId,
                    DifficultyId = oversizedResult.DifficultyId,
                    LeaseToken = oversizedResult.LeaseToken,
                    Reason = reason,
                    ProcessorKind = FFLogsJobLeaseClient.ProcessorKindValue,
                },
            ],
            cancellationToken,
            warningLog,
            debugLog).ConfigureAwait(false);
    }

    public async Task AbandonResultLeasesAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IEnumerable<ParseResult> unsubmittedResults,
        string reason,
        CancellationToken cancellationToken,
        Action<string> warningLog,
        Action<string> debugLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(unsubmittedResults);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(debugLog);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var abandonBatch = new List<AbandonFflogsLease>();
        foreach (var result in unsubmittedResults)
        {
            if (string.IsNullOrWhiteSpace(result.LeaseToken))
            {
                continue;
            }

            var key = FFLogsSubmitBuffer.GetParseResultKey(result);
            if (!seen.Add(key))
            {
                continue;
            }

            abandonBatch.Add(new AbandonFflogsLease
            {
                ContentId = result.ContentId,
                ZoneId = result.ZoneId,
                DifficultyId = result.DifficultyId,
                LeaseToken = result.LeaseToken,
                Reason = reason,
                ProcessorKind = FFLogsJobLeaseClient.ProcessorKindValue,
            });
        }

        await SendAbandonBatchAsync(
            configuration,
            uploadUrl,
            abandonBatch,
            cancellationToken,
            warningLog,
            debugLog).ConfigureAwait(false);
    }

    private async Task SendAbandonBatchAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        IReadOnlyList<AbandonFflogsLease> abandonBatch,
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

    private static string AbandonLeaseKey(AbandonFflogsLease lease)
        => $"{lease.ContentId}:{lease.ZoneId}:{lease.DifficultyId}";

    private static List<AbandonFflogsLease> BuildAbandonLeaseBatch(
        IEnumerable<ParseJob> leasedJobs,
        IEnumerable<ParseResult> processedResults,
        string reason,
        IEnumerable<AbandonFflogsLease> alreadyAbandonedLeases = null)
    {
        var excludedKeys = new HashSet<string>(
            processedResults.Select(FFLogsSubmitBuffer.GetParseResultKey),
            StringComparer.Ordinal);
        if (alreadyAbandonedLeases is not null)
        {
            foreach (var abandoned in alreadyAbandonedLeases)
            {
                excludedKeys.Add(AbandonLeaseKey(abandoned));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<AbandonFflogsLease>();

        foreach (var job in leasedJobs)
        {
            if (string.IsNullOrWhiteSpace(job.LeaseToken))
            {
                continue;
            }

            var key = ParseJobKey(job);
            if (excludedKeys.Contains(key) || !seen.Add(key))
            {
                continue;
            }

            batch.Add(CreateJobAbandonment(job, reason));
        }

        return batch;
    }

    private static bool TryParseLeaseAbandonResponse(string content, out ContributeFflogsLeaseAbandonResponse parsed)
    {
        parsed = null;
        return IngestJson.TryDeserializeObject(content, out parsed);
    }
}
