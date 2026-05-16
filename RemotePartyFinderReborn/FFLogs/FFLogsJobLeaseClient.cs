using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn;

internal sealed record FFLogsJobLeaseAttempt(
    FFLogsLeaseSession Session,
    bool HadTransientFailure);

internal class FFLogsJobLeaseClient
{
    private const string WorkerRegionHeader = "X-RPF-FFLogs-Worker-Region";
    private readonly FFLogsCollectorSeams _seams;
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;
    private readonly IFFLogsWorkerRegionProvider _workerRegionProvider;

    public FFLogsJobLeaseClient(
        FFLogsCollectorSeams seams,
        IFFLogsWorkerRegionProvider workerRegionProvider = null)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _workerRegionProvider = workerRegionProvider;
        _protectedExecutor = new ProtectedIngestEndpointExecutor(
            seams.IngestHttpSender,
            () => seams.TimeProvider.UtcNow);
    }

    public virtual async Task<FFLogsJobLeaseAttempt> TryAcquireSessionAsync(
        Configuration configuration,
        CancellationToken cancellationToken,
        Action<string> warningLog = null,
        Action<string> debugLog = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        warningLog ??= static _ => { };
        debugLog ??= static _ => { };

        var uploadUrl = SelectUploadUrl(configuration, warningLog);
        if (uploadUrl == null)
        {
            return new FFLogsJobLeaseAttempt(null, false);
        }

        if (!TryBuildWorkerRegionHeaders(_workerRegionProvider, debugLog, out var extraHeaders))
        {
            return new FFLogsJobLeaseAttempt(null, false);
        }

        var emptySession = new FFLogsLeaseSession(uploadUrl, []);
        var result = await _protectedExecutor.ExecuteAsync(
            configuration,
            uploadUrl,
            IngestRoutes.FflogsJobs,
            ProtectedEndpointCapabilityKind.FflogsJobs,
            HttpMethod.Get,
            jsonBody: null,
            recordUploadAttempt: true,
            cancellationToken,
            extraHeaders).ConfigureAwait(false);

        if (result.SkipReason == ProtectedIngestSkipReason.EndpointUnavailable)
        {
            return new FFLogsJobLeaseAttempt(null, false);
        }

        if (result.SkipReason == ProtectedIngestSkipReason.CapabilityDeferred)
        {
            return new FFLogsJobLeaseAttempt(
                new FFLogsLeaseSession(uploadUrl, [], useBaseDelayWhenNoWork: true),
                false);
        }

        if (result.Success)
        {
            var jobs = JsonConvert.DeserializeObject<List<ParseJob>>(result.Body) ?? [];
            return new FFLogsJobLeaseAttempt(
                new FFLogsLeaseSession(uploadUrl, jobs),
                false);
        }

        if (result.StatusCode == HttpStatusCode.NotFound)
        {
            return new FFLogsJobLeaseAttempt(emptySession, false);
        }

        if (result.RetryAfterSeconds.HasValue)
        {
            warningLog($"FFLogsCollector: jobs endpoint rate limited, retry_after={result.RetryAfterSeconds.Value}s");
        }

        if (!result.StatusCode.HasValue)
        {
            debugLog($"Error requesting work: {result.FailureBody}");
        }

        return new FFLogsJobLeaseAttempt(emptySession, result.TransientFailure);
    }

    private static bool TryBuildWorkerRegionHeaders(
        IFFLogsWorkerRegionProvider workerRegionProvider,
        Action<string> debugLog,
        out IReadOnlyDictionary<string, string> extraHeaders)
    {
        extraHeaders = null;
        if (workerRegionProvider == null)
        {
            return true;
        }

        if (workerRegionProvider is IFFLogsWorkerRegionSnapshotState { HasObservedSnapshot: false })
        {
            return false;
        }

        if (!workerRegionProvider.TryGetWorkerRegion(out var region))
        {
            debugLog("FFLogsCollector: worker region unavailable; skipping regional jobs poll.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            debugLog("FFLogsCollector: worker region was blank; skipping regional jobs poll.");
            return false;
        }

        extraHeaders = new Dictionary<string, string>
        {
            [WorkerRegionHeader] = region.Trim(),
        };
        return true;
    }

    private UploadUrl SelectUploadUrl(Configuration configuration, Action<string> warningLog)
    {
        foreach (var candidate in configuration.UploadUrls.Where(static uploadUrl => uploadUrl.IsEnabled))
        {
            if (UploadAttemptRecorder.IsCircuitOpen(candidate, configuration, _seams.TimeProvider.UtcNow))
            {
                continue;
            }

            if (!IngestEndpointResolver.IsValidUploadUrl(candidate, warningLog))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }
}
