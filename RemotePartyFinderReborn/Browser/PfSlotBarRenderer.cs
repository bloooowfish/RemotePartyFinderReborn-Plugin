#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal readonly record struct PfSlotVisualModel(
    int SlotIndex,
    bool IsFilled,
    byte FilledJobId,
    ulong AcceptingMask,
    PartyFinderSlotFlagFormatter.JobRoleFlags RoleFlags,
    bool MatchesPlayerJob);

/// <summary>
/// Pure slot analysis helpers plus flat DrawList 8-slot mini pill rendering matching native PF density.
/// </summary>
internal static class PfSlotBarRenderer {
    internal const int StandardBarSlotCount = 8;
    internal const float SlotWidth = 12f;
    internal const float SlotHeight = 18f;
    internal const float SlotGap = 2f;
    internal const float StandardBarWidth = (StandardBarSlotCount * SlotWidth) + ((StandardBarSlotCount - 1) * SlotGap);

    private static readonly Vector4 EmptyInsetBg = new(0.14f, 0.16f, 0.20f, 0.95f);
    private static readonly Vector4 OpenBorder = new(0.32f, 0.36f, 0.44f, 0.85f);
    private static readonly Vector4 FilledBorder = new(0.86f, 0.88f, 0.92f, 0.90f);
    private static readonly Vector4 EligibleBorder = UiTheme.EligibleHighlight;

    internal static PfSlotVisualModel AnalyzeSlot(
        int slotIndex,
        ulong acceptingMask,
        byte filledJobId,
        uint? playerClassJobId = null
    ) {
        var isFilled = filledJobId != 0;
        PartyFinderSlotFlagFormatter.JobRoleFlags roles;

        if (isFilled) {
            roles = PartyFinderSlotFlagFormatter.RoleForClassJobId(filledJobId) switch {
                PartyFinderSlotFlagFormatter.JobRoleGroup.Tank => PartyFinderSlotFlagFormatter.JobRoleFlags.Tank,
                PartyFinderSlotFlagFormatter.JobRoleGroup.Healer => PartyFinderSlotFlagFormatter.JobRoleFlags.Healer,
                PartyFinderSlotFlagFormatter.JobRoleGroup.Dps => PartyFinderSlotFlagFormatter.JobRoleFlags.Dps,
                _ => PartyFinderSlotFlagFormatter.SummarizeRoles(acceptingMask),
            };
        } else {
            roles = PartyFinderSlotFlagFormatter.SummarizeRoles(acceptingMask);
        }

        var matchesPlayer = !isFilled
            && playerClassJobId.HasValue
            && DoesJobMatchAcceptingMask(playerClassJobId.Value, acceptingMask);

        return new PfSlotVisualModel(
            slotIndex,
            isFilled,
            filledJobId,
            acceptingMask,
            roles,
            matchesPlayer);
    }

    internal static bool DoesJobMatchAcceptingMask(uint classJobId, ulong acceptingMask) {
        if (acceptingMask == 0 || classJobId == 0) {
            return false;
        }

        return PartyFinderSlotFlagFormatter.TryMapClassJobId(classJobId) is { } flag
               && (acceptingMask & (ulong)flag) != 0;
    }

    internal static bool HasAnyEligibleOpenSlot(PfListingEntry listing, uint? playerClassJobId) {
        if (!playerClassJobId.HasValue || playerClassJobId.Value == 0) {
            return false;
        }

        var slotCount = listing.Slots.Count;
        for (var i = 0; i < slotCount; i++) {
            var filledJobId = i < listing.JobsPresent.Count ? listing.JobsPresent[i] : (byte)0;
            if (filledJobId == 0 && DoesJobMatchAcceptingMask(playerClassJobId.Value, listing.Slots[i].AcceptingMask)) {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<Vector4> GetBandColors(PartyFinderSlotFlagFormatter.JobRoleFlags roles) {
        var list = new List<Vector4>(3);
        if ((roles & PartyFinderSlotFlagFormatter.JobRoleFlags.Tank) != 0) list.Add(UiTheme.RoleTank);
        if ((roles & PartyFinderSlotFlagFormatter.JobRoleFlags.Healer) != 0) list.Add(UiTheme.RoleHealer);
        if ((roles & PartyFinderSlotFlagFormatter.JobRoleFlags.Dps) != 0) list.Add(UiTheme.RoleDps);
        if (list.Count == 0) list.Add(UiTheme.RoleAny);
        return list;
    }

    internal static void DrawMiniSlotBar(
        string id,
        PfListingEntry listing,
        PfIconCatalog icons,
        uint? playerClassJobId
    ) {
        var totalSlots = Math.Clamp(
            Math.Max((int)listing.SlotsAvailable, Math.Max(listing.Slots.Count, listing.JobsPresent.Count)),
            1,
            StandardBarSlotCount);
        var displayCount = Math.Min(totalSlots, StandardBarSlotCount);

        var startPos = ImGui.GetCursorScreenPos();
        var barSize = new Vector2(StandardBarWidth, SlotHeight);
        ImGui.Dummy(barSize);
        var hoveredBar = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        var models = new List<PfSlotVisualModel>(displayCount);
        for (var i = 0; i < displayCount; i++) {
            var accepting = i < listing.Slots.Count ? listing.Slots[i].AcceptingMask : 0UL;
            var filledJob = i < listing.JobsPresent.Count ? listing.JobsPresent[i] : (byte)0;
            var model = AnalyzeSlot(i, accepting, filledJob, playerClassJobId);
            models.Add(model);

            var x0 = startPos.X + (i * (SlotWidth + SlotGap));
            var y0 = startPos.Y;
            var pMin = new Vector2(x0, y0);
            var pMax = new Vector2(x0 + SlotWidth, y0 + SlotHeight);

            DrawSinglePill(drawList, pMin, pMax, model);
        }

        if (hoveredBar) {
            DrawSlotBarTooltip(models, listing, icons);
        }
    }

    private static void DrawSinglePill(ImDrawListPtr drawList, Vector2 pMin, Vector2 pMax, PfSlotVisualModel model) {
        var bands = GetBandColors(model.RoleFlags);
        var alpha = model.IsFilled ? 1.0f : 0.34f;

        drawList.AddRectFilled(pMin, pMax, ImGui.GetColorU32(EmptyInsetBg), 2.5f);

        var innerMin = new Vector2(pMin.X + 1.5f, pMin.Y + 1.5f);
        var innerMax = new Vector2(pMax.X - 1.5f, pMax.Y - 1.5f);
        var innerH = innerMax.Y - innerMin.Y;
        var sliceH = innerH / bands.Count;

        for (var b = 0; b < bands.Count; b++) {
            var col = new Vector4(bands[b].X, bands[b].Y, bands[b].Z, alpha);
            var bMin = new Vector2(innerMin.X, innerMin.Y + (b * sliceH));
            var bMax = new Vector2(innerMax.X, b == bands.Count - 1 ? innerMax.Y : innerMin.Y + ((b + 1) * sliceH) - 0.5f);
            drawList.AddRectFilled(bMin, bMax, ImGui.GetColorU32(col), 1.5f);
        }

        var borderCol = model.IsFilled
            ? FilledBorder
            : (model.MatchesPlayerJob ? EligibleBorder : OpenBorder);
        var thickness = !model.IsFilled && model.MatchesPlayerJob ? 1.75f : 1.0f;
        drawList.AddRect(pMin, pMax, ImGui.GetColorU32(borderCol), 2.5f, ImDrawFlags.None, thickness);
    }

    private static void DrawSlotBarTooltip(
        IReadOnlyList<PfSlotVisualModel> models,
        PfListingEntry listing,
        PfIconCatalog icons
    ) {
        var lang = icons.UiLanguage;
        ImGui.BeginTooltip();
        try {
            using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 28f);
            UiTheme.DrawColoredText(UiTheme.Accent, PfUiText.SlotsFilledHeader(lang, listing.SlotsFilled, listing.SlotsAvailable));
            ImGui.Separator();
            var filledTag = PfUiText.SlotFilledLabel(lang);
            var openTag = PfUiText.SlotOpenLabel(lang);
            foreach (var slot in models) {
                if (slot.IsFilled) {
                    var abbr = icons.ResolveJobAbbreviation(slot.FilledJobId);
                    ImGui.TextUnformatted($"#{slot.SlotIndex + 1} {filledTag} {abbr}");
                } else {
                    var acceptingText = PartyFinderSlotFlagFormatter.FormatCompactAcceptingJobs(slot.AcceptingMask, lang);
                    var prefix = slot.MatchesPlayerJob ? "* " : string.Empty;
                    var color = slot.MatchesPlayerJob ? UiTheme.EligibleHighlight : UiTheme.Muted;
                    UiTheme.DrawColoredWrappedText(color, $"#{slot.SlotIndex + 1} {prefix}{openTag}: {acceptingText}");
                }
            }
        } finally {
            ImGui.EndTooltip();
        }
    }
}
