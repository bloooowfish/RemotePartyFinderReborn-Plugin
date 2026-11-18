#nullable enable

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal sealed class AdvancedPageRenderer {
    private readonly Configuration _configuration;

    internal AdvancedPageRenderer(Plugin plugin) {
        ArgumentNullException.ThrowIfNull(plugin);
        _configuration = plugin.Configuration;
    }

    internal void Draw() {
        DrawUploadAndCircuitCard();
        ImGui.Dummy(new Vector2(0f, 6f));
        DrawFFLogsWorkerTimingCard();
        ImGui.Dummy(new Vector2(0f, 6f));
        DrawTomestonePacingCard();
    }

    private void DrawUploadAndCircuitCard() {
        UiTheme.DrawCard(
            "AdvancedUploadCircuitCard",
            "Upload & Circuit Breaker",
            "Endpoint failure tolerance and retry thresholds.",
            () => {
                UiTheme.DrawFormTable("AdvancedUploadCircuitForm", () => {
                    UiTheme.FormRow(
                        "Failure Threshold",
                        "Number of consecutive failures before pausing uploads to an endpoint.",
                        () => {
                            var threshold = _configuration.CircuitBreakerFailureThreshold;
                            if (ImGui.SliderInt("##cbThreshold", ref threshold, 1, 20, "%d failures")) {
                                _configuration.CircuitBreakerFailureThreshold = threshold;
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Break Duration",
                        "Duration to pause uploads after the failure threshold is reached.",
                        () => {
                            var duration = _configuration.CircuitBreakerBreakDurationMinutes;
                            if (ImGui.SliderInt("##cbBreakDuration", ref duration, 1, 60, "%d min")) {
                                _configuration.CircuitBreakerBreakDurationMinutes = duration;
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Detail Upload Retries",
                        "Additional retries after the initial PF detail upload attempt.",
                        () => {
                            var detailUploadRetryCount = _configuration.DetailUploadRetryCount;
                            if (ImGui.SliderInt("##detailUploadRetries", ref detailUploadRetryCount, 0, 5, "%d")) {
                                _configuration.DetailUploadRetryCount = detailUploadRetryCount;
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Plate Resolve Retries",
                        "Additional retries after the initial contentId-to-plate resolve attempt.",
                        () => {
                            var charaCardResolveRetryCount = _configuration.CharaCardResolveRetryCount;
                            if (ImGui.SliderInt("##plateResolveRetries", ref charaCardResolveRetryCount, 0, 5, "%d")) {
                                _configuration.CharaCardResolveRetryCount = charaCardResolveRetryCount;
                                _configuration.Save();
                            }
                        });
                });
            },
            headerRight: () => {
                if (ImGui.Button("Reset Defaults##uploadBehaviorReset")) {
                    _configuration.ResetUploadBehaviorDefaults();
                    _configuration.Save();
                }
            });
    }

    private void DrawFFLogsWorkerTimingCard() {
        UiTheme.DrawCard(
            "AdvancedFFLogsTimingCard",
            "FFLogs Worker Timing",
            "Polling cadence, backoff ceiling, and jitter.",
            () => {
                UiTheme.DrawFormTable("AdvancedFFLogsTimingForm", () => {
                    UiTheme.FormRow(
                        "Base Delay",
                        "Base delay used for backoff calculations and recovery intervals.",
                        () => {
                            var workerBaseDelay = _configuration.FFLogsWorkerBaseDelayMs;
                            if (ImGui.SliderInt("##fflogsBaseDelay", ref workerBaseDelay, 1000, 30000, "%d ms")) {
                                _configuration.FFLogsWorkerBaseDelayMs = workerBaseDelay;
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Idle Delay",
                        "Polling delay when there is no queued work or after successful processing.",
                        () => {
                            var workerIdleDelay = _configuration.FFLogsWorkerIdleDelayMs;
                            if (ImGui.SliderInt("##fflogsIdleDelay", ref workerIdleDelay, 1000, 60000, "%d ms")) {
                                _configuration.FFLogsWorkerIdleDelayMs = workerIdleDelay;
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Max Backoff",
                        "Upper bound for retry backoff after repeated transient failures.",
                        () => {
                            var workerMaxBackoffDelay = _configuration.FFLogsWorkerMaxBackoffDelayMs;
                            if (ImGui.SliderInt("##fflogsMaxBackoff", ref workerMaxBackoffDelay, 5000, 180000, "%d ms")) {
                                _configuration.FFLogsWorkerMaxBackoffDelayMs = workerMaxBackoffDelay;
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Delay Jitter",
                        "Random jitter added to worker delays to reduce synchronized polling spikes.",
                        () => {
                            var workerJitter = _configuration.FFLogsWorkerJitterMs;
                            if (ImGui.SliderInt("##fflogsJitter", ref workerJitter, 0, 10000, "%d ms")) {
                                _configuration.FFLogsWorkerJitterMs = workerJitter;
                                _configuration.Save();
                            }
                        });
                });
            },
            headerRight: () => {
                if (ImGui.Button("Reset Defaults##fflogsTimingReset")) {
                    _configuration.ResetFFLogsWorkerTimingDefaults();
                    _configuration.Save();
                }
            });
    }

    private void DrawTomestonePacingCard() {
        var bypassClamp = _configuration.BypassTomestonePacingClampDebug;
        var minInterval = bypassClamp
            ? Configuration.DebugMinTomestoneRequestIntervalMs
            : Configuration.SafeMinTomestoneRequestIntervalMs;
        var maxInterval = bypassClamp
            ? Configuration.DebugMaxTomestoneRequestIntervalMs
            : Configuration.SafeMaxTomestoneRequestIntervalMs;
        var maxAllowedConcurrency = bypassClamp
            ? Configuration.DebugMaxTomestoneConcurrency
            : Configuration.SafeMaxTomestoneConcurrency;

        UiTheme.DrawCard(
            "AdvancedTomestonePacingCard",
            "Tomestone Request Pacing",
            "Request interval and concurrency for official Tomestone API lookups.",
            () => {
                if (bypassClamp) {
                    UiTheme.DrawColoredWrappedText(
                        UiTheme.Warning,
                        "Debug pacing clamp override is active for this session (resets on plugin reload).");
                    ImGui.Spacing();
                }

                UiTheme.DrawFormTable("AdvancedTomestonePacingForm", () => {
                    UiTheme.FormRow(
                        "Request Interval",
                        bypassClamp
                            ? "Delay between Tomestone requests (debug override active). Ctrl+Click slider to enter an exact number."
                            : "Delay between Tomestone requests (clamped to >= 1000 ms to avoid HTTP 429; override available in Debug).",
                        () => {
                            var requestIntervalMs = _configuration.GetEffectiveTomestoneRequestIntervalMs();
                            if (ImGui.SliderInt("##tomestoneInterval", ref requestIntervalMs, minInterval, maxInterval, "%d ms")) {
                                _configuration.TomestoneRequestIntervalMs =
                                    Configuration.NormalizeTomestoneRequestIntervalMs(requestIntervalMs, bypassClamp);
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Max Concurrency",
                        bypassClamp
                            ? "Maximum number of Tomestone progress requests started in the same wave (debug override active)."
                            : "Locked to 1 to prevent Tomestone API HTTP 429 bursts (override available in Debug).",
                        () => {
                            var maxConcurrency = _configuration.GetEffectiveTomestoneMaxConcurrency();
                            using (ImRaii.Disabled(!bypassClamp)) {
                                if (ImGui.SliderInt("##tomestoneConcurrency", ref maxConcurrency, 1, maxAllowedConcurrency, "%d")) {
                                    _configuration.TomestoneMaxConcurrency =
                                        Configuration.NormalizeTomestoneMaxConcurrency(maxConcurrency, bypassClamp);
                                    _configuration.Save();
                                }
                            }
                        });
                });
            },
            headerRight: () => {
                if (ImGui.Button("Reset Defaults##tomestoneAdvReset")) {
                    _configuration.ResetTomestoneAdvancedDefaults();
                    _configuration.Save();
                }
            });
    }
}
