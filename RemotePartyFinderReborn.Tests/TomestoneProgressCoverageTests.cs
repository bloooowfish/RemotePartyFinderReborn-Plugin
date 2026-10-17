using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneProgressCoverageTests
{
    static TomestoneProgressCoverageTests() => FFLogsTestAssemblyResolver.Register();

    [Fact]
    public async Task Authoritative_empty_creates_progress_only_result_with_coverage()
    {
        var results = new Dictionary<ulong, ParseResult>();
        await Enrich(new StubTomestoneApiClient(), results);

        var result = Assert.Single(results).Value;
        Assert.Equal("tomestone_api", result.SourceKind);
        Assert.Equal([101], result.TomestoneQueriedEncounters);
        Assert.Empty(result.BossPercentages);
        Assert.Equal(101, JObject.Parse(JsonConvert.SerializeObject(result))["tomestone_queried_encounters"]![0]!.Value<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_empty_or_clear_removes_only_queried_encounter(bool cleared)
    {
        var result = new ParseResult
        {
            ContentId = 1,
            BossPercentages = new() { [101] = 20, [102] = 30 },
            PhaseProgress = new() { [101] = [], [102] = [] },
        };
        var client = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = (_, _, _, _, _) => Task.FromResult(
                cleared ? TomestoneProgressData.ClearedProgress : TomestoneProgressData.Empty),
        };

        await Enrich(client, new() { [1] = result });

        Assert.Equal([101], result.TomestoneQueriedEncounters);
        Assert.False(result.BossPercentages.ContainsKey(101));
        Assert.False(result.PhaseProgress.ContainsKey(101));
        Assert.Equal(30, result.BossPercentages[102]);
        Assert.True(result.PhaseProgress.ContainsKey(102));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_query_does_not_mark_or_remove_progress(bool transient)
    {
        var result = new ParseResult { ContentId = 1, BossPercentages = new() { [101] = 20 } };
        var client = new StubTomestoneApiClient
        {
            OnFetchProgressionDataOutcomeAsync = (_, _, _, _, _) => Task.FromResult(
                OperationOutcome<TomestoneProgressData>.Failure(transient, "API failed")),
        };

        Assert.Equal(transient, await Enrich(client, new() { [1] = result }));
        Assert.Empty(result.TomestoneQueriedEncounters);
        Assert.Equal(20, result.BossPercentages[101]);

        var noResults = new Dictionary<ulong, ParseResult>();
        await Enrich(client, noResults);
        Assert.Empty(noResults);
    }

    [Fact]
    public async Task Skipped_query_does_not_claim_authoritative_empty()
    {
        var client = new StubTomestoneApiClient
        {
            OnFetchProgressionDataAsync = (_, _, _, _, _) => Task.FromResult(TomestoneProgressData.Skipped),
        };
        var results = new Dictionary<ulong, ParseResult>();
        await Enrich(client, results);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Hidden_fflogs_result_is_preserved_without_requesting_tomestone()
    {
        var hidden = new ParseResult { ContentId = 1, IsHidden = true, SourceKind = "fflogs" };
        var results = new Dictionary<ulong, ParseResult> { [1] = hidden };
        var client = new StubTomestoneApiClient();

        await Enrich(client, results);

        Assert.Same(hidden, results[1]);
        Assert.Empty(hidden.TomestoneQueriedEncounters);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Successful_primary_and_failed_secondary_mark_only_primary()
    {
        var client = new StubTomestoneApiClient
        {
            OnFetchProgressionDataOutcomeAsync = (_, _, _, encounter, _) => Task.FromResult(
                encounter == 104
                    ? OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty)
                    : OperationOutcome<TomestoneProgressData>.Failure(true, "429")),
        };
        var results = new Dictionary<ulong, ParseResult>();
        await Enrich(client, results, encounter: 104, secondary: 105);
        Assert.Equal([104], Assert.Single(results).Value.TomestoneQueriedEncounters);
    }

    private static Task<bool> Enrich(
        StubTomestoneApiClient client,
        Dictionary<ulong, ParseResult> results,
        uint encounter = 101,
        uint? secondary = null)
    {
        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            new StubFFLogsApiClient(),
            new ManualFFLogsTimeProvider(),
            client,
            new Configuration(),
            static (_, _) => Task.CompletedTask);
        var job = new ParseJob
        {
            ContentId = 1,
            ZoneId = 73,
            DifficultyId = 101,
            EncounterId = encounter,
            SecondaryEncounterId = secondary,
            Name = "Test Player",
            Server = "Tonberry",
            LeaseToken = "lease",
        };
        return new TomestoneProgressEnricher(seams).EnrichProgressAsync([job], results, 73, CancellationToken.None);
    }
}
