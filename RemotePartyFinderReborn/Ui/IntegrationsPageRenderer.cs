#nullable enable

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal sealed class IntegrationsPageRenderer {
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private readonly FFLogsCredentialDraft _fflogsDraft = new();
    private bool _revealFFLogsSecret;
    private bool _revealTomestoneKey;
    private string _fflogsCooldownStatus = string.Empty;
    private string _tomestoneCooldownStatus = string.Empty;

    internal IntegrationsPageRenderer(Plugin plugin) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
    }

    internal void Draw() {
        DrawFFLogsCard();
        ImGui.Dummy(new Vector2(0f, 6f));
        DrawTomestoneCard();
    }

    private void DrawFFLogsCard() {
        _fflogsDraft.SyncFromConfiguration(_configuration);

        UiTheme.DrawCard(
            "IntegrationsFFLogsCard",
            "FFLogs v2 API",
            "Mandatory contributor API client.",
            () => {
                UiTheme.DrawColoredWrappedText(
                    UiTheme.Muted,
                    "FFLogs background worker is permanently enabled while the plugin is unlocked.");
                ImGui.Spacing();

                UiTheme.DrawFormTable("IntegrationsFFLogsForm", () => {
                    UiTheme.FormRow("Client ID", "FFLogs v2 OAuth Client ID.", () => {
                        ImGui.InputText("##fflogsClientId", ref _fflogsDraft.ClientId, 100);
                    });

                    UiTheme.FormRow("Client Secret", "FFLogs v2 OAuth Client Secret.", () => {
                        UiTheme.DrawPasswordInputWithToggle(
                            "fflogsClientSecret",
                            ref _fflogsDraft.ClientSecret,
                            100,
                            ref _revealFFLogsSecret);
                    }, applyTooltipToControl: false);
                });

                ImGui.Spacing();

                using (ImRaii.Disabled(!_fflogsDraft.CanVerify(_plugin))) {
                    if (ImGui.Button("Verify & Update Credentials##fflogsVerify")) {
                        _fflogsDraft.SubmitForVerification(_plugin, _configuration);
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button("Clear & Lock Plugin##fflogsClearLock")) {
                    _plugin.ActivationGate.ClearCredentialsAndLock();
                    _fflogsDraft.ClearLocal();
                }
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("Clears saved FFLogs credentials and returns the plugin to the activation lock screen.");
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var fflogsStatus = _plugin.ApiCredentialStatus.FFLogs;
                var fflogsColor = fflogsStatus.State switch {
                    ApiCredentialValidationState.Accepted => UiTheme.Healthy,
                    ApiCredentialValidationState.Rejected => UiTheme.Danger,
                    ApiCredentialValidationState.RequestFailed => UiTheme.Warning,
                    _ => UiTheme.Info,
                };
                var checkedAt = fflogsStatus.UpdatedAtUtc == DateTime.MinValue
                    ? string.Empty
                    : $" (updated {UiTheme.FormatLocalClock(fflogsStatus.UpdatedAtUtc)} Local)";

                var hasFflogsCooldown = _plugin.TryGetFFLogsRateLimitCooldownRemaining(out var cooldownRemaining);

                UiTheme.DrawKeyValueTable("FFLogsStatusTable", () => {
                    UiTheme.DrawKeyValueRow(
                        "Credential Status",
                        $"{fflogsStatus.Message}{checkedAt}",
                        fflogsColor);

                    DrawRateLimitCooldownRow(
                        "fflogs",
                        hasFflogsCooldown,
                        cooldownRemaining,
                        _plugin.FFLogsRateLimitCooldownUntilUtc,
                        "Clears the local 1-hour FFLogs 429 lockout immediately.",
                        () => {
                            _plugin.ResetFFLogsRateLimitCooldown();
                            _fflogsCooldownStatus = $"[{DateTime.Now:HH:mm:ss}] FFLogs rate-limit lockout reset.";
                        });
                });

                if (!string.IsNullOrEmpty(_fflogsCooldownStatus)) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(UiTheme.Info, _fflogsCooldownStatus);
                }

                ImGui.Spacing();
                if (ImGui.CollapsingHeader("How to create an FFLogs v2 API Client")) {
                    UiTheme.DrawFFLogsClientSetupSteps(
                        "integrations",
                        "6. Paste both Client ID and Client Secret above and click \"Verify & Update Credentials\".");
                }
            },
            headerRight: () => UiTheme.DrawStatusBadge(UiTheme.Healthy, "REQUIRED · ACTIVE", alignToFramePadding: true));
    }

    private void DrawTomestoneCard() {
        var hasCooldown = _plugin.TryGetTomestoneRateLimitCooldownRemaining(out var cooldownRemaining);
        var summary = TomestoneUiStatusEvaluator.Evaluate(
            _configuration,
            _plugin.ApiCredentialStatus.Tomestone,
            hasCooldown,
            cooldownRemaining);
        var summaryColor = UiTheme.ToneColor(summary.Tone);

        UiTheme.DrawCard(
            "IntegrationsTomestoneCard",
            "Tomestone Progression",
            "Optional official API contributor for progression data.",
            () => {
                var enableProgressEnrichment = _configuration.EnableTomestoneProgressEnrichment;
                if (ImGui.Checkbox("Enable Tomestone API contributor", ref enableProgressEnrichment)) {
                    _configuration.EnableTomestoneProgressEnrichment = enableProgressEnrichment;
                    _configuration.Save();
                    if (enableProgressEnrichment
                        && _plugin.ApiCredentialStatus.Tomestone.State == ApiCredentialValidationState.Rejected) {
                        _plugin.ApiCredentialStatus.ResetTomestone();
                    }
                }
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("Contributes official Tomestone API progression lookups to the server. Requires a Tomestone API Key and never blocks FFLogs uploads.");
                }

                ImGui.Spacing();

                var clearWidth = ImGui.CalcTextSize("Clear").X + (ImGui.GetStyle().FramePadding.X * 2f) + ImGui.GetStyle().ItemSpacing.X;
                UiTheme.DrawFormTable("IntegrationsTomestoneForm", () => {
                    UiTheme.FormRow(
                        "API Key (Optional)",
                        "Stored locally using Windows DPAPI and sent only as an Authorization Bearer token for Tomestone API requests.",
                        () => {
                            var apiKey = _configuration.TomestoneApiKey ?? string.Empty;
                            if (UiTheme.DrawPasswordInputWithToggle(
                                    "tomestoneApiKey",
                                    ref apiKey,
                                    200,
                                    ref _revealTomestoneKey,
                                    reserveRightPx: clearWidth)) {
                                _configuration.TomestoneApiKey = apiKey;
                                _configuration.Save();
                                _plugin.ApiCredentialStatus.ResetTomestone();
                            }

                            ImGui.SameLine();
                            using (ImRaii.Disabled(string.IsNullOrEmpty(apiKey))) {
                                if (ImGui.Button("Clear##tomestoneKeyClear")) {
                                    _configuration.TomestoneApiKey = string.Empty;
                                    _configuration.Save();
                                    _plugin.ApiCredentialStatus.ResetTomestone();
                                }
                            }
                        },
                        applyTooltipToControl: false);
                });

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                UiTheme.DrawKeyValueTable("TomestoneStatusTable", () => {
                    UiTheme.DrawKeyValueRow("Status", () => {
                        UiTheme.DrawColoredWrappedText(summaryColor, summary.DetailMessage);
                        if (_plugin.ApiCredentialStatus.Tomestone.State == ApiCredentialValidationState.Rejected) {
                            if (ImGui.Button("Reset Status##tomestoneStatusReset")) {
                                _plugin.ApiCredentialStatus.ResetTomestone();
                            }
                            if (ImGui.IsItemHovered()) {
                                ImGui.SetTooltip("Clears the rejected API key status so the next poll can retry.");
                            }
                        }
                    });

                    DrawRateLimitCooldownRow(
                        "tomestone",
                        hasCooldown,
                        cooldownRemaining,
                        _plugin.TomestoneRateLimitCooldownUntilUtc,
                        "Clears the local 1-hour Tomestone API 429 lockout immediately.",
                        () => {
                            _plugin.ResetTomestoneRateLimitCooldown();
                            _tomestoneCooldownStatus = $"[{DateTime.Now:HH:mm:ss}] Tomestone rate-limit lockout reset.";
                        });
                });

                if (!string.IsNullOrEmpty(_tomestoneCooldownStatus)) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(UiTheme.Info, _tomestoneCooldownStatus);
                }
            },
            headerRight: () => UiTheme.DrawStatusBadge(summaryColor, summary.Badge, alignToFramePadding: true));
    }

    private static void DrawRateLimitCooldownRow(
        string idSuffix,
        bool hasCooldown,
        TimeSpan remaining,
        DateTime cooldownUntilUtc,
        string resetTooltip,
        Action onReset
    ) {
        UiTheme.DrawKeyValueRow("Rate-Limit Cooldown", () => {
            if (hasCooldown) {
                var untilLocal = UiTheme.FormatLocalClock(cooldownUntilUtc);
                UiTheme.DrawColoredText(
                    UiTheme.Warning,
                    $"Active - {remaining.TotalMinutes:F1} min remaining (until {untilLocal} Local)");
            } else {
                UiTheme.DrawColoredText(UiTheme.Healthy, "Inactive");
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(!hasCooldown)) {
                if (ImGui.Button($"Reset Lockout##{idSuffix}CooldownReset")) {
                    onReset();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                ImGui.SetTooltip(resetTooltip);
            }
        });
    }
}
