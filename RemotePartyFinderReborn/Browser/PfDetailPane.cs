#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

/// <summary>
/// Redesigned data-rich Party Finder detail view showing 8-player FFLogs & Tomestone roster data
/// alongside safe delegation to FFXIV's native LookingForGroupDetail window for joining.
/// </summary>
internal sealed class PfDetailPane {
    private readonly PfLocalMirrorStore _store;
    private readonly PfIconCatalog _icons;
    private readonly NativePfBridge _nativeBridge;
    private readonly PfEnrichmentClient? _enrichmentClient;
    private int _selectedPartyTab;
    private ulong _lastRenderedListingId;

    internal PfDetailPane(
        PfLocalMirrorStore store,
        PfIconCatalog icons,
        NativePfBridge nativeBridge,
        PfEnrichmentClient? enrichmentClient = null
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _nativeBridge = nativeBridge ?? throw new ArgumentNullException(nameof(nativeBridge));
        _enrichmentClient = enrichmentClient;
    }

    internal void NotifyListingOpened(ulong listingId, PfFilterState filter, bool isPluginOperational) {
        if (listingId == 0) {
            return;
        }

        if (_enrichmentClient == null) {
            return;
        }

        if (!isPluginOperational || filter.UseSampleEnrichment) {
            _enrichmentClient.SetActiveListing(listingId);
            return;
        }

        RequestPartyEnrichmentForListing(listingId, filter, isPluginOperational, forceRefresh: false);
    }

    internal void NotifyWindowClosed() {
        _enrichmentClient?.SetActiveListing(0);
    }

    internal void RequestPartyEnrichmentForListing(
        ulong listingId,
        PfFilterState filter,
        bool isPluginOperational,
        bool forceRefresh
    ) {
        if (_enrichmentClient == null || !isPluginOperational || filter.UseSampleEnrichment || listingId == 0) {
            return;
        }

        var listing = _store.TryGetListing(listingId, filter.IncludeSampleListings, useSampleEnrichment: false);
        if (listing == null || listing.IsSample || listing.RawDuty == 0) {
            _enrichmentClient.SetActiveListing(listingId);
            return;
        }

        var (memberIds, knownMembers) = _store.GetEnrichmentInputs(listing);
        _enrichmentClient.RequestPartyEnrichment(listing, memberIds, knownMembers, forceRefresh);
    }

    internal void Draw(
        ulong listingId,
        PfFilterState filter,
        uint? playerClassJobId,
        Action? onCloseRequested = null,
        bool isPluginOperational = true
    ) {
        var lang = _icons.UiLanguage;
        if (listingId == 0) {
            UiTheme.DrawColoredText(UiTheme.Muted, PfUiText.DetailEmptySelection(lang));
            return;
        }

        var listing = _store.TryGetListing(listingId, filter.IncludeSampleListings, filter.UseSampleEnrichment);
        if (listing == null) {
            UiTheme.DrawColoredText(UiTheme.Warning, PfUiText.DetailListingExpired(lang, listingId));
            return;
        }

        if (_lastRenderedListingId != listing.Id) {
            _lastRenderedListingId = listing.Id;
            _selectedPartyTab = 0;
        }

        var members = _store.BuildMemberRows(listing, filter.UseSampleEnrichment, playerClassJobId);

        DrawHeaderCard(listing, lang);
        ImGui.Spacing();
        DrawSummaryStrip(listing, members, filter, lang);
        ImGui.Spacing();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + 8f;
        var tableOpen = ImGui.BeginChild((ImU8String)"PfDetailRosterScroll", new Vector2(0f, -footerHeight), false, ImGuiWindowFlags.None);
        try {
            if (tableOpen) {
                DrawRosterSection(listing, members, filter.UseSampleEnrichment, lang);
            }
        } finally {
            ImGui.EndChild();
        }

        DrawBottomActionBar(listing, filter, isPluginOperational, onCloseRequested, lang);
    }

    private void DrawHeaderCard(PfListingEntry listing, PfUiLanguage lang) {
        var title = listing.IsSample ? $"{PfUiText.SampleTag(lang)}{listing.DutyName}" : listing.DutyName;
        var subtitle = PfUiText.DetailHeaderSubtitle(lang, listing.LeaderName, listing.HomeWorldName, listing.MinItemLevel);

        UiTheme.DrawCard(
            "PfDetailHeaderCard",
            title,
            subtitle,
            () => {
                DrawConditionPills(listing, lang);
                ImGui.Spacing();

                var commentText = string.IsNullOrWhiteSpace(listing.Comment) ? PfUiText.NoComment(lang) : listing.Comment;
                UiTheme.DrawColoredWrappedText(
                    string.IsNullOrWhiteSpace(listing.Comment) ? UiTheme.Muted : new Vector4(0.90f, 0.92f, 0.95f, 1.0f),
                    commentText);

                if (!string.IsNullOrWhiteSpace(listing.Comment)) {
                    ImGui.Spacing();
                    if (ImGui.SmallButton(PfUiText.ContextCopyComment(lang))) {
                        UiTheme.CopyToClipboard(listing.Comment);
                    }
                }
            },
            headerRight: () => {
                var remaining = PfUiText.DetailHeaderRemaining(lang, PfUiText.FormatRemainingTime(listing.SecondsRemaining, lang));
                UiTheme.DrawColoredText(UiTheme.Muted, remaining);
            });
    }

    private static void DrawConditionPills(PfListingEntry listing, PfUiLanguage lang) {
        var pills = new List<(string Text, Vector4 Fg)>();

        if (listing.IsHighEnd) pills.Add((PfUiText.HighEndCheckbox(lang), UiTheme.Warning));
        pills.Add((PfUiText.AreaTabLabel(lang, listing.SearchAreaTab), UiTheme.Info));

        if ((listing.Objective & ObjectiveFlags.DutyCompletion) == ObjectiveFlags.DutyCompletion) pills.Add((PfUiText.DetailPillDutyCompletion(lang), UiTheme.Healthy));
        if ((listing.Objective & ObjectiveFlags.Practice) == ObjectiveFlags.Practice) pills.Add((PfUiText.DetailPillPractice(lang), UiTheme.Info));
        if ((listing.Objective & ObjectiveFlags.Loot) == ObjectiveFlags.Loot) pills.Add((PfUiText.DetailPillLoot(lang), UiTheme.Accent));

        if ((listing.Conditions & ConditionFlags.DutyComplete) == ConditionFlags.DutyComplete) pills.Add((PfUiText.DetailPillDutyComplete(lang), UiTheme.Warning));
        if ((listing.Conditions & ConditionFlags.DutyIncomplete) == ConditionFlags.DutyIncomplete) pills.Add((PfUiText.DetailPillDutyIncomplete(lang), UiTheme.Muted));
        if ((listing.Conditions & ConditionFlags.DutyCompleteWeeklyUnclaimed) == ConditionFlags.DutyCompleteWeeklyUnclaimed) pills.Add((PfUiText.DetailPillWeeklyUnclaimed(lang), UiTheme.Info));

        if ((listing.SearchArea & SearchAreaFlags.OnePlayerPerJob) == SearchAreaFlags.OnePlayerPerJob) pills.Add((PfUiText.DetailPillOnePerJob(lang), UiTheme.Muted));
        if ((listing.DutyFinderSettings & DutyFinderSettingsFlags.UnrestrictedParty) == DutyFinderSettingsFlags.UnrestrictedParty) pills.Add((PfUiText.DetailPillUnrestricted(lang), UiTheme.Warning));
        if ((listing.DutyFinderSettings & DutyFinderSettingsFlags.MinimumIL) == DutyFinderSettingsFlags.MinimumIL) pills.Add((PfUiText.DetailPillMinIl(lang), UiTheme.Info));
        if ((listing.DutyFinderSettings & DutyFinderSettingsFlags.SilenceEcho) == DutyFinderSettingsFlags.SilenceEcho) pills.Add((PfUiText.DetailPillSilenceEcho(lang), UiTheme.Info));

        if (listing.LootRules == LootRuleFlags.GreedOnly) pills.Add((PfUiText.DetailPillGreedOnly(lang), UiTheme.Muted));
        if (listing.LootRules == LootRuleFlags.Lootmaster) pills.Add((PfUiText.DetailPillLootmaster(lang), UiTheme.Danger));

        var availX = ImGui.GetContentRegionAvail().X;
        var usedX = 0f;
        for (var i = 0; i < pills.Count; i++) {
            var (text, fg) = pills[i];
            var approxW = ImGui.CalcTextSize((ImU8String)text).X + 16f;
            if (i > 0 && usedX + approxW < availX) {
                ImGui.SameLine();
            } else if (i > 0) {
                usedX = 0f;
            }

            UiTheme.DrawPillTag(text, UiTheme.PillBg, fg);
            usedX += approxW;
        }
    }

    private static void DrawSummaryStrip(
        PfListingEntry listing,
        IReadOnlyList<PfMemberRow> members,
        PfFilterState filter,
        PfUiLanguage lang
    ) {
        var filled = members.Count(m => m.JobId != 0);
        var cleared = members.Count(m => m.Progress.Cleared);
        var cached = members.Count(m => m.EnrichmentState == PfMemberEnrichmentState.Cached);
        var loading = members.Count(m => m.EnrichmentState == PfMemberEnrichmentState.Loading);
        var eligibleOpen = members.Count(m => m.IsPlayerEligible);
        var blockedCount = members.Count(m => m.IsBlocked);

        UiTheme.DrawPillTag(PfUiText.DetailSummaryMembers(lang, listing.SlotsFilled, listing.SlotsAvailable), UiTheme.PillBg, UiTheme.Accent);
        ImGui.SameLine();
        UiTheme.DrawPillTag(PfUiText.DetailSummaryCleared(lang, cleared, filled), UiTheme.PillBg, cleared > 0 ? UiTheme.Healthy : UiTheme.Muted);
        ImGui.SameLine();
        UiTheme.DrawPillTag(
            PfUiText.DetailSummaryCache(lang, cached, filled, loading),
            UiTheme.PillBg,
            loading > 0 ? UiTheme.Info : UiTheme.Muted);

        if (eligibleOpen > 0) {
            ImGui.SameLine();
            UiTheme.DrawPillTag(PfUiText.DetailSummaryOpenForJob(lang, eligibleOpen), UiTheme.EligibleSlotBg, UiTheme.EligibleHighlight);
        }

        if (blockedCount > 0) {
            ImGui.SameLine();
            UiTheme.DrawPillTag(PfUiText.DetailSummaryBlocked(lang, blockedCount), UiTheme.DangerHeaderBg, UiTheme.Danger);
        }

        var sampleStatsText = PfUiText.SampleStatsCheckbox(lang);
        var sampleToggleW = ImGui.CalcTextSize((ImU8String)sampleStatsText).X + ImGui.GetFrameHeight() + 14f;
        ImGui.SameLine();
        var rightToggleX = MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - sampleToggleW);
        ImGui.SetCursorPosX(rightToggleX);
        var useSampleStats = filter.UseSampleEnrichment;
        if (ImGui.Checkbox($"{sampleStatsText}##Detail", ref useSampleStats)) {
            filter.UseSampleEnrichment = useSampleStats;
        }
    }

    private void DrawRosterSection(
        PfListingEntry listing,
        IReadOnlyList<PfMemberRow> members,
        bool useSampleEnrichment,
        PfUiLanguage lang
    ) {
        var partyCount = Math.Max(1, (members.Count + 7) / 8);
        if (partyCount > 1) {
            if (ImGui.BeginTabBar((ImU8String)"PfDetailPartyTabs")) {
                try {
                    for (var p = 0; p < partyCount; p++) {
                        var partyLetter = (char)('A' + p);
                        var partyMembers = members.Where(m => m.PartyIndex == p).ToList();
                        var pFilled = partyMembers.Count(m => m.JobId != 0);
                        if (ImGui.BeginTabItem((ImU8String)$"{PfUiText.DetailPartyTab(lang, partyLetter, pFilled, partyMembers.Count)}##p_{p}")) {
                            _selectedPartyTab = p;
                            ImGui.EndTabItem();
                        }
                    }
                } finally {
                    ImGui.EndTabBar();
                }
            }
        } else {
            _selectedPartyTab = 0;
        }

        var visibleRows = partyCount > 1
            ? members.Where(m => m.PartyIndex == _selectedPartyTab).ToList()
            : members;

        using var cellPadding = UiTheme.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(8f, 6f));
        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable((ImU8String)"PfMemberRosterTable", 7, flags)) {
            return;
        }

        try {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24f);
            ImGui.TableSetupColumn(PfUiText.DetailColJob(lang), ImGuiTableColumnFlags.WidthFixed, 76f);
            ImGui.TableSetupColumn(PfUiText.DetailColCharacterWorld(lang), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("FFLogs", ImGuiTableColumnFlags.WidthFixed, 88f);
            ImGui.TableSetupColumn(PfUiText.DetailColClears(lang), ImGuiTableColumnFlags.WidthFixed, 54f);
            ImGui.TableSetupColumn("Tomestone", ImGuiTableColumnFlags.WidthFixed, 108f);
            ImGui.TableSetupColumn(PfUiText.DetailColStatus(lang), ImGuiTableColumnFlags.WidthFixed, 76f);
            ImGui.TableHeadersRow();

            foreach (var row in visibleRows) {
                DrawRosterRow(row, listing.DataCenterName, useSampleEnrichment, lang);
            }
        } finally {
            ImGui.EndTable();
        }
    }

    private void DrawRosterRow(PfMemberRow row, string dataCenterName, bool useSampleEnrichment, PfUiLanguage lang) {
        ImGui.TableNextRow();
        using var rowId = ImRaii.PushId(row.SlotIndex);

        var isEmptySlot = row.JobId == 0;
        if (row.IsBlocked) {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(UiTheme.BlockedRowBg));
        } else if (row.IsPlayerEligible) {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(UiTheme.EligibleSlotBg));
        }

        // 1. Slot index
        ImGui.TableNextColumn();
        UiTheme.DrawColoredText(UiTheme.Muted, $"{(row.SlotIndex % 8) + 1}");

        // 2. Job icon + code (or frameless role icon for empty slots)
        ImGui.TableNextColumn();
        if (!isEmptySlot) {
            var jobIconId = PfIconCatalog.GetJobIconId(row.JobId);
            if (_icons.TryDrawGameIcon(jobIconId, new Vector2(20f, 20f))) {
                ImGui.SameLine(0f, 6f);
            }
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(_icons.ResolveJobAbbreviation(row.JobId));
        } else {
            var roles = PartyFinderSlotFlagFormatter.SummarizeRoles(row.AcceptingMask);
            var uv = PfIconCatalog.GetFramelessRoleUv(roles);
            if (_icons.TryDrawAtlasSprite(PfIconCatalog.LfgTexturePath, uv, new Vector2(20f, 20f))) {
                ImGui.SameLine(0f, 6f);
            }
            UiTheme.DrawColoredText(UiTheme.Muted, PartyFinderSlotFlagFormatter.FormatRoleSummary(row.AcceptingMask, lang));
        }

        // 3. Character @ World
        ImGui.TableNextColumn();
        if (isEmptySlot) {
            var flagHex = $"0x{row.AcceptingMask:X16}";
            var summary = PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(row.AcceptingMask, lang);
            var text = row.IsPlayerEligible
                ? PfUiText.DetailOpenMatching(lang, summary)
                : PfUiText.DetailOpenSummary(lang, summary);
            UiTheme.DrawColoredText(row.IsPlayerEligible ? UiTheme.EligibleHighlight : UiTheme.Muted, text);
            if (ImGui.IsItemHovered()) {
                ImGui.BeginTooltip();
                try {
                    using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 28f);
                    UiTheme.DrawColoredWrappedText(UiTheme.Muted, PartyFinderSlotFlagFormatter.FormatAcceptingJobs(flagHex, lang));
                } finally {
                    ImGui.EndTooltip();
                }
            }
        } else {
            var displayName = !string.IsNullOrWhiteSpace(row.Name)
                ? (string.IsNullOrWhiteSpace(row.HomeWorldName) ? row.Name : $"{row.Name} @ {row.HomeWorldName}")
                : (row.ContentId != 0 ? PfUiText.DetailUnresolvedMember(lang, row.ContentId) : PfUiText.DetailUnknownPlayer(lang));

            var leaderPrefix = row.IsLeader ? PfUiText.DetailLeaderPrefix(lang) : string.Empty;
            var nameColor = row.IsBlocked
                ? UiTheme.BlockedText
                : (string.IsNullOrWhiteSpace(row.Name)
                    ? UiTheme.Muted
                    : (row.IsLeader ? UiTheme.Accent : new Vector4(0.92f, 0.94f, 0.97f, 1.0f)));
            ImGui.BeginGroup();
            try {
                if (row.IsBlocked) {
                    UiTheme.DrawColoredText(UiTheme.Danger, PfUiText.DetailBlockedPrefix(lang));
                    ImGui.SameLine(0f, 0f);
                }
                UiTheme.DrawColoredText(nameColor, $"{leaderPrefix}{displayName}");
            } finally {
                ImGui.EndGroup();
            }

            if (!string.IsNullOrWhiteSpace(row.Name) && ImGui.BeginPopupContextItem($"member_ctx_{row.SlotIndex}")) {
                try {
                    var fullIdentity = string.IsNullOrWhiteSpace(row.HomeWorldName)
                        ? row.Name
                        : $"{row.Name} @ {row.HomeWorldName}";
                    var hasFflogsUrl = PfExternalLinks.TryBuildFFLogsCharacterUrl(
                        row.Name,
                        row.HomeWorldName,
                        row.HomeWorldId,
                        dataCenterName,
                        out var fflogsUrl);
                    using (ImRaii.Disabled(!hasFflogsUrl)) {
                        if (ImGui.MenuItem(PfUiText.ContextOpenOnFflogs(lang)) && hasFflogsUrl) {
                            UiTheme.OpenUrl(fflogsUrl);
                        }
                    }

                    var hasTomestoneUrl = PfExternalLinks.TryBuildTomestoneCharacterUrl(
                        row.Name,
                        row.HomeWorldName,
                        out var tomestoneUrl);
                    using (ImRaii.Disabled(!hasTomestoneUrl)) {
                        if (ImGui.MenuItem(PfUiText.ContextOpenOnTomestone(lang)) && hasTomestoneUrl) {
                            UiTheme.OpenUrl(tomestoneUrl);
                        }
                    }

                    ImGui.Separator();
                    if (ImGui.MenuItem(PfUiText.ContextCopyIdentity(lang, fullIdentity))) {
                        UiTheme.CopyToClipboard(fullIdentity);
                    }
                    if (!string.IsNullOrWhiteSpace(row.HomeWorldName) && ImGui.MenuItem(PfUiText.ContextCopyNameWorld(lang))) {
                        UiTheme.CopyToClipboard($"{row.Name}@{row.HomeWorldName}");
                    }
                } finally {
                    ImGui.EndPopup();
                }
            }
        }

        // 4. FFLogs
        ImGui.TableNextColumn();
        DrawParseCell(row, isEmptySlot, useSampleEnrichment, lang);

        // 5. Clears
        ImGui.TableNextColumn();
        if (isEmptySlot || !row.Progress.ClearCount.HasValue || row.Progress.ClearCount.Value == 0) {
            UiTheme.DrawColoredText(UiTheme.Muted, "--");
        } else {
            UiTheme.DrawColoredText(UiTheme.Healthy, $"x{row.Progress.ClearCount.Value}");
        }

        // 6. Tomestone
        ImGui.TableNextColumn();
        DrawTomestoneCell(row, isEmptySlot, useSampleEnrichment, lang);

        // 7. Status
        ImGui.TableNextColumn();
        if (isEmptySlot) {
            UiTheme.DrawColoredText(
                row.IsPlayerEligible ? UiTheme.EligibleHighlight : UiTheme.Muted,
                row.IsPlayerEligible ? PfUiText.DetailStatusEligible(lang) : PfUiText.DetailStatusOpen(lang));
        } else {
            var (statusText, statusColor) = row.EnrichmentState switch {
                PfMemberEnrichmentState.Cached => (PfUiText.DetailStatusCached(lang), UiTheme.Healthy),
                PfMemberEnrichmentState.Loading => (PfUiText.DetailStatusLoading(lang), UiTheme.Info),
                PfMemberEnrichmentState.Unresolved => (PfUiText.DetailStatusUnresolved(lang), UiTheme.Warning),
                _ => (useSampleEnrichment ? PfUiText.DetailStatusReady(lang) : PfUiText.DetailStatusLocal(lang), UiTheme.Muted),
            };
            UiTheme.DrawColoredText(statusColor, statusText);
        }
    }

    private static void DrawParseCell(PfMemberRow row, bool isEmptySlot, bool useSampleEnrichment, PfUiLanguage lang) {
        if (isEmptySlot) {
            UiTheme.DrawColoredText(UiTheme.Muted, "--");
            return;
        }

        if (row.EnrichmentState == PfMemberEnrichmentState.Loading) {
            UiTheme.DrawColoredText(UiTheme.Info, PfUiText.DetailStatusLoadingEllipsis(lang));
            return;
        }

        if (!useSampleEnrichment && !row.Parse.PrimaryPercentile.HasValue && !row.Parse.Hidden) {
            UiTheme.DrawColoredText(UiTheme.Muted, "--");
            return;
        }

        if (row.Parse.Hidden) {
            UiTheme.DrawColoredText(UiTheme.Muted, PfUiText.HiddenBadge(lang));
            return;
        }

        if (!row.Parse.PrimaryPercentile.HasValue) {
            UiTheme.DrawColoredText(UiTheme.Muted, "--");
            return;
        }

        var prefix = row.Parse.Estimated ? "~" : string.Empty;
        ImGui.BeginGroup();
        try {
            UiTheme.DrawColoredText(UiTheme.GetParseColor(row.Parse.PrimaryPercentile), $"{prefix}{row.Parse.PrimaryPercentile.Value}");
            if (row.Parse.HasSecondary) {
                ImGui.SameLine(0f, 3f);
                UiTheme.DrawColoredText(UiTheme.Muted, "/");
                ImGui.SameLine(0f, 3f);
                if (row.Parse.SecondaryPercentile.HasValue) {
                    UiTheme.DrawColoredText(
                        UiTheme.GetParseColor(row.Parse.SecondaryPercentile),
                        $"{prefix}{row.Parse.SecondaryPercentile.Value}");
                } else {
                    UiTheme.DrawColoredText(UiTheme.Muted, "--");
                }
            }

            if (!row.Parse.LockedIn) {
                ImGui.SameLine(0f, 2f);
                UiTheme.DrawColoredText(UiTheme.Warning, "*");
            }
        } finally {
            ImGui.EndGroup();
        }

        if ((row.Parse.Estimated || !row.Parse.LockedIn) && ImGui.IsItemHovered()) {
            var tips = new List<string>(2);
            if (!row.Parse.LockedIn) {
                tips.Add(PfUiText.ParseUnlockedTooltip(lang));
            }
            if (row.Parse.Estimated) {
                tips.Add(PfUiText.ParseEstimatedTooltip(lang));
            }
            ImGui.SetTooltip(string.Join("\n", tips));
        }
    }

    private static void DrawTomestoneCell(PfMemberRow row, bool isEmptySlot, bool useSampleEnrichment, PfUiLanguage lang) {
        if (isEmptySlot) {
            UiTheme.DrawColoredText(UiTheme.Muted, "--");
            return;
        }

        if (row.EnrichmentState == PfMemberEnrichmentState.Loading) {
            UiTheme.DrawColoredText(UiTheme.Info, PfUiText.DetailStatusLoadingEllipsis(lang));
            return;
        }

        if (row.Progress.Cleared && row.Parse.HasVisiblePercentile) {
            UiTheme.DrawColoredText(UiTheme.Muted, "--");
            return;
        }

        var label = row.Progress.SummaryLabel;
        if (string.IsNullOrWhiteSpace(label)) {
            UiTheme.DrawColoredText(UiTheme.Muted, useSampleEnrichment ? "--" : "--");
            return;
        }

        var displayLabel = label switch {
            "Cleared" => PfUiText.ClearedBadge(lang),
            "Hidden" => PfUiText.HiddenBadge(lang),
            _ => label,
        };
        var color = row.Progress.Cleared
            ? UiTheme.Healthy
            : (label == "Hidden" ? UiTheme.Muted : UiTheme.Warning);
        UiTheme.DrawColoredText(color, displayLabel);

        if (row.Progress.PhaseProgress.Count > 0 && ImGui.IsItemHovered()) {
            ImGui.BeginTooltip();
            try {
                foreach (var phase in row.Progress.PhaseProgress) {
                    var hpText = phase.BestHpPercent.HasValue
                        ? $"{phase.BestHpPercent.Value:0.0}%"
                        : (phase.Cleared ? PfUiText.ClearedBadge(lang) : "-");
                    ImGui.TextUnformatted(PfUiText.PhasePullsTooltip(lang, phase.PhaseName, hpText, phase.BestPulls));
                }
            } finally {
                ImGui.EndTooltip();
            }
        }
    }

    private void DrawBottomActionBar(
        PfListingEntry listing,
        PfFilterState filter,
        bool isPluginOperational,
        Action? onCloseRequested,
        PfUiLanguage lang
    ) {
        ImGui.Separator();
        var joinText = PfUiText.DetailJoinOpenNativeButton(lang);
        var joinW = MathF.Max(185f, ImGui.CalcTextSize((ImU8String)joinText).X + 18f);
        using (ImRaii.Disabled(listing.IsSample)) {
            if (ImGui.Button(joinText, new Vector2(joinW, 0f))) {
                _nativeBridge.QueueOpenListing(listing.Id);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            ImGui.SetTooltip(listing.IsSample
                ? PfUiText.DetailJoinSampleTooltip(lang)
                : PfUiText.DetailJoinLiveTooltip(lang));
        }

        ImGui.SameLine();
        var copySummaryText = PfUiText.DetailCopySummaryButton(lang);
        var copyW = MathF.Max(110f, ImGui.CalcTextSize((ImU8String)copySummaryText).X + 18f);
        if (ImGui.Button(copySummaryText, new Vector2(copyW, 0f))) {
            var worldSuffix = string.IsNullOrWhiteSpace(listing.HomeWorldName) ? string.Empty : $"@{listing.HomeWorldName}";
            UiTheme.CopyToClipboard($"{listing.DutyName} | {listing.LeaderName}{worldSuffix} | {listing.SlotsFilled}/{listing.SlotsAvailable} | {listing.Comment}");
        }

        if (_enrichmentClient != null) {
            ImGui.SameLine();
            var refreshText = PfUiText.DetailRefreshButton(lang);
            var refreshW = MathF.Max(115f, ImGui.CalcTextSize((ImU8String)refreshText).X + 18f);
            var canRefresh = !listing.IsSample && isPluginOperational && !filter.UseSampleEnrichment && listing.RawDuty != 0;
            using (ImRaii.Disabled(!canRefresh)) {
                if (ImGui.Button($"{refreshText}##PfDetailRefresh", new Vector2(refreshW, 0f))) {
                    RequestPartyEnrichmentForListing(listing.Id, filter, isPluginOperational, forceRefresh: true);
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                ImGui.SetTooltip(PfUiText.DetailRefreshTooltip(lang));
            }
        }

        if (onCloseRequested != null) {
            ImGui.SameLine();
            var closeText = PfUiText.CloseButton(lang);
            var closeW = MathF.Max(80f, ImGui.CalcTextSize((ImU8String)closeText).X + 18f);
            var closeX = MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - closeW);
            ImGui.SetCursorPosX(closeX);
            if (ImGui.Button(closeText, new Vector2(closeW, 0f))) {
                onCloseRequested();
            }
        }
    }
}
