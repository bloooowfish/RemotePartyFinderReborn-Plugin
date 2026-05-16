using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RemotePartyFinderReborn;

internal sealed class DebugTabRenderer
{
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private string _scannerCollectionStatus = string.Empty;
    private string _detailUploadStatus = string.Empty;

    public DebugTabRenderer(Plugin plugin)
    {
        _plugin = plugin;
        _configuration = plugin.Configuration;
    }

    public void Draw()
    {
        DrawPfDetailAutoScannerSection();
    }

    private void DrawPfDetailAutoScannerSection()
    {
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[PF Detail Auto Scanner (Debug)]");
        ImGui.Spacing();

        ImGui.TextWrapped("Scanner runs only from current-page snapshots or collected listings.");

        var manualCollectionEnabled = _configuration.EnableManualPageCollectionDebug;
        if (ImGui.Checkbox("Manual Page Collection (Deferred Batch)", ref manualCollectionEnabled))
        {
            _configuration.EnableManualPageCollectionDebug = manualCollectionEnabled;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When enabled, listings seen while you manually turn PF pages are collected for a later one-click batch detail run.");
        }

        var actionInterval = Configuration.NormalizeScanActionIntervalMs(_configuration.AutoDetailScanActionIntervalMs);
        if (ImGui.SliderInt("Action Interval", ref actionInterval, 100, 2000, "%d ms"))
        {
            _configuration.AutoDetailScanActionIntervalMs = Configuration.NormalizeScanActionIntervalMs(actionInterval);
            _configuration.Save();
        }

        var detailTimeout = Configuration.NormalizeScanDetailTimeoutMs(_configuration.AutoDetailScanDetailTimeoutMs);
        if (ImGui.SliderInt("Detail Timeout", ref detailTimeout, 500, 10000, "%d ms"))
        {
            _configuration.AutoDetailScanDetailTimeoutMs = Configuration.NormalizeScanDetailTimeoutMs(detailTimeout);
            _configuration.Save();
        }

        var minDwell = Configuration.NormalizeScanMinDwellMs(_configuration.AutoDetailScanMinDwellMs);
        if (ImGui.SliderInt("Min Detail Dwell", ref minDwell, 100, 3000, "%d ms"))
        {
            _configuration.AutoDetailScanMinDwellMs = Configuration.NormalizeScanMinDwellMs(minDwell);
            _configuration.Save();
        }

        var postCooldown = Configuration.NormalizeScanPostCooldownMs(_configuration.AutoDetailScanPostListingCooldownMs);
        if (ImGui.SliderInt("Post Listing Cooldown", ref postCooldown, 50, 3000, "%d ms"))
        {
            _configuration.AutoDetailScanPostListingCooldownMs = Configuration.NormalizeScanPostCooldownMs(postCooldown);
            _configuration.Save();
        }

        var dedupTtl = Configuration.NormalizeScanDedupTtlSeconds(_configuration.AutoDetailScanDedupTtlSeconds);
        if (ImGui.SliderInt("Dedup TTL", ref dedupTtl, 30, 3600, "%d s"))
        {
            _configuration.AutoDetailScanDedupTtlSeconds = Configuration.NormalizeScanDedupTtlSeconds(dedupTtl);
            _configuration.Save();
        }

        var maxFailures = Configuration.NormalizeScanMaxFailures(_configuration.AutoDetailScanMaxConsecutiveFailures);
        if (ImGui.SliderInt("Max Consecutive Failures", ref maxFailures, 1, 20, "%d"))
        {
            _configuration.AutoDetailScanMaxConsecutiveFailures = Configuration.NormalizeScanMaxFailures(maxFailures);
            _configuration.Save();
        }

        var maxPerRun = Configuration.NormalizeScanMaxPerRun(_configuration.AutoDetailScanMaxPerRun);
        if (ImGui.SliderInt("Max Listings Per Run (0=unlimited)", ref maxPerRun, 0, 500, "%d"))
        {
            _configuration.AutoDetailScanMaxPerRun = Configuration.NormalizeScanMaxPerRun(maxPerRun);
            _configuration.Save();
        }

        ImGui.TextUnformatted($"State: {_plugin.DebugPfScanner.StateName}");
        ImGui.TextUnformatted($"Target Listing: {_plugin.DebugPfScanner.CurrentTargetListingId}");
        ImGui.TextUnformatted($"Visible Cache: {_plugin.DebugPfScanner.VisibleListingCount} / Pending: {_plugin.DebugPfScanner.PendingCount}");
        ImGui.TextUnformatted($"Processed: {_plugin.DebugPfScanner.ProcessedCount} / Consecutive Failures: {_plugin.DebugPfScanner.ConsecutiveFailures}");
        ImGui.TextUnformatted($"Last Attempt: listing={_plugin.DebugPfScanner.LastAttemptListingId} success={_plugin.DebugPfScanner.LastAttemptSuccess} reason={_plugin.DebugPfScanner.LastAttemptReason}");
        ImGui.TextUnformatted($"Gatherer Ack Version: {_plugin.Gatherer.LastSuccessfulUploadAckVersion} (indexed: {_plugin.Gatherer.UploadedListingIndexCount})");
        ImGui.TextUnformatted($"Gatherer Last Success (Local): {FormatLocalClock(_plugin.Gatherer.LastSuccessfulUploadAtUtc)}");
        ImGui.TextUnformatted($"Detail Queue Ack Version: {_plugin.PartyDetailCollector.LastQueuedAckVersion} listing={_plugin.PartyDetailCollector.LastUploadedListingId}");
        ImGui.TextUnformatted($"Detail Ack Version: {_plugin.PartyDetailCollector.LastSuccessfulUploadAckVersion} listing={_plugin.PartyDetailCollector.LastSuccessfulUploadListingId}");
        ImGui.TextUnformatted($"Detail Last Success (Local): {FormatLocalClock(_plugin.PartyDetailCollector.LastSuccessfulUploadAtUtc)}");
        ImGui.TextUnformatted($"Detail Missing Ack Version: {_plugin.PartyDetailCollector.LastTerminalUploadAckVersion} listing={_plugin.PartyDetailCollector.LastTerminalUploadListingId}");
        var detailQueue = _plugin.DetailUploadQueueDebugState;
        ImGui.TextUnformatted(
            $"Shared Detail Upload Queue: pending={detailQueue.PendingCount} in-flight={detailQueue.InFlightCount} workers={detailQueue.ActiveWorkerCount}");
        ImGui.TextUnformatted(
            $"Detail Queue Sources: manual={detailQueue.ManualPendingCount} scanner={detailQueue.ScannerPendingCount}");
        ImGui.TextUnformatted(
            $"Detail Upload Ready: due={detailQueue.DueCount} eligible={detailQueue.EligibleNowCount}");
        ImGui.TextUnformatted(
            $"Detail Upload Blocks: capability={detailQueue.CapabilityDeferredCount} circuit={detailQueue.CircuitBlockedCount} targets={detailQueue.EnabledTargetCount} open-circuits={detailQueue.OpenCircuitTargetCount}");
        if (ImGui.Button("Retry Pending Details Now"))
        {
            var retry = _plugin.TriggerPendingDetailUploadNowWithState();
            var queue = retry.QueueState;
            _detailUploadStatus =
                $"[{DateTime.Now:HH:mm:ss}] triggered {retry.TriggeredCount}; pending={queue.PendingCount} in-flight={queue.InFlightCount} eligible={queue.EligibleNowCount} capability={queue.CapabilityDeferredCount} circuit={queue.CircuitBlockedCount}";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Retries queued PF detail payloads immediately without bypassing in-flight uploads or endpoint circuit breakers.");
        }
        if (!string.IsNullOrEmpty(_detailUploadStatus))
        {
            ImGui.TextWrapped(_detailUploadStatus);
        }

        ImGui.TextUnformatted($"Collected Listings: {_plugin.DebugPfScanner.CollectedListingCount}");

        DrawScannerControls();

        DrawLatestPartyDetailSection();
        DrawCharaCardResolverSection();
    }

    private void DrawScannerControls()
    {
        ImGui.Spacing();
        if (ImGui.Button("Collect Current Page Snapshot"))
        {
            var added = _plugin.DebugPfScanner.CollectCurrentPageSnapshot();
            _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] collected current-page snapshot (+{added})";
        }

        ImGui.SameLine();
        if (ImGui.Button("Run Current Page Click"))
        {
            _plugin.DebugPfScanner.StartCurrentPageRun();
            _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] scheduled current-page detail-click run";
        }

        ImGui.SameLine();
        if (ImGui.Button("Run Collected Batch Click"))
        {
            var ok = _plugin.DebugPfScanner.StartCollectedBatchRun(out var status);
            _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] {(ok ? "OK" : "FAIL")} {status}";
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear Collected"))
        {
            var removed = _plugin.DebugPfScanner.ClearCollectedListings();
            _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] cleared collected listings ({removed})";
        }

        if (!string.IsNullOrEmpty(_scannerCollectionStatus))
        {
            ImGui.TextWrapped(_scannerCollectionStatus);
        }

        if (ImGui.Button("Reset Scanner Session"))
        {
            _configuration.EnableAutoDetailScanDebug = false;
            _configuration.Save();
            _plugin.DebugPfScanner.ResetSession();
            _scannerCollectionStatus = $"[{DateTime.Now:HH:mm:ss}] scanner stopped and session reset";
        }
    }

    private void DrawLatestPartyDetailSection()
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "[Latest PF Detail]");

        if (!_plugin.PartyDetailCaptureState.TryGetLatestDebugSnapshot(out var latest))
        {
            ImGui.TextUnformatted("No captured detail snapshot yet.");
            return;
        }

        var snapshot = latest.Snapshot;
        ImGui.TextUnformatted($"Captured (Local): {FormatLocalDateTime(snapshot.CapturedAtUtc)} generation={latest.Generation} request={latest.Cycle.RequestSerial} owner={latest.Cycle.Owner}");
        ImGui.TextUnformatted($"Listing: raw={snapshot.RawListingId} upload={snapshot.UploadListingId} request={latest.Cycle.ListingId}");
        ImGui.TextUnformatted($"Leader: {snapshot.LeaderName} content={snapshot.LeaderContentId} account={snapshot.LeaderAccountId}");
        ImGui.TextUnformatted($"Worlds: world={snapshot.World} home={snapshot.HomeWorld} current={snapshot.CurrentWorld}");
        ImGui.TextUnformatted($"Duty: category=0x{snapshot.Category:X} duty={snapshot.DutyId} avgIL={snapshot.AvgItemLevel} timeLeft={snapshot.TimeLeft}s lastRestart={snapshot.LastPatchHotfixTimestamp}");
        ImGui.TextUnformatted($"Objective: raw={snapshot.ObjectiveRaw} enum={snapshot.ObjectiveName}");
        ImGui.TextUnformatted($"Flags: beginner={snapshot.BeginnerFriendly} completion={FormatByteFlag(snapshot.CompletionStatusRaw, snapshot.CompletionStatusName)} dutyFinder={FormatByteFlag(snapshot.DutyFinderSettingRaw, snapshot.DutyFinderSettingName)} loot={FormatByteFlag(snapshot.LootRuleRaw, snapshot.LootRuleName)} join={FormatByteFlag(snapshot.JoinConditionRaw, snapshot.JoinConditionName)}");
        ImGui.TextUnformatted($"Party: slots={snapshot.SlotsFilled}/{snapshot.TotalSlots} parties={snapshot.NumberOfParties} alliance={snapshot.IsAlliance} nonZeroMembers={snapshot.NonZeroMemberCount}");
        ImGui.TextUnformatted($"Language: leader=0x{snapshot.LeaderClientLanguageRaw:X2} flags=0x{snapshot.LanguageFlagsRaw:X2}");

        if (!string.IsNullOrWhiteSpace(snapshot.Comment))
        {
            ImGui.TextWrapped($"Comment: {snapshot.Comment}");
        }

        var visibleMembers = snapshot.Members
            .Where(static member => member.ContentId != 0 || member.Job != 0 || member.SlotFlag != "0x0000000000000000")
            .ToArray();

        if (!ImGui.TreeNode($"Members ({visibleMembers.Length}/{snapshot.Members.Count})"))
        {
            return;
        }

        if (ImGui.BeginTable("latestPfDetailMembers", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Slot");
            ImGui.TableSetupColumn("ContentId");
            ImGui.TableSetupColumn("Job");
            ImGui.TableSetupColumn("SlotFlags");
            ImGui.TableSetupColumn("Accepting");
            ImGui.TableHeadersRow();

            foreach (var member in visibleMembers)
            {
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

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private void DrawCharaCardResolverSection()
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1.0f), "[CharaCard Resolver]");
        ImGui.TextUnformatted($"Enabled: {_plugin.CharaCardResolver.IsEnabled}");
        ImGui.TextWrapped($"Preflight: {_plugin.CharaCardResolver.Preflight.Reason}");

        if (!_plugin.CharaCardResolver.TryGetLatestDebugSnapshot(out var latest))
        {
            ImGui.TextUnformatted("No resolved CharaCard snapshot yet.");
            return;
        }

        ImGui.TextUnformatted($"Captured (Local): {FormatLocalDateTime(latest.CapturedAtUtc)}");
        ImGui.TextUnformatted($"ContentId: {latest.ContentId}");
        ImGui.TextUnformatted($"Name: {latest.Name}");
        ImGui.TextUnformatted($"World: {latest.WorldId} ({latest.WorldName})");
        ImGui.TextUnformatted($"Packet: state={latest.SomeState} version={latest.Version} flags=0x{latest.Flags:X2} privacy=0x{latest.PrivacyFlags:X2}");
        ImGui.TextUnformatted($"Status: notCreated={latest.IsNotCreated} fantasiaReset={latest.WasResetDueToFantasia} visibleToNoOne={latest.IsVisibleToNoOne} friendsOnly={latest.IsFriendsOnly}");
    }

    private static string FormatLocalClock(DateTime utcTime)
    {
        if (utcTime == DateTime.MinValue)
        {
            return "-";
        }

        var normalizedUtc = utcTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)
            : utcTime;
        return normalizedUtc.ToLocalTime().ToString("HH:mm:ss");
    }

    private static string FormatLocalDateTime(DateTime utcTime)
    {
        if (utcTime == DateTime.MinValue)
        {
            return "-";
        }

        var normalizedUtc = utcTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)
            : utcTime;
        return normalizedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string FormatByteFlag(byte rawValue, string name)
    {
        return $"0x{rawValue:X2} ({name})";
    }
}
