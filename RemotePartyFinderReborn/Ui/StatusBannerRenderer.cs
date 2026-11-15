#nullable enable

using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal sealed class StatusBannerRenderer {
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;

    internal StatusBannerRenderer(Plugin plugin) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
    }

    internal void Draw() {
        using var cellPadding = UiTheme.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(8f, 6f));
        using var borderColor = UiTheme.PushColor(ImGuiCol.TableBorderStrong, UiTheme.CardBorder);
        using var innerBorderColor = UiTheme.PushColor(ImGuiCol.TableBorderLight, UiTheme.CardDivider);

        if (!ImGui.BeginTable(
                (ImU8String)"StatusBannerBar",
                5,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp)) {
            return;
        }

        try {
            ImGui.TableSetupColumn("##status", ImGuiTableColumnFlags.WidthFixed, 86f);
            ImGui.TableSetupColumn("##endpoints", ImGuiTableColumnFlags.WidthStretch, 1.35f);
            ImGui.TableSetupColumn("##fflogs", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("##tomestone", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("##queues", ImGuiTableColumnFlags.WidthStretch, 1.25f);

            ImGui.TableNextRow();
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(UiTheme.CardHeaderBg));

            // 1. Active indicator
            ImGui.TableNextColumn();
            UiTheme.DrawStatusBadge(UiTheme.Healthy, "Active");
            UiTheme.DrawColoredText(UiTheme.Muted, "Operational");

            // 2. Endpoints + Last PF Sync
            ImGui.TableNextColumn();
            UiTheme.DrawColoredText(UiTheme.Muted, "Endpoints");
            DrawEndpointSummary();
            var lastSync = _plugin.Gatherer.LastSuccessfulUploadAtUtc == DateTime.MinValue
                ? "Waiting"
                : UiTheme.FormatLocalClock(_plugin.Gatherer.LastSuccessfulUploadAtUtc);
            UiTheme.DrawColoredText(UiTheme.Muted, $"Last PF Sync: {lastSync}");

            // 3. FFLogs
            ImGui.TableNextColumn();
            UiTheme.DrawColoredText(UiTheme.Muted, "FFLogs");
            if (_plugin.TryGetFFLogsRateLimitCooldownRemaining(out var fflogsCooldown)) {
                UiTheme.DrawColoredText(UiTheme.Warning, $"Cooldown ({fflogsCooldown.TotalMinutes:F0}m)");
            } else {
                UiTheme.DrawColoredText(UiTheme.Healthy, "Verified");
            }

            // 4. Tomestone
            ImGui.TableNextColumn();
            UiTheme.DrawColoredText(UiTheme.Muted, "Tomestone");
            var hasTomestoneCooldown = _plugin.TryGetTomestoneRateLimitCooldownRemaining(out var tomestoneCooldown);
            var tomestoneSummary = TomestoneUiStatusEvaluator.Evaluate(
                _configuration,
                _plugin.ApiCredentialStatus.Tomestone,
                hasTomestoneCooldown,
                tomestoneCooldown);
            UiTheme.DrawStatusBadge(UiTheme.ToneColor(tomestoneSummary.Tone), tomestoneSummary.Badge);

            // 5. Queues
            ImGui.TableNextColumn();
            UiTheme.DrawColoredText(UiTheme.Muted, "Queues");
            ImGui.TextUnformatted(
                $"Details {_plugin.PendingDetailUploadCount} · Players {_plugin.PendingPlayerUploadCount}");
        } finally {
            ImGui.EndTable();
        }
    }

    private void DrawEndpointSummary() {
        var nowUtc = DateTime.UtcNow;
        var uploadUrls = _configuration.GetUploadUrlsSnapshot();
        var enabledCount = 0;
        var openCount = 0;
        foreach (var url in uploadUrls) {
            if (!url.IsEnabled) {
                continue;
            }

            enabledCount++;
            if (UploadAttemptRecorder.GetCircuitStatus(url, _configuration, nowUtc).IsOpen) {
                openCount++;
            }
        }

        if (enabledCount == 0) {
            UiTheme.DrawColoredText(UiTheme.Warning, "All Disabled");
            return;
        }

        var healthyCount = enabledCount - openCount;
        if (healthyCount == 0) {
            UiTheme.DrawColoredText(UiTheme.Danger, $"{openCount} Blocked");
            return;
        }

        if (openCount > 0) {
            UiTheme.DrawColoredText(UiTheme.Warning, $"{healthyCount}/{enabledCount} healthy");
            return;
        }

        UiTheme.DrawColoredText(UiTheme.Healthy, $"{healthyCount}/{enabledCount} healthy");
    }
}
