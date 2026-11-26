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
    [InlineData("Shadow", "EU")]
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
    [InlineData(412, "EU")]
    [InlineData(413, "EU")]
    [InlineData(414, "EU")]
    [InlineData(415, "EU")]
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

    [Theory]
    [InlineData(1u, "JP")]
    [InlineData(2u, "NA")]
    [InlineData(3u, "EU")]
    [InlineData(4u, "OC")]
    public void Data_center_region_id_maps_to_fflogs_worker_region(uint regionId, string expected)
    {
        Assert.True(FFLogsWorkerRegionResolver.TryMapDataCenterRegionId(regionId, out var region));
        Assert.Equal(expected, region);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    public void Unknown_data_center_region_id_does_not_map(uint regionId)
    {
        Assert.False(FFLogsWorkerRegionResolver.TryMapDataCenterRegionId(regionId, out var region));
        Assert.Null(region);
    }

    [Fact]
    public void Cached_provider_uses_fallback_resolver_for_new_unmapped_world_id()
    {
        var warnings = new List<string>();
        var provider = new CachedFFLogsWorkerRegionProvider(
            warnings.Add,
            fallbackRegionResolver: worldId => worldId == 999 ? "NA" : null);

        provider.UpdateCurrentWorld(999);

        Assert.True(provider.TryGetWorkerRegion(out var region));
        Assert.Equal("NA", region);
        Assert.Empty(warnings);

        provider.UpdateCurrentWorld(998);

        Assert.False(provider.TryGetWorkerRegion(out _));
        var warning = Assert.Single(warnings);
        Assert.Contains("current world id 998 is not mapped", warning, StringComparison.Ordinal);
    }
}
