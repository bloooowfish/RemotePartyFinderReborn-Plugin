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

    private uint _lastQueuedListingId;
    private long _lastQueuedAckVersion;

    private volatile uint _lastSuccessfulUploadListingId;
    private long _lastSuccessfulUploadAtUtcTicks;
    private long _lastSuccessfulUploadAckVersion;

    private volatile uint _lastTerminalUploadListingId;
    private long _lastTerminalUploadAtUtcTicks;
    private long _lastTerminalUploadAckVersion;

    private int _activeUploadWorkers;

    internal uint LastUploadedListingId => _lastQueuedListingId;
    internal long LastQueuedAckVersion => Interlocked.Read(ref _lastQueuedAckVersion);
    internal uint LastSuccessfulUploadListingId => _lastSuccessfulUploadListingId;
    internal DateTime LastSuccessfulUploadAtUtc => new(Interlocked.Read(ref _lastSuccessfulUploadAtUtcTicks), DateTimeKind.Utc);
    internal long LastSuccessfulUploadAckVersion => Interlocked.Read(ref _lastSuccessfulUploadAckVersion);
    internal uint LastTerminalUploadListingId => _lastTerminalUploadListingId;
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
        _httpClient = httpClient ?? new HttpClient();
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
        _httpClient = httpClient ?? new HttpClient();
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
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _nextJitterMs = nextJitterMs ?? (static (min, max) => Random.Shared.Next(min, max));
        _uploadPumpTimer.Start();
    }

    public void Dispose() {
        if (_plugin is not null) {
            DetachFrameworkUpdateHandler(_plugin);
        }

        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }

    private void OnUpdate(IFramework framework) {
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
        if (!_captureState.TryGetNextUnconsumedArrival(out var arrival)) {
            return;
        }

        if (!PartyDetailUploadPolicy.IsSnapshotReadyForEnqueue(arrival.Payload)) {
            return;
        }

        if (!_tryQueueArrival(arrival)) {
            return;
        }

        _captureState.TryMarkConsumed(arrival.Generation);
    }

    private bool TryQueueArrivalCore(PartyDetailPendingArrival arrival) {
        return TryQueuePayloadCore(arrival.Payload, arrival.Cycle.Owner);
    }

    private bool TryQueuePayloadCore(UploadablePartyDetail payload, PartyDetailRequestOwner owner) {
        var fingerprint = PartyDetailUploadPolicy.ComputeFingerprint(payload.MemberContentIds, payload.MemberJobs, payload.SlotFlags);
        var nowUtc = _utcNow();

        LogDebug(
            $"PartyDetailCollector: Uploading detail listing={payload.ListingId} leader={payload.LeaderContentId} world={payload.HomeWorld} members={payload.MemberContentIds.Count} fingerprint={fingerprint}");

        _pendingDetailQueue.Enqueue(payload, fingerprint, nowUtc, owner);
        if (_plugin is not null) {
            PartyDetailContentIdEnrichment.TryEnqueueForResolve(payload, _plugin.CharaCardResolver.EnqueueMany, LogWarning);
        }

        _lastQueuedListingId = payload.ListingId;
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
        var maxConcurrency = PartyDetailUploadPolicy.NormalizeDetailUploadConcurrency(_configuration?.DetailUploadMaxConcurrency ?? 1);
        var batchSize = PartyDetailUploadPolicy.NormalizeDetailUploadBatchSize(_configuration?.DetailUploadBatchSize ?? 1);
        while (Volatile.Read(ref _activeUploadWorkers) < maxConcurrency) {
            var nextBatch = _pendingDetailQueue.SelectNextDueBatch(nowUtc, batchSize);
            if (nextBatch.Count == 0) {
                return;
            }

            Interlocked.Increment(ref _activeUploadWorkers);
            _ = ProcessQueuedBatchAsync(nextBatch);
        }
    }

    internal int TriggerPendingDetailUploadNow() {
        return TriggerPendingDetailUploadNowWithState().TriggeredCount;
    }

    internal DetailUploadRetryTriggerResult TriggerPendingDetailUploadNowWithState() {
        var nowUtc = _utcNow();
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

    private Task ProcessQueuedBatchAsync(IReadOnlyList<PendingDetailEntry> pendingBatch) {
        return Task.Run(async () => {
            try {
                var uploadExecutor = _uploadExecutor
                    ?? throw new InvalidOperationException("Detail uploads require collector configuration.");
                var uploadOutcomes = await uploadExecutor.UploadBatchToAllTargetsAsync(pendingBatch);
                foreach (var pending in pendingBatch) {
                    var uploadOutcome = uploadOutcomes.TryGetValue(pending.Detail.ListingId, out var outcome)
                        ? outcome
                        : new DetailUploadOutcome(DetailUploadResult.RetryableFailure);
                    ApplyUploadOutcome(pending, uploadOutcome);
                }
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

    private void ApplyUploadOutcome(PendingDetailEntry pending, DetailUploadOutcome uploadOutcome) {
        switch (uploadOutcome.Result) {
            case DetailUploadResult.Applied:
                _pendingDetailQueue.RemoveIfUnchanged(pending);
                var successAt = _utcNow();
                Interlocked.Exchange(ref _lastSuccessfulUploadAtUtcTicks, successAt.Ticks);
                _lastSuccessfulUploadListingId = pending.Detail.ListingId;
                Interlocked.Increment(ref _lastSuccessfulUploadAckVersion);
                break;

            case DetailUploadResult.ListingMissing:
            case DetailUploadResult.TerminalRejected:
                _pendingDetailQueue.RemoveIfUnchanged(pending);
                var terminalAt = _utcNow();
                Interlocked.Exchange(ref _lastTerminalUploadAtUtcTicks, terminalAt.Ticks);
                _lastTerminalUploadListingId = pending.Detail.ListingId;
                Interlocked.Increment(ref _lastTerminalUploadAckVersion);
                if (uploadOutcome.Result == DetailUploadResult.TerminalRejected) {
                    LogWarning($"PartyDetailCollector: listing {pending.Detail.ListingId} detail rejected by server; {uploadOutcome.Message}");
                } else {
                    LogDebug($"PartyDetailCollector: listing {pending.Detail.ListingId} missing on server while applying detail update; dropping queued detail payload.");
                }
                break;

            case DetailUploadResult.Deferred:
                DropManualBestEffortDetail(pending, uploadOutcome.Result);
                break;

            default:
                if (DropManualBestEffortDetail(pending, uploadOutcome.Result)) {
                    break;
                }

                ScheduleRetry(pending, uploadOutcome.RetryAfterSeconds);
                break;
        }
    }

    private bool DropManualBestEffortDetail(PendingDetailEntry pending, DetailUploadResult result) {
        if (pending.Owner != PartyDetailRequestOwner.Manual) {
            return false;
        }

        _pendingDetailQueue.RemoveIfUnchanged(pending);
        LogDebug($"PartyDetailCollector: dropping manual detail payload listing={pending.Detail.ListingId} after {result}; manual captures are best-effort and are not kept in the retry queue.");
        return true;
    }

    private void ScheduleRetry(PendingDetailEntry pending, int? retryAfterSeconds) {
        var configuration = _configuration ?? throw new InvalidOperationException("Retry scheduling requires collector configuration.");
        if (!_pendingDetailQueue.TryGet(pending.Detail.ListingId, out var current)) {
            return;
        }

        if (current.Fingerprint != pending.Fingerprint) {
            return;
        }

        var configuredRetryCount = PartyDetailUploadPolicy.NormalizeRetryCount(configuration.DetailUploadRetryCount);
        if (PartyDetailUploadPolicy.ShouldDropPendingDetail(current.AttemptCount, configuredRetryCount)) {
            _pendingDetailQueue.RemoveIfUnchanged(pending);
            LogWarning($"PartyDetailCollector: dropping detail payload listing={pending.Detail.ListingId} after {current.AttemptCount} retries");
            return;
        }

        var nextAttemptCount = current.AttemptCount + 1;
        var updated = current with {
            AttemptCount = nextAttemptCount,
            NextAttemptAtUtc = PartyDetailUploadPolicy.ComputeNextAttemptAtUtc(
                _utcNow(),
                nextAttemptCount,
                retryAfterSeconds,
                _nextJitterMs
            ),
        };

        _pendingDetailQueue.TryUpdate(current, updated);
    }

    internal void EnqueuePendingDetailForTesting(
        UploadablePartyDetail payload,
        DateTime nowUtc,
        PartyDetailRequestOwner owner = PartyDetailRequestOwner.Scanner
    ) {
        _pendingDetailQueue.Enqueue(
            payload,
            PartyDetailUploadPolicy.ComputeFingerprint(payload.MemberContentIds, payload.MemberJobs, payload.SlotFlags),
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

        Interlocked.Increment(ref _activeUploadWorkers);
        return ProcessQueuedBatchAsync(nextBatch);
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
                UploadAttemptRecorder.IsCircuitOpen(target, _configuration, nowUtc));
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
                if (UploadAttemptRecorder.IsCircuitOpen(target, _configuration, nowUtc)) {
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

    internal PendingDetailDebugState? GetPendingDetailForTesting(uint listingId) {
        if (!_pendingDetailQueue.TryGet(listingId, out var pending)) {
            return null;
        }

        return new PendingDetailDebugState(pending.AttemptCount, pending.NextAttemptAtUtc);
    }

    internal readonly record struct PendingDetailDebugState(
        int AttemptCount,
        DateTime NextAttemptAtUtc
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
