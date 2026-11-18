#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal sealed class EndpointsPageRenderer {
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private string _uploadUrlTempString = string.Empty;
    private readonly ExpiringUiStatus _uploadUrlError = new();
    private string _playerManualUploadStatus = string.Empty;

    internal EndpointsPageRenderer(Plugin plugin) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
    }

    internal void Draw() {
        DrawUploadEndpointsCard();
        ImGui.Dummy(new Vector2(0f, 6f));
        DrawPlayerCacheCard();
    }

    private void DrawUploadEndpointsCard() {
        UiTheme.DrawCard(
            "EndpointsCard",
            "Upload Endpoints",
            "Configure which endpoints receive party finder data.",
            () => {
                var uploadUrls = _configuration.GetUploadUrlsSnapshot();
                var nowUtc = DateTime.UtcNow;
                var threshold = _configuration.CircuitBreakerFailureThreshold;

                using (UiTheme.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(6f, 4f))) {
                    if (ImGui.BeginTable(
                            (ImU8String)"EndpointsTable",
                            4,
                            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) {
                        try {
                            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30f);
                            ImGui.TableSetupColumn("Endpoint URL", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Circuit Health", ImGuiTableColumnFlags.WidthFixed, 130f);
                            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 96f);
                            ImGui.TableHeadersRow();

                            foreach (var uploadUrl in uploadUrls) {
                                using var rowId = ImRaii.PushId((ImU8String)uploadUrl.Id.ToString("N"));
                                ImGui.TableNextRow();

                                // On
                                ImGui.TableNextColumn();
                                var isEnabled = uploadUrl.IsEnabled;
                                if (ImGui.Checkbox("##enabled", ref isEnabled)) {
                                    uploadUrl.IsEnabled = isEnabled;
                                    _configuration.Save();
                                }

                                // Endpoint URL
                                ImGui.TableNextColumn();
                                ImGui.AlignTextToFramePadding();
                                ImGui.TextUnformatted(uploadUrl.Url);
                                if (uploadUrl.IsDefault) {
                                    ImGui.SameLine();
                                    UiTheme.DrawColoredText(UiTheme.Muted, "(Default)");
                                }

                                // Circuit Health
                                ImGui.TableNextColumn();
                                ImGui.AlignTextToFramePadding();
                                var circuitStatus = UploadAttemptRecorder.GetCircuitStatus(uploadUrl, _configuration, nowUtc);
                                if (!uploadUrl.IsEnabled) {
                                    UiTheme.DrawColoredText(UiTheme.Muted, "Disabled");
                                } else if (circuitStatus.IsOpen) {
                                    UiTheme.DrawColoredText(
                                        UiTheme.Danger,
                                        $"Open ({circuitStatus.RemainingBreakDuration.TotalSeconds:F0}s)");
                                } else if (circuitStatus.FailureCount >= threshold) {
                                    UiTheme.DrawColoredText(UiTheme.Warning, "Half-Open");
                                } else if (circuitStatus.FailureCount > 0) {
                                    UiTheme.DrawColoredText(
                                        UiTheme.Warning,
                                        $"Degraded ({circuitStatus.FailureCount}/{threshold})");
                                } else {
                                    UiTheme.DrawColoredText(UiTheme.Healthy, $"Healthy (0/{threshold})");
                                }

                                // Actions
                                ImGui.TableNextColumn();
                                var canReset = circuitStatus.FailureCount > 0 || circuitStatus.IsOpen;
                                using (ImRaii.Disabled(!canReset)) {
                                    if (ImGui.Button($"Reset##{uploadUrl.Id:N}")) {
                                        UploadAttemptRecorder.RecordSuccess(uploadUrl);
                                        _configuration.Save();
                                    }
                                }

                                if (CanRemoveEndpoint(uploadUrls, uploadUrl)) {
                                    ImGui.SameLine();
                                    if (ImGuiComponents.IconButton($"##del_{uploadUrl.Id:N}", FontAwesomeIcon.Trash)) {
                                        RemoveEndpoint(_configuration, uploadUrl.Id);
                                        _configuration.Save();
                                    }
                                    if (ImGui.IsItemHovered()) {
                                        ImGui.SetTooltip("Remove endpoint");
                                    }
                                }
                            }
                        } finally {
                            ImGui.EndTable();
                        }
                    }
                }

                ImGui.Spacing();

                var style = ImGui.GetStyle();
                var addBtnWidth = ImGui.CalcTextSize("Add").X + (style.FramePadding.X * 2f) + style.ItemSpacing.X;
                var resetBtnWidth = ImGui.CalcTextSize("Reset Defaults").X + (style.FramePadding.X * 2f) + style.ItemSpacing.X;
                var inputWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - addBtnWidth - resetBtnWidth);

                ImGui.SetNextItemWidth(inputWidth);
                var submitted = ImGui.InputTextWithHint(
                    "##newEndpointUrl",
                    "https://example.com",
                    ref _uploadUrlTempString,
                    300,
                    ImGuiInputTextFlags.EnterReturnsTrue);

                ImGui.SameLine();
                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_uploadUrlTempString))) {
                    if (ImGui.Button("Add##addEndpoint")) {
                        submitted = true;
                    }
                }

                if (submitted) {
                    TryAddEndpoint();
                }

                ImGui.SameLine();
                if (ImGui.Button("Reset Defaults##uploadUrlDefault")) {
                    _configuration.UpdateUploadUrls(_ => Configuration.DefaultUploadUrls());
                    _configuration.Save();
                    _uploadUrlError.Set(string.Empty, DateTime.UtcNow, TimeSpan.Zero);
                }

                var currentError = _uploadUrlError.Current(DateTime.UtcNow);
                if (!string.IsNullOrEmpty(currentError)) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(UiTheme.Danger, currentError);
                }
            });
    }

    private void DrawPlayerCacheCard() {
        UiTheme.DrawCard(
            "PlayerCacheCard",
            "Player Identity Cache",
            "Local SQLite cache and manual upload controls.",
            () => {
                UiTheme.DrawKeyValueTable("PlayerCacheTable", () => {
                    UiTheme.DrawKeyValueRow("Pending Rows", _plugin.PendingPlayerUploadCount.ToString());
                    var workerBusy = _plugin.IsPlayerUploadInProgress;
                    UiTheme.DrawKeyValueRow(
                        "Upload Worker",
                        workerBusy ? "BUSY" : "IDLE",
                        workerBusy ? UiTheme.Info : UiTheme.Healthy);
                });

                ImGui.Spacing();

                if (ImGui.Button("Upload Pending Now##playerUpload")) {
                    _plugin.TriggerPlayerUploadNow();
                    _playerManualUploadStatus = $"Triggered pending upload at {DateTime.Now:HH:mm:ss} (Local).";
                }

                ImGui.SameLine();
                if (ImGui.Button("Full Cache Resync##playerResync")) {
                    var requeued = _plugin.TriggerPlayerFullResyncUploadNow();
                    _playerManualUploadStatus =
                        $"Requeued {requeued} cached rows and triggered upload at {DateTime.Now:HH:mm:ss} (Local).";
                }
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("Marks every row in player_cache.db as dirty, then starts upload immediately.");
                }

                if (!string.IsNullOrEmpty(_playerManualUploadStatus)) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(UiTheme.Info, _playerManualUploadStatus);
                }

                ImGui.Spacing();
                UiTheme.DrawColoredWrappedText(
                    UiTheme.Muted,
                    "Chat commands: /rpr, /rpr players-upload, /rpr players-resync, /rpr details-upload");
            });
    }

    private void TryAddEndpoint() {
        var candidateUrl = (_uploadUrlTempString ?? string.Empty).Trim();
        _uploadUrlTempString = candidateUrl;
        if (string.IsNullOrWhiteSpace(candidateUrl)) {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var latestUrls = _configuration.GetUploadUrlsSnapshot();
        if (latestUrls.Any(r => string.Equals(r.Url, candidateUrl, StringComparison.InvariantCultureIgnoreCase))) {
            _uploadUrlError.Set("Endpoint already exists.", nowUtc, TimeSpan.FromSeconds(5));
            return;
        }

        if (!IngestEndpointResolver.IsValidUploadUrl(candidateUrl, out var validationError)) {
            _uploadUrlError.Set(validationError, nowUtc, TimeSpan.FromSeconds(5));
            return;
        }

        _configuration.UpdateUploadUrls(urls =>
            urls.Any(r => string.Equals(r.Url, candidateUrl, StringComparison.InvariantCultureIgnoreCase))
                ? urls
                : urls.Add(new(candidateUrl)));
        _configuration.Save();
        _uploadUrlTempString = string.Empty;
        _uploadUrlError.Set(string.Empty, nowUtc, TimeSpan.Zero);
    }

    internal static bool CanRemoveEndpoint(IReadOnlyList<UploadUrl> uploadUrls, UploadUrl candidate) {
        ArgumentNullException.ThrowIfNull(uploadUrls);
        ArgumentNullException.ThrowIfNull(candidate);

        if (uploadUrls.Count <= 1) {
            return false;
        }

        if (!candidate.IsDefault) {
            return true;
        }

        return uploadUrls.Any(other => other.Id != candidate.Id && other.IsEnabled);
    }

    internal static bool RemoveEndpoint(Configuration configuration, Guid removedId) {
        ArgumentNullException.ThrowIfNull(configuration);

        var removed = false;
        configuration.UpdateUploadUrls(urls => {
            var target = urls.FirstOrDefault(candidate => candidate.Id == removedId);
            if (target is null || !CanRemoveEndpoint(urls, target)) {
                return urls;
            }

            var updated = urls.RemoveAll(candidate => candidate.Id == removedId);
            if (updated.Count < urls.Count) {
                removed = true;
                if (updated.Count > 0 && !updated.Any(static candidate => candidate.IsDefault)) {
                    var replacement = updated.FirstOrDefault(static candidate => candidate.IsEnabled) ?? updated[0];
                    replacement.IsDefault = true;
                }
            }

            return updated;
        });

        return removed;
    }
}
