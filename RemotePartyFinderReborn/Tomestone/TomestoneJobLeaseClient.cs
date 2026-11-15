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

        var uploadUrl = SelectUploadUrl(configuration, warningLog);
        if (uploadUrl == null)
        {
            return new TomestoneJobLeaseAttempt(null, false);
        }

        if (!TryBuildWorkerHeaders(_workerRegionProvider, debugLog, out var extraHeaders))
        {
            return new TomestoneJobLeaseAttempt(null, false);
        }

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
            extraHeaders).ConfigureAwait(false);

        if (result.SkipReason == ProtectedIngestSkipReason.EndpointUnavailable)
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

    internal UploadUrl SelectAvailableUploadUrl(Configuration configuration, Action<string> warningLog)
        => SelectUploadUrl(configuration, warningLog);

    private static bool TryBuildWorkerHeaders(
        IFFLogsWorkerRegionProvider workerRegionProvider,
        Action<string> debugLog,
        out IReadOnlyDictionary<string, string> extraHeaders)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ProcessorKindHeader] = ProcessorKindValue,
            [JobModeHeader] = JobModeValue,
        };

        if (workerRegionProvider == null)
        {
            extraHeaders = headers;
            return true;
        }

        if (workerRegionProvider is IFFLogsWorkerRegionSnapshotState { HasObservedSnapshot: false })
        {
            extraHeaders = null;
            return false;
        }

        if (!workerRegionProvider.TryGetWorkerRegion(out var region))
        {
            debugLog("TomestoneCollector: worker region unavailable; skipping regional jobs poll.");
            extraHeaders = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            debugLog("TomestoneCollector: worker region was blank; skipping regional jobs poll.");
            extraHeaders = null;
            return false;
        }

        headers[WorkerRegionHeader] = region.Trim();
        extraHeaders = headers;
        return true;
    }

    private UploadUrl SelectUploadUrl(Configuration configuration, Action<string> warningLog)
    {
        foreach (var candidate in configuration.GetUploadUrlsSnapshot().Where(static uploadUrl => uploadUrl.IsEnabled))
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
