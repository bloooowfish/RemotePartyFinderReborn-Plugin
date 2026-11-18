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
    internal const string WorkerRegionHeader = "X-RPF-FFLogs-Worker-Region";
    internal const string ProcessorKindHeader = "X-RPF-FFLogs-Processor-Kind";
    internal const string ProcessorKindValue = "dalamud_plugin_worker";
    internal const string JobModeHeader = "X-RPF-FFLogs-Job-Mode";
    internal const string JobModeValue = "candidate-capable";

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

        if (!ProtectedIngestEndpointExecutor.TryBuildWorkerHeaders(
                _workerRegionProvider,
                ProcessorKindHeader,
                ProcessorKindValue,
                JobModeHeader,
                JobModeValue,
                WorkerRegionHeader,
                "FFLogsCollector",
                debugLog,
                out var extraHeaders))
        {
            return new FFLogsJobLeaseAttempt(null, false);
        }

        if (!ProtectedIngestEndpointExecutor.TrySelectTargetForProtectedAttempt(
                configuration,
                ProtectedEndpointCapabilityKind.FflogsJobs,
                _seams.TimeProvider.UtcNow,
                warningLog,
                out var uploadUrl,
                out var attemptLease,
                out var capabilityDeferred)
            || uploadUrl == null)
        {
            return new FFLogsJobLeaseAttempt(null, false);
        }

        if (capabilityDeferred)
        {
            return new FFLogsJobLeaseAttempt(
                new FFLogsLeaseSession(uploadUrl, [], useBaseDelayWhenNoWork: true),
                false);
        }

        using (attemptLease)
        {
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
                extraHeaders,
                attemptLease).ConfigureAwait(false);

            if (result.SkipReason is ProtectedIngestSkipReason.EndpointUnavailable or ProtectedIngestSkipReason.CircuitOpen)
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
                try
                {
                    var jobs = JsonConvert.DeserializeObject<List<ParseJob>>(result.Body) ?? [];
                    return new FFLogsJobLeaseAttempt(
                        new FFLogsLeaseSession(uploadUrl, jobs),
                        false);
                }
                catch (JsonException ex)
                {
                    warningLog($"FFLogsCollector: failed to deserialize leased jobs: {ex.Message}");
                    return new FFLogsJobLeaseAttempt(emptySession, true);
                }
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
    }

    internal UploadUrl SelectAvailableUploadUrl(Configuration configuration, Action<string> warningLog)
        => ProtectedIngestEndpointExecutor.SelectAvailableUploadUrl(
            configuration,
            _seams.TimeProvider.UtcNow,
            warningLog ?? (static _ => { }));
}
