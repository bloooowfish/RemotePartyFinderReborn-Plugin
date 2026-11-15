#nullable enable

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal sealed class OnboardingGateRenderer {
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private readonly FFLogsCredentialDraft _credentialDraft = new();
    private bool _revealSecret;
    private readonly ExpiringUiStatus _pasteStatus = new();

    internal OnboardingGateRenderer(Plugin plugin) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
    }

    internal void Draw() {
        _credentialDraft.SyncFromConfiguration(_configuration);

        var snapshot = _plugin.ActivationGate.GetSnapshot();
        var (headerBg, badgeColor, badgeText) = snapshot.State switch {
            PluginActivationState.Verifying => (UiTheme.CardHeaderBg, UiTheme.Info, "VERIFYING"),
            PluginActivationState.Rejected => (UiTheme.DangerHeaderBg, UiTheme.Danger, "REJECTED"),
            PluginActivationState.VerificationFailed => (UiTheme.WarningHeaderBg, UiTheme.Warning, "FAILED"),
            _ => (UiTheme.WarningHeaderBg, UiTheme.Warning, "LOCKED"),
        };

        UiTheme.DrawCard(
            "OnboardingGateBanner",
            "FFLogs v2 API Registration Required",
            "Mandatory contributor verification gate",
            () => {
                UiTheme.DrawWrappedText(
                    "FFLogs v2 API credentials are required to unlock Remote Party Finder Reborn. " +
                    "Every contributor helps process Party Finder log lookups with their own API client to share workload fairly and prevent free-riding.");
                ImGui.Spacing();
                UiTheme.DrawColoredWrappedText(
                    UiTheme.Warning,
                    "All Party Finder capture, uploads, player identity cache, and Tomestone enrichment features are disabled until your credentials are verified.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var statusLabel = snapshot.State switch {
                    PluginActivationState.Verifying => "Verifying",
                    PluginActivationState.Rejected => "Rejected",
                    PluginActivationState.VerificationFailed => "Verification Failed",
                    _ => "Awaiting Verification",
                };
                var checkedSuffix = snapshot.UpdatedAtUtc == DateTime.MinValue
                    ? string.Empty
                    : $" (checked {UiTheme.FormatLocalClock(snapshot.UpdatedAtUtc)} Local)";
                UiTheme.DrawStatusBadge(badgeColor, $"{statusLabel}:");
                ImGui.SameLine();
                UiTheme.DrawColoredWrappedText(badgeColor, $"{snapshot.StatusMessage}{checkedSuffix}");
            },
            headerRight: () => UiTheme.DrawStatusBadge(badgeColor, badgeText, alignToFramePadding: true),
            accentHeaderBg: headerBg);

        ImGui.Dummy(new Vector2(0f, 6f));

        UiTheme.DrawCard(
            "OnboardingStep1",
            "Step 1 — Register Credentials",
            "Enter your personal FFLogs v2 OAuth client credentials below.",
            () => {
                var pasteWidth = ImGui.CalcTextSize("Paste").X + (ImGui.GetStyle().FramePadding.X * 2f) + ImGui.GetStyle().ItemSpacing.X;

                UiTheme.DrawFormTable("OnboardingCredentialsForm", () => {
                    UiTheme.FormRow("Client ID", "FFLogs v2 OAuth Client ID.", () => {
                        var inputWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - pasteWidth);
                        ImGui.SetNextItemWidth(inputWidth);
                        ImGui.InputTextWithHint("##onboardClientId", "Paste FFLogs v2 Client ID", ref _credentialDraft.ClientId, 100);
                        ImGui.SameLine();
                        if (ImGui.Button("Paste##onboardPasteId")) {
                            TryPasteFromClipboard("Client ID", ref _credentialDraft.ClientId);
                        }
                    }, applyTooltipToControl: false);

                    UiTheme.FormRow("Client Secret", "FFLogs v2 OAuth Client Secret.", () => {
                        UiTheme.DrawPasswordInputWithToggle(
                            "onboardClientSecret",
                            ref _credentialDraft.ClientSecret,
                            100,
                            ref _revealSecret,
                            reserveRightPx: pasteWidth);
                        ImGui.SameLine();
                        if (ImGui.Button("Paste##onboardPasteSecret")) {
                            TryPasteFromClipboard("Client Secret", ref _credentialDraft.ClientSecret);
                        }
                    }, applyTooltipToControl: false);
                });

                var pasteNote = _pasteStatus.Current(DateTime.UtcNow);
                if (!string.IsNullOrEmpty(pasteNote)) {
                    UiTheme.DrawColoredWrappedText(UiTheme.Info, pasteNote);
                }

                UiTheme.DrawColoredWrappedText(UiTheme.Muted, "Encrypted locally using Windows DPAPI.");
                ImGui.Spacing();

                using (ImRaii.Disabled(!_credentialDraft.CanVerify(_plugin))) {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Unlock, "Verify & Unlock Plugin")) {
                        _credentialDraft.SubmitForVerification(_plugin, _configuration);
                    }
                }
            });

        ImGui.Dummy(new Vector2(0f, 6f));

        UiTheme.DrawCard(
            "OnboardingStep2",
            "Step 2 — How to Create an FFLogs v2 API Client",
            "Takes less than a minute on fflogs.com.",
            () => UiTheme.DrawFFLogsClientSetupSteps(
                "onboard",
                "6. Copy the Client ID and Client Secret into Step 1 above, then click \"Verify & Unlock Plugin\"."));
    }

    private void TryPasteFromClipboard(string fieldLabel, ref string target) {
        var clip = UiTheme.TryGetClipboardText();
        if (!string.IsNullOrEmpty(clip)) {
            target = clip;
            _pasteStatus.Set($"Pasted {fieldLabel} from clipboard.", DateTime.UtcNow, TimeSpan.FromSeconds(4));
            return;
        }

        _pasteStatus.Set("Clipboard is empty.", DateTime.UtcNow, TimeSpan.FromSeconds(4));
    }
}
