#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RemotePartyFinderReborn;

internal sealed class PlayerCollector : IDisposable {
    private const int ScanIntervalSeconds = 5;
    private static readonly JsonSerializerSettings UploadPayloadSerializerSettings = new() {
        ContractResolver = new DefaultContractResolver {
            NamingStrategy = new SnakeCaseNamingStrategy()
        },
        DefaultValueHandling = DefaultValueHandling.Include
    };

    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly Plugin? _plugin;
    private readonly Configuration _configuration;
    private readonly PlayerLocalDatabase _database;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<bool> _isOperational;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _infoLog;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _errorLog;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Stopwatch _scanTimer = new();
    private readonly PlayerObservationWriter _observationWriter;
    private readonly PlayerPendingCountCache _pendingCountCache;
    private readonly object _taskLock = new();
    private readonly Task _initialRefreshTask;

    private Task _activeUploadTask = Task.CompletedTask;
    private int _uploadState;
    private int _shutdownSignaled;
    private int _disposed;

    internal int PendingCount => _pendingCountCache.Value;
    internal bool IsUploadInProgress => Volatile.Read(ref _uploadState) != 0;

    internal PlayerCollector(Plugin plugin, PlayerLocalDatabase database)
        : this(
            plugin ?? throw new ArgumentNullException(nameof(plugin)),
            plugin.Configuration,
            database,
            httpClient: null,
            utcNow: null,
            isOperational: () => plugin.IsPluginOperational,
            infoLog: null,
            debugLog: null,
            warningLog: null,
            errorLog: null
        ) {
        _plugin!.Framework.Update += OnUpdate;
    }

    internal PlayerCollector(
        Configuration configuration,
        PlayerLocalDatabase database,
        HttpClient? httpClient = null,
        Func<DateTime>? utcNow = null,
        Func<bool>? isOperational = null,
        Action<string>? infoLog = null,
        Action<string>? debugLog = null,
        Action<string>? warningLog = null,
        Action<string>? errorLog = null
    ) : this(
        plugin: null,
        configuration,
        database,
        httpClient,
        utcNow,
        isOperational,
        infoLog,
        debugLog,
        warningLog,
        errorLog
    ) {
    }

    private PlayerCollector(
        Plugin? plugin,
        Configuration configuration,
        PlayerLocalDatabase database,
        HttpClient? httpClient,
        Func<DateTime>? utcNow,
        Func<bool>? isOperational,
        Action<string>? infoLog,
        Action<string>? debugLog,
        Action<string>? warningLog,
        Action<string>? errorLog
    ) {
        _plugin = plugin;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _httpClient = httpClient ?? PluginHttpClientFactory.CreateIngestClient();
        _ownsHttpClient = httpClient is null;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _isOperational = isOperational ?? (static () => true);
        _infoLog = infoLog ?? (static message => Plugin.Log?.Information(message));
        _debugLog = debugLog ?? (static message => Plugin.Log?.Debug(message));
        _warningLog = warningLog ?? (static message => Plugin.Log?.Warning(message));
        _errorLog = errorLog ?? (static message => Plugin.Log?.Error(message));
        _pendingCountCache = new PlayerPendingCountCache(() => _database.PendingCount);
        _observationWriter = new PlayerObservationWriter(
            database.UpsertObservedPlayers,
            warningSink: _warningLog,
            errorSink: _errorLog,
            onUpsertSucceeded: RefreshPendingCount
        );
        var disposeToken = _disposeCts.Token;
        _initialRefreshTask = Task.Run(() => {
            if (!disposeToken.IsCancellationRequested) {
                RefreshPendingCount();
            }
        });
        _scanTimer.Start();
    }

    internal void BeginShutdown() {
        if (Interlocked.Exchange(ref _shutdownSignaled, 1) != 0) {
            return;
        }

        _observationWriter.BeginShutdown();
        try {
            _disposeCts.Cancel();
        } catch (ObjectDisposedException) {
        }
    }

    internal void CollectShutdownTasks(List<Task> tasks) {
        ArgumentNullException.ThrowIfNull(tasks);
        _observationWriter.BeginShutdown();

        Task activeUploadTask;
        lock (_taskLock) {
            activeUploadTask = _activeUploadTask;
        }

        if (!activeUploadTask.IsCompleted) {
            tasks.Add(activeUploadTask);
        }

        if (!_initialRefreshTask.IsCompleted) {
            tasks.Add(_initialRefreshTask);
        }

        if (!_observationWriter.WorkerTask.IsCompleted) {
            tasks.Add(_observationWriter.WorkerTask);
        }
    }

    public void Dispose() {
        Dispose(ShutdownWaitTimeout);
    }

    internal void Dispose(TimeSpan waitTimeout) {
        if (_plugin is not null) {
            _plugin.Framework.Update -= OnUpdate;
        }

        BeginShutdown();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        _observationWriter.Dispose(waitTimeout);

        Task activeUploadTask;
        lock (_taskLock) {
            activeUploadTask = _activeUploadTask;
        }

        if (waitTimeout > TimeSpan.Zero) {
            try {
                Task.WhenAll(activeUploadTask, _initialRefreshTask).Wait(waitTimeout);
            } catch (Exception) {
            }
        }

        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }

        _disposeCts.Dispose();
    }

    internal void EnqueueObservedPlayer(UploadablePlayer player) {
        ArgumentNullException.ThrowIfNull(player);
        if (!_isOperational() || _shutdownSignaled != 0 || Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _observationWriter.Enqueue([player]);
    }

    internal void TriggerManualUploadNow() {
        if (!_isOperational() || _shutdownSignaled != 0) {
            return;
        }

        if (!TryStartUpload(out var cancellationToken)) {
            _warningLog($"PlayerCollector: Manual upload requested while upload is already in progress. Pending={PendingCount}");
            return;
        }

        _infoLog($"PlayerCollector: Manual upload requested. Pending={PendingCount}");
        TrackUploadTask(UploadPendingBatchAsync(cancellationToken));
    }

    internal int TriggerManualFullResyncUploadNow() {
        if (!_isOperational() || _shutdownSignaled != 0) {
            return 0;
        }

        var requeued = _database.MarkAllPlayersDirty();
        RefreshPendingCount();
        _infoLog($"PlayerCollector: Manual full player-cache resync requested. Requeued={requeued} Pending={PendingCount}");
        TriggerManualUploadNow();
        return requeued;
    }

    internal Task UploadPendingBatchForTestingAsync(CancellationToken cancellationToken = default) {
        return UploadPendingBatchAsync(cancellationToken);
    }

    private void OnUpdate(IFramework framework) {
        if (!_isOperational() || _shutdownSignaled != 0) {
            return;
        }

        if (_scanTimer.Elapsed < TimeSpan.FromSeconds(ScanIntervalSeconds)) {
            return;
        }

        _scanTimer.Restart();
        var observedPlayers = CollectObservedPlayers();
        if (observedPlayers.Count > 0) {
            _observationWriter.Enqueue(observedPlayers);
        }

        if (PendingCount <= 0) {
            return;
        }

        if (!UploadAttemptRecorder.HasAnyAttemptableEndpoint(_configuration, IngestRoutes.Players, _utcNow())) {
            return;
        }

        if (TryStartUpload(out var cancellationToken)) {
            TrackUploadTask(UploadPendingBatchAsync(cancellationToken));
        }
    }

    private bool TryStartUpload(out CancellationToken cancellationToken) {
        cancellationToken = CancellationToken.None;
        lock (_taskLock) {
            if (_shutdownSignaled != 0) {
                return false;
            }

            if (Interlocked.CompareExchange(ref _uploadState, 1, 0) != 0) {
                return false;
            }

            try {
                cancellationToken = _disposeCts.Token;
                return !cancellationToken.IsCancellationRequested;
            } catch (ObjectDisposedException) {
                Interlocked.Exchange(ref _uploadState, 0);
                return false;
            }
        }
    }

    private void TrackUploadTask(Task uploadTask) {
        lock (_taskLock) {
            _activeUploadTask = uploadTask;
        }
    }

    private unsafe List<UploadablePlayer> CollectObservedPlayers() {
        var players = new List<UploadablePlayer>();
        if (_plugin is null) {
            return players;
        }

#if DEBUG
        var scanStartedAt = DebugPerformanceDiagnostics.Begin();
        var scanAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
        try {
#endif
        foreach (var gameObject in _plugin.ObjectTable) {
            if (gameObject.ObjectKind != ObjectKind.Pc) {
                continue;
            }

            if (gameObject is not IPlayerCharacter character) {
                continue;
            }

            var player = (BattleChara*)gameObject.Address;
            var contentId = player->ContentId;
            var accountId = player->AccountId;
            var homeWorld = (ushort)character.HomeWorld.RowId;
            var currentWorld = (ushort)character.CurrentWorld.RowId;
            var name = gameObject.Name.TextValue;

            if (contentId == 0 || homeWorld == 0 || homeWorld >= 1000 || string.IsNullOrEmpty(name)) {
                continue;
            }

            players.Add(new UploadablePlayer {
                ContentId = contentId,
                Name = name,
                HomeWorld = homeWorld,
                CurrentWorld = currentWorld,
                AccountId = accountId,
            });
        }

        return players;
#if DEBUG
        } finally {
            DebugPerformanceDiagnostics.Record(DebugPerformancePath.PlayerObjectScan, scanStartedAt, scanAllocatedBefore, players.Count);
        }
#endif
    }

    private Task UploadPendingBatchAsync(CancellationToken cancellationToken) {
        return Task.Run(async () => {
#if DEBUG
            var uploadStartedAt = DebugPerformanceDiagnostics.Begin();
            var uploadedUnits = 0;
#endif
            try {
                if (cancellationToken.IsCancellationRequested || !_isOperational()) {
                    return;
                }

                if (!UploadAttemptRecorder.HasAnyAttemptableEndpoint(_configuration, IngestRoutes.Players, _utcNow())) {
                    _debugLog("PlayerCollector: skipping upload because all enabled endpoints are unavailable or have open circuits.");
                    return;
                }

                var batch = _database.TakePendingBatch();
#if DEBUG
                uploadedUnits = batch.Count;
#endif
                RefreshPendingCount();
                if (batch.Count == 0) {
                    return;
                }

                var jsonPayload = JsonConvert.SerializeObject(batch, UploadPayloadSerializerSettings);
                var anySuccess = false;
                var attemptedAnyEndpoint = false;
                var hadNon404Failure = false;

                foreach (var uploadTarget in _configuration.GetUploadUrlsSnapshot().Where(static candidate => candidate.IsEnabled)) {
                    if (cancellationToken.IsCancellationRequested) {
                        break;
                    }

                    if (!IngestEndpointResolver.TryBuildEndpoint(uploadTarget, IngestRoutes.Players, out var endpoint)) {
                        continue;
                    }

                    if (!UploadAttemptRecorder.TryBeginAttempt(uploadTarget, _configuration, _utcNow(), out var attemptLease)) {
                        continue;
                    }

                    attemptedAnyEndpoint = true;
                    using (attemptLease) {
                        try {
                            using var request = IngestRequestFactory.CreatePostJsonRequest(
                                _configuration,
                                endpoint,
                                jsonPayload
                            );
                            using var response = await _httpClient.SendAsync(request, cancellationToken);
                            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                            if (response.IsSuccessStatusCode) {
                                anySuccess = true;
                                attemptLease.RecordSuccess();
                                IngestResponseParser.CapturePlayersResponse(uploadTarget, responseBody);
                            } else if (response.StatusCode == HttpStatusCode.NotFound) {
                                attemptLease.RecordRouteNotFoundFailure(_utcNow());
                                _warningLog($"PlayerCollector: {endpoint.Url} returned 404 NotFound; backing off for {UploadCircuitState.DefaultRouteNotFoundCooldown.TotalMinutes:F0} minutes.");
                            } else {
                                hadNon404Failure = true;
                                attemptLease.RecordFailure(_utcNow());
                                if ((int)response.StatusCode == 429) {
                                    var retryAfter = IngestRequestFactory.ReadRetryAfterSeconds(response);
                                    if (retryAfter.HasValue) {
                                        _warningLog($"PlayerCollector: server rate limited {endpoint.Url}, retry_after={retryAfter.Value}s");
                                    }
                                }
                            }

                            _debugLog($"PlayerCollector: {endpoint.Url}: {response.StatusCode} ({batch.Count} players)");
                        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                            break;
                        } catch (Exception exception) {
                            hadNon404Failure = true;
                            attemptLease.RecordFailure(_utcNow());
                            _errorLog($"PlayerCollector upload error to {endpoint.Url}: {exception.Message}");
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested) {
                    return;
                }

                if (anySuccess) {
                    _database.MarkBatchUploaded(batch);
                    RefreshPendingCount();
                } else if (!attemptedAnyEndpoint) {
                    _debugLog($"PlayerCollector: skipped upload because all enabled circuits are open. Pending={PendingCount}");
                } else if (hadNon404Failure) {
                    _warningLog($"PlayerCollector: Upload failed, {batch.Count} players remain pending. Total pending: {PendingCount}");
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            } catch (Exception exception) {
                _errorLog($"PlayerCollector upload error: {exception.Message}");
            } finally {
#if DEBUG
                DebugPerformanceDiagnostics.RecordDuration(DebugPerformancePath.PlayerUpload, uploadStartedAt, uploadedUnits);
#endif
                Interlocked.Exchange(ref _uploadState, 0);
            }
        });
    }

    private void RefreshPendingCount() {
        try {
            _pendingCountCache.Refresh();
        } catch (Exception exception) {
            _warningLog($"PlayerCollector: Could not refresh pending player count: {exception.Message}");
        }
    }

}

internal sealed class PlayerPendingCountCache {
    private readonly Func<int> _readPendingCount;
    private int _value;

    internal PlayerPendingCountCache(Func<int> readPendingCount) {
        _readPendingCount = readPendingCount ?? throw new ArgumentNullException(nameof(readPendingCount));
    }

    internal int Value => Volatile.Read(ref _value);

    internal void Refresh() => Volatile.Write(ref _value, Math.Max(0, _readPendingCount()));
}

/// <summary>
/// Moves synchronous SQLite upserts off the Framework.Update thread. A one-batch
/// channel bounds queued batches; overflow is coalesced by content ID up to a
/// fixed limit. New unique observations beyond that limit are counted and logged.
/// </summary>
internal sealed class PlayerObservationWriter : IDisposable {
    internal const int MaxCoalescedOverflowPlayers = 10_000;
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly Channel<UploadablePlayer[]> _batches = Channel.CreateBounded<UploadablePlayer[]>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _overflowLock = new();
    private readonly Dictionary<ulong, UploadablePlayer> _coalescedOverflow = new();
    private readonly Action<IReadOnlyCollection<UploadablePlayer>> _upsert;
    private readonly Action<string> _warningSink;
    private readonly Action<string> _errorSink;
    private readonly Action _onUpsertSucceeded;
    private readonly Task _worker;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _completed;
    private int _disposed;
    private long _overflowDropsSinceWarning;
    private long _lastOverflowWarningUtcTicks;

    internal Task WorkerTask => _worker;

    internal PlayerObservationWriter(
        Action<IReadOnlyCollection<UploadablePlayer>> upsert,
        Action<string>? warningSink = null,
        Action<string>? errorSink = null,
        Action? onUpsertSucceeded = null) {
        _upsert = upsert ?? throw new ArgumentNullException(nameof(upsert));
        _warningSink = warningSink ?? (message => Plugin.Log?.Warning(message));
        _errorSink = errorSink ?? (message => Plugin.Log?.Error(message));
        _onUpsertSucceeded = onUpsertSucceeded ?? (() => { });
        _worker = Task.Run(ProcessAsync);
    }

    internal void Enqueue(IEnumerable<UploadablePlayer> observations) {
        ArgumentNullException.ThrowIfNull(observations);
        var batch = observations as UploadablePlayer[] ?? observations.ToArray();
        if (batch.Length == 0) {
            return;
        }

        lock (_overflowLock) {
            if (_completed != 0) {
                return;
            }

            if (_coalescedOverflow.Count > 0 || !_batches.Writer.TryWrite(batch)) {
                CoalesceOverflow(batch);
            }
        }

        Signal();
    }

    internal void BeginShutdown() {
        lock (_overflowLock) {
            if (_completed != 0) {
                return;
            }

            Volatile.Write(ref _completed, 1);
            _batches.Writer.TryComplete();
        }

        try {
            _shutdownCts.Cancel();
        } catch (ObjectDisposedException) {
        }

        Signal();
    }

    public void Dispose() {
        Dispose(ShutdownWaitTimeout);
    }

    internal void Dispose(TimeSpan waitTimeout) {
        BeginShutdown();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        var completedInTime = _worker.IsCompleted;
        if (!completedInTime && waitTimeout > TimeSpan.Zero) {
            try {
                completedInTime = _worker.Wait(waitTimeout);
            } catch (Exception) {
                completedInTime = true;
            }
        }

        if (completedInTime) {
            _shutdownCts.Dispose();
            _signal.Dispose();
        } else {
            _warningSink($"PlayerCollector: observation writer did not finish within {waitTimeout.TotalSeconds:F1}s during shutdown.");
        }
    }

    private async Task ProcessAsync() {
        while (true) {
            try {
                await _signal.WaitAsync().ConfigureAwait(false);
            } catch (ObjectDisposedException) {
                return;
            }

            UploadablePlayer[]? retryBatch = null;

            while (true) {
                if (Volatile.Read(ref _completed) != 0) {
                    if (!DrainPendingOnShutdown(retryBatch)) {
                        return;
                    }

                    return;
                }

                if (retryBatch is not null) {
                    if (TryUpsert(retryBatch)) {
                        retryBatch = null;
                        continue;
                    }

                    try {
                        await Task.Delay(TimeSpan.FromSeconds(1), _shutdownCts.Token).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        // Shutdown requested; proceed immediately to shutdown drain.
                    }
                    continue;
                }

                if (_batches.Reader.TryRead(out var batch)) {
                    if (!TryUpsert(batch)) {
                        retryBatch = batch;
                        try {
                            await Task.Delay(TimeSpan.FromSeconds(1), _shutdownCts.Token).ConfigureAwait(false);
                        } catch (OperationCanceledException) {
                            // Shutdown requested; proceed immediately to shutdown drain.
                        }
                    }
                    continue;
                }

                UploadablePlayer[] overflow;
                lock (_overflowLock) {
                    overflow = _coalescedOverflow.Values.ToArray();
                    _coalescedOverflow.Clear();
                }

                if (overflow.Length > 0) {
                    if (!TryUpsert(overflow)) {
                        retryBatch = overflow;
                        try {
                            await Task.Delay(TimeSpan.FromSeconds(1), _shutdownCts.Token).ConfigureAwait(false);
                        } catch (OperationCanceledException) {
                            // Shutdown requested; proceed immediately to shutdown drain.
                        }
                    }
                    continue;
                }

                if (Volatile.Read(ref _completed) != 0 && !_batches.Reader.TryPeek(out _)) {
                    return;
                }

                break;
            }
        }
    }

    private void Signal() {
        try {
            _signal.Release();
        } catch (SemaphoreFullException) {
            // A wake-up is already pending; the worker drains all queued data.
        }
    }

    private bool TryUpsert(UploadablePlayer[] batch) {
        try {
#if DEBUG
            var writeStartedAt = DebugPerformanceDiagnostics.Begin();
            var writeAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
            try {
#endif
            _upsert(batch);
#if DEBUG
            } finally {
                DebugPerformanceDiagnostics.Record(DebugPerformancePath.PlayerObservationDbWrite, writeStartedAt, writeAllocatedBefore);
            }
#endif
            _onUpsertSucceeded();
            return true;
        } catch (Exception exception) {
            _errorSink($"PlayerCollector: Could not persist {batch.Length} player observations; queued for retry. {exception.Message}");
            return false;
        }
    }

    private void CoalesceOverflow(IEnumerable<UploadablePlayer> batch) {
        var dropped = 0;
        foreach (var player in batch) {
            if (_coalescedOverflow.ContainsKey(player.ContentId)) {
                _coalescedOverflow[player.ContentId] = player;
            } else if (_coalescedOverflow.Count < MaxCoalescedOverflowPlayers) {
                _coalescedOverflow.Add(player.ContentId, player);
            } else {
                dropped++;
            }
        }

        if (dropped == 0) {
            return;
        }

        Interlocked.Add(ref _overflowDropsSinceWarning, dropped);
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastWarningTicks = Interlocked.Read(ref _lastOverflowWarningUtcTicks);
        if (nowTicks - lastWarningTicks < TimeSpan.FromMinutes(1).Ticks
            || Interlocked.CompareExchange(ref _lastOverflowWarningUtcTicks, nowTicks, lastWarningTicks) != lastWarningTicks) {
            return;
        }

        var droppedSinceWarning = Interlocked.Exchange(ref _overflowDropsSinceWarning, 0);
        _warningSink($"PlayerCollector: Coalesced observation buffer reached {MaxCoalescedOverflowPlayers} unique players; dropped {droppedSinceWarning} new unique observations. Existing IDs continue to coalesce.");
    }

    private bool DrainPendingOnShutdown(UploadablePlayer[]? retryBatch) {
        if (retryBatch is { Length: > 0 } && !TryPersistShutdownBatch(retryBatch)) {
            return false;
        }

        while (_batches.Reader.TryRead(out var queuedBatch)) {
            if (queuedBatch.Length > 0 && !TryPersistShutdownBatch(queuedBatch)) {
                return false;
            }
        }

        UploadablePlayer[] overflow;
        lock (_overflowLock) {
            overflow = _coalescedOverflow.Values.ToArray();
            _coalescedOverflow.Clear();
        }

        return overflow.Length == 0 || TryPersistShutdownBatch(overflow);
    }

    private bool TryPersistShutdownBatch(UploadablePlayer[] pending) {
        for (var attempt = 1; attempt <= 3; attempt++) {
            if (TryUpsert(pending)) {
                return true;
            }
        }

        _warningSink($"PlayerCollector: Shutdown stopped after 3 failed database writes; dropped {pending.Length} pending player observations.");
        return false;
    }
}
