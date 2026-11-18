using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn;

internal sealed class Gatherer : IDisposable {
    internal const int MaxRetainedListings = 200;
    private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UploadedListingIndexTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UploadedListingGrace = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly Plugin _plugin;
    private readonly object _batchesLock = new();
    private Dictionary<int, List<UploadableListing>> _batches = new();
    private readonly ConcurrentDictionary<uint, DateTime> _lastUploadedListingAtUtc = new();
    private readonly Stopwatch _uploadTimer = new();
    private readonly HttpClient _httpClient = PluginHttpClientFactory.CreateIngestClient();
    private readonly CancellationTokenSource _disposeCts = new();

    private int _uploadWorkerRunning;
    private Task _activeUploadTask = Task.CompletedTask;
    private int _shutdownSignaled;
    private int _disposed;
    private long _lastSuccessfulUploadAtUtcTicks;
    private long _lastSuccessfulUploadAckVersion;

    internal long LastSuccessfulUploadAckVersion => Interlocked.Read(ref this._lastSuccessfulUploadAckVersion);
    internal DateTime LastSuccessfulUploadAtUtc => new(Interlocked.Read(ref this._lastSuccessfulUploadAtUtcTicks), DateTimeKind.Utc);
    internal int UploadedListingIndexCount => this._lastUploadedListingAtUtc.Count;

    internal bool HasPendingUploadWork {
        get {
            lock (_batchesLock) {
                return _uploadWorkerRunning != 0 || _batches.Count > 0;
            }
        }
    }

    internal Gatherer(Plugin plugin) {
        _plugin = plugin;

        _uploadTimer.Start();
        _plugin.PartyFinderGui.ReceiveListing += OnListing;
        _plugin.Framework.Update += OnUpdate;
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

    public void Dispose() {
        _plugin.Framework.Update -= OnUpdate;
        _plugin.PartyFinderGui.ReceiveListing -= OnListing;
        BeginShutdown();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        Task activeTask;
        lock (_batchesLock) {
            activeTask = _activeUploadTask;
        }

        try {
            activeTask.Wait(ShutdownWaitTimeout);
        } catch (Exception) {
        }

        _httpClient.Dispose();
        _disposeCts.Dispose();
    }

    internal bool WasListingUploadedAfter(uint listingId, DateTime observedAtUtc) {
        if (!_lastUploadedListingAtUtc.TryGetValue(listingId, out var uploadedAtUtc)) {
            return false;
        }

        return uploadedAtUtc >= observedAtUtc - UploadedListingGrace;
    }

    internal static bool ShouldUploadListing(UploadableListing listing) {
        ArgumentNullException.ThrowIfNull(listing);
        return listing.ClientHighEndDuty;
    }

    internal void ClearPendingBatches() {
        lock (_batchesLock) {
            _batches.Clear();
        }
    }

    private void OnListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args) {
        if (!_plugin.IsPluginOperational) {
            ClearPendingBatches();
            return;
        }

        if (!UploadableListing.IsSourceClientHighEndDuty(listing)) {
            return;
        }

        // Snapshot Lumina World/Duty values synchronously on the framework main thread.
        UploadableListing uploadableListing;
        try {
            uploadableListing = new UploadableListing(listing);
        } catch (ArgumentOutOfRangeException exception) {
            Plugin.Log.Warning($"Gatherer: skipped listing with unsupported API15 id {listing.Id}. {exception.Message}");
            return;
        }

        if (!ShouldUploadListing(uploadableListing)) {
            return;
        }

        lock (_batchesLock) {
            if (!_batches.TryGetValue(args.BatchNumber, out var list)) {
                list = [];
                _batches[args.BatchNumber] = list;
            }

            list.Add(uploadableListing);
        }
    }

    internal void TriggerImmediateUpload() {
        if (!_plugin.IsPluginOperational) {
            ClearPendingBatches();
            return;
        }

        _uploadTimer.Restart();
        TryStartUploadWorker();
    }

    private void OnUpdate(IFramework framework) {
        if (!_plugin.IsPluginOperational) {
            ClearPendingBatches();
            return;
        }

        if (_uploadTimer.Elapsed < UploadInterval) {
            return;
        }

        _uploadTimer.Restart();
        TryStartUploadWorker();
    }

    private void TryStartUploadWorker() {
        CancellationToken cancellationToken;
        lock (_batchesLock) {
            if (_shutdownSignaled != 0 || _uploadWorkerRunning != 0 || _batches.Count == 0) {
                return;
            }

            cancellationToken = _disposeCts.Token;
            _uploadWorkerRunning = 1;
            _activeUploadTask = ProcessPendingBatchesAsync(cancellationToken);
        }
    }

    private Task ProcessPendingBatchesAsync(CancellationToken cancellationToken) {
        return Task.Run(async () => {
            var exitedUnderLock = false;
            try {
                while (!cancellationToken.IsCancellationRequested && _plugin.IsPluginOperational) {
                    var listingsToUpload = DrainBatchesOrExitWorker();
                    if (listingsToUpload.Count == 0) {
                        exitedUnderLock = true;
                        return;
                    }

                    var payload = JsonConvert.SerializeObject(listingsToUpload);
                    var uploadedListingIds = listingsToUpload.Select(static listing => listing.Id).Distinct().ToArray();
                    var uploadSucceeded = await UploadBatchToEnabledEndpointsAsync(payload, listingsToUpload.Count, cancellationToken);

                    if (!uploadSucceeded) {
                        if (!cancellationToken.IsCancellationRequested && _plugin.IsPluginOperational) {
                            RequeueFailedListings(listingsToUpload);
                        }

                        return;
                    }

                    var now = DateTime.UtcNow;
                    foreach (var listingId in uploadedListingIds) {
                        _lastUploadedListingAtUtc[listingId] = now;
                    }

                    Interlocked.Exchange(ref _lastSuccessfulUploadAtUtcTicks, now.Ticks);
                    Interlocked.Increment(ref _lastSuccessfulUploadAckVersion);
                    PruneUploadIndex(now);
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            } finally {
                if (!exitedUnderLock) {
                    lock (_batchesLock) {
                        _uploadWorkerRunning = 0;
                    }
                }
            }
        });
    }

    private void RequeueFailedListings(IReadOnlyList<UploadableListing> failedListings) {
        lock (_batchesLock) {
            if (_shutdownSignaled != 0) {
                return;
            }

            RetainFailedBatchListings(_batches, failedListings);
        }
    }

    internal static void RetainFailedBatchListings(
        Dictionary<int, List<UploadableListing>> batches,
        IReadOnlyList<UploadableListing> failedListings,
        int maxCount = MaxRetainedListings
    ) {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(failedListings);

        if (maxCount <= 0) {
            batches.Clear();
            return;
        }

        var byId = new Dictionary<uint, UploadableListing>();
        foreach (var listing in failedListings) {
            byId[listing.Id] = listing;
        }

        foreach (var (_, list) in batches) {
            foreach (var listing in list) {
                byId[listing.Id] = listing;
            }
        }

        batches.Clear();
        if (byId.Count == 0) {
            return;
        }

        batches[-1] = byId.Values
            .OrderByDescending(static listing => listing.Id)
            .Take(maxCount)
            .OrderBy(static listing => listing.Id)
            .ToList();
    }

    private List<UploadableListing> DrainBatchesOrExitWorker() {
        Dictionary<int, List<UploadableListing>> drained;
        lock (_batchesLock) {
            if (_batches.Count == 0) {
                _uploadWorkerRunning = 0;
                return [];
            }

            drained = _batches;
            _batches = new Dictionary<int, List<UploadableListing>>();
        }

        var result = new List<UploadableListing>();
        foreach (var (_, list) in drained) {
            result.AddRange(list);
        }

        return result;
    }

    private async Task<bool> UploadBatchToEnabledEndpointsAsync(string jsonPayload, int listingCount, CancellationToken cancellationToken) {
        var anySuccess = false;

        foreach (var uploadUrl in _plugin.Configuration.UploadUrls.Where(static candidate => candidate.IsEnabled)) {
            if (cancellationToken.IsCancellationRequested) {
                break;
            }

            if (!IngestEndpointResolver.TryBuildEndpoint(uploadUrl, IngestRoutes.ListingsMultiple, out var endpoint)) {
                continue;
            }

            if (!UploadAttemptRecorder.TryBeginAttempt(uploadUrl, _plugin.Configuration, DateTime.UtcNow, out var attemptLease)) {
                continue;
            }

            using (attemptLease) {
                var attempt = await TryUploadAsync(endpoint, jsonPayload, cancellationToken);

                if (attempt.Canceled) {
                    break;
                }

                if (attempt.Success) {
                    anySuccess = true;
                    attemptLease.RecordSuccess();
                    IngestResponseParser.CaptureMultipleResponse(uploadUrl, attempt.ResponseBody);
                } else {
                    attemptLease.RecordFailure(DateTime.UtcNow);

                    if (!string.IsNullOrEmpty(attempt.ErrorMessage)) {
                        Plugin.Log.Error($"Gatherer upload error to {endpoint.Url}: {attempt.ErrorMessage}");
                    }

                    if (attempt.RetryAfterSeconds.HasValue) {
                        Plugin.Log.Warning($"Gatherer: rate limited by {endpoint.Url}, retry_after={attempt.RetryAfterSeconds.Value}s");
                    }
                }

                var statusForLog = attempt.StatusCode?.ToString() ?? "Exception";
                Plugin.Log.Debug($"Gatherer: {endpoint.Url}: {statusForLog} ({listingCount} listings)");
            }
        }

        return anySuccess;
    }

    private async Task<UploadAttemptResult> TryUploadAsync(IngestEndpoint endpoint, string jsonPayload, CancellationToken cancellationToken) {
        try {
            using var request = IngestRequestFactory.CreatePostJsonRequest(
                _plugin.Configuration,
                endpoint,
                jsonPayload
            );
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            var retryAfterSeconds = (int)response.StatusCode == 429
                ? IngestRequestFactory.ReadRetryAfterSeconds(response)
                : null;

            return new UploadAttemptResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                retryAfterSeconds,
                null,
                responseBody,
                false
            );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return new UploadAttemptResult(false, null, null, null, string.Empty, true);
        } catch (Exception ex) {
            return new UploadAttemptResult(false, null, null, ex.Message, string.Empty, false);
        }
    }

    private void PruneUploadIndex(DateTime now) {
        var cutoff = now - UploadedListingIndexTtl;
        foreach (var (listingId, uploadedAtUtc) in _lastUploadedListingAtUtc) {
            if (uploadedAtUtc >= cutoff) {
                continue;
            }

            _lastUploadedListingAtUtc.TryRemove(listingId, out _);
        }
    }

    private readonly record struct UploadAttemptResult(
        bool Success,
        int? StatusCode,
        int? RetryAfterSeconds,
        string ErrorMessage,
        string ResponseBody,
        bool Canceled
    );
}
