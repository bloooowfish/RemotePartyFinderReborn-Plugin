using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn.Tomestone;

internal sealed class TomestoneLeaseSession
{
    public TomestoneLeaseSession(
        UploadUrl uploadUrl,
        List<TomestoneJob> jobs,
        bool useBaseDelayWhenNoWork = false)
    {
        UploadUrl = uploadUrl ?? throw new ArgumentNullException(nameof(uploadUrl));
        Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        UseBaseDelayWhenNoWork = useBaseDelayWhenNoWork;
    }

    public UploadUrl UploadUrl { get; }
    public List<TomestoneJob> Jobs { get; }
    public bool UseBaseDelayWhenNoWork { get; }
    public bool HasJobs => Jobs.Count > 0;
}

internal sealed record TomestoneJobLeaseAttempt(
    TomestoneLeaseSession Session,
    bool HadTransientFailure);

internal class TomestoneJobLeaseClient
{
    internal const string WorkerRegionHeader = "X-RPF-Tomestone-Worker-Region";
    internal const string ProcessorKindHeader = "X-RPF-Tomestone-Processor-Kind";
    internal const string ProcessorKindValue = "dalamud_plugin_worker";
    internal const string JobModeHeader = "X-RPF-Tomestone-Job-Mode";
    internal const string JobModeValue = "exact-only";
    internal const string SourceKindValue = "tomestone_api";

    private readonly TomestoneCollectorSeams _seams;
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;
    private readonly IFFLogsWorkerRegionProvider _workerRegionProvider;

    public TomestoneJobLeaseClient(
        TomestoneCollectorSeams seams,
        IFFLogsWorkerRegionProvider workerRegionProvider = null)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _workerRegionProvider = workerRegionProvider;
        _protectedExecutor = new ProtectedIngestEndpointExecutor(
            seams.IngestHttpSender,
            () => seams.TimeProvider.UtcNow);
    }

    public virtual async Task<TomestoneJobLeaseAttempt> TryAcquireSessionAsync(
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
                "TomestoneCollector",
                debugLog,
                out var extraHeaders))
        {
            return new TomestoneJobLeaseAttempt(null, false);
        }

        if (!ProtectedIngestEndpointExecutor.TrySelectTargetForProtectedAttempt(
                configuration,
                ProtectedEndpointCapabilityKind.TomestoneJobs,
                _seams.TimeProvider.UtcNow,
                warningLog,
                out var uploadUrl,
                out var attemptLease,
                out var capabilityDeferred)
            || uploadUrl == null)
        {
            return new TomestoneJobLeaseAttempt(null, false);
        }

        if (capabilityDeferred)
        {
            return new TomestoneJobLeaseAttempt(
                new TomestoneLeaseSession(uploadUrl, [], useBaseDelayWhenNoWork: true),
                false);
        }

        using (attemptLease)
        {
            var emptySession = new TomestoneLeaseSession(uploadUrl, []);
            var result = await _protectedExecutor.ExecuteAsync(
                configuration,
                uploadUrl,
                IngestRoutes.TomestoneJobs,
                ProtectedEndpointCapabilityKind.TomestoneJobs,
                HttpMethod.Get,
                jsonBody: null,
                recordUploadAttempt: true,
                cancellationToken,
                extraHeaders,
                attemptLease).ConfigureAwait(false);

            if (result.SkipReason is ProtectedIngestSkipReason.EndpointUnavailable or ProtectedIngestSkipReason.CircuitOpen)
            {
                return new TomestoneJobLeaseAttempt(null, false);
            }

            if (result.SkipReason == ProtectedIngestSkipReason.CapabilityDeferred)
            {
                return new TomestoneJobLeaseAttempt(
                    new TomestoneLeaseSession(uploadUrl, [], useBaseDelayWhenNoWork: true),
                    false);
            }

            if (result.Success)
            {
                try
                {
                    var jobs = JsonConvert.DeserializeObject<List<TomestoneJob>>(result.Body) ?? [];
                    return new TomestoneJobLeaseAttempt(
                        new TomestoneLeaseSession(uploadUrl, jobs),
                        false);
                }
                catch (JsonException ex)
                {
                    warningLog($"TomestoneCollector: failed to deserialize leased jobs: {ex.Message}");
                    return new TomestoneJobLeaseAttempt(emptySession, true);
                }
            }

            if (result.StatusCode == HttpStatusCode.NotFound)
            {
                return new TomestoneJobLeaseAttempt(emptySession, false);
            }

            if (result.RetryAfterSeconds.HasValue)
            {
                warningLog($"TomestoneCollector: jobs endpoint rate limited, retry_after={result.RetryAfterSeconds.Value}s");
            }

            if (!result.StatusCode.HasValue)
            {
                debugLog($"TomestoneCollector: error requesting work: {result.FailureBody}");
            }

            return new TomestoneJobLeaseAttempt(emptySession, result.TransientFailure);
        }
    }

    internal UploadUrl SelectAvailableUploadUrl(Configuration configuration, Action<string> warningLog)
        => ProtectedIngestEndpointExecutor.SelectAvailableUploadUrl(
            configuration,
            _seams.TimeProvider.UtcNow,
            warningLog ?? (static _ => { }));
}
