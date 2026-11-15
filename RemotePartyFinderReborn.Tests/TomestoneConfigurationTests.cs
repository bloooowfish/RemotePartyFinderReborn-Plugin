using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
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
        Assert.False(configuration.HasTomestoneApiKeyConfigured);
        Assert.False(configuration.EnableTomestoneProgressEnrichment);
        Assert.Equal(750, configuration.TomestoneRequestIntervalMs);
        Assert.Equal(1, configuration.TomestoneMaxConcurrency);

        var apiKeyProp = typeof(Configuration).GetProperty(nameof(Configuration.TomestoneApiKey));
        Assert.NotNull(apiKeyProp);
        var converterAttr = apiKeyProp!.GetCustomAttributes<JsonConverterAttribute>().SingleOrDefault();
        Assert.NotNull(converterAttr);
        Assert.Equal(typeof(DpapiProtectedStringJsonConverter), converterAttr!.ConverterType);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("token-123", true)]
    public void HasTomestoneApiKeyConfigured_checks_non_whitespace_key(string apiKey, bool expected)
    {
        var configuration = new Configuration { TomestoneApiKey = apiKey };
        Assert.Equal(expected, configuration.HasTomestoneApiKeyConfigured);
    }

    [Fact]
    public void ResetTomestoneAdvancedDefaults_restores_pacing_defaults()
    {
        var configuration = new Configuration
        {
            TomestoneRequestIntervalMs = 2500,
            TomestoneMaxConcurrency = 3,
        };

        configuration.ResetTomestoneAdvancedDefaults();

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
