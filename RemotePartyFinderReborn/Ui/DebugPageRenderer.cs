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
    }

    private void DrawAutoScannerTab() {
        UiTheme.DrawCard(
            "DebugScannerControlsCard",
            "Scanner Controls & Live State",
            "Scanner runs only from current-page snapshots or collected listings.",
            () => {
                var manualCollectionEnabled = _configuration.EnableManualPageCollectionDebug;
                if (ImGui.Checkbox("Manual Page Collection (Deferred Batch)", ref manualCollectionEnabled)) {
                    _configuration.EnableManualPageCollectionDebug = manualCollectionEnabled;
                    _configuration.Save();
                }
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("When enabled, listings seen while you manually turn PF pages are collected for a later one-click batch detail run.");
                }

                ImGui.Spacing();

                UiTheme.DrawKeyValueTable("ScannerStateTable", () => {
                    UiTheme.DrawKeyValueRow("State", _plugin.DebugPfScanner.StateName);
                    UiTheme.DrawKeyValueRow("Target Listing", _plugin.DebugPfScanner.CurrentTargetListingId.ToString());
                    UiTheme.DrawKeyValueRow(
                        "Visible / Pending",
                        $"{_plugin.DebugPfScanner.VisibleListingCount} / {_plugin.DebugPfScanner.PendingCount}");
                    UiTheme.DrawKeyValueRow("Collected Listings", _plugin.DebugPfScanner.CollectedListingCount.ToString());
                    UiTheme.DrawKeyValueRow(
                        "Processed / Failures",
                        $"{_plugin.DebugPfScanner.ProcessedCount} / {_plugin.DebugPfScanner.ConsecutiveFailures}");
                    UiTheme.DrawKeyValueRow(
                        "Last Attempt",
                        $"listing={_plugin.DebugPfScanner.LastAttemptListingId}, success={_plugin.DebugPfScanner.LastAttemptSuccess}, reason={_plugin.DebugPfScanner.LastAttemptReason}");
                });

                ImGui.Spacing();

                // Row 1
                if (ImGui.Button("Collect Page Snapshot##scannerCollect")) {
                    var added = _plugin.DebugPfScanner.CollectCurrentPageSnapshot();
                    _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] Collected current-page snapshot (+{added}).";
                }

                ImGui.SameLine();
                if (ImGui.Button("Run Current Page##scannerRunCurrent")) {
                    _plugin.DebugPfScanner.StartCurrentPageRun();
                    _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] Scheduled current-page detail-click run.";
                }

                ImGui.SameLine();
                if (ImGui.Button("Run Collected Batch##scannerRunBatch")) {
                    var ok = _plugin.DebugPfScanner.StartCollectedBatchRun(out var status);
                    _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] {(ok ? "OK" : "FAIL")}: {status}";
                }

                // Row 2
                if (ImGui.Button("Clear Collected##scannerClear")) {
                    var removed = _plugin.DebugPfScanner.ClearCollectedListings();
                    _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] Cleared collected listings ({removed}).";
                }

                ImGui.SameLine();
                if (ImGui.Button("Stop & Reset Session##scannerReset")) {
                    _configuration.EnableAutoDetailScanDebug = false;
                    _configuration.Save();
                    _plugin.DebugPfScanner.ResetSession();
                    _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] Scanner stopped and session reset.";
                }

                if (!string.IsNullOrEmpty(_scannerCollectionStatus)) {
                    ImGui.Spacing();
                    UiTheme.DrawColoredWrappedText(UiTheme.Info, _scannerCollectionStatus);
                }
            });

        ImGui.Dummy(new Vector2(0f, 6f));

        UiTheme.DrawCard(
            "DebugScannerTimingCard",
            "Scanner Timing & Limits",
            "Tuning parameters for automated PF detail inspection.",
            () => {
                UiTheme.DrawFormTable("ScannerTimingForm", () => {
                    UiTheme.FormRow("Action Interval", null, () => {
                        var actionInterval = Configuration.NormalizeScanActionIntervalMs(
                            _configuration.AutoDetailScanActionIntervalMs);
                        if (ImGui.SliderInt("##scanActionInterval", ref actionInterval, 100, 2000, "%d ms")) {
                            _configuration.AutoDetailScanActionIntervalMs =
                                Configuration.NormalizeScanActionIntervalMs(actionInterval);
                            _configuration.Save();
                        }
                    });

                    UiTheme.FormRow("Detail Timeout", null, () => {
                        var detailTimeout = Configuration.NormalizeScanDetailTimeoutMs(
                            _configuration.AutoDetailScanDetailTimeoutMs);
                        if (ImGui.SliderInt("##scanDetailTimeout", ref detailTimeout, 500, 10000, "%d ms")) {
                            _configuration.AutoDetailScanDetailTimeoutMs =
                                Configuration.NormalizeScanDetailTimeoutMs(detailTimeout);
                            _configuration.Save();
                        }
                    });

                    UiTheme.FormRow("Min Detail Dwell", null, () => {
                        var minDwell = Configuration.NormalizeScanMinDwellMs(
                            _configuration.AutoDetailScanMinDwellMs);
                        if (ImGui.SliderInt("##scanMinDwell", ref minDwell, 100, 3000, "%d ms")) {
                            _configuration.AutoDetailScanMinDwellMs =
                                Configuration.NormalizeScanMinDwellMs(minDwell);
                            _configuration.Save();
                        }
                    });

                    UiTheme.FormRow("Post Listing Cooldown", null, () => {
                        var postCooldown = Configuration.NormalizeScanPostCooldownMs(
                            _configuration.AutoDetailScanPostListingCooldownMs);
                        if (ImGui.SliderInt("##scanPostCooldown", ref postCooldown, 50, 3000, "%d ms")) {
                            _configuration.AutoDetailScanPostListingCooldownMs =
                                Configuration.NormalizeScanPostCooldownMs(postCooldown);
                            _configuration.Save();
                        }
                    });

                    UiTheme.FormRow("Dedup TTL", null, () => {
                        var dedupTtl = Configuration.NormalizeScanDedupTtlSeconds(
                            _configuration.AutoDetailScanDedupTtlSeconds);
                        if (ImGui.SliderInt("##scanDedupTtl", ref dedupTtl, 30, 3600, "%d s")) {
                            _configuration.AutoDetailScanDedupTtlSeconds =
                                Configuration.NormalizeScanDedupTtlSeconds(dedupTtl);
                            _configuration.Save();
                        }
                    });

                    UiTheme.FormRow("Max Consecutive Failures", null, () => {
                        var maxFailures = Configuration.NormalizeScanMaxFailures(
                            _configuration.AutoDetailScanMaxConsecutiveFailures);
                        if (ImGui.SliderInt("##scanMaxFailures", ref maxFailures, 1, 20, "%d")) {
                            _configuration.AutoDetailScanMaxConsecutiveFailures =
                                Configuration.NormalizeScanMaxFailures(maxFailures);
                            _configuration.Save();
                        }
                    });

                    UiTheme.FormRow(
                        "Max Listings Per Run",
                        "Maximum listings scanned per run (0 = unlimited).",
                        () => {
                            var maxPerRun = Configuration.NormalizeScanMaxPerRun(
                                _configuration.AutoDetailScanMaxPerRun);
                            if (ImGui.SliderInt("##scanMaxPerRun", ref maxPerRun, 0, 500, "%d")) {
                                _configuration.AutoDetailScanMaxPerRun =
                                    Configuration.NormalizeScanMaxPerRun(maxPerRun);
                                _configuration.Save();
                            }
                        });

                    UiTheme.FormRow(
                        "Auto Scanner Retries",
                        "Additional retries after the initial PF auto scanner interaction attempt.",
                        () => {
                            var autoDetailScanRetryCount = _configuration.AutoDetailScanRetryCount;
                            if (ImGui.SliderInt("##scanRetries", ref autoDetailScanRetryCount, 0, 5, "%d")) {
                                _configuration.AutoDetailScanRetryCount = autoDetailScanRetryCount;
                                _configuration.Save();
                            }
                        });
                });
            },
            headerRight: () => {
                if (ImGui.Button("Reset Defaults##scannerDefaults")) {
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
