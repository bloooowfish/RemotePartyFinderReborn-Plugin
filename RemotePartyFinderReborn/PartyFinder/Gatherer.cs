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
    private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UploadedListingIndexTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UploadedListingGrace = TimeSpan.FromMilliseconds(250);

    private readonly Plugin _plugin;
    private readonly ConcurrentDictionary<int, ConcurrentQueue<IPartyFinderListing>> _batches = new();
    private readonly ConcurrentDictionary<uint, DateTime> _lastUploadedListingAtUtc = new();
    private readonly Stopwatch _uploadTimer = new();
    private readonly HttpClient _httpClient = new();

    private volatile bool _uploadInProgress;
    private long _lastSuccessfulUploadAtUtcTicks;
    private long _lastSuccessfulUploadAckVersion;

    internal long LastSuccessfulUploadAckVersion => Interlocked.Read(ref this._lastSuccessfulUploadAckVersion);
    internal DateTime LastSuccessfulUploadAtUtc => new(Interlocked.Read(ref this._lastSuccessfulUploadAtUtcTicks), DateTimeKind.Utc);
    internal int UploadedListingIndexCount => this._lastUploadedListingAtUtc.Count;

    internal Gatherer(Plugin plugin) {
        _plugin = plugin;

        _uploadTimer.Start();
        _plugin.PartyFinderGui.ReceiveListing += OnListing;
        _plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() {
        _plugin.Framework.Update -= OnUpdate;
        _plugin.PartyFinderGui.ReceiveListing -= OnListing;
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

    private void OnListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args) {
        if (!UploadableListing.IsSourceClientHighEndDuty(listing)) {
            return;
        }

        var queue = _batches.GetOrAdd(args.BatchNumber, static _ => new ConcurrentQueue<IPartyFinderListing>());
        queue.Enqueue(listing);
    }

    private void OnUpdate(IFramework framework) {
        if (_uploadInProgress) {
            return;
        }

        if (_uploadTimer.Elapsed < UploadInterval) {
            return;
        }

        _uploadTimer.Restart();
        _uploadInProgress = true;
        _ = ProcessPendingBatchesAsync();
    }

    private Task ProcessPendingBatchesAsync() {
        return Task.Run(async () => {
            try {
                var listingsToUpload = DrainBatches();
                if (listingsToUpload.Count == 0) {
                    return;
                }

                var payload = JsonConvert.SerializeObject(listingsToUpload);
                var uploadedListingIds = listingsToUpload.Select(static listing => listing.Id).Distinct().ToArray();
                var uploadSucceeded = await UploadBatchToEnabledEndpointsAsync(payload, listingsToUpload.Count);

                if (!uploadSucceeded) {
                    return;
                }

                var now = DateTime.UtcNow;
                foreach (var listingId in uploadedListingIds) {
                    _lastUploadedListingAtUtc[listingId] = now;
                }

                Interlocked.Exchange(ref _lastSuccessfulUploadAtUtcTicks, now.Ticks);
                Interlocked.Increment(ref _lastSuccessfulUploadAckVersion);
                PruneUploadIndex(now);
            } finally {
                _uploadInProgress = false;
            }
        });
    }

    private List<UploadableListing> DrainBatches() {
        var result = new List<UploadableListing>();

        foreach (var (batchId, _) in _batches.ToList()) {
            if (!_batches.TryRemove(batchId, out var queue) || queue == null) {
                continue;
            }

            while (queue.TryDequeue(out var listing)) {
                try {
                    var uploadableListing = new UploadableListing(listing);
                    if (ShouldUploadListing(uploadableListing)) {
                        result.Add(uploadableListing);
                    }
                } catch (ArgumentOutOfRangeException exception) {
                    Plugin.Log.Warning($"Gatherer: skipped listing with unsupported API15 id {listing.Id}. {exception.Message}");
                }
            }
        }

        return result;
    }

    private async Task<bool> UploadBatchToEnabledEndpointsAsync(string jsonPayload, int listingCount) {
        var anySuccess = false;

        foreach (var uploadUrl in _plugin.Configuration.UploadUrls.Where(static candidate => candidate.IsEnabled)) {
            if (UploadAttemptRecorder.IsCircuitOpen(uploadUrl, _plugin.Configuration, DateTime.UtcNow)) {
                continue;
            }

            if (!IngestEndpointResolver.TryBuildEndpoint(uploadUrl, IngestRoutes.ListingsMultiple, out var endpoint)) {
                continue;
            }
            var attempt = await TryUploadAsync(endpoint, jsonPayload);

            if (attempt.Success) {
                anySuccess = true;
                UploadAttemptRecorder.RecordSuccess(uploadUrl);
                IngestResponseParser.CaptureMultipleResponse(uploadUrl, attempt.ResponseBody);
            } else {
                UploadAttemptRecorder.RecordFailure(uploadUrl, DateTime.UtcNow);

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

        return anySuccess;
    }

    private async Task<UploadAttemptResult> TryUploadAsync(IngestEndpoint endpoint, string jsonPayload) {
        try {
            using var request = IngestRequestFactory.CreatePostJsonRequest(
                _plugin.Configuration,
                endpoint,
                jsonPayload
            );
            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            var retryAfterSeconds = (int)response.StatusCode == 429
                ? IngestRequestFactory.ReadRetryAfterSeconds(response)
                : null;

            return new UploadAttemptResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                retryAfterSeconds,
                null,
                responseBody
            );
        } catch (Exception ex) {
            return new UploadAttemptResult(false, null, null, ex.Message, string.Empty);
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
        string ResponseBody
    );
}
