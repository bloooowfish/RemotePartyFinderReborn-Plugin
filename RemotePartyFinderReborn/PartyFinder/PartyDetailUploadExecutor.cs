using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class PartyDetailUploadExecutor {
    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _errorLog;

    internal PartyDetailUploadExecutor(
        Configuration configuration,
        HttpClient httpClient,
        Func<DateTime> utcNow,
        Action<string> debugLog,
        Action<string> warningLog,
        Action<string> errorLog
    ) {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _warningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        _errorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
    }

    internal async Task<Dictionary<uint, DetailUploadOutcome>> UploadBatchToAllTargetsAsync(
        IReadOnlyList<PendingDetailEntry> pendingBatch,
        CancellationToken cancellationToken = default
    ) {
        var enabledTargets = _configuration.GetUploadUrlsSnapshot()
            .Where(static candidate => candidate.IsEnabled)
            .ToArray();
        var aggregations = new Dictionary<uint, ListingBatchAggregation>(pendingBatch.Count);
        foreach (var pending in pendingBatch) {
            aggregations[pending.Detail.ListingId] = new ListingBatchAggregation();
        }

        foreach (var uploadTarget in enabledTargets) {
            cancellationToken.ThrowIfCancellationRequested();

            var targetCandidates = pendingBatch
                .Where(pending => !pending.IsTargetResolved(uploadTarget.Id))
                .ToList();
            if (targetCandidates.Count == 0) {
                continue;
            }

            var initialRoute = uploadTarget.DetailBatchUnsupported ? IngestRoutes.Detail : IngestRoutes.DetailBatch;
            if (!IngestEndpointResolver.TryBuildEndpoint(uploadTarget, initialRoute, out var endpoint)) {
                continue;
            }

            var nowOffset = new DateTimeOffset(_utcNow());
            var sendableCandidates = PartitionSendableCandidates(uploadTarget, targetCandidates, nowOffset, aggregations);
            if (sendableCandidates.Count == 0) {
                continue;
            }

            if (!UploadAttemptRecorder.TryBeginAttempt(uploadTarget, _configuration, _utcNow(), out var attemptLease)) {
                continue;
            }

            using (attemptLease) {
                if (uploadTarget.DetailBatchUnsupported) {
                    await UploadSinglesToTargetAsync(
                        uploadTarget,
                        sendableCandidates,
                        aggregations,
                        cancellationToken
                    );
                    continue;
                }

                var sendable = BuildSendableBatchItems(uploadTarget, sendableCandidates, nowOffset);
                if (sendable.Count == 0) {
                    continue;
                }

                try {
                    using var request = IngestRequestFactory.CreatePostJsonRequest(
                        _configuration,
                        endpoint,
                        JsonConvert.SerializeObject(sendable.Select(static item => item.Payload)),
                        capabilityToken: null
                    );
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(
                        TimeSpan.FromMilliseconds(PartyDetailUploadPolicy.NormalizeDetailUploadTimeoutMs(_configuration.DetailUploadTimeoutMs))
                    );
                    using var response = await _httpClient.SendAsync(request, timeout.Token);
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);

                    if (response.IsSuccessStatusCode) {
                        HandleBatchSuccessResponse(uploadTarget, endpoint, response.StatusCode, body, sendable, attemptLease, aggregations);
                        continue;
                    }

                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) {
                        uploadTarget.MarkDetailBatchUnsupported();
                        _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode}; falling back to single detail uploads");
                        await UploadSinglesToTargetAsync(
                            uploadTarget,
                            sendable.Select(static item => item.Pending).ToList(),
                            aggregations,
                            cancellationToken
                        );
                        continue;
                    }

                    var isAuthFailure = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
                    if (isAuthFailure) {
                        uploadTarget.MarkDetailCapabilitiesRequired();
                        foreach (var item in sendable) {
                            var listingId = item.Pending.Detail.ListingId;
                            uploadTarget.InvalidateDetailCapability(listingId);
                            aggregations[listingId].HasDeferred = true;
                        }
                    } else {
                        attemptLease.RecordFailure(_utcNow());
                        int? retryAfterSeconds = (int)response.StatusCode == 429
                            ? IngestRequestFactory.ReadRetryAfterSeconds(response)
                            : null;
                        if (retryAfterSeconds.HasValue) {
                            _warningLog($"PartyDetailCollector: rate limited by {endpoint.Url}, retry_after={retryAfterSeconds.Value}s");
                        }

                        foreach (var item in sendable) {
                            aggregations[item.Pending.Detail.ListingId].AddRetryableFailure(retryAfterSeconds);
                        }
                    }

                    _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode} {body}");
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    throw;
                } catch (OperationCanceledException exception) {
                    attemptLease.RecordFailure(_utcNow());
                    foreach (var item in sendable) {
                        aggregations[item.Pending.Detail.ListingId].AddRetryableFailure(null);
                    }
                    _errorLog($"PartyDetailCollector batch upload timed out to {endpoint.Url}: {exception.Message}");
                } catch (Exception exception) {
                    attemptLease.RecordFailure(_utcNow());
                    foreach (var item in sendable) {
                        aggregations[item.Pending.Detail.ListingId].AddRetryableFailure(null);
                    }
                    _errorLog($"PartyDetailCollector batch upload error to {endpoint.Url}: {exception.Message}");
                }
            }
        }

        var outcomes = new Dictionary<uint, DetailUploadOutcome>(pendingBatch.Count);
        foreach (var pending in pendingBatch) {
            var listingId = pending.Detail.ListingId;
            outcomes[listingId] = BuildFinalOutcome(pending, enabledTargets, aggregations[listingId]);
        }

        return outcomes;
    }

    private void HandleBatchSuccessResponse(
        UploadUrl uploadTarget,
        IngestEndpoint endpoint,
        HttpStatusCode statusCode,
        string body,
        IReadOnlyList<SendableDetailBatchItem> sendable,
        UploadAttemptLease attemptLease,
        Dictionary<uint, ListingBatchAggregation> aggregations
    ) {
        if (DetailUploadResponseMapper.TryParseDetailBatchResponse(body, out var batchResponse) && batchResponse.Results.Count > 0) {
            var targetApplied = new HashSet<uint>();
            var targetMissing = new HashSet<uint>();
            var targetRejected = new Dictionary<uint, string>();
            var targetRetryAfter = new Dictionary<uint, int?>();
            var targetCapabilityForbidden = new HashSet<uint>();
            var targetRetryableFailed = new HashSet<uint>();

            DetailUploadResponseMapper.ApplyBatchResponse(
                batchResponse,
                sendable.Select(static item => item.Pending.Detail.ListingId),
                targetApplied,
                targetMissing,
                targetRejected,
                targetRetryAfter,
                targetCapabilityForbidden,
                targetRetryableFailed
            );

            foreach (var listingId in targetApplied) {
                aggregations[listingId].MarkApplied(uploadTarget.Id);
            }

            foreach (var (listingId, message) in targetRejected) {
                aggregations[listingId].MarkTerminalRejected(uploadTarget.Id, message);
            }

            foreach (var listingId in targetMissing) {
                aggregations[listingId].HasMissing = true;
            }

            if (targetCapabilityForbidden.Count > 0) {
                uploadTarget.MarkDetailCapabilitiesRequired();
                foreach (var listingId in targetCapabilityForbidden) {
                    uploadTarget.InvalidateDetailCapability(listingId);
                    aggregations[listingId].HasDeferred = true;
                }
            }

            foreach (var listingId in targetRetryableFailed) {
                aggregations[listingId].AddRetryableFailure(targetRetryAfter.GetValueOrDefault(listingId));
            }

            // Keep endpoint failure state honest when every item in the batch was rejected for capability reasons.
            if (targetCapabilityForbidden.Count < sendable.Count) {
                attemptLease.RecordSuccess();
            }
        } else {
            attemptLease.RecordSuccess();
            foreach (var item in sendable) {
                aggregations[item.Pending.Detail.ListingId].AddRetryableFailure(null);
            }
            _warningLog($"PartyDetailCollector: {endpoint.Url}: unexpected batch response without item results; retrying sent detail payloads");
        }

        _debugLog($"PartyDetailCollector: {endpoint.Url}: {statusCode} {body}");
    }

    private static List<PendingDetailEntry> PartitionSendableCandidates(
        UploadUrl uploadTarget,
        IReadOnlyList<PendingDetailEntry> candidates,
        DateTimeOffset nowOffset,
        Dictionary<uint, ListingBatchAggregation> aggregations
    ) {
        var sendable = new List<PendingDetailEntry>(candidates.Count);
        foreach (var pending in candidates) {
            var listingId = pending.Detail.ListingId;
            if (uploadTarget.ShouldDeferDetailRequest(listingId, nowOffset)) {
                aggregations[listingId].HasDeferred = true;
                continue;
            }

            sendable.Add(pending);
        }

        return sendable;
    }

    private static DetailUploadOutcome BuildFinalOutcome(
        PendingDetailEntry pending,
        IReadOnlyList<UploadUrl> enabledTargets,
        ListingBatchAggregation state
    ) {
        var resolvedThisRun = state.ResolvedTargetIds.Count > 0 ? state.ResolvedTargetIds.ToArray() : null;
        if (enabledTargets.Count == 0) {
            return new DetailUploadOutcome(DetailUploadResult.CircuitBlocked);
        }

        var hasUnresolvedEnabledTarget = enabledTargets.Any(
            target => !pending.IsTargetResolved(target.Id) && !state.ResolvedTargetIds.Contains(target.Id)
        );
        var hasAppliedOverall = pending.HasAppliedTarget || state.HasApplied;

        if (!hasUnresolvedEnabledTarget) {
            return hasAppliedOverall
                ? new DetailUploadOutcome(
                    DetailUploadResult.Applied,
                    ResolvedTargetIds: resolvedThisRun,
                    HasAppliedTarget: state.HasApplied
                )
                : new DetailUploadOutcome(
                    DetailUploadResult.TerminalRejected,
                    Message: state.TerminalMessage ?? "terminal detail upload rejection",
                    ResolvedTargetIds: resolvedThisRun,
                    HasAppliedTarget: false
                );
        }

        if (state.HasDeferred) {
            return new DetailUploadOutcome(
                DetailUploadResult.Deferred,
                ResolvedTargetIds: resolvedThisRun,
                HasAppliedTarget: state.HasApplied
            );
        }

        if (state.HasMissing) {
            return new DetailUploadOutcome(
                DetailUploadResult.ListingMissing,
                ResolvedTargetIds: resolvedThisRun,
                HasAppliedTarget: state.HasApplied
            );
        }

        if (state.HasRetryableFailure) {
            return new DetailUploadOutcome(
                DetailUploadResult.RetryableFailure,
                RetryAfterSeconds: state.MaxRetryAfterSeconds,
                ResolvedTargetIds: resolvedThisRun,
                HasAppliedTarget: state.HasApplied
            );
        }

        return new DetailUploadOutcome(
            DetailUploadResult.CircuitBlocked,
            ResolvedTargetIds: resolvedThisRun,
            HasAppliedTarget: state.HasApplied
        );
    }

    private async Task UploadSinglesToTargetAsync(
        UploadUrl uploadTarget,
        IReadOnlyList<PendingDetailEntry> pendingEntries,
        Dictionary<uint, ListingBatchAggregation> aggregations,
        CancellationToken cancellationToken
    ) {
        foreach (var pending in pendingEntries) {
            cancellationToken.ThrowIfCancellationRequested();

            var listingId = pending.Detail.ListingId;
            if (pending.IsTargetResolved(uploadTarget.Id) || aggregations[listingId].ResolvedTargetIds.Contains(uploadTarget.Id)) {
                continue;
            }

            var outcome = await TryUploadSingleToTargetAsync(uploadTarget, pending.Detail, cancellationToken);
            if (outcome == null) {
                continue;
            }

            var aggregation = aggregations[listingId];
            switch (outcome.Value.Result) {
                case DetailUploadResult.Applied:
                    aggregation.MarkApplied(uploadTarget.Id);
                    break;
                case DetailUploadResult.ListingMissing:
                    aggregation.HasMissing = true;
                    break;
                case DetailUploadResult.TerminalRejected:
                    aggregation.MarkTerminalRejected(uploadTarget.Id, outcome.Value.Message);
                    break;
                case DetailUploadResult.Deferred:
                    aggregation.HasDeferred = true;
                    break;
                default:
                    aggregation.AddRetryableFailure(outcome.Value.RetryAfterSeconds);
                    if (UploadAttemptRecorder.PeekIsCircuitOpen(uploadTarget, _configuration, _utcNow())) {
                        return;
                    }
                    break;
            }
        }
    }

    private static List<SendableDetailBatchItem> BuildSendableBatchItems(
        UploadUrl uploadTarget,
        IReadOnlyList<PendingDetailEntry> candidates,
        DateTimeOffset now
    ) {
        var sendable = new List<SendableDetailBatchItem>(candidates.Count);
        foreach (var pending in candidates) {
            var listingId = pending.Detail.ListingId;
            if (uploadTarget.ShouldDeferDetailRequest(listingId, now)) {
                continue;
            }

            var capabilityToken = uploadTarget.TryGetDetailCapability(listingId, out var cachedCapability, now)
                ? cachedCapability
                : null;
            sendable.Add(new SendableDetailBatchItem(
                pending,
                UploadablePartyDetailBatchItem.FromDetail(pending.Detail, capabilityToken)
            ));
        }

        return sendable;
    }

    private async Task<DetailUploadOutcome?> TryUploadSingleToTargetAsync(
        UploadUrl uploadTarget,
        UploadablePartyDetail payload,
        CancellationToken cancellationToken
    ) {
        if (cancellationToken.IsCancellationRequested) {
            return null;
        }

        if (!IngestEndpointResolver.TryBuildEndpoint(uploadTarget, IngestRoutes.Detail, out var endpoint)) {
            return null;
        }

        var now = new DateTimeOffset(_utcNow());
        if (uploadTarget.ShouldDeferDetailRequest(payload.ListingId, now)) {
            return new DetailUploadOutcome(DetailUploadResult.Deferred);
        }

        var capabilityToken = uploadTarget.TryGetDetailCapability(payload.ListingId, out var cachedCapability, now)
            ? cachedCapability
            : null;
        try {
            using var request = IngestRequestFactory.CreatePostJsonRequest(
                _configuration,
                endpoint,
                JsonConvert.SerializeObject(payload),
                capabilityToken
            );
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(
                TimeSpan.FromMilliseconds(PartyDetailUploadPolicy.NormalizeDetailUploadTimeoutMs(_configuration.DetailUploadTimeoutMs))
            );
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (response.IsSuccessStatusCode) {
                UploadAttemptRecorder.RecordSuccess(uploadTarget);

                var outcome = DetailUploadResponseMapper.MapSuccessfulSingleResponse(
                    body,
                    out var parsed,
                    out var matchedCount,
                    out var modifiedCount
                );
                if (parsed) {
                    _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode} matched={matchedCount} modified={modifiedCount}");
                    return outcome;
                }

                _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode} {body}");
                return outcome;
            }

            var isAuthFailure = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (isAuthFailure) {
                uploadTarget.MarkDetailCapabilitiesRequired();
                uploadTarget.InvalidateDetailCapability(payload.ListingId);
                _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode} {body}");
                return new DetailUploadOutcome(DetailUploadResult.Deferred);
            }

            UploadAttemptRecorder.RecordFailure(uploadTarget, _utcNow());

            int? retryAfterSeconds = null;
            if ((int)response.StatusCode == 429) {
                var retryAfter = IngestRequestFactory.ReadRetryAfterSeconds(response);
                if (retryAfter.HasValue) {
                    retryAfterSeconds = retryAfter.Value;
                    _warningLog($"PartyDetailCollector: rate limited by {endpoint.Url}, retry_after={retryAfter.Value}s");
                }
            }

            _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode} {body}");
            return new DetailUploadOutcome(DetailUploadResult.RetryableFailure, retryAfterSeconds);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException exception) {
            UploadAttemptRecorder.RecordFailure(uploadTarget, _utcNow());
            _errorLog($"PartyDetailCollector upload timed out to {endpoint.Url}: {exception.Message}");
        } catch (Exception exception) {
            UploadAttemptRecorder.RecordFailure(uploadTarget, _utcNow());
            _errorLog($"PartyDetailCollector upload error to {endpoint.Url}: {exception.Message}");
        }

        return new DetailUploadOutcome(DetailUploadResult.RetryableFailure);
    }

    private static int? MaxRetryAfterSeconds(int? current, int? candidate) {
        if (!candidate.HasValue) {
            return current;
        }

        return current.HasValue ? Math.Max(current.Value, candidate.Value) : candidate.Value;
    }

    private sealed class ListingBatchAggregation {
        public HashSet<Guid> ResolvedTargetIds { get; } = new();
        public bool HasApplied { get; private set; }
        public string? TerminalMessage { get; private set; }
        public bool HasDeferred { get; set; }
        public bool HasMissing { get; set; }
        public bool HasRetryableFailure { get; private set; }
        public int? MaxRetryAfterSeconds { get; private set; }

        public void MarkApplied(Guid targetId) {
            ResolvedTargetIds.Add(targetId);
            HasApplied = true;
        }

        public void MarkTerminalRejected(Guid targetId, string? message) {
            ResolvedTargetIds.Add(targetId);
            if (!string.IsNullOrWhiteSpace(message)) {
                TerminalMessage ??= message;
            }
        }

        public void AddRetryableFailure(int? retryAfterSeconds) {
            HasRetryableFailure = true;
            MaxRetryAfterSeconds = PartyDetailUploadExecutor.MaxRetryAfterSeconds(MaxRetryAfterSeconds, retryAfterSeconds);
        }
    }

    private sealed record SendableDetailBatchItem(
        PendingDetailEntry Pending,
        UploadablePartyDetailBatchItem Payload
    );
}
