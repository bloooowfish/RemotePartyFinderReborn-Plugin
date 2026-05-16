using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsWorkerRegionProviderTests
{
    [Theory]
    [InlineData("Elemental", "JP")]
    [InlineData("Gaia", "JP")]
    [InlineData("Mana", "JP")]
    [InlineData("Meteor", "JP")]
    [InlineData("Aether", "NA")]
    [InlineData("Primal", "NA")]
    [InlineData("Crystal", "NA")]
    [InlineData("Dynamis", "NA")]
    [InlineData("Chaos", "EU")]
    [InlineData("Light", "EU")]
    [InlineData("Materia", "OC")]
    public void Data_center_name_maps_to_fflogs_worker_region(string dataCenterName, string expected)
    {
        Assert.True(FFLogsWorkerRegionResolver.TryMapDataCenterName(dataCenterName, out var region));
        Assert.Equal(expected, region);
    }

    [Fact]
    public void Unknown_data_center_name_does_not_map_to_global()
    {
        Assert.False(FFLogsWorkerRegionResolver.TryMapDataCenterName("Unknown", out var region));
        Assert.Null(region);
    }

    [Theory]
    [InlineData(23, "JP")]
    [InlineData(73, "NA")]
    [InlineData(402, "EU")]
    [InlineData(21, "OC")]
    public void World_id_maps_to_fflogs_worker_region(ushort worldId, string expected)
    {
        Assert.True(FFLogsWorkerRegionResolver.TryMapWorldId(worldId, out var region));
        Assert.Equal(expected, region);
    }

    [Fact]
    public void Unknown_world_id_does_not_map_to_global()
    {
        Assert.False(FFLogsWorkerRegionResolver.TryMapWorldId(0, out var region));
        Assert.Null(region);
    }

    [Fact]
    public void Cached_provider_returns_region_from_main_thread_snapshot()
    {
        var warnings = new List<string>();
        var provider = new CachedFFLogsWorkerRegionProvider(warnings.Add);

        provider.UpdateCurrentWorld(23);

        Assert.True(provider.TryGetWorkerRegion(out var region));
        Assert.Equal("JP", region);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Cached_provider_logs_unavailable_once_until_region_recovers()
    {
        var warnings = new List<string>();
        var provider = new CachedFFLogsWorkerRegionProvider(warnings.Add);

        provider.UpdateCurrentWorld(null);

        Assert.False(provider.TryGetWorkerRegion(out _));
        Assert.False(provider.TryGetWorkerRegion(out _));

        Assert.Single(warnings);
        Assert.Contains("local player is not available", warnings[0], StringComparison.Ordinal);

        provider.UpdateCurrentWorld(21);
        Assert.True(provider.TryGetWorkerRegion(out var region));
        Assert.Equal("OC", region);

        provider.UpdateCurrentWorld(null);
        Assert.False(provider.TryGetWorkerRegion(out _));

        Assert.Equal(2, warnings.Count);
        Assert.Contains("local player is not available", warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Cached_provider_is_silent_before_first_main_thread_snapshot()
    {
        var warnings = new List<string>();
        var provider = new CachedFFLogsWorkerRegionProvider(warnings.Add);

        Assert.False(provider.HasObservedSnapshot);
        Assert.False(provider.TryGetWorkerRegion(out _));

        Assert.Empty(warnings);
    }

    [Fact]
    public void Cached_provider_clears_region_when_world_is_unmapped()
    {
        var warnings = new List<string>();
        var provider = new CachedFFLogsWorkerRegionProvider(warnings.Add);

        provider.UpdateCurrentWorld(73);
        Assert.True(provider.TryGetWorkerRegion(out var region));
        Assert.Equal("NA", region);

        provider.UpdateCurrentWorld(999);

        Assert.False(provider.TryGetWorkerRegion(out _));
        var warning = Assert.Single(warnings);
        Assert.Contains("current world id 999 is not mapped", warning, StringComparison.Ordinal);
    }
}
