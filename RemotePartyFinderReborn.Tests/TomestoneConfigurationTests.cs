using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneConfigurationTests
{
    static TomestoneConfigurationTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public void Configuration_defaults_disable_tomestone_enrichment()
    {
        var configuration = new Configuration();

        Assert.Equal(string.Empty, configuration.TomestoneApiKey);
        Assert.False(configuration.EnableTomestoneProgressEnrichment);
        Assert.Equal(750, configuration.TomestoneRequestIntervalMs);
        Assert.Equal(1, configuration.TomestoneMaxConcurrency);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(750, 750)]
    [InlineData(5_000, 5_000)]
    [InlineData(60_000, 60_000)]
    [InlineData(60_001, 60_000)]
    public void Tomestone_request_interval_is_normalized_consistently(int configured, int expected)
    {
        Assert.Equal(expected, Configuration.NormalizeTomestoneRequestIntervalMs(configured));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public void Tomestone_max_concurrency_is_normalized_consistently(int configured, int expected)
    {
        Assert.Equal(expected, Configuration.NormalizeTomestoneMaxConcurrency(configured));
    }
}
