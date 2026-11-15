#nullable enable

using System;

namespace RemotePartyFinderReborn;

internal enum ConfigNavPage {
    Endpoints,
    Integrations,
    Advanced,
    Debug,
}

internal enum DebugSubTab {
    QueuesAndTelemetry,
    AutoScanner,
    LatestPfDetail,
    CharaCard,
}

internal enum TomestoneStatusTone {
    Muted,
    Info,
    Healthy,
    Warning,
    Danger,
}

internal readonly record struct TomestoneUiStatusSummary(
    TomestoneStatusTone Tone,
    string Badge,
    string DetailMessage
);

internal sealed class UiNavigationState {
    internal ConfigNavPage SelectedPage { get; set; } = ConfigNavPage.Endpoints;
    internal DebugSubTab SelectedDebugSubTab { get; set; } = DebugSubTab.QueuesAndTelemetry;

    internal ConfigNavPage GetEffectivePage(bool advancedEnabled) {
        if (!advancedEnabled && SelectedPage is ConfigNavPage.Advanced or ConfigNavPage.Debug) {
            SelectedPage = ConfigNavPage.Endpoints;
            return ConfigNavPage.Endpoints;
        }

        return SelectedPage;
    }
}

internal static class TomestoneUiStatusEvaluator {
    internal static TomestoneUiStatusSummary Evaluate(
        Configuration configuration,
        ApiCredentialStatusSnapshot apiStatus,
        bool hasCooldown,
        TimeSpan cooldownRemaining
    ) {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(apiStatus);

        if (!configuration.EnableTomestoneProgressEnrichment) {
            return new TomestoneUiStatusSummary(
                TomestoneStatusTone.Muted,
                "Off (Optional)",
                "Optional Tomestone API contributor is disabled.");
        }

        if (!configuration.HasTomestoneApiKeyConfigured) {
            return new TomestoneUiStatusSummary(
                TomestoneStatusTone.Warning,
                "No API Key",
                "Enter a Tomestone API Key to enable lookups.");
        }

        if (hasCooldown) {
            return new TomestoneUiStatusSummary(
                TomestoneStatusTone.Warning,
                "Rate-Limited",
                $"Rate-limit cooldown active ({cooldownRemaining.TotalMinutes:F1} min remaining).");
        }

        return apiStatus.State switch {
            ApiCredentialValidationState.Accepted => new TomestoneUiStatusSummary(
                TomestoneStatusTone.Healthy,
                "API Verified",
                string.IsNullOrWhiteSpace(apiStatus.Message)
                    ? "Tomestone API Key verified."
                    : apiStatus.Message),
            ApiCredentialValidationState.Rejected => new TomestoneUiStatusSummary(
                TomestoneStatusTone.Danger,
                "API Rejected",
                string.IsNullOrWhiteSpace(apiStatus.Message)
                    ? "Tomestone API Key was rejected."
                    : apiStatus.Message),
            ApiCredentialValidationState.RequestFailed => new TomestoneUiStatusSummary(
                TomestoneStatusTone.Warning,
                "Check Failed",
                string.IsNullOrWhiteSpace(apiStatus.Message)
                    ? "Last Tomestone API check failed."
                    : apiStatus.Message),
            _ => new TomestoneUiStatusSummary(
                TomestoneStatusTone.Info,
                "API Configured",
                "Tomestone API Key configured (waiting for next request)."),
        };
    }
}
