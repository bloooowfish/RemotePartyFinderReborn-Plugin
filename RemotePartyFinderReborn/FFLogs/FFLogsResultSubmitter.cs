using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn;

internal sealed record FFLogsResultSubmitAttempt(
    bool HadTransientFailure,
    bool ShouldUseBaseDelayBeforeNextPoll);

internal sealed class FFLogsResultSubmitter
{
    private readonly FFLogsCollectorSeams _seams;
    private readonly FFLogsSubmitBuffer _submitBuffer;
    private readonly FFLogsLeaseAbandoner _leaseAbandoner;
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;

    public FFLogsResultSubmitter(
        FFLogsCollectorSeams seams,
        FFLogsSubmitBuffer submitBuffer,
        FFLogsLeaseAbandoner leaseAbandoner = null)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _submitBuffer = submitBuffer ?? throw new ArgumentNullException(nameof(submitBuffer));
        _leaseAbandoner = leaseAbandoner ?? new FFLogsLeaseAbandoner(seams);
        _protectedExecutor = new ProtectedIngestEndpointExecutor(
            seams.IngestHttpSender,
            () => seams.TimeProvider.UtcNow);
    }

    public bool HasPendingResults => _submitBuffer.HasPendingResults;

    public List<ParseResult> DrainPendingResults()
        => _submitBuffer.DrainPendingResults();

    public Task<FFLogsResultSubmitAttempt> TryFlushPendingAsync(
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
            return Task.FromResult(new FFLogsResultSubmitAttempt(false, false));
        }

        return TrySubmitResultsAsync(
            configuration,
            new FFLogsLeaseSession(uploadUrl, []),
            [],
            cancellationToken,
            infoLog,
            warningLog,
            errorLog,
            debugLog);
    }

    public async Task<FFLogsResultSubmitAttempt> TrySubmitResultsAsync(
        Configuration configuration,
        FFLogsLeaseSession leaseSession,
        IEnumerable<ParseResult> freshResults,
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
            return new FFLogsResultSubmitAttempt(false, false);
        }

        var pendingChunks = new Stack<List<ParseResult>>();
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
                IngestRoutes.FflogsResults,
                ProtectedEndpointCapabilityKind.FflogsResults,
                HttpMethod.Post,
                jsonContent,
                recordUploadAttempt: false,
                cancellationToken).ConfigureAwait(false);

            if (result.Deferred)
            {
                RequeueRemainingChunks(chunk, pendingChunks);
                return new FFLogsResultSubmitAttempt(false, true);
            }

            if (result.Success)
            {
                if (TryParseResultsSubmitResponse(result.Body, out var parsed))
                {
                    infoLog(
                        $"Uploaded parse results: updated={parsed.Updated}, accepted={parsed.Accepted}/{parsed.Submitted}, submitted={parsed.Submitted}, rejected={parsed.Rejected}, status={parsed.Status}.");
                }
                else
                {
                    infoLog($"Uploaded {chunk.Count} parse results.");
                }

                continue;
            }

            if (result.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                if (chunk.Count > 1)
                {
                    var mid = chunk.Count / 2;
                    warningLog($"FFLogsCollector: results payload too large (413) for {chunk.Count} results; splitting batch.");
                    pendingChunks.Push(chunk.Skip(mid).ToList());
                    pendingChunks.Push(chunk.Take(mid).ToList());
                    continue;
                }

                var oversized = chunk[0];
                errorLog($"FFLogsCollector: dropping oversized parse result for content_id={oversized.ContentId} (413 Payload Too Large).");
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
                warningLog($"FFLogsCollector: results endpoint rate limited, retry_after={result.RetryAfterSeconds.Value}s");
            }

            if (!result.StatusCode.HasValue)
            {
                errorLog($"Failed to upload results (exception): {result.FailureBody} (requeued {chunk.Count})");
                return new FFLogsResultSubmitAttempt(true, false);
            }

            errorLog($"Failed to upload results: {result.StatusCode} (requeued {chunk.Count}) body={result.Body}");
            return new FFLogsResultSubmitAttempt(!result.AuthFailure, false);
        }

        return new FFLogsResultSubmitAttempt(false, false);
    }

    private void RequeueRemainingChunks(List<ParseResult> currentChunk, Stack<List<ParseResult>> remainingChunks)
    {
        _submitBuffer.RequeueSubmitBatch(currentChunk);
        while (remainingChunks.Count > 0)
        {
            _submitBuffer.RequeueSubmitBatch(remainingChunks.Pop());
        }
    }

    private static bool TryParseResultsSubmitResponse(string content, out ContributeFflogsResultsResponse parsed)
    {
        parsed = null;
        return IngestJson.TryDeserializeObject(content, out parsed);
    }
}
