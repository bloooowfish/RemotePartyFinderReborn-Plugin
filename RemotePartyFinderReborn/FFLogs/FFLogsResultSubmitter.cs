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

        var submitSummary = SummarizeSubmitBatch(submitBatch);
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
                    $"Uploaded parse results: updated={parsed.Updated}, accepted={parsed.Accepted}/{parsed.Submitted}, submitted={parsed.Submitted}, rejected={parsed.Rejected}, status={parsed.Status}, {submitSummary.ToLogFragment()}.");
            }
            else
            {
                infoLog($"Uploaded {submitBatch.Count} parse results: {submitSummary.ToLogFragment()}.");
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
            errorLog($"Failed to upload results (exception): {result.FailureBody} (requeued {submitBatch.Count}, {submitSummary.ToLogFragment()})");
            return new FFLogsResultSubmitAttempt(true, false);
        }

        errorLog($"Failed to upload results: {result.StatusCode} (requeued {submitBatch.Count}, {submitSummary.ToLogFragment()}) body={result.Body}");
        return new FFLogsResultSubmitAttempt(!result.AuthFailure, false);
    }

    private static bool TryParseResultsSubmitResponse(string content, out ContributeFflogsResultsResponse parsed)
    {
        parsed = null;
        return IngestJson.TryDeserializeObject(content, out parsed);
    }

    private static FFLogsSubmitBatchSummary SummarizeSubmitBatch(IReadOnlyCollection<ParseResult> submitBatch)
    {
        var tomestoneOnlyResults = 0;
        var tomestoneProgressResults = 0;
        var phaseProgressResults = 0;
        var phaseProgressEntries = 0;
        var bossPercentageResults = 0;
        var bossPercentageEntries = 0;

        foreach (var result in submitBatch)
        {
            if (string.Equals(result.SourceKind, "tomestone_api", StringComparison.Ordinal))
            {
                tomestoneOnlyResults++;
            }

            var resultPhaseEntries = result.PhaseProgress?
                .Sum(static entry => entry.Value?.Count ?? 0) ?? 0;
            var resultBossEntries = result.BossPercentages?.Count ?? 0;

            if (resultPhaseEntries > 0)
            {
                phaseProgressResults++;
                phaseProgressEntries += resultPhaseEntries;
            }

            if (resultBossEntries > 0)
            {
                bossPercentageResults++;
                bossPercentageEntries += resultBossEntries;
            }

            if (resultPhaseEntries > 0 || resultBossEntries > 0)
            {
                tomestoneProgressResults++;
            }
        }

        return new FFLogsSubmitBatchSummary(
            tomestoneOnlyResults,
            tomestoneProgressResults,
            phaseProgressResults,
            phaseProgressEntries,
            bossPercentageResults,
            bossPercentageEntries);
    }

    private sealed record FFLogsSubmitBatchSummary(
        int TomestoneOnlyResults,
        int TomestoneProgressResults,
        int PhaseProgressResults,
        int PhaseProgressEntries,
        int BossPercentageResults,
        int BossPercentageEntries)
    {
        public string ToLogFragment()
            => $"tomestone_only={TomestoneOnlyResults}, "
               + $"tomestone_progress_results={TomestoneProgressResults}, "
               + $"phase_progress_results={PhaseProgressResults}, "
               + $"phase_progress_entries={PhaseProgressEntries}, "
               + $"boss_percentage_results={BossPercentageResults}, "
               + $"boss_percentage_entries={BossPercentageEntries}";
    }
}
