using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn.Tomestone;

internal sealed class TomestoneBackgroundWorker
{
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan ShutdownAbandonTimeout = TimeSpan.FromMilliseconds(800);

    private readonly Configuration _configuration;
    private readonly ITomestoneProgressionService _progressionService;
    private readonly ApiCredentialStatusStore _apiCredentialStatus;
    private readonly IFFLogsTimeProvider _timeProvider;
    private readonly FFLogsWorkerPolicy _workerPolicy;
    private readonly TomestoneJobLeaseClient _jobLeaseClient;
    private readonly TomestoneBatchProcessor _batchProcessor;
    private readonly TomestoneResultSubmitter _resultSubmitter;
    private readonly TomestoneLeaseAbandoner _leaseAbandoner;
    private readonly Action<string> _infoLog;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _errorLog;
    private readonly Action<string> _debugLog;
    private readonly Func<bool> _isPluginOperational;
    private UploadUrl _lastSessionUploadUrl;
    private DateTime _lastCooldownSkipLogUtc = DateTime.MinValue;

    public TomestoneBackgroundWorker(
        Configuration configuration,
        ITomestoneProgressionService progressionService,
        ApiCredentialStatusStore apiCredentialStatus,
        IFFLogsTimeProvider timeProvider,
        FFLogsWorkerPolicy workerPolicy,
        TomestoneJobLeaseClient jobLeaseClient,
        TomestoneBatchProcessor batchProcessor,
        TomestoneResultSubmitter resultSubmitter,
        TomestoneLeaseAbandoner leaseAbandoner,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<string> debugLog,
        Func<bool> isPluginOperational = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _progressionService = progressionService ?? throw new ArgumentNullException(nameof(progressionService));
        _apiCredentialStatus = apiCredentialStatus ?? throw new ArgumentNullException(nameof(apiCredentialStatus));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _workerPolicy = workerPolicy ?? throw new ArgumentNullException(nameof(workerPolicy));
        _jobLeaseClient = jobLeaseClient ?? throw new ArgumentNullException(nameof(jobLeaseClient));
        _batchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
        _resultSubmitter = resultSubmitter ?? throw new ArgumentNullException(nameof(resultSubmitter));
        _leaseAbandoner = leaseAbandoner ?? throw new ArgumentNullException(nameof(leaseAbandoner));
        _infoLog = infoLog ?? throw new ArgumentNullException(nameof(infoLog));
        _warningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        _errorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _isPluginOperational = isPluginOperational;
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
                    _errorLog($"TomestoneCollector Loop Error: {ex.Message}");
                    consecutiveFailures = await _workerPolicy.DelayWithBackoffAsync(consecutiveFailures, cancellationToken);
                }
            }
        }
        finally
        {
            await FlushOrAbandonOnShutdownAsync().ConfigureAwait(false);
        }
    }

    internal Task<int> RunSingleIterationForTestingAsync(int consecutiveFailures, CancellationToken cancellationToken)
        => RunIterationAsync(consecutiveFailures, cancellationToken);

    private async Task<int> RunIterationAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        var hadTransientFailure = false;

        // Pre-lease Gate Condition 1: Plugin.IsPluginOperational == true
        if (_isPluginOperational != null && !_isPluginOperational())
        {
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        // Pre-lease Gate Condition 2: Configuration.EnableTomestoneProgressEnrichment == true
        if (!_configuration.EnableTomestoneProgressEnrichment)
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        // Pre-lease Gate Condition 3: Configuration.HasTomestoneApiKeyConfigured == true
        if (!_configuration.HasTomestoneApiKeyConfigured)
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        // Pre-lease Gate Condition 4: ApiCredentialStatus.Tomestone.State != Rejected
        if (_apiCredentialStatus.Tomestone.State == ApiCredentialValidationState.Rejected)
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        // Pre-lease Gate Condition 5: !TomestoneApiClient.TryGetRateLimitRemaining(out _)
        if (_progressionService.TryGetRateLimitRemaining(out var rateLimitRemaining))
        {
            await TryFlushPendingIfNeededAsync(preferredUrl: null, cancellationToken);
            LogCooldownSkipIfNeeded(rateLimitRemaining);
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
        _debugLog($"Received {jobs.Count} Tomestone progression job(s) from server.");

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

        if (processResult.AbandonedLeases.Count > 0)
        {
            await _leaseAbandoner.AbandonLeasesAsync(
                _configuration,
                leaseSession.UploadUrl,
                processResult.AbandonedLeases,
                cancellationToken,
                _warningLog,
                _debugLog);
        }

        if (submitAttempt.ShouldUseBaseDelayBeforeNextPoll)
        {
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        if (processResult.HitRateLimitCooldown)
        {
            LogCooldownSkipIfNeeded(processResult.CooldownRemaining);
            await _workerPolicy.DelayAsync(_workerPolicy.WorkerBaseDelayMs, cancellationToken);
            return 0;
        }

        if (processResult.HitApiKeyRejected)
        {
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

    private void LogCooldownSkipIfNeeded(TimeSpan remaining)
    {
        var now = _timeProvider.UtcNow;
        if ((now - _lastCooldownSkipLogUtc) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCooldownSkipLogUtc = now;
        var minutesRemaining = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        _warningLog($"TomestoneCollector: Tomestone cooldown active; skipping server polling for about {minutesRemaining} minute(s).");
    }

    private async Task<TomestoneResultSubmitAttempt> TryFlushPendingIfNeededAsync(
        UploadUrl preferredUrl,
        CancellationToken cancellationToken)
    {
        if (!_resultSubmitter.HasPendingResults)
        {
            return new TomestoneResultSubmitAttempt(false, false);
        }

        var targetUrl = preferredUrl
                        ?? _lastSessionUploadUrl
                        ?? _jobLeaseClient.SelectAvailableUploadUrl(_configuration, _warningLog);
        if (targetUrl == null)
        {
            return new TomestoneResultSubmitAttempt(false, false);
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
                using var abandonCts = new CancellationTokenSource(ShutdownAbandonTimeout);
                await _leaseAbandoner.AbandonResultLeasesAsync(
                    _configuration,
                    targetUrl,
                    remaining,
                    "worker_shutdown",
                    "worker_shutdown",
                    abandonCts.Token,
                    _warningLog,
                    _debugLog).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _debugLog($"TomestoneCollector: shutdown lease abandon skipped: {ex.Message}");
            }
        }
    }
}
