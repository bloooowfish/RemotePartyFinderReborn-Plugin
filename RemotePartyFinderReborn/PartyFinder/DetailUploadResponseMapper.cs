using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

#nullable enable

namespace RemotePartyFinderReborn;

internal static class DetailUploadResponseMapper {
    internal static DetailUploadOutcome MapSuccessfulSingleResponse(
        string rawBody,
        out bool parsed,
        out long matchedCount,
        out long modifiedCount
    ) {
        parsed = TryParseDetailResponse(rawBody, out matchedCount, out modifiedCount);
        if (!parsed) {
            return new DetailUploadOutcome(DetailUploadResult.Applied);
        }

        return matchedCount == 0
            ? new DetailUploadOutcome(DetailUploadResult.ListingMissing)
            : new DetailUploadOutcome(DetailUploadResult.Applied);
    }

    internal static bool TryParseDetailResponse(string rawBody, out long matchedCount, out long modifiedCount) {
        matchedCount = 0;
        modifiedCount = 0;

        if (!IngestJson.TryDeserializeObject(rawBody, out ContributeDetailResponse? parsed)
            || parsed?.MatchedCount is null
            || parsed.ModifiedCount is null) {
            return false;
        }

        matchedCount = parsed.MatchedCount.Value;
        modifiedCount = parsed.ModifiedCount.Value;
        return true;
    }

    internal static bool TryParseDetailBatchResponse(string rawBody, out ContributeDetailBatchResponse batchResponse) {
        batchResponse = null!;

        if (!IngestJson.TryDeserializeObject(rawBody, out ContributeDetailBatchResponse? parsed)
            || parsed?.Results is null) {
            return false;
        }

        batchResponse = parsed;
        return true;
    }

    internal static void ApplyBatchResponse(
        ContributeDetailBatchResponse batchResponse,
        IEnumerable<uint> sendableListingIds,
        HashSet<uint> applied,
        HashSet<uint> missing,
        Dictionary<uint, string> terminalRejected,
        Dictionary<uint, int?> retryAfterByListing
    ) {
        var sendableByListingId = sendableListingIds.ToHashSet();
        var seen = new HashSet<uint>();

        foreach (var result in batchResponse.Results ?? []) {
            if (!sendableByListingId.Contains(result.ListingId) || !seen.Add(result.ListingId)) {
                continue;
            }

            var status = result.Status?.Trim().ToLowerInvariant() ?? string.Empty;
            if (status == "ok" || result.MatchedCount > 0) {
                applied.Add(result.ListingId);
                continue;
            }

            if (status == "missing") {
                missing.Add(result.ListingId);
                continue;
            }

            if (status is "invalid" or "forbidden") {
                terminalRejected[result.ListingId] = string.IsNullOrWhiteSpace(result.Message)
                    ? status
                    : result.Message.Trim();
            }
        }
    }

    internal static void MergeUploadOutcome(
        uint listingId,
        DetailUploadOutcome outcome,
        HashSet<uint> applied,
        HashSet<uint> missing,
        Dictionary<uint, string> terminalRejected,
        Dictionary<uint, int?> retryAfterByListing
    ) {
        switch (outcome.Result) {
            case DetailUploadResult.Applied:
                applied.Add(listingId);
                break;
            case DetailUploadResult.ListingMissing:
                missing.Add(listingId);
                break;
            case DetailUploadResult.TerminalRejected:
                terminalRejected[listingId] = outcome.Message ?? "terminal detail upload rejection";
                break;
            case DetailUploadResult.Deferred:
                break;
            default:
                retryAfterByListing[listingId] = MaxRetryAfterSeconds(
                    retryAfterByListing.GetValueOrDefault(listingId),
                    outcome.RetryAfterSeconds
                );
                break;
        }
    }

    private static int? MaxRetryAfterSeconds(int? current, int? candidate) {
        if (!candidate.HasValue) {
            return current;
        }

        return current.HasValue ? Math.Max(current.Value, candidate.Value) : candidate.Value;
    }
}

internal sealed record ContributeDetailResponse {
    [JsonProperty("matched_count")]
    public long? MatchedCount { get; init; }

    [JsonProperty("modified_count")]
    public long? ModifiedCount { get; init; }
}

internal sealed record ContributeDetailBatchResponse {
    [JsonProperty("status")]
    public string Status { get; init; } = string.Empty;

    [JsonProperty("results")]
    public List<ContributeDetailBatchItemResponse> Results { get; init; } = [];
}

internal sealed record ContributeDetailBatchItemResponse {
    [JsonProperty("listing_id")]
    public uint ListingId { get; init; }

    [JsonProperty("status")]
    public string Status { get; init; } = string.Empty;

    [JsonProperty("matched_count")]
    public long MatchedCount { get; init; }

    [JsonProperty("modified_count")]
    public long ModifiedCount { get; init; }

    [JsonProperty("message")]
    public string? Message { get; init; }
}

internal readonly record struct DetailUploadOutcome(
    DetailUploadResult Result,
    int? RetryAfterSeconds = null,
    string? Message = null
);

internal enum DetailUploadResult {
    Applied,
    ListingMissing,
    TerminalRejected,
    Deferred,
    RetryableFailure,
}
