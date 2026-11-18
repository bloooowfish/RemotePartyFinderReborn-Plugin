#nullable enable

using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal sealed class DebugPageRenderer {
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private readonly UiNavigationState _navState;
    private string _scannerCollectionStatus = string.Empty;
    private string _detailUploadStatus = string.Empty;

    internal DebugPageRenderer(Plugin plugin, UiNavigationState navState) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
        _navState = navState ?? throw new ArgumentNullException(nameof(navState));
    }

    internal void Draw() {
        if (!ImGui.BeginTabBar((ImU8String)"DebugSubTabs")) {
            return;
        }

        try {
            if (ImGui.BeginTabItem((ImU8String)"Queues & Telemetry")) {
                _navState.SelectedDebugSubTab = DebugSubTab.QueuesAndTelemetry;
                ImGui.Spacing();
                DrawQueuesAndTelemetryTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem((ImU8String)"Auto Scanner")) {
                _navState.SelectedDebugSubTab = DebugSubTab.AutoScanner;
                ImGui.Spacing();
                DrawAutoScannerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem((ImU8String)"Latest PF Detail")) {
                _navState.SelectedDebugSubTab = DebugSubTab.LatestPfDetail;
                ImGui.Spacing();
                DrawLatestPfDetailTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem((ImU8String)"CharaCard")) {
                _navState.SelectedDebugSubTab = DebugSubTab.CharaCard;
                ImGui.Spacing();
                DrawCharaCardTab();
                ImGui.EndTabItem();
            }
        } finally {
            ImGui.EndTabBar();
        }
    }

    private void DrawQueuesAndTelemetryTab() {
        UiTheme.DrawCard(
            "DebugGathererSyncCard",
            "Gatherer & Detail Sync",
            "Listing and party detail upload acknowledgement counters.",
            () => {
                UiTheme.DrawKeyValueTable("GathererSyncTable", () => {
                    UiTheme.DrawKeyValueRow(
                        "Gatherer Ack Version",
                        $"{_plugin.Gatherer.LastSuccessfulUploadAckVersion} (indexed: {_plugin.Gatherer.UploadedListingIndexCount})");
                    UiTheme.DrawKeyValueRow(
                        "Gatherer Last Success",
                        $"{UiTheme.FormatLocalClock(_plugin.Gatherer.LastSuccessfulUploadAtUtc)} Local");
                    UiTheme.DrawKeyValueRow(
                        "Detail Queue Ack Version",
                        $"{_plugin.PartyDetailCollector.LastQueuedAckVersion} (listing: {_plugin.PartyDetailCollector.LastUploadedListingId})");
                    UiTheme.DrawKeyValueRow(
                        "Detail Upload Ack Version",
                        $"{_plugin.PartyDetailCollector.LastSuccessfulUploadAckVersion} (listing: {_plugin.PartyDetailCollector.LastSuccessfulUploadListingId})");
                    UiTheme.DrawKeyValueRow(
                        "Detail Last Success",
                        $"{UiTheme.FormatLocalClock(_plugin.PartyDetailCollector.LastSuccessfulUploadAtUtc)} Local");
                    UiTheme.DrawKeyValueRow(
                        "Detail Missing Ack",
                        $"{_plugin.PartyDetailCollector.LastTerminalUploadAckVersion} (listing: {_plugin.PartyDetailCollector.LastTerminalUploadListingId})");
                });
            });

        ImGui.Dummy(new Vector2(0f, 6f));

        UiTheme.DrawCard(
            "DebugDetailQueueCard",
            "Shared Detail Upload Queue",
            "Pending queue composition, readiness, and block counters.",
            () => {
                var detailQueue = _plugin.DetailUploadQueueDebugState;
                UiTheme.DrawKeyValueTable("DetailQueueTable", () => {
                    UiTheme.DrawKeyValueRow(
                        "Pending / In-Flight",
                        $"pending={detailQueue.PendingCount}, in-flight={detailQueue.InFlightCount}, workers={detailQueue.ActiveWorkerCount}");
                    UiTheme.DrawKeyValueRow(
                        "Queue Sources",
                        $"manual={detailQueue.ManualPendingCount}, scanner={detailQueue.ScannerPendingCount}");
                    UiTheme.DrawKeyValueRow(
                        "Ready",
                        $"due={detailQueue.DueCount}, eligible={detailQueue.EligibleNowCount}");
                    UiTheme.DrawKeyValueRow(
                        "Blocks",
                        $"capability={detailQueue.CapabilityDeferredCount}, circuit={detailQueue.CircuitBlockedCount}");
                    UiTheme.DrawKeyValueRow(
                        "Targets",
                        $"enabled={detailQueue.EnabledTargetCount}, open-circuits={detailQueue.OpenCircuitTargetCount}");
                });

                ImGui.Spacing();

                if (ImGui.Button("Retry Pending Details Now##debugRetryDetails")) {
                    var retry = _plugin.TriggerPendingDetailUploadNowWithState();
                    var queue = retry.QueueState;
                    _detailUploadStatus =
                        $"[{DateTime.Now:HH:mm:ss}] Triggered {retry.TriggeredCount}; pending={queue.PendingCount}, in-flight={queue.InFlightCount}, eligible={queue.EligibleNowCount}, capability={queue.CapabilityDeferredCount}, circuit={queue.CircuitBlockedCount}";
                }
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("Retries queued PF detail payloads immediately without bypassing in-flight uploads or endpoint circuit breakers.");
                }

                if (!string.IsNullOrEmpty(_detailUploadStatus)) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(UiTheme.Info, _detailUploadStatus);
                }
            });

        ImGui.Dummy(new Vector2(0f, 6f));

        UiTheme.DrawCard(
            "DebugTomestonePacingOverrideCard",
            "Tomestone Pacing Override",
            "Bypasses safe anti-burst clamping for Tomestone API requests until the plugin reloads.",
            () => {
                var bypassClamp = _configuration.BypassTomestonePacingClampDebug;
                if (ImGui.Checkbox("Bypass Tomestone Pacing Clamp (Session Only)##debugBypassTomestoneClamp", ref bypassClamp)) {
                    _configuration.BypassTomestonePacingClampDebug = bypassClamp;
                }
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("Unlocks <1000 ms interval and >1 concurrency in Advanced -> Tomestone Request Pacing for the current session only. Not saved to disk.");
                }

                ImGui.Spacing();

                var effectiveConcurrency = _configuration.GetEffectiveTomestoneMaxConcurrency();
                var effectiveIntervalMs = _configuration.GetEffectiveTomestoneRequestIntervalMs();
                UiTheme.DrawKeyValueTable("TomestonePacingEffectiveTable", () => {
                    UiTheme.DrawKeyValueRow(
                        "Clamp Mode",
                        bypassClamp ? "Bypassed (Session Only)" : "Safe Clamped (Max Concurrency 1, >= 1000 ms)");
                    UiTheme.DrawKeyValueRow(
                        "Effective Pacing",
                        $"concurrency={effectiveConcurrency}, interval={effectiveIntervalMs} ms (configured={_configuration.TomestoneMaxConcurrency}, {_configuration.TomestoneRequestIntervalMs} ms)");
                });

                if (bypassClamp) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(
                        UiTheme.Warning,
                        "Anti-burst clamping is bypassed for this session. Tomestone rate-limit or Cloudflare 429 bursts may occur.");
                }
            });
    }

    private void DrawAutoScannerTab() {
        var scanner = _plugin.DebugPfScanner;
        var detailQueue = _plugin.DetailUploadQueueDebugState;
        var collectedCount = scanner.CollectedListingCount;
        var pageBatchCount = scanner.CollectedPageBatchCount;

        UiTheme.DrawCard(
            "DebugAutoScannerCard",
            "Auto Scanner",
            "Automatically collects current and paged high-end Party Finder listings, then scans and uploads party details in one click.",
            () => {
                UiTheme.DrawKeyValueTable("ScannerStateTable", () => {
                    UiTheme.DrawKeyValueRow(
                        "Collection",
                        !_plugin.IsPluginOperational
                            ? "Blocked (FFLogs API required)"
                            : scanner.IsPartyFinderOpen
                                ? "Active (PF Open)"
                                : "Standing By (Open PF to collect)");
                    UiTheme.DrawKeyValueRow(
                        "Collected Ready",
                        $"{collectedCount} listing(s) across {pageBatchCount} page batch(es)");
                    UiTheme.DrawKeyValueRow(
                        "Scanner",
                        scanner.IsRunning
                            ? $"{scanner.StateName} (target={scanner.CurrentTargetListingId}, pending={scanner.PendingCount})"
                            : scanner.StateName);
                    UiTheme.DrawKeyValueRow(
                        "Progress",
                        $"processed={scanner.ProcessedCount}, failures={scanner.ConsecutiveFailures}");
                    UiTheme.DrawKeyValueRow(
                        "Detail Upload",
                        $"pending={detailQueue.PendingCount}, in-flight={detailQueue.InFlightCount}, workers={detailQueue.ActiveWorkerCount}");
                    UiTheme.DrawKeyValueRow(
                        "Last Attempt",
                        scanner.LastAttemptListingId == 0
                            ? "none"
                            : $"listing={scanner.LastAttemptListingId}, success={scanner.LastAttemptSuccess}, reason={scanner.LastAttemptReason}");
                });

                ImGui.Spacing();

                if (scanner.IsRunning) {
                    if (ImGui.Button("Stop Scan##scannerStop")) {
                        scanner.StopScan();
                        _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] Scan stopped; remaining unscanned listings kept.";
                    }
                } else {
                    var canStart = _plugin.IsPluginOperational && scanner.IsPartyFinderOpen && collectedCount > 0;
                    using (ImRaii.Disabled(!canStart)) {
                        if (ImGui.Button($"Scan & Upload Details ({collectedCount})##scannerStart")) {
                            var ok = scanner.StartScan(out var status);
                            _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] {(ok ? "OK" : "FAIL")}: {status}";
                        }
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                        ImGui.SetTooltip("Flushes pending listing summaries, opens each collected high-end listing in PF to capture party details, and uploads them immediately.");
                    }
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(collectedCount == 0 && !scanner.IsRunning)) {
                    if (ImGui.Button("Clear##scannerClear")) {
                        var removed = scanner.ClearCollectedListings();
                        _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] Cleared {removed} collected listing(s).";
                    }
                }

                if (!_plugin.IsPluginOperational) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(
                        UiTheme.Warning,
                        "FFLogs API registration is required before listings can be collected or scanned.");
                } else if (!scanner.IsPartyFinderOpen) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(
                        UiTheme.Muted,
                        "Open Party Finder in-game and browse pages to auto-collect high-end listings.");
                }

                if (!string.IsNullOrEmpty(_scannerCollectionStatus)) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(UiTheme.Info, _scannerCollectionStatus);
                }
            });

        ImGui.Dummy(new Vector2(0f, 6f));

        UiTheme.DrawCard(
            "DebugScannerSpeedCard",
            "Scanner Speed & Retry",
            "Pacing between single-flight PF detail requests, timeout, and retry budget.",
            () => {
                UiTheme.DrawFormTable("DebugScannerSpeedForm", () => {
                    UiTheme.FormRow(
                        "Action Interval",
                        "Minimum time between consecutive PF detail open requests.",
                        () => {
                            var actionInterval = Configuration.NormalizeScanActionIntervalMs(_configuration.AutoDetailScanActionIntervalMs);
                            if (ImGui.SliderInt("##scanActionInterval", ref actionInterval, 50, 2000, "%d ms")) {
                                _configuration.AutoDetailScanActionIntervalMs = Configuration.NormalizeScanActionIntervalMs(actionInterval);
                            }
                            if (ImGui.IsItemDeactivatedAfterEdit()) {
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Post Cooldown",
                        "Pause after each completed listing before advancing (0 ms chains immediately).",
                        () => {
                            var postCooldown = Configuration.NormalizeScanPostCooldownMs(_configuration.AutoDetailScanPostListingCooldownMs);
                            if (ImGui.SliderInt("##scanPostCooldown", ref postCooldown, 0, 2000, "%d ms")) {
                                _configuration.AutoDetailScanPostListingCooldownMs = Configuration.NormalizeScanPostCooldownMs(postCooldown);
                            }
                            if (ImGui.IsItemDeactivatedAfterEdit()) {
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Detail Timeout",
                        "Maximum wait for PF detail data before retrying or skipping.",
                        () => {
                            var detailTimeout = Configuration.NormalizeScanDetailTimeoutMs(_configuration.AutoDetailScanDetailTimeoutMs);
                            if (ImGui.SliderInt("##scanDetailTimeout", ref detailTimeout, 500, 10000, "%d ms")) {
                                _configuration.AutoDetailScanDetailTimeoutMs = Configuration.NormalizeScanDetailTimeoutMs(detailTimeout);
                            }
                            if (ImGui.IsItemDeactivatedAfterEdit()) {
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Retry Count",
                        "Additional open attempts for transient PF retrieval failures or timeouts.",
                        () => {
                            var retryCount = Configuration.NormalizeScanRetryCount(_configuration.AutoDetailScanRetryCount);
                            if (ImGui.SliderInt("##scanRetryCount", ref retryCount, 0, 5, "%d")) {
                                _configuration.AutoDetailScanRetryCount = Configuration.NormalizeScanRetryCount(retryCount);
                            }
                            if (ImGui.IsItemDeactivatedAfterEdit()) {
                                _configuration.Save();
                            }
                        });
                });
            },
            headerRight: () => {
                if (ImGui.Button("Reset Defaults##scannerSpeedReset")) {
                    _configuration.ResetScannerDefaults();
                    _configuration.Save();
                }
            });
    }

    private void DrawLatestPfDetailTab() {
        UiTheme.DrawCard(
            "DebugLatestPfDetailCard",
            "Captured Detail Snapshot",
            "Most recent Party Finder detail payload captured from game memory.",
            () => {
                if (!_plugin.PartyDetailCaptureState.TryGetLatestDebugSnapshot(out var latest)) {
                    UiTheme.DrawColoredText(UiTheme.Muted, "No captured detail snapshot yet.");
                    return;
                }

                var snapshot = latest.Snapshot;
                UiTheme.DrawKeyValueTable("LatestPfDetailSummary", () => {
                    UiTheme.DrawKeyValueRow(
                        "Captured (Local)",
                        $"{UiTheme.FormatLocalDateTime(snapshot.CapturedAtUtc)} (gen={latest.Generation}, req={latest.Cycle.RequestSerial}, owner={latest.Cycle.Owner})");
                    UiTheme.DrawKeyValueRow(
                        "Listing",
                        $"raw={snapshot.RawListingId}, upload={snapshot.UploadListingId}, request={latest.Cycle.ListingId}");
                    UiTheme.DrawKeyValueRow(
                        "Leader",
                        $"{snapshot.LeaderName} (content={snapshot.LeaderContentId}, account={snapshot.LeaderAccountId})");
                    UiTheme.DrawKeyValueRow(
                        "Worlds",
                        $"world={snapshot.World}, home={snapshot.HomeWorld}, current={snapshot.CurrentWorld}");
                    UiTheme.DrawKeyValueRow(
                        "Duty",
                        $"category=0x{snapshot.Category:X}, duty={snapshot.DutyId}, avgIL={snapshot.AvgItemLevel}, timeLeft={snapshot.TimeLeft}s, lastRestart={snapshot.LastPatchHotfixTimestamp}");
                    UiTheme.DrawKeyValueRow(
                        "Objective",
                        $"raw={snapshot.ObjectiveRaw}, enum={snapshot.ObjectiveName}");
                    UiTheme.DrawKeyValueRow(
                        "Flags",
                        $"beginner={snapshot.BeginnerFriendly}, completion={FormatByteFlag(snapshot.CompletionStatusRaw, snapshot.CompletionStatusName)}, dutyFinder={FormatByteFlag(snapshot.DutyFinderSettingRaw, snapshot.DutyFinderSettingName)}, loot={FormatByteFlag(snapshot.LootRuleRaw, snapshot.LootRuleName)}, join={FormatByteFlag(snapshot.JoinConditionRaw, snapshot.JoinConditionName)}");
                    UiTheme.DrawKeyValueRow(
                        "Party",
                        $"slots={snapshot.SlotsFilled}/{snapshot.TotalSlots}, parties={snapshot.NumberOfParties}, alliance={snapshot.IsAlliance}, nonZeroMembers={snapshot.NonZeroMemberCount}");
                    UiTheme.DrawKeyValueRow(
                        "Language",
                        $"leader=0x{snapshot.LeaderClientLanguageRaw:X2}, flags=0x{snapshot.LanguageFlagsRaw:X2}");

                    if (!string.IsNullOrWhiteSpace(snapshot.Comment)) {
                        UiTheme.DrawKeyValueRow("Comment", snapshot.Comment);
                    }
                });

                ImGui.Spacing();

                var visibleMembers = snapshot.Members
                    .Where(static member => member.ContentId != 0 || member.Job != 0 || member.SlotFlag != "0x0000000000000000")
                    .ToArray();

                if (!ImGui.TreeNode($"Members ({visibleMembers.Length}/{snapshot.Members.Count})")) {
                    return;
                }

                try {
                    using (UiTheme.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(6f, 3f))) {
                        if (ImGui.BeginTable(
                                (ImU8String)"latestPfDetailMembers",
                                5,
                                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable)) {
                            try {
                                ImGui.TableSetupColumn("Slot");
                                ImGui.TableSetupColumn("ContentId");
                                ImGui.TableSetupColumn("Job");
                                ImGui.TableSetupColumn("SlotFlags");
                                ImGui.TableSetupColumn("Accepting");
                                ImGui.TableHeadersRow();

                                foreach (var member in visibleMembers) {
                                    ImGui.TableNextRow();
                                    ImGui.TableNextColumn();
                                    ImGui.TextUnformatted(member.SlotIndex.ToString());
                                    ImGui.TableNextColumn();
                                    ImGui.TextUnformatted(member.ContentId.ToString());
                                    ImGui.TableNextColumn();
                                    ImGui.TextUnformatted(member.Job.ToString());
                                    ImGui.TableNextColumn();
                                    ImGui.TextUnformatted(member.SlotFlag);
                                    ImGui.TableNextColumn();
                                    ImGui.TextUnformatted(PartyFinderSlotFlagFormatter.FormatAcceptingJobs(member.SlotFlag));
                                }
                            } finally {
                                ImGui.EndTable();
                            }
                        }
                    }
                } finally {
                    ImGui.TreePop();
                }
            });
    }

    private void DrawCharaCardTab() {
        UiTheme.DrawCard(
            "DebugCharaCardStatusCard",
            "Resolver Status",
            "CharaCard contentId-to-identity hook runtime state.",
            () => {
                UiTheme.DrawKeyValueTable("CharaCardStatusTable", () => {
                    var enabled = _plugin.CharaCardResolver.IsEnabled;
                    UiTheme.DrawKeyValueRow(
                        "Enabled",
                        enabled ? "Yes" : "No",
                        enabled ? UiTheme.Healthy : UiTheme.Danger);
                    UiTheme.DrawKeyValueRow("Preflight", _plugin.CharaCardResolver.Preflight.Reason);
                });
            });

        ImGui.Dummy(new Vector2(0f, 6f));

        UiTheme.DrawCard(
            "DebugCharaCardPacketCard",
            "Latest Resolved Packet",
            "Most recent character identity resolved through CharaCard.",
            () => {
                if (!_plugin.CharaCardResolver.TryGetLatestDebugSnapshot(out var latest)) {
                    UiTheme.DrawColoredText(UiTheme.Muted, "No resolved CharaCard snapshot yet.");
                    return;
                }

                UiTheme.DrawKeyValueTable("CharaCardPacketTable", () => {
                    UiTheme.DrawKeyValueRow("Captured (Local)", UiTheme.FormatLocalDateTime(latest.CapturedAtUtc));
                    UiTheme.DrawKeyValueRow("ContentId", latest.ContentId.ToString());
                    UiTheme.DrawKeyValueRow("Name", latest.Name);
                    UiTheme.DrawKeyValueRow("World", $"{latest.WorldId} ({latest.WorldName})");
                    UiTheme.DrawKeyValueRow(
                        "Packet",
                        $"state={latest.SomeState}, version={latest.Version}, flags=0x{latest.Flags:X2}, privacy=0x{latest.PrivacyFlags:X2}");
                    UiTheme.DrawKeyValueRow(
                        "Status",
                        $"notCreated={latest.IsNotCreated}, fantasiaReset={latest.WasResetDueToFantasia}, visibleToNoOne={latest.IsVisibleToNoOne}, friendsOnly={latest.IsFriendsOnly}");
                });
            });
    }

    private static string FormatByteFlag(byte rawValue, string name) => $"0x{rawValue:X2} ({name})";
}
