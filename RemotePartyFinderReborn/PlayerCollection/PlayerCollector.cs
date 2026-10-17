#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    private readonly Plugin _plugin;
    private readonly PlayerLocalDatabase _database;
    private readonly HttpClient _httpClient = new();
    private readonly Stopwatch _scanTimer = new();
    private readonly PlayerObservationWriter _observationWriter;
    private readonly PlayerPendingCountCache _pendingCountCache;

    private volatile bool _uploadInProgress;

    internal int PendingCount => _pendingCountCache.Value;
    internal bool IsUploadInProgress => _uploadInProgress;

    internal PlayerCollector(Plugin plugin, PlayerLocalDatabase database) {
        _plugin = plugin;
        _database = database;
        _pendingCountCache = new PlayerPendingCountCache(() => _database.PendingCount);
        _observationWriter = new PlayerObservationWriter(database.UpsertObservedPlayers, onUpsertSucceeded: RefreshPendingCount);
        _ = Task.Run(RefreshPendingCount);
        _scanTimer.Start();
        _plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() {
        _plugin.Framework.Update -= OnUpdate;
        _observationWriter.Dispose();
        _httpClient.Dispose();
    }

    internal void TriggerManualUploadNow() {
        if (_uploadInProgress) {
            Plugin.Log.Warning($"PlayerCollector: Manual upload requested while upload is already in progress. Pending={PendingCount}");
            return;
        }

        _uploadInProgress = true;
        Plugin.Log.Information($"PlayerCollector: Manual upload requested. Pending={PendingCount}");
        _ = UploadPendingBatchAsync();
    }

    internal int TriggerManualFullResyncUploadNow() {
        var requeued = _database.MarkAllPlayersDirty();
        Plugin.Log.Information($"PlayerCollector: Manual full player-cache resync requested. Requeued={requeued} Pending={PendingCount}");
        _ = Task.Run(RefreshPendingCount);
        TriggerManualUploadNow();
        return requeued;
    }

    private void OnUpdate(IFramework framework) {
        if (_scanTimer.Elapsed < TimeSpan.FromSeconds(ScanIntervalSeconds)) {
            return;
        }

        _scanTimer.Restart();
        var observedPlayers = CollectObservedPlayers();
        if (observedPlayers.Count > 0) {
            _observationWriter.Enqueue(observedPlayers);
        }

        if (_uploadInProgress) {
            return;
        }

        if (PendingCount <= 0) {
            return;
        }

        _uploadInProgress = true;
        _ = UploadPendingBatchAsync();
    }

    private unsafe List<UploadablePlayer> CollectObservedPlayers() {
        var players = new List<UploadablePlayer>();
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

    private Task UploadPendingBatchAsync() {
        return Task.Run(async () => {
#if DEBUG
            var uploadStartedAt = DebugPerformanceDiagnostics.Begin();
            var uploadedUnits = 0;
#endif
            try {
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

                foreach (var uploadTarget in _plugin.Configuration.UploadUrls.Where(static candidate => candidate.IsEnabled)) {
                    if (UploadAttemptRecorder.IsCircuitOpen(uploadTarget, _plugin.Configuration, DateTime.UtcNow)) {
                        continue;
                    }

                    if (!IngestEndpointResolver.TryBuildEndpoint(uploadTarget, IngestRoutes.Players, out var endpoint)) {
                        continue;
                    }
                    try {
                        using var request = IngestRequestFactory.CreatePostJsonRequest(
                            _plugin.Configuration,
                            endpoint,
                            jsonPayload
                        );
                        var response = await _httpClient.SendAsync(request);
                        var responseBody = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode) {
                            anySuccess = true;
                            UploadAttemptRecorder.RecordSuccess(uploadTarget);
                            IngestResponseParser.CapturePlayersResponse(uploadTarget, responseBody);
                        } else {
                            UploadAttemptRecorder.RecordFailure(uploadTarget, DateTime.UtcNow);
                            if ((int)response.StatusCode == 429) {
                                var retryAfter = IngestRequestFactory.ReadRetryAfterSeconds(response);
                                if (retryAfter.HasValue) {
                                    Plugin.Log.Warning($"PlayerCollector: server rate limited {endpoint.Url}, retry_after={retryAfter.Value}s");
                                }
                            }
                        }

                        Plugin.Log.Debug($"PlayerCollector: {endpoint.Url}: {response.StatusCode} ({batch.Count} players)");
                    } catch (Exception exception) {
                        UploadAttemptRecorder.RecordFailure(uploadTarget, DateTime.UtcNow);
                        Plugin.Log.Error($"PlayerCollector upload error to {endpoint.Url}: {exception.Message}");
                    }
                }

                if (anySuccess) {
                    _database.MarkBatchUploaded(batch);
                    RefreshPendingCount();
                } else {
                    Plugin.Log.Warning($"PlayerCollector: Upload failed, {batch.Count} players remain pending. Total pending: {PendingCount}");
                }
            } catch (Exception exception) {
                Plugin.Log.Error($"PlayerCollector upload error: {exception.Message}");
            } finally {
#if DEBUG
                DebugPerformanceDiagnostics.RecordDuration(DebugPerformancePath.PlayerUpload, uploadStartedAt, uploadedUnits);
#endif
                _uploadInProgress = false;
            }
        });
    }

    private void RefreshPendingCount() {
        try {
            _pendingCountCache.Refresh();
        } catch (Exception exception) {
            Plugin.Log.Warning($"PlayerCollector: Could not refresh pending player count: {exception.Message}");
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
    private int _completed;
    private long _overflowDropsSinceWarning;
    private long _lastOverflowWarningUtcTicks;

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

    public void Dispose() {
        lock (_overflowLock) {
            if (_completed != 0) {
                return;
            }

            Volatile.Write(ref _completed, 1);
            _batches.Writer.TryComplete();
        }

        Signal();
        _worker.GetAwaiter().GetResult();
        _signal.Dispose();
    }

    private async Task ProcessAsync() {
        while (true) {
            await _signal.WaitAsync().ConfigureAwait(false);
            UploadablePlayer[]? retryBatch = null;

            while (true) {
                if (Volatile.Read(ref _completed) != 0) {
                    var pending = TakeAllPending(retryBatch);
                    if (pending.Length == 0) {
                        return;
                    }

                    var persisted = false;
                    for (var attempt = 1; attempt <= 3; attempt++) {
                        if (TryUpsert(pending)) {
                            persisted = true;
                            break;
                        }

                        if (attempt < 3) {
                            await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false);
                        }
                    }

                    if (!persisted) {
                        _warningSink($"PlayerCollector: Shutdown stopped after 3 failed database writes; dropped {pending.Length} pending player observations.");
                    }

                    return;
                }

                if (retryBatch is not null) {
                    if (TryUpsert(retryBatch)) {
                        retryBatch = null;
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    continue;
                }

                if (_batches.Reader.TryRead(out var batch)) {
                    if (!TryUpsert(batch)) {
                        retryBatch = batch;
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
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
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
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

    private UploadablePlayer[] TakeAllPending(UploadablePlayer[]? retryBatch) {
        var pendingByContentId = new Dictionary<ulong, UploadablePlayer>();
        if (retryBatch is not null) {
            foreach (var player in retryBatch) {
                pendingByContentId[player.ContentId] = player;
            }
        }

        while (_batches.Reader.TryRead(out var queuedBatch)) {
            foreach (var player in queuedBatch) {
                pendingByContentId[player.ContentId] = player;
            }
        }

        lock (_overflowLock) {
            foreach (var player in _coalescedOverflow.Values) {
                pendingByContentId[player.ContentId] = player;
            }

            _coalescedOverflow.Clear();
        }

        return pendingByContentId.Values.ToArray();
    }
}
