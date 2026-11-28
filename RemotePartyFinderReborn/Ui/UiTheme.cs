#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal static class UiTheme {
    internal static readonly Vector4 Healthy = new(0.32f, 0.86f, 0.45f, 1.0f);
    internal static readonly Vector4 Warning = new(0.95f, 0.72f, 0.25f, 1.0f);
    internal static readonly Vector4 Danger = new(0.95f, 0.34f, 0.34f, 1.0f);
    internal static readonly Vector4 Info = new(0.42f, 0.75f, 0.98f, 1.0f);
    internal static readonly Vector4 Muted = new(0.65f, 0.67f, 0.72f, 1.0f);
    internal static readonly Vector4 Accent = new(0.55f, 0.80f, 1.00f, 1.0f);

    internal static readonly Vector4 CardHeaderBg = new(0.16f, 0.19f, 0.24f, 0.95f);
    internal static readonly Vector4 CardBodyBg = new(0.11f, 0.12f, 0.15f, 0.75f);
    internal static readonly Vector4 CardBorder = new(0.28f, 0.32f, 0.39f, 0.90f);
    internal static readonly Vector4 CardDivider = new(0.24f, 0.27f, 0.33f, 0.65f);
    internal static readonly Vector4 WarningHeaderBg = new(0.29f, 0.22f, 0.11f, 0.95f);
    internal static readonly Vector4 DangerHeaderBg = new(0.30f, 0.14f, 0.15f, 0.95f);
    internal static readonly Vector4 SidebarSelectedBg = new(0.23f, 0.38f, 0.56f, 0.70f);

    internal static readonly Vector4 RoleTank = new(0.27f, 0.51f, 0.90f, 1.0f);
    internal static readonly Vector4 RoleHealer = new(0.27f, 0.70f, 0.35f, 1.0f);
    internal static readonly Vector4 RoleDps = new(0.82f, 0.28f, 0.31f, 1.0f);
    internal static readonly Vector4 RoleAny = new(0.62f, 0.65f, 0.71f, 1.0f);
    internal static readonly Vector4 RoleCrafter = new(0.58f, 0.44f, 0.86f, 1.0f);
    internal static readonly Vector4 RoleGatherer = new(0.80f, 0.70f, 0.30f, 1.0f);

    internal static readonly Vector4 RowHoverBg = new(0.19f, 0.23f, 0.30f, 0.85f);
    internal static readonly Vector4 RowSelectedBg = new(0.20f, 0.31f, 0.46f, 0.85f);
    internal static readonly Vector4 EligibleHighlight = new(0.98f, 0.82f, 0.28f, 1.0f);
    internal static readonly Vector4 EligibleSlotBg = new(0.26f, 0.22f, 0.11f, 0.65f);
    internal static readonly Vector4 BlockedRowBg = new(0.18f, 0.10f, 0.11f, 0.78f);
    internal static readonly Vector4 BlockedAccentBar = new(0.86f, 0.28f, 0.30f, 0.85f);
    internal static readonly Vector4 BlockedText = new(0.46f, 0.44f, 0.46f, 1.0f);
    internal static readonly Vector4 UnopenedRowBg = new(0.10f, 0.11f, 0.14f, 0.62f);
    internal static readonly Vector4 UnopenedText = new(0.56f, 0.60f, 0.68f, 1.0f);
    internal static readonly Vector4 UnopenedBadgeBg = new(0.18f, 0.21f, 0.27f, 0.92f);
    internal static readonly Vector4 UnopenedBadgeFg = new(0.68f, 0.74f, 0.84f, 1.0f);
    internal static readonly Vector4 PillBg = new(0.20f, 0.23f, 0.29f, 0.92f);

    internal static readonly Vector4 ParseGray = new(0.60f, 0.62f, 0.67f, 1.0f);
    internal static readonly Vector4 ParseGreen = new(0.12f, 1.00f, 0.00f, 1.0f);
    internal static readonly Vector4 ParseBlue = new(0.00f, 0.44f, 1.00f, 1.0f);
    internal static readonly Vector4 ParsePurple = new(0.64f, 0.21f, 0.93f, 1.0f);
    internal static readonly Vector4 ParseOrange = new(1.00f, 0.50f, 0.00f, 1.0f);
    internal static readonly Vector4 ParsePink = new(0.89f, 0.41f, 0.66f, 1.0f);
    internal static readonly Vector4 ParseGold = new(0.90f, 0.80f, 0.50f, 1.0f);

    internal static Vector4 GetParseColor(byte? percentile, bool hidden = false) {
        if (hidden || !percentile.HasValue) {
            return Muted;
        }

        return percentile.Value switch {
            >= 100 => ParseGold,
            >= 99 => ParsePink,
            >= 95 => ParseOrange,
            >= 75 => ParsePurple,
            >= 50 => ParseBlue,
            >= 25 => ParseGreen,
            _ => ParseGray,
        };
    }

    internal static void DrawPillTag(string text, Vector4 bgColor, Vector4 textColor, string? tooltip = null) {
        var textSize = ImGui.CalcTextSize((ImU8String)text);
        var padX = 6f;
        var padY = 2f;
        var size = new Vector2(textSize.X + (padX * 2f), textSize.Y + (padY * 2f));
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var drawList = ImGui.GetWindowDrawList();
        var max = new Vector2(pos.X + size.X, pos.Y + size.Y);
        drawList.AddRectFilled(pos, max, ImGui.GetColorU32(bgColor), 4f);
        drawList.AddText(new Vector2(pos.X + padX, pos.Y + padY), ImGui.GetColorU32(textColor), (ImU8String)text);
        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered()) {
            ImGui.SetTooltip(tooltip);
        }
    }

    internal static Vector4 ToneColor(TomestoneStatusTone tone) => tone switch {
        TomestoneStatusTone.Healthy => Healthy,
        TomestoneStatusTone.Warning => Warning,
        TomestoneStatusTone.Danger => Danger,
        TomestoneStatusTone.Info => Info,
        _ => Muted,
    };

    internal static StyleColorScope PushColor(ImGuiCol col, Vector4 value, bool enabled = true)
        => new(col, value, enabled);

    internal static StyleVarScope PushStyle(ImGuiStyleVar styleVar, Vector2 value, bool enabled = true)
        => new(styleVar, value, enabled);

    internal readonly ref struct StyleColorScope {
        private readonly bool _pushed;

        internal StyleColorScope(ImGuiCol col, Vector4 value, bool enabled = true) {
            if (enabled) {
                ImGui.PushStyleColor(col, value);
                _pushed = true;
            }
        }

        public void Dispose() {
            if (_pushed) {
                ImGui.PopStyleColor();
            }
        }
    }

    internal readonly ref struct StyleVarScope {
        private readonly bool _pushed;

        internal StyleVarScope(ImGuiStyleVar styleVar, Vector2 value, bool enabled = true) {
            if (enabled) {
                ImGui.PushStyleVar(styleVar, value);
                _pushed = true;
            }
        }

        public void Dispose() {
            if (_pushed) {
                ImGui.PopStyleVar();
            }
        }
    }

    internal static void DrawCard(
        string id,
        string title,
        string? subtitle,
        Action drawBody,
        Action? headerRight = null,
        Vector4? accentHeaderBg = null
    ) {
        using var cellPadding = PushStyle(ImGuiStyleVar.CellPadding, new Vector2(10f, 8f));
        using var borderStrong = PushColor(ImGuiCol.TableBorderStrong, CardBorder);
        using var borderLight = PushColor(ImGuiCol.TableBorderLight, CardDivider);

        if (ImGui.BeginTable((ImU8String)id, 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchSame)) {
            try {
                ImGui.TableSetupColumn("##card", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(accentHeaderBg ?? CardHeaderBg));

                if (headerRight is not null) {
                    using (PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero)) {
                        if (ImGui.BeginTable((ImU8String)$"{id}__hdr", 2, ImGuiTableFlags.SizingFixedFit)) {
                            try {
                                ImGui.TableSetupColumn("##title", ImGuiTableColumnFlags.WidthStretch);
                                ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthFixed);
                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.AlignTextToFramePadding();
                                DrawColoredText(Accent, title);
                                ImGui.TableNextColumn();
                                headerRight();
                            } finally {
                                ImGui.EndTable();
                            }
                        }
                    }
                } else {
                    DrawColoredText(Accent, title);
                }

                if (!string.IsNullOrWhiteSpace(subtitle)) {
                    DrawColoredWrappedText(Muted, subtitle);
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(CardBodyBg));
                using (PushStyle(ImGuiStyleVar.CellPadding, new Vector2(6f, 4f))) {
                    drawBody();
                }
            } finally {
                ImGui.EndTable();
            }
        }
    }

    internal static void DrawFormTable(string id, Action drawRows, float labelWidth = 175f) {
        using var cellPadding = PushStyle(ImGuiStyleVar.CellPadding, new Vector2(6f, 4f));
        if (ImGui.BeginTable((ImU8String)id, 2, ImGuiTableFlags.SizingFixedFit)) {
            try {
                ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, labelWidth);
                ImGui.TableSetupColumn("##control", ImGuiTableColumnFlags.WidthStretch);
                drawRows();
            } finally {
                ImGui.EndTable();
            }
        }
    }

    internal static void FormRow(
        string label,
        string? tooltip,
        Action drawControl,
        bool applyTooltipToControl = true
    ) {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered()) {
            ImGui.SetTooltip(tooltip);
        }

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        drawControl();
        if (applyTooltipToControl && !string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered()) {
            ImGui.SetTooltip(tooltip);
        }
    }

    internal static void DrawKeyValueTable(
        string id,
        IReadOnlyList<(string Label, Action DrawValue)> rows,
        float labelWidth = 175f
    ) {
        using var cellPadding = PushStyle(ImGuiStyleVar.CellPadding, new Vector2(6f, 3f));
        if (ImGui.BeginTable((ImU8String)id, 2, ImGuiTableFlags.SizingFixedFit)) {
            try {
                ImGui.TableSetupColumn("##k", ImGuiTableColumnFlags.WidthFixed, labelWidth);
                ImGui.TableSetupColumn("##v", ImGuiTableColumnFlags.WidthStretch);
                foreach (var (label, drawValue) in rows) {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    DrawColoredText(Muted, label);
                    ImGui.TableNextColumn();
                    drawValue();
                }
            } finally {
                ImGui.EndTable();
            }
        }
    }

    internal static void DrawKeyValueTable(string id, Action drawRows, float labelWidth = 175f) {
        using var cellPadding = PushStyle(ImGuiStyleVar.CellPadding, new Vector2(6f, 3f));
        if (ImGui.BeginTable((ImU8String)id, 2, ImGuiTableFlags.SizingFixedFit)) {
            try {
                ImGui.TableSetupColumn("##k", ImGuiTableColumnFlags.WidthFixed, labelWidth);
                ImGui.TableSetupColumn("##v", ImGuiTableColumnFlags.WidthStretch);
                drawRows();
            } finally {
                ImGui.EndTable();
            }
        }
    }

    internal static void DrawKeyValueRow(string label, string value, Vector4? valueColor = null) {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawColoredText(Muted, label);
        ImGui.TableNextColumn();
        if (valueColor.HasValue) {
            DrawColoredWrappedText(valueColor.Value, value);
        } else {
            DrawWrappedText(value);
        }
    }

    internal static void DrawKeyValueRow(string label, Action drawValue) {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawColoredText(Muted, label);
        ImGui.TableNextColumn();
        drawValue();
    }

    internal static void DrawStatusBadge(Vector4 color, string text, bool alignToFramePadding = false) {
        if (alignToFramePadding) {
            ImGui.AlignTextToFramePadding();
        }

        var height = alignToFramePadding ? ImGui.GetFrameHeight() : ImGui.GetTextLineHeight();
        var pos = ImGui.GetCursorScreenPos();
        var radius = MathF.Max(3.5f, ImGui.GetTextLineHeight() * 0.24f);
        var center = new Vector2(pos.X + radius + 1f, pos.Y + (height * 0.5f));
        ImGui.Dummy(new Vector2((radius * 2f) + 2f, height));
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.GetColorU32(color));
        ImGui.SameLine(0f, 5f);
        if (alignToFramePadding) {
            ImGui.AlignTextToFramePadding();
        }
        DrawColoredText(color, text);
    }

    internal static bool DrawPasswordInputWithToggle(
        string id,
        ref string value,
        int maxLength,
        ref bool revealPassword,
        float reserveRightPx = 0f,
        bool trimOnEdit = false
    ) {
        var eyeReserve = ImGui.GetFrameHeight() + 8f + ImGui.GetStyle().ItemSpacing.X;
        var inputWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - eyeReserve - reserveRightPx);
        ImGui.SetNextItemWidth(inputWidth);

        var flags = revealPassword ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        var edited = ImGui.InputText((ImU8String)$"##{id}", ref value, maxLength, flags);
        if (edited && trimOnEdit) {
            value = value.Trim();
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButton($"##eye_{id}", revealPassword ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye)) {
            revealPassword = !revealPassword;
        }

        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(revealPassword ? "Hide" : "Show");
        }

        return edited;
    }

    internal static void DrawColoredText(Vector4 color, string text) {
        using var pushed = PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    internal static void DrawWrappedText(string text) {
        using var wrap = ImRaii.TextWrapPos(0.0f);
        ImGui.TextUnformatted(text);
    }

    internal static void DrawColoredWrappedText(Vector4 color, string text) {
        using var pushed = PushColor(ImGuiCol.Text, color);
        using var wrap = ImRaii.TextWrapPos(0.0f);
        ImGui.TextUnformatted(text);
    }

    internal static string FormatLocalClock(DateTime utcTime) {
        if (utcTime == DateTime.MinValue) {
            return "-";
        }

        var normalizedUtc = utcTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)
            : utcTime;
        return normalizedUtc.ToLocalTime().ToString("HH:mm:ss");
    }

    internal static string FormatLocalDateTime(DateTime utcTime) {
        if (utcTime == DateTime.MinValue) {
            return "-";
        }

        var normalizedUtc = utcTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)
            : utcTime;
        return normalizedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    internal static void CopyToClipboard(string text) {
        try {
            ImGui.SetClipboardText(text);
        } catch (Exception) {
            // Ignore clipboard access failures.
        }
    }

    internal static string TryGetClipboardText() {
        try {
            return ImGui.GetClipboardText()?.Trim() ?? string.Empty;
        } catch (Exception) {
            return string.Empty;
        }
    }

    internal static void OpenUrl(string url) {
        try {
            Process.Start(new ProcessStartInfo {
                FileName = url,
                UseShellExecute = true,
            });
        } catch (Exception) {
            // Ignore browser launch failures.
        }
    }

    internal static void DrawFFLogsClientSetupSteps(string idPrefix, string finalStepText) {
        const string fflogsApiClientsUrl = "https://www.fflogs.com/api/clients/";

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("1. Open https://www.fflogs.com/api/clients/ or");
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ExternalLinkAlt, $"Open FFLogs API Clients##{idPrefix}OpenClients")) {
            OpenUrl(fflogsApiClientsUrl);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("2. Click \"Create Client\".");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("3. Enter any client name, for example: \"Plugin\"");
        ImGui.SameLine();
        if (ImGui.Button($"Copy##{idPrefix}CopyName")) {
            CopyToClipboard("Plugin");
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("4. Enter any redirect URL, for example: \"https://www.example.com\"");
        ImGui.SameLine();
        if (ImGui.Button($"Copy##{idPrefix}CopyUrl")) {
            CopyToClipboard("https://www.example.com");
        }

        ImGui.Spacing();
        DrawColoredWrappedText(Warning, "5. IMPORTANT: Do NOT check \"Public Client\".");
        ImGui.Spacing();

        DrawWrappedText(finalStepText);
    }
}

internal sealed class FFLogsCredentialDraft {
    private bool _initialized;
    private string _lastSyncedClientId = string.Empty;
    private string _lastSyncedClientSecret = string.Empty;

    internal string ClientId = string.Empty;
    internal string ClientSecret = string.Empty;

    internal void SyncFromConfiguration(Configuration configuration) {
        var configId = configuration.FFLogsClientId ?? string.Empty;
        var configSecret = configuration.FFLogsClientSecret ?? string.Empty;

        if (!_initialized) {
            ClientId = configId;
            ClientSecret = configSecret;
            _lastSyncedClientId = configId;
            _lastSyncedClientSecret = configSecret;
            _initialized = true;
            return;
        }

        if (!string.Equals(configId, _lastSyncedClientId, StringComparison.Ordinal)) {
            ClientId = configId;
            _lastSyncedClientId = configId;
        }

        if (!string.Equals(configSecret, _lastSyncedClientSecret, StringComparison.Ordinal)) {
            ClientSecret = configSecret;
            _lastSyncedClientSecret = configSecret;
        }
    }

    internal bool CanVerify(Plugin plugin)
        => !string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(ClientSecret)
            && !plugin.ActivationGate.IsVerificationInFlight;

    internal void SubmitForVerification(Plugin plugin, Configuration configuration) {
        var trimmedId = ClientId.Trim();
        var trimmedSecret = ClientSecret.Trim();
        if (string.IsNullOrWhiteSpace(trimmedId) || string.IsNullOrWhiteSpace(trimmedSecret)) {
            return;
        }

        ClientId = trimmedId;
        ClientSecret = trimmedSecret;
        _lastSyncedClientId = trimmedId;
        _lastSyncedClientSecret = trimmedSecret;

        configuration.FFLogsClientId = trimmedId;
        configuration.FFLogsClientSecret = trimmedSecret;
        configuration.VerifiedFFLogsCredentialFingerprint = string.Empty;
        configuration.EnableFFLogsWorker = true;
        configuration.Save();

        plugin.ApiCredentialStatus.ResetFFLogs();
        _ = plugin.VerifyFFLogsCredentialsAsync();
    }

    internal void ClearLocal() {
        ClientId = string.Empty;
        ClientSecret = string.Empty;
        _lastSyncedClientId = string.Empty;
        _lastSyncedClientSecret = string.Empty;
    }
}
