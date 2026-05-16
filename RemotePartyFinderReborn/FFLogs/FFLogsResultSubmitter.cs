using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;

    public FFLogsResultSubmitter(FFLogsCollectorSeams seams, FFLogsSubmitBuffer submitBuffer)
    {
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
        _submitBuffer = submitBuffer ?? throw new ArgumentNullException(nameof(submitBuffer));
        _protectedExecutor = new ProtectedIngestEndpointExecutor(
            seams.IngestHttpSender,
            () => seams.TimeProvider.UtcNow);
    }

    public async Task<FFLogsResultSubmitAttempt> TrySubmitResultsAsync(
        Configuration configuration,
        FFLogsLeaseSession leaseSession,
        IEnumerable<ParseResult> freshResults,
        CancellationToken cancellationToken,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(leaseSession);
        ArgumentNullException.ThrowIfNull(freshResults);
        ArgumentNullException.ThrowIfNull(infoLog);
        ArgumentNullException.ThrowIfNull(warningLog);
        ArgumentNullException.ThrowIfNull(errorLog);

        var submitBatch = _submitBuffer.BuildSubmitBatch(freshResults.ToList());
        if (submitBatch.Count == 0)
        {
            return new FFLogsResultSubmitAttempt(false, false);
        }

        var jsonContent = JsonConvert.SerializeObject(submitBatch);
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
            _submitBuffer.RequeueSubmitBatch(submitBatch);
            return new FFLogsResultSubmitAttempt(false, true);
        }

        if (result.Success)
        {
            if (TryParseResultsSubmitResponse(result.Body, out var parsed))
            {
                infoLog(
                    $"Uploaded parse results: updated={parsed.Updated}, accepted={parsed.Accepted}/{parsed.Submitted}, rejected={parsed.Rejected}, status={parsed.Status}.");
            }
            else
            {
                infoLog($"Uploaded {submitBatch.Count} parse results.");
            }

            return new FFLogsResultSubmitAttempt(false, false);
        }

        _submitBuffer.RequeueSubmitBatch(submitBatch);
        if (result.RetryAfterSeconds.HasValue)
        {
            warningLog($"FFLogsCollector: results endpoint rate limited, retry_after={result.RetryAfterSeconds.Value}s");
        }

        if (!result.StatusCode.HasValue)
        {
            errorLog($"Failed to upload results (exception): {result.FailureBody} (requeued {submitBatch.Count})");
            return new FFLogsResultSubmitAttempt(true, false);
        }

        errorLog($"Failed to upload results: {result.StatusCode} (requeued {submitBatch.Count}) body={result.Body}");
        return new FFLogsResultSubmitAttempt(!result.AuthFailure, false);
    }

    private static bool TryParseResultsSubmitResponse(string content, out ContributeFflogsResultsResponse parsed)
    {
        parsed = null;
        return IngestJson.TryDeserializeObject(content, out parsed);
    }

}
