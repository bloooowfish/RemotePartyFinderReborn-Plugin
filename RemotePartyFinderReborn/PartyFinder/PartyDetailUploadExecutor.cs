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
        IReadOnlyList<PendingDetailEntry> pendingBatch
    ) {
        var applied = new HashSet<uint>();
        var missing = new HashSet<uint>();
        var terminalRejected = new Dictionary<uint, string>();
        var retryAfterByListing = new Dictionary<uint, int?>();
        var attempted = new HashSet<uint>();

        foreach (var uploadTarget in _configuration.UploadUrls.Where(static candidate => candidate.IsEnabled)) {
            if (UploadAttemptRecorder.IsCircuitOpen(uploadTarget, _configuration, _utcNow())) {
                continue;
            }

            var candidates = pendingBatch
                .Where(pending => !applied.Contains(pending.Detail.ListingId))
                .Where(pending => !terminalRejected.ContainsKey(pending.Detail.ListingId))
                .ToList();
            if (candidates.Count == 0) {
                break;
            }

            if (uploadTarget.DetailBatchUnsupported) {
                await UploadSinglesToTargetAsync(uploadTarget, candidates, applied, missing, terminalRejected, retryAfterByListing, attempted);
                continue;
            }

            if (!IngestEndpointResolver.TryBuildEndpoint(uploadTarget, IngestRoutes.DetailBatch, out var endpoint)) {
                continue;
            }

            var sendable = BuildSendableBatchItems(uploadTarget, candidates, new DateTimeOffset(_utcNow()));
            if (sendable.Count == 0) {
                continue;
            }
            foreach (var item in sendable) {
                attempted.Add(item.Pending.Detail.ListingId);
            }

            try {
                using var request = IngestRequestFactory.CreatePostJsonRequest(
                    _configuration,
                    endpoint,
                    JsonConvert.SerializeObject(sendable.Select(static item => item.Payload)),
                    capabilityToken: null
                );
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(PartyDetailUploadPolicy.NormalizeDetailUploadTimeoutMs(_configuration.DetailUploadTimeoutMs))
                );
                using var response = await _httpClient.SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode) {
                    UploadAttemptRecorder.RecordSuccess(uploadTarget);
                    foreach (var item in sendable) {
                        uploadTarget.InvalidateDetailCapability(item.Pending.Detail.ListingId);
                    }

                    if (DetailUploadResponseMapper.TryParseDetailBatchResponse(body, out var batchResponse) && batchResponse.Results.Count > 0) {
                        DetailUploadResponseMapper.ApplyBatchResponse(
                            batchResponse,
                            sendable.Select(static item => item.Pending.Detail.ListingId),
                            applied,
                            missing,
                            terminalRejected,
                            retryAfterByListing
                        );
                    } else {
                        _warningLog($"PartyDetailCollector: {endpoint.Url}: unexpected batch response without item results; retrying sent detail payloads");
                    }

                    _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode} {body}");
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) {
                    uploadTarget.MarkDetailBatchUnsupported();
                    _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode}; falling back to single detail uploads");
                    await UploadSinglesToTargetAsync(
                        uploadTarget,
                        sendable.Select(static item => item.Pending).ToList(),
                        applied,
                        missing,
                        terminalRejected,
                        retryAfterByListing,
                        attempted
                    );
                    continue;
                }

                var isAuthFailure = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
                if (isAuthFailure) {
                    uploadTarget.MarkDetailCapabilitiesRequired();
                    foreach (var item in sendable) {
                        uploadTarget.InvalidateDetailCapability(item.Pending.Detail.ListingId);
                    }
                } else {
                    UploadAttemptRecorder.RecordFailure(uploadTarget, _utcNow());
                }

                if ((int)response.StatusCode == 429) {
                    var retryAfter = IngestRequestFactory.ReadRetryAfterSeconds(response);
                    if (retryAfter.HasValue) {
                        foreach (var item in sendable) {
                            retryAfterByListing[item.Pending.Detail.ListingId] = MaxRetryAfterSeconds(
                                retryAfterByListing.GetValueOrDefault(item.Pending.Detail.ListingId),
                                retryAfter.Value
                            );
                        }
                        _warningLog($"PartyDetailCollector: rate limited by {endpoint.Url}, retry_after={retryAfter.Value}s");
                    }
                }

                _debugLog($"PartyDetailCollector: {endpoint.Url}: {response.StatusCode} {body}");
            } catch (OperationCanceledException exception) {
                UploadAttemptRecorder.RecordFailure(uploadTarget, _utcNow());
                _errorLog($"PartyDetailCollector batch upload timed out to {endpoint.Url}: {exception.Message}");
            } catch (Exception exception) {
                UploadAttemptRecorder.RecordFailure(uploadTarget, _utcNow());
                _errorLog($"PartyDetailCollector batch upload error to {endpoint.Url}: {exception.Message}");
            }
        }

        var outcomes = new Dictionary<uint, DetailUploadOutcome>();
        foreach (var pending in pendingBatch) {
            var listingId = pending.Detail.ListingId;
            if (applied.Contains(listingId)) {
                outcomes[listingId] = new DetailUploadOutcome(DetailUploadResult.Applied);
            } else if (terminalRejected.TryGetValue(listingId, out var message)) {
                outcomes[listingId] = new DetailUploadOutcome(DetailUploadResult.TerminalRejected, Message: message);
            } else if (missing.Contains(listingId)) {
                outcomes[listingId] = new DetailUploadOutcome(DetailUploadResult.ListingMissing);
            } else if (!attempted.Contains(listingId)) {
                outcomes[listingId] = new DetailUploadOutcome(DetailUploadResult.Deferred);
            } else {
                outcomes[listingId] = new DetailUploadOutcome(
                    DetailUploadResult.RetryableFailure,
                    retryAfterByListing.GetValueOrDefault(listingId)
                );
            }
        }

        return outcomes;
    }

    private async Task UploadSinglesToTargetAsync(
        UploadUrl uploadTarget,
        IReadOnlyList<PendingDetailEntry> pendingEntries,
        HashSet<uint> applied,
        HashSet<uint> missing,
        Dictionary<uint, string> terminalRejected,
        Dictionary<uint, int?> retryAfterByListing,
        HashSet<uint> attempted
    ) {
        foreach (var pending in pendingEntries) {
            var listingId = pending.Detail.ListingId;
            if (applied.Contains(listingId) || terminalRejected.ContainsKey(listingId)) {
                continue;
            }

            var outcome = await TryUploadSingleToTargetAsync(uploadTarget, pending.Detail);
            if (outcome == null) {
                continue;
            }
            attempted.Add(listingId);

            DetailUploadResponseMapper.MergeUploadOutcome(listingId, outcome.Value, applied, missing, terminalRejected, retryAfterByListing);
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
        UploadablePartyDetail payload
    ) {
        if (UploadAttemptRecorder.IsCircuitOpen(uploadTarget, _configuration, _utcNow())) {
            return null;
        }

        if (!IngestEndpointResolver.TryBuildEndpoint(uploadTarget, IngestRoutes.Detail, out var endpoint)) {
            return null;
        }

        var now = new DateTimeOffset(_utcNow());
        if (uploadTarget.ShouldDeferDetailRequest(payload.ListingId, now)) {
            return null;
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
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(PartyDetailUploadPolicy.NormalizeDetailUploadTimeoutMs(_configuration.DetailUploadTimeoutMs))
            );
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) {
                UploadAttemptRecorder.RecordSuccess(uploadTarget);
                uploadTarget.InvalidateDetailCapability(payload.ListingId);

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
            } else {
                UploadAttemptRecorder.RecordFailure(uploadTarget, _utcNow());
            }

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

    private sealed record SendableDetailBatchItem(
        PendingDetailEntry Pending,
        UploadablePartyDetailBatchItem Payload
    );
}
