#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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

    private readonly Plugin? _plugin;
    private readonly Configuration _configuration;
    private readonly object _batchesLock = new();
    private Dictionary<int, List<UploadableListing>> _batches = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _lastUploadedListingAtUtc = new();
    private readonly Stopwatch _uploadTimer = new();
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<bool> _isOperational;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _errorLog;
    private readonly Action<UploadablePlayer>? _onLeaderObserved;
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

    internal Gatherer(Plugin plugin)
        : this(
            plugin ?? throw new ArgumentNullException(nameof(plugin)),
            plugin.Configuration,
            httpClient: null,
            utcNow: null,
            isOperational: () => plugin.IsPluginOperational,
            debugLog: null,
            warningLog: null,
            errorLog: null,
            onLeaderObserved: player => plugin.PlayerCollector.EnqueueObservedPlayer(player)
        ) {
        _plugin!.PartyFinderGui.ReceiveListing += OnListing;
        _plugin.Framework.Update += OnUpdate;
    }

    internal Gatherer(
        Configuration configuration,
        HttpClient? httpClient = null,
        Func<DateTime>? utcNow = null,
        Func<bool>? isOperational = null,
        Action<string>? debugLog = null,
        Action<string>? warningLog = null,
        Action<string>? errorLog = null,
        Action<UploadablePlayer>? onLeaderObserved = null
    ) : this(
        plugin: null,
        configuration,
        httpClient,
        utcNow,
        isOperational,
        debugLog,
        warningLog,
        errorLog,
        onLeaderObserved
    ) {
    }

    private Gatherer(
        Plugin? plugin,
        Configuration configuration,
        HttpClient? httpClient,
        Func<DateTime>? utcNow,
        Func<bool>? isOperational,
        Action<string>? debugLog,
        Action<string>? warningLog,
        Action<string>? errorLog,
        Action<UploadablePlayer>? onLeaderObserved
    ) {
        _plugin = plugin;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? PluginHttpClientFactory.CreateIngestClient();
        _ownsHttpClient = httpClient is null;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _isOperational = isOperational ?? (static () => true);
        _debugLog = debugLog ?? (static message => Plugin.Log?.Debug(message));
        _warningLog = warningLog ?? (static message => Plugin.Log?.Warning(message));
        _errorLog = errorLog ?? (static message => Plugin.Log?.Error(message));
        _onLeaderObserved = onLeaderObserved;
        _uploadTimer.Start();
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
        Task activeTask;
        lock (_batchesLock) {
            activeTask = _activeUploadTask;
        }

        if (!activeTask.IsCompleted) {
            tasks.Add(activeTask);
        }
    }

    public void Dispose() {
        Dispose(ShutdownWaitTimeout);
    }

    internal void Dispose(TimeSpan waitTimeout) {
        if (_plugin is not null) {
            _plugin.Framework.Update -= OnUpdate;
            _plugin.PartyFinderGui.ReceiveListing -= OnListing;
        }

        BeginShutdown();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        Task activeTask;
        lock (_batchesLock) {
            activeTask = _activeUploadTask;
        }

        if (waitTimeout > TimeSpan.Zero && !activeTask.IsCompleted) {
            try {
                activeTask.Wait(waitTimeout);
            } catch (Exception) {
            }
        }

        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }

        _disposeCts.Dispose();
    }

    internal bool WasListingUploadedAfter(ulong listingId, DateTime observedAtUtc) {
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
        if (!_isOperational()) {
            ClearPendingBatches();
            return;
        }

        if (_onLeaderObserved is not null
            && TryBuildLeaderObservation(
                listing.ContentId,
                listing.Name?.TextValue,
                (ushort)listing.HomeWorld.RowId,
                (ushort)listing.CurrentWorld.RowId,
                out var leaderObservation)) {
            _onLeaderObserved(leaderObservation);
        }

        if (!UploadableListing.IsSourceClientHighEndDuty(listing)) {
            return;
        }

        // Snapshot Lumina World/Duty values synchronously on the framework main thread.
        UploadableListing uploadableListing;
        try {
            uploadableListing = new UploadableListing(listing);
        } catch (ArgumentOutOfRangeException exception) {
            _warningLog($"Gatherer: skipped listing with unsupported API15 id {listing.Id}. {exception.Message}");
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

    internal static bool TryBuildLeaderObservation(
        ulong contentId,
        string? name,
        ushort homeWorld,
        ushort currentWorld,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UploadablePlayer? player
    ) {
        if (contentId == 0 || contentId > long.MaxValue || homeWorld == 0 || homeWorld >= 1000 || string.IsNullOrWhiteSpace(name)) {
            player = null;
            return false;
        }

        var normalizedCurrentWorld = currentWorld > 0 && currentWorld < 1000 ? currentWorld : homeWorld;
        player = new UploadablePlayer {
            ContentId = contentId,
            Name = name.Trim(),
            HomeWorld = homeWorld,
            CurrentWorld = normalizedCurrentWorld,
            AccountId = 0,
        };
        return true;
    }

    internal void ForwardListingLeaderForTesting(ulong contentId, string? name, ushort homeWorld, ushort currentWorld = 0) {
        if (_onLeaderObserved is not null
            && TryBuildLeaderObservation(contentId, name, homeWorld, currentWorld, out var leader)) {
            _onLeaderObserved(leader);
        }
    }

    internal void EnqueueListingForTesting(UploadableListing listing, int batchNumber = 0) {
        ArgumentNullException.ThrowIfNull(listing);
        lock (_batchesLock) {
            if (!_batches.TryGetValue(batchNumber, out var list)) {
                list = [];
                _batches[batchNumber] = list;
            }

            list.Add(listing);
        }
    }

    internal Task TriggerImmediateUploadAndWaitForTestingAsync() {
        TriggerImmediateUpload();
        Task activeTask;
        lock (_batchesLock) {
            activeTask = _activeUploadTask;
        }

        return activeTask;
    }

    internal void TriggerImmediateUpload() {
        if (!_isOperational()) {
            ClearPendingBatches();
            return;
        }

        _uploadTimer.Restart();
        TryStartUploadWorker();
    }

    private void OnUpdate(IFramework framework) {
        if (!_isOperational()) {
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
        if (!UploadAttemptRecorder.HasAnyAttemptableEndpoint(_configuration, IngestRoutes.ListingsMultiple, _utcNow())) {
            return;
        }

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
                while (!cancellationToken.IsCancellationRequested && _isOperational()) {
                    if (!UploadAttemptRecorder.HasAnyAttemptableEndpoint(_configuration, IngestRoutes.ListingsMultiple, _utcNow())) {
                        _debugLog("Gatherer: skipping upload because all enabled endpoints are unavailable or have open circuits.");
                        return;
                    }

                    var listingsToUpload = DrainBatchesOrExitWorker();
                    if (listingsToUpload.Count == 0) {
                        exitedUnderLock = true;
                        return;
                    }

                    var payload = JsonConvert.SerializeObject(listingsToUpload);
                    var uploadedListingIds = listingsToUpload.Select(static listing => listing.Id).Distinct().ToArray();
                    var uploadSucceeded = await UploadBatchToEnabledEndpointsAsync(payload, listingsToUpload.Count, cancellationToken);

                    if (!uploadSucceeded) {
                        if (!cancellationToken.IsCancellationRequested && _isOperational()) {
                            RequeueFailedListings(listingsToUpload);
                        }

                        return;
                    }

                    var now = _utcNow();
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

        var byId = new Dictionary<ulong, UploadableListing>();
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

        foreach (var uploadUrl in _configuration.GetUploadUrlsSnapshot().Where(static candidate => candidate.IsEnabled)) {
            if (cancellationToken.IsCancellationRequested) {
                break;
            }

            if (!IngestEndpointResolver.TryBuildEndpoint(uploadUrl, IngestRoutes.ListingsMultiple, out var endpoint)) {
                continue;
            }

            if (!UploadAttemptRecorder.TryBeginAttempt(uploadUrl, _configuration, _utcNow(), out var attemptLease)) {
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
                } else if (attempt.StatusCode == (int)HttpStatusCode.NotFound) {
                    attemptLease.RecordRouteNotFoundFailure(_utcNow());
                    _warningLog($"Gatherer: {endpoint.Url} returned 404 NotFound; backing off for {UploadCircuitState.DefaultRouteNotFoundCooldown.TotalMinutes:F0} minutes.");
                } else {
                    attemptLease.RecordFailure(_utcNow());

                    if (!string.IsNullOrEmpty(attempt.ErrorMessage)) {
                        _errorLog($"Gatherer upload error to {endpoint.Url}: {attempt.ErrorMessage}");
                    }

                    if (attempt.RetryAfterSeconds.HasValue) {
                        _warningLog($"Gatherer: rate limited by {endpoint.Url}, retry_after={attempt.RetryAfterSeconds.Value}s");
                    }
                }

                var statusForLog = attempt.StatusCode?.ToString() ?? "Exception";
                _debugLog($"Gatherer: {endpoint.Url}: {statusForLog} ({listingCount} listings)");
            }
        }

        return anySuccess;
    }

    private async Task<UploadAttemptResult> TryUploadAsync(IngestEndpoint endpoint, string jsonPayload, CancellationToken cancellationToken) {
        try {
            using var request = IngestRequestFactory.CreatePostJsonRequest(
                _configuration,
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
        string? ErrorMessage,
        string ResponseBody,
        bool Canceled
    );
}
