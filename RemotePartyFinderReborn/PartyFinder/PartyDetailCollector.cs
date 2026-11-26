using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class PartyDetailCollector : IDisposable {
    private static readonly TimeSpan UploadPumpInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly Plugin? _plugin;
    private readonly Configuration? _configuration;
    private readonly PartyDetailCaptureState _captureState;
    private readonly Func<PartyDetailPendingArrival, bool> _tryQueueArrival;
    private readonly Action _pumpPendingUploads;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<int, int, int> _nextJitterMs;
    private readonly PartyDetailUploadExecutor? _uploadExecutor;
    private readonly Stopwatch _uploadPumpTimer = new();
    private readonly PendingDetailQueue _pendingDetailQueue = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _activeTasksLock = new();
    private readonly HashSet<Task> _activeUploadTasks = new();

    private int _shutdownSignaled;
    private int _disposed;

    private long _lastQueuedListingId;
    private long _lastQueuedAckVersion;

    private long _lastSuccessfulUploadListingId;
    private long _lastSuccessfulUploadAtUtcTicks;
    private long _lastSuccessfulUploadAckVersion;

    private long _lastTerminalUploadListingId;
    private long _lastTerminalUploadAtUtcTicks;
    private long _lastTerminalUploadAckVersion;

    private int _activeUploadWorkers;

    internal ulong LastUploadedListingId => unchecked((ulong)Interlocked.Read(ref _lastQueuedListingId));
    internal long LastQueuedAckVersion => Interlocked.Read(ref _lastQueuedAckVersion);
    internal ulong LastSuccessfulUploadListingId => unchecked((ulong)Interlocked.Read(ref _lastSuccessfulUploadListingId));
    internal DateTime LastSuccessfulUploadAtUtc => new(Interlocked.Read(ref _lastSuccessfulUploadAtUtcTicks), DateTimeKind.Utc);
    internal long LastSuccessfulUploadAckVersion => Interlocked.Read(ref _lastSuccessfulUploadAckVersion);
    internal ulong LastTerminalUploadListingId => unchecked((ulong)Interlocked.Read(ref _lastTerminalUploadListingId));
    internal DateTime LastTerminalUploadAtUtc => new(Interlocked.Read(ref _lastTerminalUploadAtUtcTicks), DateTimeKind.Utc);
    internal long LastTerminalUploadAckVersion => Interlocked.Read(ref _lastTerminalUploadAckVersion);
    internal int PendingQueueCount => _pendingDetailQueue.Count;
    internal PartyDetailCaptureState CaptureState => _captureState;

    internal PartyDetailCollector(Plugin plugin)
        : this(plugin, plugin?.PartyDetailCaptureState ?? throw new ArgumentNullException(nameof(plugin))) {
    }

    internal PartyDetailCollector(
        Plugin plugin,
        PartyDetailCaptureState captureState,
        HttpClient? httpClient = null,
        Func<DateTime>? utcNow = null,
        Func<int, int, int>? nextJitterMs = null
    ) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
        _captureState = captureState ?? throw new ArgumentNullException(nameof(captureState));
        _tryQueueArrival = TryQueueArrivalCore;
        _pumpPendingUploads = PumpUploadQueue;
        _httpClient = httpClient ?? PluginHttpClientFactory.CreateIngestClient();
        _ownsHttpClient = httpClient is null;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _nextJitterMs = nextJitterMs ?? (static (min, max) => Random.Shared.Next(min, max));
        _uploadExecutor = new PartyDetailUploadExecutor(_configuration, _httpClient, _utcNow, LogDebug, LogWarning, LogError);
        _uploadPumpTimer.Start();
        _plugin.Framework.Update += OnUpdate;
    }

    internal PartyDetailCollector(
        PartyDetailCaptureState captureState,
        Configuration configuration,
        HttpClient? httpClient = null,
        Func<DateTime>? utcNow = null,
        Func<int, int, int>? nextJitterMs = null
    ) {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _captureState = captureState ?? throw new ArgumentNullException(nameof(captureState));
        _tryQueueArrival = TryQueueArrivalCore;
        _pumpPendingUploads = PumpUploadQueue;
        _httpClient = httpClient ?? PluginHttpClientFactory.CreateIngestClient();
        _ownsHttpClient = httpClient is null;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _nextJitterMs = nextJitterMs ?? (static (min, max) => Random.Shared.Next(min, max));
        _uploadExecutor = new PartyDetailUploadExecutor(_configuration, _httpClient, _utcNow, LogDebug, LogWarning, LogError);
        _uploadPumpTimer.Start();
    }

    internal PartyDetailCollector(
        PartyDetailCaptureState captureState,
        Func<UploadablePartyDetail, bool> tryQueuePayload,
        Action pumpPendingUploads,
        HttpClient? httpClient = null,
        Func<DateTime>? utcNow = null,
        Func<int, int, int>? nextJitterMs = null
    ) {
        _captureState = captureState ?? throw new ArgumentNullException(nameof(captureState));
        ArgumentNullException.ThrowIfNull(tryQueuePayload);
        _tryQueueArrival = arrival => tryQueuePayload(arrival.Payload);
        _pumpPendingUploads = pumpPendingUploads ?? throw new ArgumentNullException(nameof(pumpPendingUploads));
        _httpClient = httpClient ?? PluginHttpClientFactory.CreateIngestClient();
        _ownsHttpClient = httpClient is null;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _nextJitterMs = nextJitterMs ?? (static (min, max) => Random.Shared.Next(min, max));
        _uploadPumpTimer.Start();
    }

    internal void BeginShutdown() {
        if (Interlocked.Exchange(ref _shutdownSignaled, 1) != 0) {
            return;
        }

        try {
            _disposeCts.Cancel();
        } catch (ObjectDisposedException) {
        }
    }

    internal void CollectShutdownTasks(List<Task> tasks) {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_activeTasksLock) {
            foreach (var task in _activeUploadTasks) {
                if (!task.IsCompleted) {
                    tasks.Add(task);
                }
            }
        }
    }

    public void Dispose() {
        Dispose(ShutdownWaitTimeout);
    }

    internal void Dispose(TimeSpan waitTimeout) {
        if (_plugin is not null) {
            DetachFrameworkUpdateHandler(_plugin);
        }

        BeginShutdown();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        Task[] activeTasks;
        lock (_activeTasksLock) {
            activeTasks = _activeUploadTasks.ToArray();
        }

        if (activeTasks.Length > 0 && waitTimeout > TimeSpan.Zero) {
            try {
                Task.WhenAll(activeTasks).Wait(waitTimeout);
            } catch (Exception) {
            }
        }

        _disposeCts.Dispose();
        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }

    private void OnUpdate(IFramework framework) {
        if (_plugin is { IsPluginOperational: false }) {
            return;
        }

        _plugin?.PartyDetailCaptureRuntime.OnFrameworkUpdate();
        Update();
    }

    private void DetachFrameworkUpdateHandler(Plugin plugin) {
        plugin.Framework.Update -= OnUpdate;
    }

    internal void Update() {
        TryCapturePendingGeneration();
        _pumpPendingUploads();
    }

    private void TryCapturePendingGeneration() {
        while (_captureState.TryGetNextUnconsumedArrival(out var arrival)) {
            if (!PartyDetailUploadPolicy.IsSnapshotReadyForEnqueue(arrival.Payload)) {
                break;
            }

            if (!_tryQueueArrival(arrival)) {
                break;
            }

            _captureState.TryMarkConsumed(arrival.Generation);
        }
    }

    private bool TryQueueArrivalCore(PartyDetailPendingArrival arrival) {
        return TryQueuePayloadCore(arrival.Payload, arrival.Cycle.Owner);
    }

    private bool TryQueuePayloadCore(UploadablePartyDetail payload, PartyDetailRequestOwner owner) {
        var fingerprint = PartyDetailUploadPolicy.ComputeFingerprint(payload);
        var nowUtc = _utcNow();

        LogDebug(
            $"PartyDetailCollector: Uploading detail listing={payload.ListingId} leader={payload.LeaderContentId} world={payload.HomeWorld} members={payload.MemberContentIds.Count} fingerprint={fingerprint}");

        _pendingDetailQueue.Enqueue(payload, fingerprint, nowUtc, owner);
        if (_plugin is not null) {
            PartyDetailContentIdEnrichment.TryEnqueueForResolve(payload, _plugin.CharaCardResolver.EnqueueMany, LogWarning);
        }

        Interlocked.Exchange(ref _lastQueuedListingId, unchecked((long)payload.ListingId));
        Interlocked.Increment(ref _lastQueuedAckVersion);
        return true;
    }

    private void PumpUploadQueue() {
        if (_uploadPumpTimer.Elapsed < UploadPumpInterval) {
            return;
        }

        _uploadPumpTimer.Restart();
        StartDueUploads(_utcNow());
    }

    private void StartDueUploads(DateTime nowUtc) {
        if (_shutdownSignaled != 0) {
            return;
        }

        _pendingDetailQueue.PruneExpiredManualEntries(nowUtc, TimeSpan.FromMinutes(1));
        _captureState.PruneExpiredManualRequests();
        if (!HasAnyAttemptableTarget(nowUtc)) {
            return;
        }

        var maxConcurrency = PartyDetailUploadPolicy.NormalizeDetailUploadConcurrency(_configuration?.DetailUploadMaxConcurrency ?? 1);
        var batchSize = PartyDetailUploadPolicy.NormalizeDetailUploadBatchSize(_configuration?.DetailUploadBatchSize ?? 1);
        while (_shutdownSignaled == 0 && Volatile.Read(ref _activeUploadWorkers) < maxConcurrency) {
#if DEBUG
            var pendingQueueUnits = _pendingDetailQueue.Count;
            var queueStartedAt = DebugPerformanceDiagnostics.Begin();
            var queueAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
            var nextBatch = _pendingDetailQueue.SelectNextDueBatch(nowUtc, batchSize);
#else
            var nextBatch = _pendingDetailQueue.SelectNextDueBatch(nowUtc, batchSize);
#endif
#if DEBUG
            DebugPerformanceDiagnostics.Record(DebugPerformancePath.PendingDetailQueueSelect, queueStartedAt, queueAllocatedBefore, pendingQueueUnits);
            DebugPerformanceDiagnostics.Count(DebugPerformancePath.PendingDetailQueueSelected, nextBatch.Count);
#endif
            if (nextBatch.Count == 0) {
                return;
            }

            if (!TryGetDisposeToken(out var cancellationToken)) {
                _pendingDetailQueue.ReleaseInFlight(nextBatch);
                return;
            }

            Interlocked.Increment(ref _activeUploadWorkers);
            TrackUploadTask(ProcessQueuedBatchAsync(nextBatch, cancellationToken));
        }
    }

    internal int TriggerPendingDetailUploadNow() {
        return TriggerPendingDetailUploadNowWithState().TriggeredCount;
    }

    internal DetailUploadRetryTriggerResult TriggerPendingDetailUploadNowWithState() {
        var nowUtc = _utcNow();
        if (_plugin is { IsPluginOperational: false }) {
            return new DetailUploadRetryTriggerResult(
                0,
                BuildUploadQueueDebugState(nowUtc)
            );
        }

        var triggered = _pendingDetailQueue.TriggerRetryNow(nowUtc);

        if (triggered > 0) {
            LogDebug($"PartyDetailCollector: manual retry requested for {triggered} pending detail payload(s).");
            StartDueUploads(nowUtc);
        }

        return new DetailUploadRetryTriggerResult(
            triggered,
            BuildUploadQueueDebugState(nowUtc)
        );
    }

    private Task ProcessQueuedBatchAsync(
        IReadOnlyList<PendingDetailEntry> pendingBatch,
        CancellationToken cancellationToken
    ) {
        return Task.Run(async () => {
            try {
                var uploadExecutor = _uploadExecutor
                    ?? throw new InvalidOperationException("Detail uploads require collector configuration.");
                var uploadOutcomes = await uploadExecutor.UploadBatchToAllTargetsAsync(pendingBatch, cancellationToken);
                foreach (var pending in pendingBatch) {
                    var uploadOutcome = uploadOutcomes.TryGetValue(pending.Detail.ListingId, out var outcome)
                        ? outcome
                        : new DetailUploadOutcome(DetailUploadResult.RetryableFailure);
                    ApplyUploadOutcome(pending, uploadOutcome);
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            } catch (Exception exception) {
                LogError($"PartyDetailCollector upload error: {exception.Message}");
                foreach (var pending in pendingBatch) {
                    ScheduleRetry(pending, retryAfterSeconds: null);
                }
            } finally {
                _pendingDetailQueue.ReleaseInFlight(pendingBatch);

                Interlocked.Decrement(ref _activeUploadWorkers);
            }
        });
    }

    private bool TryGetDisposeToken(out CancellationToken cancellationToken) {
        if (_shutdownSignaled != 0) {
            cancellationToken = default;
            return false;
        }

        try {
            cancellationToken = _disposeCts.Token;
            return !cancellationToken.IsCancellationRequested;
        } catch (ObjectDisposedException) {
            cancellationToken = default;
            return false;
        }
    }

    private Task TrackUploadTask(Task uploadTask) {
        if (uploadTask.IsCompleted) {
            return uploadTask;
        }

        lock (_activeTasksLock) {
            _activeUploadTasks.Add(uploadTask);
        }

        _ = uploadTask.ContinueWith(
            static (completed, state) => {
                var owner = (PartyDetailCollector)state!;
                lock (owner._activeTasksLock) {
                    owner._activeUploadTasks.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        return uploadTask;
    }

    private void ApplyUploadOutcome(PendingDetailEntry pending, DetailUploadOutcome uploadOutcome) {
        switch (uploadOutcome.Result) {
            case DetailUploadResult.Applied:
                _pendingDetailQueue.RemoveIfUnchanged(pending);
                RecordSuccessfulUpload(pending.Detail.ListingId);
                break;

            case DetailUploadResult.TerminalRejected:
                _pendingDetailQueue.RemoveIfUnchanged(pending);
                RecordTerminalUpload(pending.Detail.ListingId);
                LogWarning($"PartyDetailCollector: listing {pending.Detail.ListingId} detail rejected by server; {uploadOutcome.Message}");
                break;

            case DetailUploadResult.ListingMissing:
            case DetailUploadResult.Deferred:
                ScheduleDeferredBackoff(pending, uploadOutcome);
                break;

            case DetailUploadResult.CircuitBlocked:
                ScheduleCircuitBlockedBackoff(pending, uploadOutcome);
                break;

            default:
                if (DropManualBestEffortDetail(pending, uploadOutcome)) {
                    break;
                }

                ScheduleRetry(pending, uploadOutcome);
                break;
        }
    }

    private void RecordSuccessfulUpload(ulong listingId) {
        var successAt = _utcNow();
        Interlocked.Exchange(ref _lastSuccessfulUploadAtUtcTicks, successAt.Ticks);
        Interlocked.Exchange(ref _lastSuccessfulUploadListingId, unchecked((long)listingId));
        Interlocked.Increment(ref _lastSuccessfulUploadAckVersion);
    }

    private void RecordTerminalUpload(ulong listingId) {
        var terminalAt = _utcNow();
        Interlocked.Exchange(ref _lastTerminalUploadAtUtcTicks, terminalAt.Ticks);
        Interlocked.Exchange(ref _lastTerminalUploadListingId, unchecked((long)listingId));
        Interlocked.Increment(ref _lastTerminalUploadAckVersion);
    }

    private bool DropManualBestEffortDetail(PendingDetailEntry pending, DetailUploadOutcome uploadOutcome) {
        if (pending.Owner != PartyDetailRequestOwner.Manual) {
            return false;
        }

        _pendingDetailQueue.RemoveIfUnchanged(pending);
        if (pending.HasAppliedTarget || uploadOutcome.HasAppliedTarget) {
            RecordSuccessfulUpload(pending.Detail.ListingId);
        }

        LogDebug($"PartyDetailCollector: dropping manual detail payload listing={pending.Detail.ListingId} after {uploadOutcome.Result}; manual captures are best-effort and are not kept in the retry queue.");
        return true;
    }

    private void ScheduleRetry(PendingDetailEntry pending, int? retryAfterSeconds) {
        ScheduleRetry(pending, new DetailUploadOutcome(DetailUploadResult.RetryableFailure, retryAfterSeconds));
    }

    private void ScheduleRetry(PendingDetailEntry pending, DetailUploadOutcome uploadOutcome) {
        var configuration = _configuration ?? throw new InvalidOperationException("Retry scheduling requires collector configuration.");
        if (!_pendingDetailQueue.TryGet(pending.Detail.ListingId, out var current)) {
            return;
        }

        if (current.Fingerprint != pending.Fingerprint) {
            return;
        }

        var merged = MergeOutcomeProgress(current, uploadOutcome);
        var configuredRetryCount = PartyDetailUploadPolicy.NormalizeRetryCount(configuration.DetailUploadRetryCount);
        if (PartyDetailUploadPolicy.ShouldDropPendingDetail(current.AttemptCount, configuredRetryCount)) {
            _pendingDetailQueue.RemoveIfUnchanged(pending);
            if (merged.HasAppliedTarget) {
                RecordSuccessfulUpload(pending.Detail.ListingId);
            }
            LogWarning($"PartyDetailCollector: dropping detail payload listing={pending.Detail.ListingId} after {current.AttemptCount} retries");
            return;
        }

        var nextAttemptCount = current.AttemptCount + 1;
        var updated = merged with {
            AttemptCount = nextAttemptCount,
            NextAttemptAtUtc = PartyDetailUploadPolicy.ComputeNextAttemptAtUtc(
                _utcNow(),
                nextAttemptCount,
                uploadOutcome.RetryAfterSeconds,
                _nextJitterMs
            ),
        };

        _pendingDetailQueue.TryUpdate(current, updated);
    }

    private void ScheduleDeferredBackoff(PendingDetailEntry pending, DetailUploadOutcome uploadOutcome) {
        if (!_pendingDetailQueue.TryGet(pending.Detail.ListingId, out var current)) {
            return;
        }

        if (current.Fingerprint != pending.Fingerprint) {
            return;
        }

        var merged = MergeOutcomeProgress(current, uploadOutcome);
        var configuredRetryCount = _configuration?.DetailUploadRetryCount ?? 0;
        if (PartyDetailUploadPolicy.ShouldDropDeferredPendingDetail(current.DeferredCount, configuredRetryCount)) {
            _pendingDetailQueue.RemoveIfUnchanged(pending);
            if (merged.HasAppliedTarget) {
                RecordSuccessfulUpload(pending.Detail.ListingId);
            } else if (uploadOutcome.Result == DetailUploadResult.ListingMissing) {
                RecordTerminalUpload(pending.Detail.ListingId);
                LogDebug($"PartyDetailCollector: listing {pending.Detail.ListingId} missing on server after {current.DeferredCount} deferral(s); dropping queued detail payload.");
            } else {
                LogWarning($"PartyDetailCollector: dropping detail payload listing={pending.Detail.ListingId} after {current.DeferredCount} deferrals ({uploadOutcome.Result})");
            }
            return;
        }

        var nextDeferredCount = current.DeferredCount + 1;
        var updated = merged with {
            DeferredCount = nextDeferredCount,
            NextAttemptAtUtc = PartyDetailUploadPolicy.ComputeDeferredNextAttemptAtUtc(
                _utcNow(),
                nextDeferredCount,
                _nextJitterMs
            ),
        };

        _pendingDetailQueue.TryUpdate(current, updated);
    }

    private void ScheduleCircuitBlockedBackoff(PendingDetailEntry pending, DetailUploadOutcome uploadOutcome) {
        if (!_pendingDetailQueue.TryGet(pending.Detail.ListingId, out var current)) {
            return;
        }

        if (current.Fingerprint != pending.Fingerprint) {
            return;
        }

        var merged = MergeOutcomeProgress(current, uploadOutcome);
        var nowUtc = _utcNow();
        var minRemaining = GetMinRemainingBreakDuration(merged, nowUtc);
        var updated = merged with {
            NextAttemptAtUtc = PartyDetailUploadPolicy.ComputeCircuitBlockedNextAttemptAtUtc(
                nowUtc,
                minRemaining,
                _nextJitterMs
            ),
        };

        _pendingDetailQueue.TryUpdate(current, updated);
    }

    private static PendingDetailEntry MergeOutcomeProgress(PendingDetailEntry current, DetailUploadOutcome uploadOutcome) {
        var resolvedIds = current.ResolvedTargetIds;
        if (uploadOutcome.ResolvedTargetIds is { Count: > 0 } newlyResolved) {
            resolvedIds = current.SafeResolvedTargetIds.Union(newlyResolved);
        }

        var hasAppliedTarget = current.HasAppliedTarget || uploadOutcome.HasAppliedTarget;
        if (ReferenceEquals(resolvedIds, current.ResolvedTargetIds) && hasAppliedTarget == current.HasAppliedTarget) {
            return current;
        }

        return current with {
            ResolvedTargetIds = resolvedIds,
            HasAppliedTarget = hasAppliedTarget,
        };
    }

    private TimeSpan? GetMinRemainingBreakDuration(PendingDetailEntry entry, DateTime nowUtc) {
        if (_configuration is null) {
            return null;
        }

        TimeSpan? minRemaining = null;
        foreach (var target in _configuration.GetUploadUrlsSnapshot()) {
            if (!target.IsEnabled || entry.IsTargetResolved(target.Id)) {
                continue;
            }

            if (!IngestEndpointResolver.IsValidUploadUrl(target.Url, out _)) {
                continue;
            }

            var remaining = UploadAttemptRecorder.GetCircuitStatus(target, _configuration, nowUtc).RemainingBreakDuration;
            if (!minRemaining.HasValue || remaining < minRemaining.Value) {
                minRemaining = remaining;
            }
        }

        return minRemaining;
    }

    private bool HasAnyAttemptableTarget(DateTime nowUtc) {
        if (_configuration is null) {
            return true;
        }

        foreach (var target in _configuration.GetUploadUrlsSnapshot()) {
            if (!target.IsEnabled || !IngestEndpointResolver.IsValidUploadUrl(target.Url, out _)) {
                continue;
            }

            if (!UploadAttemptRecorder.PeekIsCircuitOpen(target, _configuration, nowUtc)) {
                return true;
            }
        }

        return false;
    }

    internal void EnqueuePendingDetailForTesting(
        UploadablePartyDetail payload,
        DateTime nowUtc,
        PartyDetailRequestOwner owner = PartyDetailRequestOwner.Scanner
    ) {
        _pendingDetailQueue.Enqueue(
            payload,
            PartyDetailUploadPolicy.ComputeFingerprint(payload),
            nowUtc,
            owner
        );
    }

    internal Task ProcessNextDueUploadForTestingAsync() {
        var batchSize = PartyDetailUploadPolicy.NormalizeDetailUploadBatchSize(_configuration?.DetailUploadBatchSize ?? 1);
        var nextBatch = _pendingDetailQueue.SelectNextDueBatch(_utcNow(), batchSize);
        if (nextBatch.Count == 0) {
            return Task.CompletedTask;
        }

        if (!TryGetDisposeToken(out var cancellationToken)) {
            _pendingDetailQueue.ReleaseInFlight(nextBatch);
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _activeUploadWorkers);
        return TrackUploadTask(ProcessQueuedBatchAsync(nextBatch, cancellationToken));
    }

    internal void PumpDueUploadsForTesting() {
        StartDueUploads(_utcNow());
    }

    internal int ActiveUploadWorkerCount => Volatile.Read(ref _activeUploadWorkers);
    internal int InFlightDetailCount => _pendingDetailQueue.InFlightCount;
    internal int ActiveUploadWorkerCountForTesting => ActiveUploadWorkerCount;
    internal int InFlightDetailCountForTesting => InFlightDetailCount;

    internal DetailUploadQueueDebugState GetUploadQueueDebugState() {
        return BuildUploadQueueDebugState(_utcNow());
    }

    private DetailUploadQueueDebugState BuildUploadQueueDebugState(DateTime nowUtc) {
        var pendingEntries = _pendingDetailQueue.SnapshotEntries();
        var enabledTargets = _configuration?.UploadUrls
            .Where(static target => target.IsEnabled)
            .ToArray() ?? [];
        var nowOffset = new DateTimeOffset(nowUtc);

        var manualPending = 0;
        var scannerPending = 0;
        var due = 0;
        var eligibleNow = 0;
        var capabilityDeferred = 0;
        var circuitBlocked = 0;
        var openCircuitTargets = 0;

        if (_configuration is not null) {
            openCircuitTargets = enabledTargets.Count(target =>
                UploadAttemptRecorder.PeekIsCircuitOpen(target, _configuration, nowUtc));
        }

        foreach (var pending in pendingEntries) {
            if (pending.Owner == PartyDetailRequestOwner.Scanner) {
                scannerPending++;
            } else {
                manualPending++;
            }

            if (pending.NextAttemptAtUtc > nowUtc || _pendingDetailQueue.IsInFlight(pending.Detail.ListingId)) {
                continue;
            }

            due++;
            if (_configuration is null || enabledTargets.Length == 0) {
                continue;
            }

            var hasAvailableTarget = false;
            var hasCapabilityEligibleTarget = false;
            foreach (var target in enabledTargets) {
                if (pending.IsTargetResolved(target.Id)) {
                    continue;
                }

                if (UploadAttemptRecorder.PeekIsCircuitOpen(target, _configuration, nowUtc)) {
                    continue;
                }

                hasAvailableTarget = true;
                if (!target.ShouldDeferDetailRequest(pending.Detail.ListingId, nowOffset)) {
                    hasCapabilityEligibleTarget = true;
                    break;
                }
            }

            if (hasCapabilityEligibleTarget) {
                eligibleNow++;
            } else if (hasAvailableTarget) {
                capabilityDeferred++;
            } else {
                circuitBlocked++;
            }
        }

        return new DetailUploadQueueDebugState(
            pendingEntries.Count,
            manualPending,
            scannerPending,
            due,
            eligibleNow,
            capabilityDeferred,
            circuitBlocked,
            InFlightDetailCount,
            ActiveUploadWorkerCount,
            enabledTargets.Length,
            openCircuitTargets
        );
    }

    internal PendingDetailDebugState? GetPendingDetailForTesting(ulong listingId) {
        if (!_pendingDetailQueue.TryGet(listingId, out var pending)) {
            return null;
        }

        return new PendingDetailDebugState(pending.AttemptCount, pending.NextAttemptAtUtc, pending.DeferredCount);
    }

    internal readonly record struct PendingDetailDebugState(
        int AttemptCount,
        DateTime NextAttemptAtUtc,
        int DeferredCount = 0
    );

    private static void LogDebug(string message) {
        Plugin.Log?.Debug(message);
    }

    private static void LogWarning(string message) {
        Plugin.Log?.Warning(message);
    }

    private static void LogError(string message) {
        Plugin.Log?.Error(message);
    }
}
