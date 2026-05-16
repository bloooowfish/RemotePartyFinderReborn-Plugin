using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyDetailUploadResponseMapperTests {
    [Fact]
    public void Successful_single_response_with_zero_matches_maps_to_listing_missing() {
        var outcome = DetailUploadResponseMapper.MapSuccessfulSingleResponse(
            """{"status":"ok","matched_count":0,"modified_count":0}""",
            out var parsed,
            out var matchedCount,
            out var modifiedCount
        );

        Assert.True(parsed);
        Assert.Equal(0, matchedCount);
        Assert.Equal(0, modifiedCount);
        Assert.Equal(DetailUploadResult.ListingMissing, outcome.Result);
    }

    [Fact]
    public void Successful_single_response_with_match_maps_to_applied() {
        var outcome = DetailUploadResponseMapper.MapSuccessfulSingleResponse(
            """{"status":"ok","matched_count":1,"modified_count":1}""",
            out var parsed,
            out var matchedCount,
            out var modifiedCount
        );

        Assert.True(parsed);
        Assert.Equal(1, matchedCount);
        Assert.Equal(1, modifiedCount);
        Assert.Equal(DetailUploadResult.Applied, outcome.Result);
    }

    [Fact]
    public void Malformed_successful_single_response_maps_to_applied() {
        var outcome = DetailUploadResponseMapper.MapSuccessfulSingleResponse(
            """{"status":"ok","matched_count":1""",
            out var parsed,
            out var matchedCount,
            out var modifiedCount
        );

        Assert.False(parsed);
        Assert.Equal(0, matchedCount);
        Assert.Equal(0, modifiedCount);
        Assert.Equal(DetailUploadResult.Applied, outcome.Result);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"status":"ok"}""")]
    [InlineData("""{"status":"ok","matched_count":1}""")]
    [InlineData("""{"status":"ok","modified_count":1}""")]
    public void Incomplete_successful_single_response_maps_to_applied(string rawBody) {
        var outcome = DetailUploadResponseMapper.MapSuccessfulSingleResponse(
            rawBody,
            out var parsed,
            out var matchedCount,
            out var modifiedCount
        );

        Assert.False(parsed);
        Assert.Equal(0, matchedCount);
        Assert.Equal(0, modifiedCount);
        Assert.Equal(DetailUploadResult.Applied, outcome.Result);
    }

    [Fact]
    public void Batch_response_with_null_results_is_unparsed() {
        var parsed = DetailUploadResponseMapper.TryParseDetailBatchResponse(
            """{"status":"ok","results":null}""",
            out _
        );

        Assert.False(parsed);
    }

    [Fact]
    public void Batch_status_ok_maps_to_applied() {
        var state = ApplyBatchResponse("""
            {"status":"ok","results":[
                {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
            ]}
            """, [9001U]);

        Assert.Contains(9001U, state.Applied);
        Assert.Empty(state.Missing);
        Assert.Empty(state.TerminalRejected);
    }

    [Fact]
    public void Batch_status_missing_maps_to_missing() {
        var state = ApplyBatchResponse("""
            {"status":"partial","results":[
                {"listing_id":9001,"status":"missing","matched_count":0,"modified_count":0}
            ]}
            """, [9001U]);

        Assert.Empty(state.Applied);
        Assert.Contains(9001U, state.Missing);
        Assert.Empty(state.TerminalRejected);
    }

    [Theory]
    [InlineData("invalid", "too many members in request")]
    [InlineData("forbidden", "capability resource mismatch")]
    public void Batch_status_invalid_or_forbidden_maps_to_terminal_rejected(string status, string message) {
        var state = ApplyBatchResponse($$"""
            {"status":"partial","results":[
                {"listing_id":9001,"status":"{{status}}","matched_count":0,"modified_count":0,"message":"{{message}}"}
            ]}
            """, [9001U]);

        Assert.Empty(state.Applied);
        Assert.Empty(state.Missing);
        Assert.Equal(message, state.TerminalRejected[9001U]);
    }

    [Fact]
    public void Duplicate_batch_result_is_ignored_after_first_result() {
        var state = ApplyBatchResponse("""
            {"status":"partial","results":[
                {"listing_id":9001,"status":"missing","matched_count":0,"modified_count":0},
                {"listing_id":9001,"status":"ok","matched_count":1,"modified_count":1}
            ]}
            """, [9001U]);

        Assert.Empty(state.Applied);
        Assert.Contains(9001U, state.Missing);
        Assert.Empty(state.TerminalRejected);
    }

    private static BatchMapperState ApplyBatchResponse(string rawBody, uint[] sendableListingIds) {
        Assert.True(DetailUploadResponseMapper.TryParseDetailBatchResponse(rawBody, out var batchResponse));
        var state = new BatchMapperState();

        DetailUploadResponseMapper.ApplyBatchResponse(
            batchResponse,
            sendableListingIds,
            state.Applied,
            state.Missing,
            state.TerminalRejected,
            state.RetryAfterByListing
        );

        return state;
    }

    private sealed class BatchMapperState {
        internal HashSet<uint> Applied { get; } = [];
        internal HashSet<uint> Missing { get; } = [];
        internal Dictionary<uint, string> TerminalRejected { get; } = [];
        internal Dictionary<uint, int?> RetryAfterByListing { get; } = [];
    }
}
