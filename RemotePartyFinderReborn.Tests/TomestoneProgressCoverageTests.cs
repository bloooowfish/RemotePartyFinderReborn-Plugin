using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneProgressCoverageTests
{
    static TomestoneProgressCoverageTests() => FFLogsTestAssemblyResolver.Register();

    [Fact]
    public async Task Authoritative_empty_creates_result_with_queried_encounter_coverage()
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = (_, _, _, _, _) => Task.FromResult(TomestoneProgressData.Empty),
        };

        var batchResult = await ProcessSingleJobAsync(progression, zoneId: 73, encounterId: 101);

        Assert.False(batchResult.HadTransientFailure);
        Assert.Empty(batchResult.AbandonedLeases);
        var result = Assert.Single(batchResult.ProcessedResults);
        Assert.Equal("tomestone_api", result.SourceKind);
        Assert.Equal([101], result.TomestoneQueriedEncounters);
        Assert.Empty(result.BossPercentages);
        Assert.Empty(result.PhaseProgress);
        Assert.Empty(result.Encounters);
        Assert.Empty(result.ClearCounts);
        Assert.Equal("lease", result.LeaseToken);

        var serialized = JObject.Parse(JsonConvert.SerializeObject(result));
        Assert.Equal(101, serialized["tomestone_queried_encounters"]![0]!.Value<int>());
        Assert.Equal(101u, serialized["encounter_id"]!.Value<uint>());
        Assert.Equal("tomestone_api", serialized["source_kind"]!.Value<string>());
        Assert.IsType<JObject>(serialized["encounters"]);
        Assert.IsType<JObject>(serialized["clear_counts"]);
    }

    [Fact]
    public async Task Cleared_without_percentiles_abandons_lease_with_tomestone_no_data()
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = (_, _, _, _, _) => Task.FromResult(TomestoneProgressData.ClearedProgress),
        };

        var batchResult = await ProcessSingleJobAsync(progression, zoneId: 73, encounterId: 101);

        Assert.False(batchResult.HadTransientFailure);
        Assert.Empty(batchResult.ProcessedResults);
        var abandon = Assert.Single(batchResult.AbandonedLeases);
        Assert.Equal("tomestone_no_data", abandon.Reason);
        Assert.Equal("tomestone_cleared_without_percentiles", abandon.FailureCode);
        Assert.Equal("lease", abandon.LeaseToken);
    }

    [Theory]
    [InlineData(false, "worker_non_transient_failure", "tomestone_non_transient_failure")]
    [InlineData(true, "worker_transient_failure", "tomestone_transient_failure")]
    public async Task Failed_query_does_not_emit_result_and_abandons_lease(
        bool transient,
        string expectedReason,
        string expectedCode)
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataOutcomeAsync = (_, _, _, _, _) => Task.FromResult(
                OperationOutcome<TomestoneProgressData>.Failure(transient, "API failed")),
        };

        var batchResult = await ProcessSingleJobAsync(progression, zoneId: 73, encounterId: 101);

        Assert.Equal(transient, batchResult.HadTransientFailure);
        Assert.Empty(batchResult.ProcessedResults);
        var abandon = Assert.Single(batchResult.AbandonedLeases);
        Assert.Equal(expectedReason, abandon.Reason);
        Assert.Equal(expectedCode, abandon.FailureCode);
        Assert.Equal("lease", abandon.LeaseToken);
    }

    [Fact]
    public async Task Skipped_query_does_not_emit_result_and_abandons_as_worker_skipped()
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = (_, _, _, _, _) => Task.FromResult(TomestoneProgressData.Skipped),
        };

        var batchResult = await ProcessSingleJobAsync(progression, zoneId: 73, encounterId: 101);

        Assert.False(batchResult.HadTransientFailure);
        Assert.Empty(batchResult.ProcessedResults);
        var abandon = Assert.Single(batchResult.AbandonedLeases);
        Assert.Equal("worker_skipped", abandon.Reason);
        Assert.Equal("tomestone_skipped", abandon.FailureCode);
    }

    [Fact]
    public async Task Cleared_response_does_not_emit_progress_percentages_when_data_also_has_values()
    {
        var progression = new StubTomestoneProgressionService
        {
            OnFetchProgressionDataAsync = (_, _, _, _, _) => Task.FromResult(new TomestoneProgressData(
                [
                    new PhaseProgress
                    {
                        PhaseKey = "p5",
                        PhaseName = "Pandora",
                        Order = 5,
                        BossPercentage = 0.0,
                        DisplayText = "Cleared",
                        Source = "tomestone_api",
                    },
                ],
                BossPercentage: 0.0,
                Cleared: true,
                IsAuthoritative: true)),
        };

        var batchResult = await ProcessSingleJobAsync(progression, zoneId: 65, encounterId: 1079);

        Assert.False(batchResult.HadTransientFailure);
        Assert.Empty(batchResult.ProcessedResults);
        var abandon = Assert.Single(batchResult.AbandonedLeases);
        Assert.Equal("tomestone_no_data", abandon.Reason);
        Assert.Equal("tomestone_cleared_without_percentiles", abandon.FailureCode);
    }

    private static Task<TomestoneBatchProcessResult> ProcessSingleJobAsync(
        StubTomestoneProgressionService progression,
        uint zoneId,
        uint encounterId)
    {
        var seams = TomestoneCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            progression,
            new ManualFFLogsTimeProvider(),
            new Configuration { TomestoneRequestIntervalMs = 0, TomestoneMaxConcurrency = 1 },
            new ApiCredentialStatusStore(),
            static (_, _) => Task.CompletedTask);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                new TomestoneJob
                {
                    ContentId = 1,
                    ZoneId = zoneId,
                    DifficultyId = 101,
                    EncounterId = encounterId,
                    Name = "Test Player",
                    Server = "Tonberry",
                    LeaseToken = "lease",
                },
            ]);
        return new TomestoneBatchProcessor(seams).ProcessLeaseSessionAsync(session, CancellationToken.None);
    }
}
