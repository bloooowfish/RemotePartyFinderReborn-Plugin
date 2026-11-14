using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsBackgroundWorker
{
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly Configuration _configuration;
    private readonly IFFLogsApiClient _apiClient;
    private readonly FFLogsWorkerPolicy _workerPolicy;
    private readonly FFLogsJobLeaseClient _jobLeaseClient;
    private readonly FFLogsBatchProcessor _batchProcessor;
    private readonly FFLogsResultSubmitter _resultSubmitter;
    private readonly FFLogsLeaseAbandoner _leaseAbandoner;
    private readonly Action<string> _infoLog;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _errorLog;
    private readonly Action<string> _debugLog;
    private UploadUrl _lastSessionUploadUrl;

    public FFLogsBackgroundWorker(
        Configuration configuration,
        IFFLogsApiClient apiClient,
        FFLogsWorkerPolicy workerPolicy,
        FFLogsJobLeaseClient jobLeaseClient,
        FFLogsBatchProcessor batchProcessor,
        FFLogsResultSubmitter resultSubmitter,
        FFLogsLeaseAbandoner leaseAbandoner,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<string> debugLog)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _workerPolicy = workerPolicy ?? throw new ArgumentNullException(nameof(workerPolicy));
        _jobLeaseClient = jobLeaseClient ?? throw new ArgumentNullException(nameof(jobLeaseClient));
        _batchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
        _resultSubmitter = resultSubmitter ?? throw new ArgumentNullException(nameof(resultSubmitter));
        _leaseAbandoner = leaseAbandoner ?? throw new ArgumentNullException(nameof(leaseAbandoner));
        _infoLog = infoLog ?? throw new ArgumentNullException(nameof(infoLog));
        _warningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        _errorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    consecutiveFailures = await RunIterationAsync(consecutiveFailures, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _errorLog($"FFLogsCollector Loop Error: {ex.Message}");
                    consecutiveFailures = await _workerPolicy.DelayWithBackoffAsync(consecutiveFailures, cancellationToken);
                }
            }
        }
        finally
        {
            await FlushOrAbandonOnShutdownAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> RunIterationAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        var hadTransientFailure = false;

        if (!_configuration.EnableFFLogsWorker)
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        if (string.IsNullOrEmpty(_configuration.FFLogsClientId)
            || string.IsNullOrEmpty(_configuration.FFLogsClientSecret))
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        if (_apiClient.TryGetRateLimitRemaining(out var rateLimitRemaining))
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            _workerPolicy.LogCooldownSkipIfNeeded(rateLimitRemaining);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        var leaseAttempt = await _jobLeaseClient.TryAcquireSessionAsync(
            _configuration,
            cancellationToken,
            _warningLog,
            _debugLog);
        hadTransientFailure |= leaseAttempt.HadTransientFailure;
        var leaseSession = leaseAttempt.Session;

        if (leaseSession == null)
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        _lastSessionUploadUrl = leaseSession.UploadUrl ?? _lastSessionUploadUrl;

        if (!leaseSession.HasJobs)
        {
            var pendingFlush = await TryFlushPendingIfNeededAsync(leaseSession.UploadUrl, cancellationToken);
            hadTransientFailure |= pendingFlush.HadTransientFailure;
            if (pendingFlush.ShouldUseBaseDelayBeforeNextPoll)
            {
                await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
                return 0;
            }

            if (hadTransientFailure)
            {
                return await _workerPolicy.DelayWithBackoffAsync(consecutiveFailures, cancellationToken);
            }

            if (leaseSession.UseBaseDelayWhenNoWork)
            {
                await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
                return 0;
            }

            await _workerPolicy.DelayAsync(_workerPolicy.ComputeIdleDelayMs(), cancellationToken);
            return 0;
        }

        var jobs = leaseSession.Jobs;
        _debugLog($"Received {jobs.Count} players to fetch from FFLogs.");

        var processResult = await _batchProcessor.ProcessLeaseSessionAsync(leaseSession, cancellationToken);
        hadTransientFailure |= processResult.HadTransientFailure;

        var submitAttempt = await _resultSubmitter.TrySubmitResultsAsync(
            _configuration,
            leaseSession,
            processResult.ProcessedResults,
            cancellationToken,
            _infoLog,
            _warningLog,
            _errorLog,
            _debugLog);
        hadTransientFailure |= submitAttempt.HadTransientFailure;
        if (submitAttempt.ShouldUseBaseDelayBeforeNextPoll)
        {
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        if (processResult.ShouldAbandonRemainingLeases || processResult.HadTransientFailure)
        {
            var abandonReason = processResult.HitRateLimitCooldown
                ? "fflogs_rate_limit_cooldown"
                : "worker_transient_failure";
            await _leaseAbandoner.AbandonUnprocessedLeasesAsync(
                _configuration,
                leaseSession,
                jobs,
                processResult.ProcessedResults,
                abandonReason,
                cancellationToken,
                _warningLog,
                _debugLog);
        }

        if (processResult.HitRateLimitCooldown)
        {
            _workerPolicy.LogCooldownSkipIfNeeded(processResult.CooldownRemaining);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        if (hadTransientFailure)
        {
            return await _workerPolicy.DelayWithBackoffAsync(consecutiveFailures, cancellationToken);
        }

        await _workerPolicy.DelayAsync(_workerPolicy.ComputeIdleDelayMs(), cancellationToken);
        return 0;
    }

    private async Task<FFLogsResultSubmitAttempt> TryFlushPendingIfNeededAsync(
        UploadUrl preferredUrl,
        CancellationToken cancellationToken)
    {
        if (!_resultSubmitter.HasPendingResults)
        {
            return new FFLogsResultSubmitAttempt(false, false);
        }

        var targetUrl = preferredUrl
                        ?? _lastSessionUploadUrl
                        ?? _jobLeaseClient.SelectAvailableUploadUrl(_configuration, _warningLog);
        if (targetUrl == null)
        {
            return new FFLogsResultSubmitAttempt(false, false);
        }

        return await _resultSubmitter.TryFlushPendingAsync(
            _configuration,
            targetUrl,
            cancellationToken,
            _infoLog,
            _warningLog,
            _errorLog,
            _debugLog).ConfigureAwait(false);
    }

    private async Task FlushOrAbandonOnShutdownAsync()
    {
        if (!_resultSubmitter.HasPendingResults)
        {
            return;
        }

        var targetUrl = _lastSessionUploadUrl
                        ?? _jobLeaseClient.SelectAvailableUploadUrl(_configuration, _warningLog);
        if (targetUrl == null)
        {
            _resultSubmitter.DrainPendingResults();
            return;
        }

        using var shutdownCts = new CancellationTokenSource(ShutdownFlushTimeout);
        try
        {
            await _resultSubmitter.TryFlushPendingAsync(
                _configuration,
                targetUrl,
                shutdownCts.Token,
                _infoLog,
                _warningLog,
                _errorLog,
                _debugLog).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // Fall through to abandon remaining unsubmitted leases.
        }

        if (_resultSubmitter.HasPendingResults)
        {
            var remaining = _resultSubmitter.DrainPendingResults();
            try
            {
                using var abandonCts = new CancellationTokenSource(ShutdownFlushTimeout);
                await _leaseAbandoner.AbandonResultLeasesAsync(
                    _configuration,
                    targetUrl,
                    remaining,
                    "worker_shutdown",
                    abandonCts.Token,
                    _warningLog,
                    _debugLog).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _debugLog($"FFLogsCollector: shutdown lease abandon skipped: {ex.Message}");
            }
        }
    }
}
