using System;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class UiNavigationStateTests {
    static UiNavigationStateTests() {
        DalamudAssemblyResolver.Register();
    }

    [Fact]
    public void Navigation_state_defaults_to_endpoints_and_queues_subtab() {
        var state = new UiNavigationState();

        Assert.Equal(ConfigNavPage.Endpoints, state.SelectedPage);
        Assert.Equal(DebugSubTab.QueuesAndTelemetry, state.SelectedDebugSubTab);
    }

    [Theory]
    [InlineData("Endpoints")]
    [InlineData("Integrations")]
    public void GetEffectivePage_keeps_standard_pages_when_advanced_disabled(string pageName) {
        var page = Enum.Parse<ConfigNavPage>(pageName);
        var state = new UiNavigationState { SelectedPage = page };

        var effective = state.GetEffectivePage(advancedEnabled: false);

        Assert.Equal(page, effective);
        Assert.Equal(page, state.SelectedPage);
    }

    [Theory]
    [InlineData("Advanced")]
    [InlineData("Debug")]
    public void GetEffectivePage_resets_developer_pages_to_endpoints_when_advanced_disabled(string pageName) {
        var page = Enum.Parse<ConfigNavPage>(pageName);
        var state = new UiNavigationState { SelectedPage = page };

        var effective = state.GetEffectivePage(advancedEnabled: false);

        Assert.Equal(ConfigNavPage.Endpoints, effective);
        Assert.Equal(ConfigNavPage.Endpoints, state.SelectedPage);
    }

    [Theory]
    [InlineData("Endpoints")]
    [InlineData("Integrations")]
    [InlineData("Advanced")]
    [InlineData("Debug")]
    public void GetEffectivePage_preserves_all_pages_when_advanced_enabled(string pageName) {
        var page = Enum.Parse<ConfigNavPage>(pageName);
        var state = new UiNavigationState { SelectedPage = page };

        var effective = state.GetEffectivePage(advancedEnabled: true);

        Assert.Equal(page, effective);
        Assert.Equal(page, state.SelectedPage);
    }

    [Fact]
    public void Evaluator_reports_rate_limited_warning_when_cooldown_active() {
        var configuration = new Configuration {
            TomestoneApiKey = "valid-key",
            EnableTomestoneProgressEnrichment = true,
        };
        var status = new ApiCredentialStatusSnapshot(ApiCredentialValidationState.Accepted, "Verified", DateTime.UtcNow);

        var summary = TomestoneUiStatusEvaluator.Evaluate(
            configuration,
            status,
            hasCooldown: true,
            cooldownRemaining: TimeSpan.FromMinutes(42.5));

        Assert.Equal(TomestoneStatusTone.Warning, summary.Tone);
        Assert.Equal("Rate-Limited", summary.Badge);
        Assert.Contains("42.5 min", summary.DetailMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "NotChecked")]
    [InlineData("saved-key", "Rejected")]
    public void Evaluator_reports_off_optional_when_disabled_even_if_key_is_saved(
        string apiKey,
        string stateName) {
        var configuration = new Configuration {
            TomestoneApiKey = apiKey,
            EnableTomestoneProgressEnrichment = false,
        };
        var state = Enum.Parse<ApiCredentialValidationState>(stateName);
        var status = new ApiCredentialStatusSnapshot(state, "Ignored while disabled", DateTime.UtcNow);

        var summary = TomestoneUiStatusEvaluator.Evaluate(configuration, status, hasCooldown: true, TimeSpan.FromMinutes(10));

        Assert.Equal(TomestoneStatusTone.Muted, summary.Tone);
        Assert.Equal("Off (Optional)", summary.Badge);
        Assert.Equal("Optional Tomestone API contributor is disabled.", summary.DetailMessage);
    }

    [Fact]
    public void Evaluator_reports_no_api_key_warning_when_enabled_without_key() {
        var configuration = new Configuration {
            TomestoneApiKey = "   ",
            EnableTomestoneProgressEnrichment = true,
        };
        var status = new ApiCredentialStatusSnapshot(ApiCredentialValidationState.NotChecked, string.Empty, DateTime.MinValue);

        var summary = TomestoneUiStatusEvaluator.Evaluate(configuration, status, hasCooldown: false, TimeSpan.Zero);

        Assert.Equal(TomestoneStatusTone.Warning, summary.Tone);
        Assert.Equal("No API Key", summary.Badge);
        Assert.Equal("Enter a Tomestone API Key to enable lookups.", summary.DetailMessage);
    }

    [Fact]
    public void Evaluator_reports_accepted_api_key() {
        var configuration = new Configuration {
            TomestoneApiKey = "secret-key",
            EnableTomestoneProgressEnrichment = true,
        };
        var status = new ApiCredentialStatusSnapshot(ApiCredentialValidationState.Accepted, "Accepted by Tomestone.", DateTime.UtcNow);

        var summary = TomestoneUiStatusEvaluator.Evaluate(configuration, status, hasCooldown: false, TimeSpan.Zero);

        Assert.Equal(TomestoneStatusTone.Healthy, summary.Tone);
        Assert.Equal("API Verified", summary.Badge);
        Assert.Equal("Accepted by Tomestone.", summary.DetailMessage);
    }

    [Theory]
    [InlineData("", "Tomestone API Key was rejected.")]
    [InlineData("401 Unauthorized", "401 Unauthorized")]
    public void Evaluator_reports_rejected_api_key(string statusMessage, string expectedDetail) {
        var configuration = new Configuration {
            TomestoneApiKey = "bad-key",
            EnableTomestoneProgressEnrichment = true,
        };
        var status = new ApiCredentialStatusSnapshot(ApiCredentialValidationState.Rejected, statusMessage, DateTime.UtcNow);

        var summary = TomestoneUiStatusEvaluator.Evaluate(configuration, status, hasCooldown: false, TimeSpan.Zero);

        Assert.Equal(TomestoneStatusTone.Danger, summary.Tone);
        Assert.Equal("API Rejected", summary.Badge);
        Assert.Equal(expectedDetail, summary.DetailMessage);
    }

    [Theory]
    [InlineData("", "Last Tomestone API check failed.")]
    [InlineData("Timeout contacting tomestone.gg", "Timeout contacting tomestone.gg")]
    public void Evaluator_reports_request_failed_api_key(string statusMessage, string expectedDetail) {
        var configuration = new Configuration {
            TomestoneApiKey = "secret-key",
            EnableTomestoneProgressEnrichment = true,
        };
        var status = new ApiCredentialStatusSnapshot(ApiCredentialValidationState.RequestFailed, statusMessage, DateTime.UtcNow);

        var summary = TomestoneUiStatusEvaluator.Evaluate(configuration, status, hasCooldown: false, TimeSpan.Zero);

        Assert.Equal(TomestoneStatusTone.Warning, summary.Tone);
        Assert.Equal("Check Failed", summary.Badge);
        Assert.Equal(expectedDetail, summary.DetailMessage);
    }

    [Fact]
    public void Evaluator_reports_not_checked_api_key() {
        var configuration = new Configuration {
            TomestoneApiKey = "secret-key",
            EnableTomestoneProgressEnrichment = true,
        };
        var status = new ApiCredentialStatusSnapshot(ApiCredentialValidationState.NotChecked, "Waiting", DateTime.MinValue);

        var summary = TomestoneUiStatusEvaluator.Evaluate(configuration, status, hasCooldown: false, TimeSpan.Zero);

        Assert.Equal(TomestoneStatusTone.Info, summary.Tone);
        Assert.Equal("API Configured", summary.Badge);
        Assert.Equal("Tomestone API Key configured (waiting for next request).", summary.DetailMessage);
    }
}
