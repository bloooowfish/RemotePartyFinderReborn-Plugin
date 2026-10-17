using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class IdentityUploadPump {
    internal static readonly TimeSpan DefaultIdlePollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] DefaultRetryBackoffSchedule = [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
    ];

    private readonly PlayerLocalDatabase _database;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<IReadOnlyList<CharacterIdentityUploadPayload>, CancellationToken, Task<bool>> _uploadResolvedIdentitiesAsync;
    private readonly CancellationToken _disposeToken;
    private readonly Action<string>? _debugSink;
    private readonly Action<string>? _warningSink;
    private readonly TimeSpan[] _retryBackoffSchedule;
    private readonly TimeSpan _idlePollInterval;
    private readonly object _sync = new();
    private int _identityUploadWorkerBusy;
    private int _identityUploadFailureCount;
    private DateTime _nextIdentityUploadAttemptAtUtc = DateTime.MinValue;
    private DateTime _nextQueueCheckAtUtc = DateTime.MinValue;
    private long _queueWakeVersion;

    internal IdentityUploadPump(
        PlayerLocalDatabase database,
        Func<DateTime> utcNow,
        Func<IReadOnlyList<CharacterIdentityUploadPayload>, CancellationToken, Task<bool>> uploadResolvedIdentitiesAsync,
        CancellationToken disposeToken,
        Action<string>? debugSink = null,
        Action<string>? warningSink = null,
        IReadOnlyList<TimeSpan>? retryBackoffSchedule = null,
        TimeSpan? idlePollInterval = null
    ) {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _uploadResolvedIdentitiesAsync = uploadResolvedIdentitiesAsync ?? throw new ArgumentNullException(nameof(uploadResolvedIdentitiesAsync));
        _disposeToken = disposeToken;
        _debugSink = debugSink;
        _warningSink = warningSink;
        _retryBackoffSchedule = retryBackoffSchedule?.ToArray() ?? DefaultRetryBackoffSchedule;
        _idlePollInterval = idlePollInterval ?? DefaultIdlePollInterval;
    }

    internal bool IsWorkerBusy => Volatile.Read(ref _identityUploadWorkerBusy) != 0;

    internal int FailureCount {
        get {
            lock (_sync) {
                return _identityUploadFailureCount;
            }
        }
    }

    internal DateTime NextAttemptAtUtc {
        get {
            lock (_sync) {
                return _nextIdentityUploadAttemptAtUtc;
            }
        }
    }

    internal DateTime NextQueueCheckAtUtc {
        get {
            lock (_sync) {
                return _nextQueueCheckAtUtc;
            }
        }
    }

    internal void PumpPendingIdentityUploads() {
        var nowUtc = _utcNow();
        lock (_sync) {
            if (nowUtc < _nextIdentityUploadAttemptAtUtc || nowUtc < _nextQueueCheckAtUtc) {
                return;
            }
        }

        if (Interlocked.CompareExchange(ref _identityUploadWorkerBusy, 1, 0) != 0) {
            return;
        }

        var wakeVersion = Interlocked.Read(ref _queueWakeVersion);

        // The database probe and all later DB operations stay off the framework thread.
        // Do not pass the dispose token to Task.Run: if it is canceled before scheduling,
        // the delegate (and the busy-flag cleanup) would never execute.
        _ = Task.Run(async () => {
            var attempt = new UploadAttemptResult(false, false);
            try {
                attempt = await DrainPendingIdentityUploadsWithResultAsync(_disposeToken).ConfigureAwait(false);
            } catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested) {
            } catch (Exception exception) {
                _warningSink?.Invoke($"CharaCardResolver: identity upload worker failed. {exception.Message}");
            } finally {
                if (!_disposeToken.IsCancellationRequested && attempt.HasWork) {
                    RecordIdentityUploadAttempt(attempt.Succeeded, _utcNow());
                } else if (!_disposeToken.IsCancellationRequested) {
                    lock (_sync) {
                        _nextQueueCheckAtUtc = Interlocked.Read(ref _queueWakeVersion) == wakeVersion
                            ? _utcNow().Add(_idlePollInterval)
                            : DateTime.MinValue;
                    }
                }

                Interlocked.Exchange(ref _identityUploadWorkerBusy, 0);
            }
        });
    }

    internal void NotifyPendingIdentityChanged() {
        Interlocked.Increment(ref _queueWakeVersion);
        lock (_sync) {
            _nextQueueCheckAtUtc = DateTime.MinValue;
        }
    }

    internal async Task<bool> DrainPendingIdentityUploadsOnceAsync(CancellationToken cancellationToken) {
        var result = await DrainPendingIdentityUploadsWithResultAsync(cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task<UploadAttemptResult> DrainPendingIdentityUploadsWithResultAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

#if DEBUG
        var probeStartedAt = DebugPerformanceDiagnostics.Begin();
        var probeAllocatedBefore = DebugPerformanceDiagnostics.AllocatedBytes();
#endif
        var snapshots = _database.TakePendingIdentityUploads(PlayerLocalDatabase.MaxBatchSize);
#if DEBUG
        DebugPerformanceDiagnostics.Record(DebugPerformancePath.IdentityUploadDbProbe, probeStartedAt, probeAllocatedBefore, snapshots.Count);
#endif
        if (snapshots.Count == 0) {
            return new UploadAttemptResult(false, false);
        }

        var payloads = snapshots
            .Select(static snapshot => CharacterIdentityUploadPayload.FromSnapshot(snapshot, snapshot.LastResolvedAtUtc))
            .ToArray();

        bool uploadSucceeded;
        try {
            uploadSucceeded = await _uploadResolvedIdentitiesAsync(payloads, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposeToken.IsCancellationRequested) {
            return new UploadAttemptResult(true, false);
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to upload resolved identities. {exception.Message}");
            return new UploadAttemptResult(true, false);
        }

        if (!uploadSucceeded) {
            return new UploadAttemptResult(true, false);
        }

        try {
            _database.MarkIdentityUploadsSubmitted(payloads.Select(static payload => payload.ContentId));
            _debugSink?.Invoke($"CharaCardResolver: uploaded resolved identity batch count={payloads.Length}.");
            return new UploadAttemptResult(true, true);
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to mark identity uploads as submitted. {exception.Message}");
            return new UploadAttemptResult(true, false);
        }
    }

    private void RecordIdentityUploadAttempt(bool uploadSucceeded, DateTime observedAtUtc) {
        lock (_sync) {
            if (uploadSucceeded) {
                _identityUploadFailureCount = 0;
                _nextIdentityUploadAttemptAtUtc = DateTime.MinValue;
                _nextQueueCheckAtUtc = DateTime.MinValue;
                return;
            }

            var nextDelay = _retryBackoffSchedule[Math.Min(_identityUploadFailureCount, _retryBackoffSchedule.Length - 1)];
            _identityUploadFailureCount++;
            _nextIdentityUploadAttemptAtUtc = observedAtUtc.Add(nextDelay);
        }
    }

    private readonly record struct UploadAttemptResult(bool HasWork, bool Succeeded);
}
