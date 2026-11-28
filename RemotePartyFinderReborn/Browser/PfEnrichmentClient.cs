#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RemotePartyFinderReborn;

internal sealed record PfKnownMemberHint(ulong ContentId, string Name, ushort HomeWorld);

internal sealed record CachedLeaderEnrichment(
    uint ZoneId,
    int DifficultyId,
    PfParseDisplay Parse,
    PfProgressDisplay Progress,
    int CachedMemberCount,
    DateTime ExpiresAtUtc);

internal sealed record CachedMemberEnrichment(
    string Name,
    ushort HomeWorldId,
    PfParseDisplay Parse,
    PfProgressDisplay Progress,
    PfMemberEnrichmentState State,
    DateTime ExpiresAtUtc);

/// <summary>
/// Thread-safe client for /api/plugin/listings-enrichment and /api/plugin/party-enrichment
/// with TTL caching, debounced batching, self-lease execution, and bounded loading backoff polling.
/// </summary>
internal sealed class PfEnrichmentClient : IDisposable
{
    internal const int MaxListingsBatchSize = 50;
    internal const int MaxLoadingPollAttempts = 3;

    private static readonly TimeSpan ListingsDebounceDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LeaderCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MemberCachedTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MemberLoadingTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MemberUnresolvedTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MemberNoneTtl = TimeSpan.FromMinutes(2);
    private static readonly int[] LoadingBackoffScheduleMs = [2000, 3000, 5000];

    private readonly record struct LeaderSummaryCacheKey(ulong ListingId, ushort RawDuty, ulong LeaderContentId);
    private readonly record struct MemberEnrichmentCacheKey(ulong ContentId, ushort RawDuty);

    private sealed class HttpClientIngestHttpSender(HttpClient httpClient) : IIngestHttpSender
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => httpClient.SendAsync(request, cancellationToken);
    }

    private readonly Configuration _configuration;
    private readonly ProtectedIngestEndpointExecutor _protectedExecutor;
    private readonly IFFLogsWorkerRegionProvider? _workerRegionProvider;
    private readonly Func<bool> _isPluginOperational;
    private readonly Func<bool> _isFFLogsSelfLeaseAvailable;
    private readonly Func<UploadUrl, IReadOnlyList<ParseJob>, CancellationToken, Task<bool>>? _executeFFLogsSelfLeaseAsync;
    private readonly Func<bool> _isTomestoneSelfLeaseAvailable;
    private readonly Func<UploadUrl, IReadOnlyList<Tomestone.TomestoneJob>, CancellationToken, Task<bool>>? _executeTomestoneSelfLeaseAsync;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _debugLog;
    private readonly HttpClient? _ownedHttpClient;

    private readonly object _cacheGate = new();
    private readonly Dictionary<LeaderSummaryCacheKey, CachedLeaderEnrichment> _leaderCache = new();
    private readonly Dictionary<MemberEnrichmentCacheKey, CachedMemberEnrichment> _memberCache = new();

    private readonly object _listingsGate = new();
    private readonly HashSet<LeaderSummaryCacheKey> _inFlightListingsKeys = new();
    private List<ListingsEnrichmentItemDto> _pendingListingsItems = [];
    private HashSet<LeaderSummaryCacheKey> _pendingListingsKeySet = new();
    private CancellationTokenSource? _listingsDebounceCts;
    private Task _currentListingsTask = Task.CompletedTask;

    private readonly object _detailGate = new();
    private ulong _activeViewedListingId;
    private ulong _activeDetailListingId;
    private string _activeDetailMemberSignature = string.Empty;
    private CancellationTokenSource? _activeDetailCts;
    private Task _activeDetailTask = Task.CompletedTask;

    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    internal PfEnrichmentClient(
        Configuration configuration,
        IIngestHttpSender? httpSender = null,
        IFFLogsWorkerRegionProvider? workerRegionProvider = null,
        Func<bool>? isPluginOperational = null,
        Func<bool>? isFFLogsSelfLeaseAvailable = null,
        Func<UploadUrl, IReadOnlyList<ParseJob>, CancellationToken, Task<bool>>? executeFFLogsSelfLeaseAsync = null,
        Func<bool>? isTomestoneSelfLeaseAvailable = null,
        Func<UploadUrl, IReadOnlyList<Tomestone.TomestoneJob>, CancellationToken, Task<bool>>? executeTomestoneSelfLeaseAsync = null,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<string>? warningLog = null,
        Action<string>? debugLog = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workerRegionProvider = workerRegionProvider;
        _isPluginOperational = isPluginOperational ?? (static () => true);
        _isFFLogsSelfLeaseAvailable = isFFLogsSelfLeaseAvailable ?? (static () => false);
        _executeFFLogsSelfLeaseAsync = executeFFLogsSelfLeaseAsync;
        _isTomestoneSelfLeaseAvailable = isTomestoneSelfLeaseAvailable ?? (static () => false);
        _executeTomestoneSelfLeaseAsync = executeTomestoneSelfLeaseAsync;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _delayAsync = delayAsync ?? Task.Delay;
        _warningLog = warningLog ?? (static _ => { });
        _debugLog = debugLog ?? (static _ => { });

        if (httpSender == null)
        {
            _ownedHttpClient = PluginHttpClientFactory.CreateIngestClient();
            httpSender = new HttpClientIngestHttpSender(_ownedHttpClient);
        }

        _protectedExecutor = new ProtectedIngestEndpointExecutor(httpSender, _utcNow);
    }

    internal ulong ActiveViewedListingId
    {
        get
        {
            lock (_detailGate)
            {
                return _activeViewedListingId;
            }
        }
    }

    internal bool TryGetLeaderSummary(
        ulong listingId,
        ushort rawDuty,
        ulong leaderContentId,
        out CachedLeaderEnrichment summary)
    {
        if (listingId == 0 || rawDuty == 0 || leaderContentId == 0)
        {
            summary = default!;
            return false;
        }

        var key = new LeaderSummaryCacheKey(listingId, rawDuty, leaderContentId);
        var now = _utcNow();
        lock (_cacheGate)
        {
            if (_leaderCache.TryGetValue(key, out var cached))
            {
                if (now < cached.ExpiresAtUtc)
                {
                    summary = cached;
                    return true;
                }

                _leaderCache.Remove(key);
            }
        }

        summary = default!;
        return false;
    }

    internal bool TryGetMemberEnrichment(
        ulong contentId,
        ushort rawDuty,
        out CachedMemberEnrichment member)
    {
        if (contentId == 0 || rawDuty == 0)
        {
            member = default!;
            return false;
        }

        var key = new MemberEnrichmentCacheKey(contentId, rawDuty);
        var now = _utcNow();
        lock (_cacheGate)
        {
            if (_memberCache.TryGetValue(key, out var cached))
            {
                if (now < cached.ExpiresAtUtc)
                {
                    member = cached;
                    return true;
                }

                _memberCache.Remove(key);
            }
        }

        member = default!;
        return false;
    }

    internal void NotifyVisibleListings(IReadOnlyList<PfListingEntry> highEndListings)
    {
        if (highEndListings == null
            || highEndListings.Count == 0
            || Volatile.Read(ref _disposed) != 0
            || !_isPluginOperational())
        {
            return;
        }

        var now = _utcNow();
        var candidates = new List<ListingsEnrichmentItemDto>();
        var candidateKeys = new HashSet<LeaderSummaryCacheKey>();

        for (var i = 0; i < highEndListings.Count; i++)
        {
            var entry = highEndListings[i];
            if (entry == null
                || entry.IsSample
                || !entry.IsHighEnd
                || entry.Id == 0
                || entry.RawDuty == 0
                || entry.LeaderContentId == 0)
            {
                continue;
            }

            var key = new LeaderSummaryCacheKey(entry.Id, entry.RawDuty, entry.LeaderContentId);
            if (!candidateKeys.Add(key))
            {
                continue;
            }

            if (IsLeaderCachedOrInFlight(key, now))
            {
                continue;
            }

            candidates.Add(new ListingsEnrichmentItemDto
            {
                ListingId = entry.Id,
                RawDuty = entry.RawDuty,
                LeaderContentId = entry.LeaderContentId,
                LeaderName = entry.LeaderName ?? string.Empty,
                LeaderHomeWorld = entry.HomeWorldId,
            });
        }

        if (candidates.Count == 0)
        {
            return;
        }

        lock (_listingsGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!_currentListingsTask.IsCompleted
                && _pendingListingsKeySet.SetEquals(candidateKeys))
            {
                return;
            }

            _pendingListingsItems = candidates;
            _pendingListingsKeySet = candidateKeys;

            _listingsDebounceCts?.Cancel();
            _listingsDebounceCts?.Dispose();
            _listingsDebounceCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            var cts = _listingsDebounceCts;
            _currentListingsTask = Task.Run(() => RunDebouncedListingsEnrichmentAsync(cts.Token), cts.Token);
        }
    }

    internal void SetActiveListing(ulong listingId)
    {
        CancellationTokenSource? ctsToCancel = null;
        lock (_detailGate)
        {
            _activeViewedListingId = listingId;
            if (listingId == 0 || listingId != _activeDetailListingId)
            {
                ctsToCancel = _activeDetailCts;
                _activeDetailCts = null;
                _activeDetailListingId = 0;
                _activeDetailMemberSignature = string.Empty;
            }
        }

        if (ctsToCancel != null)
        {
            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            ctsToCancel.Dispose();
        }
    }

    internal void RequestPartyEnrichment(
        PfListingEntry listing,
        IReadOnlyList<ulong> memberContentIds,
        IReadOnlyList<PfKnownMemberHint> knownMembers,
        bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(listing);

        if (Volatile.Read(ref _disposed) != 0
            || listing.IsSample
            || listing.Id == 0
            || listing.RawDuty == 0
            || !_isPluginOperational())
        {
            return;
        }

        var uniqueMemberIds = BuildUniqueMemberIds(listing, memberContentIds);
        if (uniqueMemberIds.Count == 0)
        {
            SetActiveListing(listing.Id);
            return;
        }

        var knownInputs = BuildKnownMemberInputs(listing, knownMembers);
        var signature = string.Join(",", uniqueMemberIds);

        if (!forceRefresh && AreAllMembersCachedNonLoading(uniqueMemberIds, listing.RawDuty, _utcNow()))
        {
            lock (_detailGate)
            {
                _activeViewedListingId = listing.Id;
            }
            return;
        }

        CancellationTokenSource? ctsToCancel = null;
        CancellationToken ct;
        lock (_detailGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _activeViewedListingId = listing.Id;

            if (!forceRefresh
                && _activeDetailListingId == listing.Id
                && string.Equals(_activeDetailMemberSignature, signature, StringComparison.Ordinal)
                && !_activeDetailTask.IsCompleted)
            {
                return;
            }

            ctsToCancel = _activeDetailCts;
            _activeDetailCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _activeDetailListingId = listing.Id;
            _activeDetailMemberSignature = signature;
            ct = _activeDetailCts.Token;

            var anchorWorld = listing.CreatedWorldId != 0 ? listing.CreatedWorldId : listing.HomeWorldId;
            _activeDetailTask = Task.Run(
                () => RunPartyEnrichmentFlowAsync(
                    listing.Id,
                    listing.RawDuty,
                    anchorWorld,
                    listing.LeaderContentId,
                    uniqueMemberIds,
                    knownInputs,
                    ct),
                ct);
        }

        if (ctsToCancel != null)
        {
            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            ctsToCancel.Dispose();
        }
    }

    internal Task WaitForListingsEnrichmentForTestingAsync()
    {
        lock (_listingsGate)
        {
            return _currentListingsTask;
        }
    }

    internal Task WaitForPartyEnrichmentForTestingAsync()
    {
        lock (_detailGate)
        {
            return _activeDetailTask;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_listingsGate)
        {
            try
            {
                _listingsDebounceCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _listingsDebounceCts?.Dispose();
            _listingsDebounceCts = null;
        }

        lock (_detailGate)
        {
            _activeViewedListingId = 0;
            try
            {
                _activeDetailCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _activeDetailCts?.Dispose();
            _activeDetailCts = null;
        }

        _ownedHttpClient?.Dispose();
        _disposeCts.Dispose();
    }

    private bool IsLeaderCachedOrInFlight(LeaderSummaryCacheKey key, DateTime now)
    {
        lock (_listingsGate)
        {
            if (_inFlightListingsKeys.Contains(key))
            {
                return true;
            }
        }

        lock (_cacheGate)
        {
            if (_leaderCache.TryGetValue(key, out var cached))
            {
                if (now < cached.ExpiresAtUtc)
                {
                    return true;
                }

                _leaderCache.Remove(key);
            }
        }

        return false;
    }

    private async Task RunDebouncedListingsEnrichmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _delayAsync(ListingsDebounceDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 || !_isPluginOperational())
        {
            return;
        }

        List<ListingsEnrichmentItemDto> batch;
        List<LeaderSummaryCacheKey> batchKeys;
        lock (_listingsGate)
        {
            if (cancellationToken.IsCancellationRequested || _pendingListingsItems.Count == 0)
            {
                return;
            }

            batch = _pendingListingsItems.Take(MaxListingsBatchSize).ToList();
            batchKeys = new List<LeaderSummaryCacheKey>(batch.Count);
            foreach (var item in batch)
            {
                var key = new LeaderSummaryCacheKey(item.ListingId, item.RawDuty, item.LeaderContentId);
                batchKeys.Add(key);
                _inFlightListingsKeys.Add(key);
            }

            _pendingListingsItems.Clear();
            _pendingListingsKeySet.Clear();
        }

        try
        {
            if (!ProtectedIngestEndpointExecutor.TrySelectTargetForProtectedAttempt(
                    _configuration,
                    ProtectedEndpointCapabilityKind.PluginEnrichment,
                    _utcNow(),
                    _warningLog,
                    out var uploadUrl,
                    out var attemptLease,
                    out var capabilityDeferred)
                || uploadUrl == null
                || capabilityDeferred)
            {
                return;
            }

            using (attemptLease)
            {
                var payload = new ListingsEnrichmentRequestDto { Items = batch };
                var json = JsonConvert.SerializeObject(payload);
                var result = await _protectedExecutor.ExecuteAsync(
                    _configuration,
                    uploadUrl,
                    IngestRoutes.PluginListingsEnrichment,
                    ProtectedEndpointCapabilityKind.PluginEnrichment,
                    HttpMethod.Post,
                    json,
                    recordUploadAttempt: true,
                    cancellationToken,
                    extraHeaders: null,
                    attemptLease).ConfigureAwait(false);

                if (!result.Success
                    || !IngestJson.TryDeserializeObject(result.Body, out ListingsEnrichmentResponseDto? response)
                    || response == null)
                {
                    return;
                }

                var now = _utcNow();
                var expiresAt = now + LeaderCacheTtl;
                var returnedIds = new HashSet<ulong>();

                lock (_cacheGate)
                {
                    PruneExpiredLocked(now);
                    foreach (var item in response.Results)
                    {
                        var sentItem = batch.FirstOrDefault(b => b.ListingId == item.ListingId);
                        if (sentItem == null)
                        {
                            continue;
                        }

                        returnedIds.Add(item.ListingId);
                        var key = new LeaderSummaryCacheKey(item.ListingId, sentItem.RawDuty, sentItem.LeaderContentId);
                        var parse = ToPfParseDisplay(item.LeaderParse, item.ZoneId, item.DifficultyId);
                        var progress = ToPfProgressDisplay(item.LeaderProgress, parse);
                        _leaderCache[key] = new CachedLeaderEnrichment(
                            item.ZoneId,
                            item.DifficultyId,
                            parse,
                            progress,
                            item.CachedMemberCount,
                            expiresAt);
                    }

                    foreach (var sent in batch)
                    {
                        if (returnedIds.Contains(sent.ListingId))
                        {
                            continue;
                        }

                        var key = new LeaderSummaryCacheKey(sent.ListingId, sent.RawDuty, sent.LeaderContentId);
                        _leaderCache[key] = new CachedLeaderEnrichment(
                            0,
                            0,
                            PfParseDisplay.Empty,
                            PfProgressDisplay.Empty,
                            0,
                            expiresAt);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _debugLog($"PfEnrichmentClient: listings enrichment failed: {ex.Message}");
        }
        finally
        {
            lock (_listingsGate)
            {
                foreach (var key in batchKeys)
                {
                    _inFlightListingsKeys.Remove(key);
                }
            }
        }
    }

    private async Task RunPartyEnrichmentFlowAsync(
        ulong listingId,
        ushort rawDuty,
        ushort anchorWorld,
        ulong leaderContentId,
        List<ulong> memberContentIds,
        List<KnownMemberInputDto> knownMembers,
        CancellationToken cancellationToken)
    {
        try
        {
            var initialCall = await ExecutePartyEnrichmentRequestAsync(
                listingId,
                rawDuty,
                anchorWorld,
                memberContentIds,
                knownMembers,
                allowSelfLeaseFflogs: _isFFLogsSelfLeaseAvailable(),
                allowSelfLeaseTomestone: _isTomestoneSelfLeaseAvailable(),
                cancellationToken).ConfigureAwait(false);

            if (initialCall.Response == null || initialCall.UploadUrl == null)
            {
                return;
            }

            var latestResponse = initialCall.Response;
            MergePartyEnrichmentResponse(listingId, rawDuty, leaderContentId, latestResponse);

            var hasFflogsJobs = latestResponse.SelfLeaseFflogsJobs is { Count: > 0 } && _executeFFLogsSelfLeaseAsync != null;
            var hasTomestoneJobs = latestResponse.SelfLeaseTomestoneJobs is { Count: > 0 } && _executeTomestoneSelfLeaseAsync != null;

            if (hasFflogsJobs || hasTomestoneJobs)
            {
                await ExecuteSelfLeaseJobsAsync(
                    initialCall.UploadUrl,
                    latestResponse.SelfLeaseFflogsJobs,
                    latestResponse.SelfLeaseTomestoneJobs,
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested || ActiveViewedListingId != listingId)
                {
                    return;
                }

                var refreshedCall = await ExecutePartyEnrichmentRequestAsync(
                    listingId,
                    rawDuty,
                    anchorWorld,
                    memberContentIds,
                    knownMembers,
                    allowSelfLeaseFflogs: false,
                    allowSelfLeaseTomestone: false,
                    cancellationToken).ConfigureAwait(false);

                if (refreshedCall.Response != null)
                {
                    latestResponse = refreshedCall.Response;
                    MergePartyEnrichmentResponse(listingId, rawDuty, leaderContentId, latestResponse);
                }
            }

            var attempt = 0;
            while (attempt < MaxLoadingPollAttempts
                   && !cancellationToken.IsCancellationRequested
                   && ActiveViewedListingId == listingId
                   && HasAnyLoadingMember(latestResponse))
            {
                var delayMs = attempt == 0 && latestResponse.RetryAfterMs > 0
                    ? (int)Math.Min(latestResponse.RetryAfterMs, 10_000UL)
                    : LoadingBackoffScheduleMs[attempt];
                attempt++;

                await _delayAsync(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || ActiveViewedListingId != listingId)
                {
                    return;
                }

                var pollCall = await ExecutePartyEnrichmentRequestAsync(
                    listingId,
                    rawDuty,
                    anchorWorld,
                    memberContentIds,
                    knownMembers,
                    allowSelfLeaseFflogs: false,
                    allowSelfLeaseTomestone: false,
                    cancellationToken).ConfigureAwait(false);

                if (pollCall.Response == null)
                {
                    break;
                }

                latestResponse = pollCall.Response;
                MergePartyEnrichmentResponse(listingId, rawDuty, leaderContentId, latestResponse);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _debugLog($"PfEnrichmentClient: party enrichment failed for listing={listingId}: {ex.Message}");
        }
    }

    private async Task ExecuteSelfLeaseJobsAsync(
        UploadUrl uploadUrl,
        List<ParseJob>? fflogsJobs,
        List<Tomestone.TomestoneJob>? tomestoneJobs,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(2);

        if (fflogsJobs is { Count: > 0 } && _executeFFLogsSelfLeaseAsync != null)
        {
            tasks.Add(RunSafeSelfLeaseAsync(
                () => _executeFFLogsSelfLeaseAsync(uploadUrl, fflogsJobs, cancellationToken),
                "FFLogs"));
        }

        if (tomestoneJobs is { Count: > 0 } && _executeTomestoneSelfLeaseAsync != null)
        {
            tasks.Add(RunSafeSelfLeaseAsync(
                () => _executeTomestoneSelfLeaseAsync(uploadUrl, tomestoneJobs, cancellationToken),
                "Tomestone"));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task RunSafeSelfLeaseAsync(Func<Task<bool>> action, string kind)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _warningLog($"PfEnrichmentClient: {kind} self-lease execution failed: {ex.Message}");
        }
    }

    private async Task<(UploadUrl? UploadUrl, PartyEnrichmentResponseDto? Response)> ExecutePartyEnrichmentRequestAsync(
        ulong listingId,
        ushort rawDuty,
        ushort anchorWorld,
        List<ulong> memberContentIds,
        List<KnownMemberInputDto> knownMembers,
        bool allowSelfLeaseFflogs,
        bool allowSelfLeaseTomestone,
        CancellationToken cancellationToken)
    {
        if (!ProtectedIngestEndpointExecutor.TrySelectTargetForProtectedAttempt(
                _configuration,
                ProtectedEndpointCapabilityKind.PluginEnrichment,
                _utcNow(),
                _warningLog,
                out var uploadUrl,
                out var attemptLease,
                out var capabilityDeferred)
            || uploadUrl == null
            || capabilityDeferred)
        {
            return (null, null);
        }

        using (attemptLease)
        {
            string? workerRegion = null;
            if (_workerRegionProvider != null && _workerRegionProvider.TryGetWorkerRegion(out var resolvedRegion) && !string.IsNullOrWhiteSpace(resolvedRegion))
            {
                workerRegion = resolvedRegion.Trim();
            }

            var requestDto = new PartyEnrichmentRequestDto
            {
                ListingId = listingId,
                RawDuty = rawDuty,
                AnchorWorld = anchorWorld,
                MemberContentIds = memberContentIds,
                KnownMembers = knownMembers,
                AllowSelfLeaseFflogs = allowSelfLeaseFflogs,
                AllowSelfLeaseTomestone = allowSelfLeaseTomestone,
                WorkerRegion = workerRegion,
            };

            var json = JsonConvert.SerializeObject(requestDto);
            var result = await _protectedExecutor.ExecuteAsync(
                _configuration,
                uploadUrl,
                IngestRoutes.PluginPartyEnrichment,
                ProtectedEndpointCapabilityKind.PluginEnrichment,
                HttpMethod.Post,
                json,
                recordUploadAttempt: true,
                cancellationToken,
                extraHeaders: null,
                attemptLease).ConfigureAwait(false);

            if (!result.Success
                || !IngestJson.TryDeserializeObject(result.Body, out PartyEnrichmentResponseDto? response)
                || response == null)
            {
                return (uploadUrl, null);
            }

            return (uploadUrl, response);
        }
    }

    private void MergePartyEnrichmentResponse(
        ulong listingId,
        ushort rawDuty,
        ulong leaderContentId,
        PartyEnrichmentResponseDto response)
    {
        var now = _utcNow();
        PartyEnrichmentMemberDto? leaderDto = null;
        var cachedCount = 0;

        lock (_cacheGate)
        {
            PruneExpiredLocked(now);
            foreach (var member in response.Members)
            {
                if (member.ContentId == 0)
                {
                    continue;
                }

                var state = ParseMemberState(member.State);
                if (state == PfMemberEnrichmentState.Cached)
                {
                    cachedCount++;
                }

                var ttl = state switch
                {
                    PfMemberEnrichmentState.Cached => MemberCachedTtl,
                    PfMemberEnrichmentState.Loading => MemberLoadingTtl,
                    PfMemberEnrichmentState.Unresolved => MemberUnresolvedTtl,
                    _ => MemberNoneTtl,
                };

                var parse = ToPfParseDisplay(member.Parse, response.ZoneId, response.DifficultyId);
                var progress = ToPfProgressDisplay(member.Progress, parse);
                var key = new MemberEnrichmentCacheKey(member.ContentId, rawDuty);
                _memberCache[key] = new CachedMemberEnrichment(
                    member.Name ?? string.Empty,
                    member.HomeWorld,
                    parse,
                    progress,
                    state,
                    now + ttl);

                if (leaderContentId != 0 && member.ContentId == leaderContentId)
                {
                    leaderDto = member;
                }
            }

            if (leaderDto != null && listingId != 0 && leaderContentId != 0)
            {
                var leaderParse = ToPfParseDisplay(leaderDto.Parse, response.ZoneId, response.DifficultyId);
                var leaderProgress = ToPfProgressDisplay(leaderDto.Progress, leaderParse);
                _leaderCache[new LeaderSummaryCacheKey(listingId, rawDuty, leaderContentId)] =
                    new CachedLeaderEnrichment(
                        response.ZoneId,
                        response.DifficultyId,
                        leaderParse,
                        leaderProgress,
                        cachedCount,
                        now + LeaderCacheTtl);
            }
        }
    }

    private bool AreAllMembersCachedNonLoading(IReadOnlyList<ulong> memberContentIds, ushort rawDuty, DateTime now)
    {
        lock (_cacheGate)
        {
            for (var i = 0; i < memberContentIds.Count; i++)
            {
                var key = new MemberEnrichmentCacheKey(memberContentIds[i], rawDuty);
                if (!_memberCache.TryGetValue(key, out var cached)
                    || now >= cached.ExpiresAtUtc
                    || cached.State == PfMemberEnrichmentState.Loading)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private void PruneExpiredLocked(DateTime now)
    {
        if (_leaderCache.Count > 512)
        {
            foreach (var pair in _leaderCache.ToArray())
            {
                if (now >= pair.Value.ExpiresAtUtc)
                {
                    _leaderCache.Remove(pair.Key);
                }
            }
        }

        if (_memberCache.Count > 2048)
        {
            foreach (var pair in _memberCache.ToArray())
            {
                if (now >= pair.Value.ExpiresAtUtc)
                {
                    _memberCache.Remove(pair.Key);
                }
            }
        }
    }

    private static bool HasAnyLoadingMember(PartyEnrichmentResponseDto response)
    {
        foreach (var member in response.Members)
        {
            if (string.Equals(member.State, "loading", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<ulong> BuildUniqueMemberIds(PfListingEntry listing, IReadOnlyList<ulong>? memberContentIds)
    {
        var result = new List<ulong>();
        var seen = new HashSet<ulong>();

        if (memberContentIds != null)
        {
            foreach (var cid in memberContentIds)
            {
                if (cid != 0 && seen.Add(cid))
                {
                    result.Add(cid);
                }
            }
        }

        if (result.Count == 0 && listing.LeaderContentId != 0 && seen.Add(listing.LeaderContentId))
        {
            result.Add(listing.LeaderContentId);
        }

        return result;
    }

    private static List<KnownMemberInputDto> BuildKnownMemberInputs(
        PfListingEntry listing,
        IReadOnlyList<PfKnownMemberHint>? knownMembers)
    {
        var result = new List<KnownMemberInputDto>();
        var seen = new HashSet<ulong>();

        if (knownMembers != null)
        {
            foreach (var hint in knownMembers)
            {
                if (hint == null
                    || hint.ContentId == 0
                    || hint.HomeWorld == 0
                    || string.IsNullOrWhiteSpace(hint.Name)
                    || !seen.Add(hint.ContentId))
                {
                    continue;
                }

                result.Add(new KnownMemberInputDto
                {
                    ContentId = hint.ContentId,
                    Name = hint.Name.Trim(),
                    HomeWorld = hint.HomeWorld,
                });
            }
        }

        if (listing.LeaderContentId != 0
            && listing.HomeWorldId != 0
            && !string.IsNullOrWhiteSpace(listing.LeaderName)
            && seen.Add(listing.LeaderContentId))
        {
            result.Add(new KnownMemberInputDto
            {
                ContentId = listing.LeaderContentId,
                Name = listing.LeaderName.Trim(),
                HomeWorld = listing.HomeWorldId,
            });
        }

        return result;
    }

    private static PfMemberEnrichmentState ParseMemberState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "cached" => PfMemberEnrichmentState.Cached,
        "loading" => PfMemberEnrichmentState.Loading,
        "unresolved" => PfMemberEnrichmentState.Unresolved,
        _ => PfMemberEnrichmentState.None,
    };

    internal static PfParseDisplay ToPfParseDisplay(ApiParseDisplayDto? dto, uint zoneId, int difficultyId)
    {
        if (dto == null)
        {
            return PfParseDisplay.Empty;
        }

        return new PfParseDisplay(
            PrimaryPercentile: dto.PrimaryPercentile,
            SecondaryPercentile: dto.SecondaryPercentile,
            HasSecondary: dto.HasSecondary,
            Hidden: dto.Hidden,
            OriginallyHidden: dto.OriginallyHidden,
            Estimated: dto.Estimated,
            ZoneId: zoneId,
            DifficultyId: difficultyId,
            LockedIn: true);
    }

    internal static PfProgressDisplay ToPfProgressDisplay(ApiProgressDisplayDto? dto, PfParseDisplay parse)
    {
        if (dto == null)
        {
            return PfProgressDisplay.Empty;
        }

        var cleared = (dto.FinalClearCount is > 0) || parse.HasVisiblePercentile;
        var phases = dto.PhaseProgress is { Count: > 0 }
            ? dto.PhaseProgress
                .OrderBy(static p => p.Order)
                .Select(p => new PfPhaseProgressItem(
                    PhaseName: string.IsNullOrWhiteSpace(p.PhaseName) ? p.PhaseKey : p.PhaseName,
                    BestPulls: 0,
                    BestHpPercent: p.BossPercentage,
                    Cleared: !p.BossPercentage.HasValue && cleared))
                .ToArray()
            : Array.Empty<PfPhaseProgressItem>();

        string summaryLabel;
        if (dto.PhaseProgress is { Count: > 0 })
        {
            var latestPhase = dto.PhaseProgress
                .OrderByDescending(static p => p.Order)
                .FirstOrDefault(static p => !string.IsNullOrWhiteSpace(p.DisplayText));
            summaryLabel = latestPhase?.DisplayText ?? string.Empty;
        }
        else if (dto.FinalBossPercentage.HasValue)
        {
            summaryLabel = dto.FinalBossPhase is > 0
                ? $"P{dto.FinalBossPhase.Value} {dto.FinalBossPercentage.Value:0.#}%"
                : $"{dto.FinalBossPercentage.Value:0.#}%";
        }
        else if (cleared)
        {
            summaryLabel = "Cleared";
        }
        else if (parse.Hidden)
        {
            summaryLabel = "Hidden";
        }
        else
        {
            summaryLabel = string.Empty;
        }

        return new PfProgressDisplay(
            Cleared: cleared,
            ClearCount: dto.FinalClearCount,
            BossPercentage: dto.FinalBossPercentage,
            BossPhase: dto.FinalBossPhase,
            PhaseProgress: phases,
            SummaryLabel: summaryLabel);
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class ListingsEnrichmentRequestDto
    {
        public List<ListingsEnrichmentItemDto> Items { get; set; } = [];
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class ListingsEnrichmentItemDto
    {
        public ulong ListingId { get; set; }
        public ushort RawDuty { get; set; }
        public ulong LeaderContentId { get; set; }
        public string LeaderName { get; set; } = string.Empty;
        public ushort LeaderHomeWorld { get; set; }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class ListingsEnrichmentResponseDto
    {
        public List<ListingsEnrichmentResultItemDto> Results { get; set; } = [];
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class ListingsEnrichmentResultItemDto
    {
        public ulong ListingId { get; set; }
        public uint ZoneId { get; set; }
        public int DifficultyId { get; set; }
        public ApiParseDisplayDto? LeaderParse { get; set; }
        public ApiProgressDisplayDto? LeaderProgress { get; set; }
        public int CachedMemberCount { get; set; }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class PartyEnrichmentRequestDto
    {
        public ulong ListingId { get; set; }
        public ushort RawDuty { get; set; }
        public ushort AnchorWorld { get; set; }
        public List<ulong> MemberContentIds { get; set; } = [];
        public List<KnownMemberInputDto> KnownMembers { get; set; } = [];
        public bool AllowSelfLeaseFflogs { get; set; }
        public bool AllowSelfLeaseTomestone { get; set; }
        public string? WorkerRegion { get; set; }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class KnownMemberInputDto
    {
        public ulong ContentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public ushort HomeWorld { get; set; }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class PartyEnrichmentResponseDto
    {
        public ulong ListingId { get; set; }
        public uint ZoneId { get; set; }
        public int DifficultyId { get; set; }
        public List<PartyEnrichmentMemberDto> Members { get; set; } = [];
        public List<ParseJob> SelfLeaseFflogsJobs { get; set; } = [];
        public List<Tomestone.TomestoneJob> SelfLeaseTomestoneJobs { get; set; } = [];
        public ulong RetryAfterMs { get; set; }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class PartyEnrichmentMemberDto
    {
        public ulong ContentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public ushort HomeWorld { get; set; }
        public ApiParseDisplayDto? Parse { get; set; }
        public ApiProgressDisplayDto? Progress { get; set; }
        public string State { get; set; } = string.Empty;
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class ApiParseDisplayDto
    {
        public byte? PrimaryPercentile { get; set; }
        public string PrimaryColorClass { get; set; } = string.Empty;
        public byte? SecondaryPercentile { get; set; }
        public string SecondaryColorClass { get; set; } = string.Empty;
        public bool HasSecondary { get; set; }
        public bool Hidden { get; set; }
        public bool OriginallyHidden { get; set; }
        public bool Estimated { get; set; }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class ApiProgressDisplayDto
    {
        public float? FinalBossPercentage { get; set; }
        public byte? FinalBossPhase { get; set; }
        public ushort? FinalClearCount { get; set; }
        public List<ApiPhaseProgressDto> PhaseProgress { get; set; } = [];
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    internal sealed class ApiPhaseProgressDto
    {
        public string PhaseKey { get; set; } = string.Empty;
        public string PhaseName { get; set; } = string.Empty;
        public byte Order { get; set; }
        public float? BossPercentage { get; set; }
        public string DisplayText { get; set; } = string.Empty;
        public string? Source { get; set; }
    }
}
