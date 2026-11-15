using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn.Tomestone;

internal sealed record TomestoneResultSubmitAttempt(
    bool HadTransientFailure,
    bool ShouldUseBaseDelayBeforeNextPoll);

internal sealed class TomestoneResultSubmitter
{
    private readonly TomestoneCollectorSeams _seams;
    private readonly TomestoneSubmitBuffer _submitBuffer;
    private readonly TomestoneLeaseAbandoner _leaseAbandoner;
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;

    public TomestoneResultSubmitter(
        TomestoneCollectorSeams seams,
        TomestoneSubmitBuffer submitBuffer,
        TomestoneLeaseAbandoner leaseAbandoner = null)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _submitBuffer = submitBuffer ?? throw new ArgumentNullException(nameof(submitBuffer));
        _leaseAbandoner = leaseAbandoner ?? new TomestoneLeaseAbandoner(seams);
        _protectedExecutor = new ProtectedIngestEndpointExecutor(
            seams.IngestHttpSender,
            () => seams.TimeProvider.UtcNow);
    }

    public bool HasPendingResults => _submitBuffer.HasPendingResults;

    public List<TomestoneResult> DrainPendingResults()
        => _submitBuffer.DrainPendingResults();

    public Task<TomestoneResultSubmitAttempt> TryFlushPendingAsync(
        Configuration configuration,
        UploadUrl uploadUrl,
        CancellationToken cancellationToken,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<string> debugLog = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (uploadUrl == null || !_submitBuffer.HasPendingResults)
        {
            return Task.FromResult(new TomestoneResultSubmitAttempt(false, false));
        }

        return TrySubmitResultsAsync(
            configuration,
            new TomestoneLeaseSession(uploadUrl, []),
            [],
            cancellationToken,
            infoLog,
            warningLog,
            errorLog,
            debugLog);
    }

    public async Task<TomestoneResultSubmitAttempt> TrySubmitResultsAsync(
        Configuration configuration,
        TomestoneLeaseSession leaseSession,
        IEnumerable<TomestoneResult> freshResults,
        CancellationToken cancellationToken,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<string> debugLog = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(leaseSession);
        ArgumentNullException.ThrowIfNull(freshResults);
        ArgumentNullException.ThrowIfNull(infoLog);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(errorLog);
        debugLog ??= static _ => { };

        var submitBatch = _submitBuffer.BuildSubmitBatch(freshResults.ToList());
        if (submitBatch.Count == 0)
        {
            return new TomestoneResultSubmitAttempt(false, false);
        }

        foreach (var item in submitBatch)
        {
            if (string.IsNullOrWhiteSpace(item.SourceKind))
            {
                item.SourceKind = TomestoneJobLeaseClient.SourceKindValue;
            }
        }

        var pendingChunks = new Stack<List<TomestoneResult>>();
        pendingChunks.Push(submitBatch);

        while (pendingChunks.Count > 0)
        {
            var chunk = pendingChunks.Pop();
            if (chunk.Count == 0)
            {
                continue;
            }

            var jsonContent = JsonConvert.SerializeObject(chunk);
            var result = await _protectedExecutor.ExecuteAsync(
                configuration,
                leaseSession.UploadUrl,
                IngestRoutes.TomestoneResults,
                ProtectedEndpointCapabilityKind.TomestoneResults,
                HttpMethod.Post,
                jsonContent,
                recordUploadAttempt: false,
                cancellationToken).ConfigureAwait(false);

            if (result.Deferred)
            {
                RequeueRemainingChunks(chunk, pendingChunks);
                return new TomestoneResultSubmitAttempt(false, true);
            }

            if (result.Success)
            {
                var summary = SummarizeBatch(chunk);
                if (TryParseResultsSubmitResponse(result.Body, out var parsed))
                {
                    infoLog(
                        $"Uploaded Tomestone results: updated={parsed.Updated}, accepted={parsed.Accepted}/{parsed.Submitted}, submitted={parsed.Submitted}, rejected={parsed.Rejected}, status={parsed.Status}, {summary}.");
                }
                else
                {
                    infoLog($"Uploaded {chunk.Count} Tomestone results: {summary}.");
                }

                continue;
            }

            if (result.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                if (chunk.Count > 1)
                {
                    var mid = chunk.Count / 2;
                    warningLog($"TomestoneCollector: results payload too large (413) for {chunk.Count} results; splitting batch.");
                    pendingChunks.Push(chunk.Skip(mid).ToList());
                    pendingChunks.Push(chunk.Take(mid).ToList());
                    continue;
                }

                var oversized = chunk[0];
                errorLog(
                    $"TomestoneCollector: dropping oversized result for content_id={oversized.ContentId}, encounter_id={oversized.EncounterId} (413 Payload Too Large).");
                await _leaseAbandoner.AbandonOversizedResultLeaseAsync(
                    configuration,
                    leaseSession.UploadUrl,
                    oversized,
                    "payload_too_large",
                    cancellationToken,
                    warningLog,
                    debugLog).ConfigureAwait(false);
                continue;
            }

            RequeueRemainingChunks(chunk, pendingChunks);
            if (result.RetryAfterSeconds.HasValue)
            {
                warningLog($"TomestoneCollector: results endpoint rate limited, retry_after={result.RetryAfterSeconds.Value}s");
            }

            if (!result.StatusCode.HasValue)
            {
                errorLog($"TomestoneCollector: failed to upload results (exception): {result.FailureBody} (requeued {chunk.Count})");
                return new TomestoneResultSubmitAttempt(true, false);
            }

            errorLog($"TomestoneCollector: failed to upload results: {result.StatusCode} (requeued {chunk.Count}) body={result.Body}");
            return new TomestoneResultSubmitAttempt(!result.AuthFailure, false);
        }

        return new TomestoneResultSubmitAttempt(false, false);
    }

    private void RequeueRemainingChunks(List<TomestoneResult> currentChunk, Stack<List<TomestoneResult>> remainingChunks)
    {
        _submitBuffer.RequeueSubmitBatch(currentChunk);
        while (remainingChunks.Count > 0)
        {
            _submitBuffer.RequeueSubmitBatch(remainingChunks.Pop());
        }
    }

    private static bool TryParseResultsSubmitResponse(string content, out ContributeTomestoneResultsResponse parsed)
    {
        parsed = null;
        return IngestJson.TryDeserializeObject(content, out parsed);
    }

    private static string SummarizeBatch(IReadOnlyCollection<TomestoneResult> chunk)
    {
        var phaseEntries = 0;
        var bossEntries = 0;
        foreach (var item in chunk)
        {
            if (item.PhaseProgress != null)
            {
                foreach (var entry in item.PhaseProgress.Values)
                {
                    phaseEntries += entry?.Count ?? 0;
                }
            }

            bossEntries += item.BossPercentages?.Count ?? 0;
        }

        return $"phase_entries={phaseEntries}, boss_entries={bossEntries}";
    }
}
