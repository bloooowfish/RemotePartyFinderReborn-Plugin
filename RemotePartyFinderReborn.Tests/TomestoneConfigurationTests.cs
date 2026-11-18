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
        Assert.Equal(1000, configuration.TomestoneRequestIntervalMs);
        Assert.Equal(1, configuration.TomestoneMaxConcurrency);
        Assert.False(configuration.BypassTomestonePacingClampDebug);

        var apiKeyProp = typeof(Configuration).GetProperty(nameof(Configuration.TomestoneApiKey));
        Assert.NotNull(apiKeyProp);
        var converterAttr = apiKeyProp!.GetCustomAttributes<JsonConverterAttribute>().SingleOrDefault();
        Assert.NotNull(converterAttr);
        Assert.Equal(typeof(DpapiProtectedStringJsonConverter), converterAttr!.ConverterType);

        var bypassProp = typeof(Configuration).GetProperty(
            nameof(Configuration.BypassTomestonePacingClampDebug),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(bypassProp);
        Assert.NotNull(bypassProp!.GetCustomAttribute<JsonIgnoreAttribute>());
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

        Assert.Equal(1000, configuration.TomestoneRequestIntervalMs);
        Assert.Equal(1, configuration.TomestoneMaxConcurrency);
    }

    [Theory]
    [InlineData(-1, false, 1000)]
    [InlineData(0, false, 1000)]
    [InlineData(750, false, 1000)]
    [InlineData(1000, false, 1000)]
    [InlineData(5_000, false, 5_000)]
    [InlineData(60_000, false, 60_000)]
    [InlineData(60_001, false, 60_000)]
    [InlineData(-1, true, 0)]
    [InlineData(0, true, 0)]
    [InlineData(250, true, 250)]
    [InlineData(60_001, true, 60_000)]
    public void Tomestone_request_interval_is_normalized_consistently(int configured, bool bypassClamp, int expected)
    {
        Assert.Equal(expected, Configuration.NormalizeTomestoneRequestIntervalMs(configured, bypassClamp));
    }

    [Theory]
    [InlineData(-1, false, 1)]
    [InlineData(0, false, 1)]
    [InlineData(1, false, 1)]
    [InlineData(3, false, 1)]
    [InlineData(10, false, 1)]
    [InlineData(-1, true, 1)]
    [InlineData(0, true, 1)]
    [InlineData(1, true, 1)]
    [InlineData(3, true, 3)]
    [InlineData(10, true, 10)]
    [InlineData(11, true, 10)]
    public void Tomestone_max_concurrency_is_normalized_consistently(int configured, bool bypassClamp, int expected)
    {
        Assert.Equal(expected, Configuration.NormalizeTomestoneMaxConcurrency(configured, bypassClamp));
    }

    [Fact]
    public void Effective_tomestone_pacing_clamps_unless_session_debug_bypass_is_enabled()
    {
        var configuration = new Configuration
        {
            TomestoneRequestIntervalMs = 50,
            TomestoneMaxConcurrency = 3,
        };

        Assert.Equal(1000, configuration.GetEffectiveTomestoneRequestIntervalMs());
        Assert.Equal(1, configuration.GetEffectiveTomestoneMaxConcurrency());

        configuration.BypassTomestonePacingClampDebug = true;

        Assert.Equal(50, configuration.GetEffectiveTomestoneRequestIntervalMs());
        Assert.Equal(3, configuration.GetEffectiveTomestoneMaxConcurrency());
    }
}
