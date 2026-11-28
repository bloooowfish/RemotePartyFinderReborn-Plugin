#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

/// <summary>
/// Clean, flat ImGui Party Finder list view mirroring native LFG.uld density and workflow.
/// </summary>
internal sealed class PfListPane {
    private const float TimeColumnWidth = 40f;

    private readonly PfLocalMirrorStore _store;
    private readonly PfIconCatalog _icons;
    private readonly NativePfBridge _nativeBridge;
    private readonly PfEnrichmentClient? _enrichmentClient;
    private readonly Action<PfBlockedDisplayMode>? _setBlockedDisplayMode;
    private readonly PfFilterState _filter = new();
    private string _searchBuffer = string.Empty;
    private int _lastRenderedPageGeneration;
    private bool _scrollToTopRequested;
    private bool _pendingNativeFilterSync;
    private bool _wasDetailedSearchActive;

    internal PfListPane(
        PfLocalMirrorStore store,
        PfIconCatalog icons,
        NativePfBridge nativeBridge,
        Func<PfBlockedDisplayMode>? getBlockedDisplayMode = null,
        Action<PfBlockedDisplayMode>? setBlockedDisplayMode = null,
        PfEnrichmentClient? enrichmentClient = null
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _nativeBridge = nativeBridge ?? throw new ArgumentNullException(nameof(nativeBridge));
        _enrichmentClient = enrichmentClient;
        _setBlockedDisplayMode = setBlockedDisplayMode;
        if (getBlockedDisplayMode != null) {
            _filter.BlockedDisplayMode = getBlockedDisplayMode();
        }
    }

    internal PfFilterState Filter => _filter;
    internal ulong SelectedListingId { get; set; }

    internal event Action<ulong, bool>? ListingActivated;

    internal void OnWindowOpened() {
        _store.RefreshBlacklistCacheFromGame(force: true);
        var nativeUi = _store.RefreshNativeUiSnapshotFromGame(force: true);
        if (nativeUi.HasServerData) {
            SyncFilterFromNativeUi(nativeUi);
            _pendingNativeFilterSync = false;
        } else {
            _pendingNativeFilterSync = true;
        }

        _nativeBridge.QueueEnsureHiddenNativePfHost();
        _scrollToTopRequested = true;
    }

    internal void OnWindowClosed() {
        _pendingNativeFilterSync = false;
        _nativeBridge.QueueReleaseHiddenNativePfHost();
    }

    private void SyncFilterFromNativeUi(PfNativeUiSnapshot nativeUi) {
        _filter.AreaTab = nativeUi.SearchAreaTab switch {
            1 => PfSearchAreaTab.World,
            2 => PfSearchAreaTab.Private,
            _ => PfSearchAreaTab.All,
        };

        if (nativeUi.CategoryTab == 0 || nativeUi.CategoryTab >= PfIconCatalog.CategoryTabs.Count) {
            _filter.SelectAllCategories();
        } else {
            var activeTab = PfIconCatalog.CategoryTabs[nativeUi.CategoryTab];
            if (activeTab.IsOther) {
                _filter.SelectOtherCategory();
            } else if (activeTab.Category is { } cat) {
                _filter.SelectCategory(cat);
            } else {
                _filter.SelectAllCategories();
            }
        }
    }

    internal void Draw(uint? playerClassJobId, bool isPluginOperational, ushort? currentWorldId = null) {
        var nativeUi = _store.RefreshNativeUiSnapshotFromGame();
        if (_pendingNativeFilterSync && nativeUi.HasServerData) {
            SyncFilterFromNativeUi(nativeUi);
            _pendingNativeFilterSync = false;
        }

        if (nativeUi.IsDetailedSearchActive && !_wasDetailedSearchActive) {
            _filter.SelectAllCategories();
            _scrollToTopRequested = true;
        }
        _wasDetailedSearchActive = nativeUi.IsDetailedSearchActive;

        var currentPageGen = _store.CurrentPageGeneration;
        if (currentPageGen != _lastRenderedPageGeneration) {
            _lastRenderedPageGeneration = currentPageGen;
            _scrollToTopRequested = true;
        }

        var allListings = _store.GetListingsSnapshot(_filter.IncludeSampleListings, _filter.UseSampleEnrichment);
        var regionDcs = currentWorldId is > 0 ? _icons.GetRegionDataCenters(currentWorldId.Value) : Array.Empty<string>();
        var availableDcs = PfFilterEngine.GetAvailableDataCenters(allListings, _filter.IncludeSampleListings, currentPageGen, regionDcs);
        if (!string.IsNullOrEmpty(_filter.SelectedDataCenter)
            && !availableDcs.Contains(_filter.SelectedDataCenter, StringComparer.OrdinalIgnoreCase)) {
            _filter.SelectedDataCenter = string.Empty;
        }

        var hideBlocked = _filter.BlockedDisplayMode == PfBlockedDisplayMode.Hide;
        var areaCounts = PfFilterEngine.ComputeAreaCounts(allListings, _filter.IncludeSampleListings, currentPageGen, hideBlocked);
        var categoryCounts = PfFilterEngine.ComputeCategoryCounts(allListings, _filter.AreaTab, _filter.IncludeSampleListings, _filter.SelectedDataCenter, currentPageGen, hideBlocked);
        var filtered = PfFilterEngine.FilterAndSort(allListings, _filter, playerClassJobId, currentPageGen, nativeUi.NativeOrderedListingIds);

        if (isPluginOperational && !_filter.UseSampleEnrichment) {
            _enrichmentClient?.NotifyVisibleListings(filtered);
        }

        DrawTopAreaBar(areaCounts, nativeUi, playerClassJobId);
        ImGui.Spacing();
        DrawCategoryBar(categoryCounts, nativeUi);
        ImGui.Spacing();
        DrawToolbar(availableDcs);
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + 6f;
        var listOpen = ImGui.BeginChild((ImU8String)"PfListScroll", new Vector2(0f, -footerHeight), true, ImGuiWindowFlags.None);
        try {
            if (listOpen) {
                if (_scrollToTopRequested) {
                    ImGui.SetScrollY(0f);
                    _scrollToTopRequested = false;
                }

                DrawListingRows(filtered, nativeUi, playerClassJobId);
            }
        } finally {
            ImGui.EndChild();
        }

        DrawFooterBar(filtered.Count, _store.CurrentPageLiveCount, nativeUi, isPluginOperational);
    }

    private void DrawTopAreaBar(
        IReadOnlyDictionary<PfSearchAreaTab, int> areaCounts,
        PfNativeUiSnapshot nativeUi,
        uint? playerClassJobId
    ) {
        var lang = _icons.UiLanguage;
        var useServerAreaCounts = nativeUi.HasServerData
            && nativeUi.AreaCounts.Count == 3
            && string.IsNullOrEmpty(_filter.SelectedDataCenter);

        DrawAreaTabButton(PfUiText.AreaTabLabel(lang, PfSearchAreaTab.All), PfSearchAreaTab.All, areaCounts[PfSearchAreaTab.All], useServerAreaCounts ? nativeUi.AreaCounts[0] : -1, 88f, nativeUi.IsNativeAgentInitialized, lang);
        ImGui.SameLine();
        DrawAreaTabButton(PfUiText.AreaTabLabel(lang, PfSearchAreaTab.World), PfSearchAreaTab.World, areaCounts[PfSearchAreaTab.World], useServerAreaCounts ? nativeUi.AreaCounts[1] : -1, 88f, nativeUi.IsNativeAgentInitialized, lang);
        ImGui.SameLine();
        DrawAreaTabButton(PfUiText.AreaTabLabel(lang, PfSearchAreaTab.Private), PfSearchAreaTab.Private, areaCounts[PfSearchAreaTab.Private], useServerAreaCounts ? nativeUi.AreaCounts[2] : -1, 88f, nativeUi.IsNativeAgentInitialized, lang);

        var jobAbbr = playerClassJobId is > 0 ? _icons.ResolveJobAbbreviation((byte)playerClassJobId.Value) : null;
        var jobLabel = PfUiText.MyJobLabel(lang, jobAbbr);
        var highEndLabel = PfUiText.HighEndCheckbox(lang);
        var samplesLabel = PfUiText.SamplesCheckbox(lang);
        var sampleStatsLabel = PfUiText.SampleStatsCheckbox(lang);

        var frameH = ImGui.GetFrameHeight();
        var itemSpacingX = ImGui.GetStyle().ItemSpacing.X;
        var rightCheckboxesW = (ImGui.CalcTextSize((ImU8String)jobLabel).X + frameH + 8f)
                               + (ImGui.CalcTextSize((ImU8String)highEndLabel).X + frameH + 8f)
                               + (ImGui.CalcTextSize((ImU8String)samplesLabel).X + frameH + 8f)
                               + (ImGui.CalcTextSize((ImU8String)sampleStatsLabel).X + frameH + 8f)
                               + (itemSpacingX * 3f);

        ImGui.SameLine();
        var toggleStartX = MathF.Max(ImGui.GetCursorPosX() + 8f, ImGui.GetWindowContentRegionMax().X - rightCheckboxesW);
        ImGui.SetCursorPosX(toggleStartX);

        var myJob = _filter.MyJobOnly;
        if (ImGui.Checkbox($"{jobLabel}##PfMyJob", ref myJob)) {
            _filter.MyJobOnly = myJob;
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.MyJobTooltip(lang));
        }

        ImGui.SameLine();
        var highEndOnly = _filter.HighEndOnly;
        if (ImGui.Checkbox($"{highEndLabel}##PfHighEnd", ref highEndOnly)) {
            _filter.HighEndOnly = highEndOnly;
        }

        ImGui.SameLine();
        var includeSamples = _filter.IncludeSampleListings;
        if (ImGui.Checkbox($"{samplesLabel}##PfSamples", ref includeSamples)) {
            _filter.IncludeSampleListings = includeSamples;
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.SamplesTooltip(lang));
        }

        ImGui.SameLine();
        var useSampleStats = _filter.UseSampleEnrichment;
        if (ImGui.Checkbox($"{sampleStatsLabel}##PfSampleStats", ref useSampleStats)) {
            _filter.UseSampleEnrichment = useSampleStats;
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.SampleStatsTooltip(lang));
        }
    }

    private void DrawAreaTabButton(
        string label,
        PfSearchAreaTab tab,
        int localCount,
        int serverCount,
        float minWidth,
        bool canQueryNative,
        PfUiLanguage lang
    ) {
        var isSelected = _filter.AreaTab == tab;
        var displayCount = serverCount >= 0 ? serverCount : localCount;
        var text = $"{label} ({displayCount})";
        var width = MathF.Max(minWidth, ImGui.CalcTextSize((ImU8String)text).X + 16f);
        using var btnCol = UiTheme.PushColor(ImGuiCol.Button, UiTheme.SidebarSelectedBg, isSelected);
        using var hoverCol = UiTheme.PushColor(ImGuiCol.ButtonHovered, UiTheme.RowHoverBg, isSelected);
        if (ImGui.Button($"{text}##area_{tab}", new Vector2(width, 0f))) {
            _filter.AreaTab = tab;
            _filter.SelectAllCategories();
            _scrollToTopRequested = true;
            if (canQueryNative) {
                _nativeBridge.QueueSelectAreaTab((byte)tab);
            }
        }
        if (serverCount >= 0 && ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.AreaTabTooltip(lang, label, serverCount, localCount));
        }
    }

    private void DrawCategoryBar(IReadOnlyDictionary<string, int> categoryCounts, PfNativeUiSnapshot nativeUi) {
        var lang = _icons.UiLanguage;
        const float topZoneHeight = 26f;
        const float bottomZoneHeight = 18f;
        const float gapX = 3f;
        var btnSize = new Vector2(41f, topZoneHeight + bottomZoneHeight);
        var availW = ImGui.GetContentRegionAvail().X;
        var stepX = btnSize.X + gapX;
        var cursorStartX = ImGui.GetCursorPosX();
        var currentX = cursorStartX;

        var useServerCounts = nativeUi.HasServerData
            && nativeUi.CategoryCounts.Count == PfIconCatalog.CategoryTabs.Count
            && string.IsNullOrEmpty(_filter.SelectedDataCenter)
            && nativeUi.SearchAreaTab == (byte)_filter.AreaTab;

        var isDetailedSearchActive = nativeUi.IsDetailedSearchActive;
        var dividerColor = new Vector4(UiTheme.CardDivider.X, UiTheme.CardDivider.Y, UiTheme.CardDivider.Z, 0.45f);

        for (var i = 0; i < PfIconCatalog.CategoryTabs.Count; i++) {
            var tab = PfIconCatalog.CategoryTabs[i];
            if (i > 0) {
                if (currentX + stepX <= cursorStartX + availW) {
                    ImGui.SameLine(0f, gapX);
                } else {
                    currentX = cursorStartX;
                }
            }

            var isSelected = !isDetailedSearchActive
                && (tab.IsOther
                    ? _filter.OtherCategorySelected
                    : (!_filter.OtherCategorySelected && _filter.SelectedCategory == tab.Category));
            categoryCounts.TryGetValue(tab.Id, out var localCount);
            var serverCount = useServerCounts ? nativeUi.CategoryCounts[i] : -1;
            var displayCount = useServerCounts ? serverCount : localCount;

            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##cat_{tab.Id}", btnSize)) {
                if (tab.IsOther) {
                    _filter.SelectOtherCategory();
                } else if (tab.Category is { } cat) {
                    _filter.SelectCategory(cat);
                } else {
                    _filter.SelectAllCategories();
                }

                _scrollToTopRequested = true;
                if (nativeUi.IsNativeAgentInitialized) {
                    _nativeBridge.QueueRequestCategoryListings((byte)i);
                }
            }

            var hovered = ImGui.IsItemHovered();
            var drawList = ImGui.GetWindowDrawList();
            var pMax = new Vector2(pos.X + btnSize.X, pos.Y + btnSize.Y);
            var bg = isSelected
                ? UiTheme.SidebarSelectedBg
                : (hovered ? UiTheme.RowHoverBg : UiTheme.CardBodyBg);
            drawList.AddRectFilled(pos, pMax, ImGui.GetColorU32(bg), 4f);
            drawList.AddRect(pos, pMax, ImGui.GetColorU32(isSelected ? UiTheme.Accent : UiTheme.CardBorder), 4f, ImDrawFlags.None, isSelected ? 1.5f : 1f);

            var dividerY = pos.Y + topZoneHeight;
            drawList.AddLine(new Vector2(pos.X + 4f, dividerY), new Vector2(pMax.X - 4f, dividerY), ImGui.GetColorU32(dividerColor));

            if (tab.Category is null && !tab.IsOther) {
                var text = PfUiText.CategoryAllShort(lang);
                var textSz = ImGui.CalcTextSize((ImU8String)text);
                drawList.AddText(
                    new Vector2(pos.X + ((btnSize.X - textSz.X) * 0.5f), pos.Y + ((topZoneHeight - textSz.Y) * 0.5f)),
                    ImGui.GetColorU32(UiTheme.Accent),
                    (ImU8String)text);
            } else if (tab.IsOther) {
                var text = PfUiText.CategoryOtherShort(lang);
                var textSz = ImGui.CalcTextSize((ImU8String)text);
                drawList.AddText(
                    new Vector2(pos.X + ((btnSize.X - textSz.X) * 0.5f), pos.Y + ((topZoneHeight - textSz.Y) * 0.5f)),
                    ImGui.GetColorU32(UiTheme.Muted),
                    (ImU8String)text);
            } else {
                var iconMin = new Vector2(pos.X + ((btnSize.X - 22f) * 0.5f), pos.Y + 2f);
                var iconMax = new Vector2(iconMin.X + 22f, iconMin.Y + 22f);
                if (!_icons.TryDrawGameIconAt(drawList, tab.IconId, iconMin, iconMax)) {
                    var localizedName = PfUiText.CategoryLabel(lang, tab.Id, tab.Label);
                    var shortLabel = localizedName.Length > 3 ? localizedName[..3] : localizedName;
                    var sz = ImGui.CalcTextSize((ImU8String)shortLabel);
                    drawList.AddText(
                        new Vector2(pos.X + ((btnSize.X - sz.X) * 0.5f), pos.Y + ((topZoneHeight - sz.Y) * 0.5f)),
                        ImGui.GetColorU32(UiTheme.Muted),
                        (ImU8String)shortLabel);
                }
            }

            if (displayCount > 0 || useServerCounts) {
                var countText = displayCount > 999 ? "999+" : displayCount.ToString();
                var countSz = ImGui.CalcTextSize((ImU8String)countText);
                var countY = dividerY + ((bottomZoneHeight - countSz.Y) * 0.5f) - 1f;
                drawList.AddText(
                    new Vector2(pos.X + ((btnSize.X - countSz.X) * 0.5f), countY),
                    ImGui.GetColorU32(isSelected ? UiTheme.Accent : UiTheme.Muted),
                    (ImU8String)countText);
            }

            if (hovered) {
                var label = PfUiText.CategoryLabel(lang, tab.Id, tab.Label);
                ImGui.SetTooltip(PfUiText.CategoryTooltip(lang, label, serverCount, localCount, useServerCounts));
            }

            currentX += stepX;
        }

        var filterBtnSize = new Vector2(42f, topZoneHeight + bottomZoneHeight);
        var resetBtnSize = new Vector2(42f, topZoneHeight + bottomZoneHeight);
        var neededW = filterBtnSize.X + (isDetailedSearchActive ? resetBtnSize.X + 4f : 0f);
        var rightAlignX = MathF.Max(cursorStartX, cursorStartX + availW - neededW);

        if (currentX + 6f + neededW <= cursorStartX + availW) {
            ImGui.SameLine(MathF.Max(currentX + 6f, rightAlignX));
        } else {
            ImGui.SetCursorPosX(rightAlignX);
        }

        DrawDetailedSearchButton(filterBtnSize, topZoneHeight, bottomZoneHeight, dividerColor, isDetailedSearchActive, lang);

        if (isDetailedSearchActive) {
            ImGui.SameLine(0f, 4f);
            DrawDetailedSearchResetButton(resetBtnSize, topZoneHeight, bottomZoneHeight, dividerColor, lang);
        }
    }

    private void DrawDetailedSearchButton(
        Vector2 btnSize,
        float topZoneHeight,
        float bottomZoneHeight,
        Vector4 dividerColor,
        bool isDetailedSearchActive,
        PfUiLanguage lang
    ) {
        var pos = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##PfDetailedSearchBtn", btnSize)) {
            _nativeBridge.QueueOpenDetailedSearch();
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var pMax = new Vector2(pos.X + btnSize.X, pos.Y + btnSize.Y);
        var bg = isDetailedSearchActive
            ? UiTheme.SidebarSelectedBg
            : (hovered ? UiTheme.RowHoverBg : UiTheme.CardBodyBg);
        drawList.AddRectFilled(pos, pMax, ImGui.GetColorU32(bg), 4f);
        drawList.AddRect(pos, pMax, ImGui.GetColorU32(isDetailedSearchActive ? UiTheme.Accent : UiTheme.CardBorder), 4f, ImDrawFlags.None, isDetailedSearchActive ? 1.5f : 1f);

        var dividerY = pos.Y + topZoneHeight;
        drawList.AddLine(new Vector2(pos.X + 4f, dividerY), new Vector2(pMax.X - 4f, dividerY), ImGui.GetColorU32(dividerColor));

        var iconMin = new Vector2(pos.X + ((btnSize.X - 22f) * 0.5f), pos.Y + 2f);
        var iconMax = new Vector2(iconMin.X + 22f, iconMin.Y + 22f);
        if (!_icons.TryDrawAtlasSpriteAt(drawList, PfIconCatalog.PartyFinderTexturePath, PfIconCatalog.UvFilterSearch, iconMin, iconMax)) {
            var fallback = PfUiText.DetailedSearchTopFallback(lang);
            var sz = ImGui.CalcTextSize((ImU8String)fallback);
            drawList.AddText(
                new Vector2(pos.X + ((btnSize.X - sz.X) * 0.5f), pos.Y + ((topZoneHeight - sz.Y) * 0.5f)),
                ImGui.GetColorU32(isDetailedSearchActive ? UiTheme.Accent : UiTheme.Muted),
                (ImU8String)fallback);
        }

        var bottomText = PfUiText.DetailedSearchBottomLabel(lang, isDetailedSearchActive);
        var bottomSz = ImGui.CalcTextSize((ImU8String)bottomText);
        var bottomY = dividerY + ((bottomZoneHeight - bottomSz.Y) * 0.5f) - 1f;
        drawList.AddText(
            new Vector2(pos.X + ((btnSize.X - bottomSz.X) * 0.5f), bottomY),
            ImGui.GetColorU32(isDetailedSearchActive ? UiTheme.Accent : UiTheme.Muted),
            (ImU8String)bottomText);

        if (hovered) {
            ImGui.SetTooltip(PfUiText.DetailedSearchTooltip(lang, isDetailedSearchActive));
        }
    }

    private void DrawDetailedSearchResetButton(
        Vector2 btnSize,
        float topZoneHeight,
        float bottomZoneHeight,
        Vector4 dividerColor,
        PfUiLanguage lang
    ) {
        var pos = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##PfDetailedSearchResetBtn", btnSize)) {
            _filter.SelectAllCategories();
            _scrollToTopRequested = true;
            _nativeBridge.QueueRequestCategoryListings(0);
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var pMax = new Vector2(pos.X + btnSize.X, pos.Y + btnSize.Y);
        var bg = hovered ? UiTheme.WarningHeaderBg : UiTheme.CardBodyBg;
        drawList.AddRectFilled(pos, pMax, ImGui.GetColorU32(bg), 4f);
        drawList.AddRect(pos, pMax, ImGui.GetColorU32(UiTheme.Warning), 4f, ImDrawFlags.None, 1f);

        var dividerY = pos.Y + topZoneHeight;
        drawList.AddLine(new Vector2(pos.X + 4f, dividerY), new Vector2(pMax.X - 4f, dividerY), ImGui.GetColorU32(dividerColor));

        const string topText = "X";
        var topSz = ImGui.CalcTextSize((ImU8String)topText);
        drawList.AddText(
            new Vector2(pos.X + ((btnSize.X - topSz.X) * 0.5f), pos.Y + ((topZoneHeight - topSz.Y) * 0.5f)),
            ImGui.GetColorU32(UiTheme.Warning),
            (ImU8String)topText);

        var bottomText = PfUiText.DetailedSearchResetBottomLabel(lang);
        var bottomSz = ImGui.CalcTextSize((ImU8String)bottomText);
        var bottomY = dividerY + ((bottomZoneHeight - bottomSz.Y) * 0.5f) - 1f;
        drawList.AddText(
            new Vector2(pos.X + ((btnSize.X - bottomSz.X) * 0.5f), bottomY),
            ImGui.GetColorU32(UiTheme.Warning),
            (ImU8String)bottomText);

        if (hovered) {
            ImGui.SetTooltip(PfUiText.DetailedSearchResetTooltip(lang));
        }
    }

    private void DrawToolbar(IReadOnlyList<string> availableDataCenters) {
        var lang = _icons.UiLanguage;
        var sortLabels = PfUiText.GetSortLabels(lang);
        var blockedLabels = PfUiText.GetBlockedLabels(lang);
        var isCompact = _filter.ViewMode == PfListViewMode.Compact;
        var modeButtonId = PfUiText.ViewModeButton(lang, isCompact);
        var modeVisibleLabel = modeButtonId[..modeButtonId.IndexOf("##", StringComparison.Ordinal)];
        var dcPreview = PfUiText.DcComboPreview(lang, _filter.SelectedDataCenter);

        var dcW = MathF.Max(108f, ImGui.CalcTextSize((ImU8String)PfUiText.DcComboPreview(lang, string.Empty)).X + 34f);
        var sortW = MathF.Max(135f, MaxTextWidth(sortLabels) + 34f);
        var blockedW = MathF.Max(118f, MaxTextWidth(blockedLabels) + 34f);
        var modeW = MathF.Max(78f, ImGui.CalcTextSize((ImU8String)modeVisibleLabel).X + 18f);
        var rightControlsW = dcW + sortW + blockedW + modeW + (ImGui.GetStyle().ItemSpacing.X * 4f);

        var searchW = MathF.Max(120f, ImGui.GetContentRegionAvail().X - rightControlsW);
        ImGui.SetNextItemWidth(searchW);
        if (ImGui.InputTextWithHint((ImU8String)"##PfSearchInput", (ImU8String)PfUiText.SearchInputHint(lang), ref _searchBuffer, 128)) {
            _filter.SearchText = _searchBuffer;
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.SearchInputTooltip(lang));
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(dcW);
        if (ImGui.BeginCombo((ImU8String)"##PfDcCombo", (ImU8String)dcPreview)) {
            try {
                var isAllSelected = string.IsNullOrEmpty(_filter.SelectedDataCenter);
                if (ImGui.Selectable((ImU8String)PfUiText.DcComboAllOption(lang), isAllSelected)) {
                    _filter.SelectedDataCenter = string.Empty;
                }

                foreach (var dc in availableDataCenters) {
                    var selected = string.Equals(_filter.SelectedDataCenter, dc, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable((ImU8String)dc, selected)) {
                        _filter.SelectedDataCenter = dc;
                    }
                }
            } finally {
                ImGui.EndCombo();
            }
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.DcComboTooltip(lang));
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(sortW);
        var sortIdx = (int)_filter.SortMode;
        if (ImGui.Combo((ImU8String)"##PfSortCombo", ref sortIdx, sortLabels, sortLabels.Length)) {
            _filter.SortMode = (PfSortMode)sortIdx;
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.SortTooltip(lang));
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(blockedW);
        var blockedIdx = (int)_filter.BlockedDisplayMode;
        if (ImGui.Combo((ImU8String)"##PfBlockedCombo", ref blockedIdx, blockedLabels, blockedLabels.Length)) {
            _filter.BlockedDisplayMode = (PfBlockedDisplayMode)blockedIdx;
            _setBlockedDisplayMode?.Invoke(_filter.BlockedDisplayMode);
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.BlockedDisplayTooltip(lang));
        }

        ImGui.SameLine();
        if (ImGui.Button(modeButtonId, new Vector2(modeW, 0f))) {
            _filter.ViewMode = isCompact ? PfListViewMode.Standard : PfListViewMode.Compact;
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.ViewModeTooltip(lang));
        }
    }

    private static float MaxTextWidth(IReadOnlyList<string> labels) {
        var max = 0f;
        for (var i = 0; i < labels.Count; i++) {
            var w = ImGui.CalcTextSize((ImU8String)labels[i]).X;
            if (w > max) {
                max = w;
            }
        }
        return max;
    }

    private void DrawListingRows(IReadOnlyList<PfListingEntry> filtered, PfNativeUiSnapshot nativeUi, uint? playerClassJobId) {
        var lang = _icons.UiLanguage;
        if (filtered.Count == 0) {
            ImGui.Spacing();
            if (_nativeBridge.IsPendingHiddenSetup) {
                UiTheme.DrawColoredText(UiTheme.Muted, PfUiText.InitializingMessage(lang));
            } else if (!nativeUi.IsNativeAgentInitialized) {
                UiTheme.DrawColoredText(UiTheme.Muted, PfUiText.NotInitializedMessage(lang));
                ImGui.Spacing();
                if (ImGui.Button(PfUiText.OpenNativePfToInitButton(lang))) {
                    _nativeBridge.QueueOpenNativePfWindow();
                }
            } else {
                UiTheme.DrawColoredText(UiTheme.Muted, PfUiText.NoMatchingListings(lang));
            }
            return;
        }

        var rowHeight = _filter.ViewMode == PfListViewMode.Standard ? 52f : 28f;
        var availWidth = ImGui.GetContentRegionAvail().X;
        var isWindowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.None);

        using (UiTheme.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero)) {
            unsafe {
                ImGuiListClipper clipper = new();
                var ptr = new ImGuiListClipperPtr(&clipper);
                ptr.Begin(filtered.Count, rowHeight);
                try {
                    while (ptr.Step()) {
                        for (var i = ptr.DisplayStart; i < ptr.DisplayEnd; i++) {
                            DrawSingleListingRow(filtered[i], availWidth, rowHeight, isWindowHovered, playerClassJobId);
                        }
                    }
                } finally {
                    ptr.End();
                }
            }
        }
    }

    private void DrawSingleListingRow(
        PfListingEntry entry,
        float rowWidth,
        float rowHeight,
        bool isListWindowHovered,
        uint? playerClassJobId
    ) {
        using var rowId = ImRaii.PushId($"row_{entry.Id}");
        var rowMin = ImGui.GetCursorScreenPos();
        var rowMax = new Vector2(rowMin.X + rowWidth, rowMin.Y + rowHeight);
        var drawList = ImGui.GetWindowDrawList();

        var isSelected = SelectedListingId == entry.Id;
        var isHovered = isListWindowHovered && ImGui.IsMouseHoveringRect(rowMin, rowMax, true);

        if (isSelected) {
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(UiTheme.RowSelectedBg));
        } else if (isHovered) {
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(UiTheme.RowHoverBg));
        } else if (entry.IsLeaderBlocked) {
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(UiTheme.BlockedRowBg));
        } else if (entry.IsDutyUnopened) {
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(UiTheme.UnopenedRowBg));
        } else if (entry.IsSample) {
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(UiTheme.CardBodyBg));
        }

        if (entry.IsLeaderBlocked) {
            drawList.AddRectFilled(
                rowMin,
                new Vector2(rowMin.X + 3f, rowMax.Y - 1f),
                ImGui.GetColorU32(UiTheme.BlockedAccentBar));
        }

        drawList.AddLine(
            new Vector2(rowMin.X, rowMax.Y - 1f),
            new Vector2(rowMax.X, rowMax.Y - 1f),
            ImGui.GetColorU32(UiTheme.CardDivider));

        if (isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            SelectedListingId = entry.Id;
            ListingActivated?.Invoke(entry.Id, ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left));
        }

        if (_filter.ViewMode == PfListViewMode.Standard) {
            DrawStandardRowContents(entry, rowMin, rowWidth, rowHeight, drawList, isListWindowHovered, playerClassJobId);
        } else {
            DrawCompactRowContents(entry, rowMin, rowWidth, rowHeight, drawList, isListWindowHovered, playerClassJobId);
        }

        ImGui.SetCursorScreenPos(rowMin);
        ImGui.Dummy(new Vector2(rowWidth, rowHeight));
        DrawListingContextMenu(entry);
    }

    private void DrawListingContextMenu(PfListingEntry entry) {
        if (!ImGui.BeginPopupContextItem($"pf_row_ctx_{entry.Id}", ImGuiPopupFlags.MouseButtonRight)) {
            return;
        }

        var lang = _icons.UiLanguage;
        try {
            var leaderDisplay = string.IsNullOrWhiteSpace(entry.LeaderName)
                ? PfUiText.UnknownLeader(lang)
                : (string.IsNullOrWhiteSpace(entry.HomeWorldName)
                    ? entry.LeaderName
                    : $"{entry.LeaderName}@{entry.HomeWorldName}");
            UiTheme.DrawColoredText(UiTheme.Muted, leaderDisplay);
            ImGui.Separator();

            var hasFflogsUrl = PfExternalLinks.TryBuildFFLogsCharacterUrl(
                entry.LeaderName,
                entry.HomeWorldName,
                entry.HomeWorldId,
                entry.DataCenterName,
                out var fflogsUrl);
            using (ImRaii.Disabled(!hasFflogsUrl)) {
                if (ImGui.MenuItem(PfUiText.ContextOpenLeaderFflogs(lang)) && hasFflogsUrl) {
                    UiTheme.OpenUrl(fflogsUrl);
                }
            }

            var hasTomestoneUrl = PfExternalLinks.TryBuildTomestoneCharacterUrl(
                entry.LeaderName,
                entry.HomeWorldName,
                out var tomestoneUrl);
            using (ImRaii.Disabled(!hasTomestoneUrl)) {
                if (ImGui.MenuItem(PfUiText.ContextOpenLeaderTomestone(lang)) && hasTomestoneUrl) {
                    UiTheme.OpenUrl(tomestoneUrl);
                }
            }

            ImGui.Separator();
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(entry.LeaderName))) {
                if (ImGui.MenuItem(PfUiText.ContextCopyLeader(lang))) {
                    UiTheme.CopyToClipboard(leaderDisplay);
                }
            }

            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(entry.Comment))) {
                if (ImGui.MenuItem(PfUiText.ContextCopyComment(lang))) {
                    UiTheme.CopyToClipboard(entry.Comment);
                }
            }

            using (ImRaii.Disabled(entry.IsSample)) {
                if (ImGui.MenuItem(PfUiText.ContextOpenNativeDetail(lang)) && !entry.IsSample) {
                    _nativeBridge.QueueOpenListing(entry.Id);
                }
            }
        } finally {
            ImGui.EndPopup();
        }
    }

    private void DrawStandardRowContents(
        PfListingEntry entry,
        Vector2 rowMin,
        float rowWidth,
        float rowHeight,
        ImDrawListPtr drawList,
        bool isListWindowHovered,
        uint? playerClassJobId
    ) {
        var lang = _icons.UiLanguage;
        var line1Y = rowMin.Y + 4f;
        var line2Y = rowMin.Y + 28f;
        var x = rowMin.X + 6f;

        // Status indicator (Private lock / Unopened duty / Duty Complete)
        DrawRowStatusMarker(entry, new Vector2(x, line1Y + 2f), drawList, isListWindowHovered, lang);
        x += 20f;

        // Category icon (24x24)
        var catIconId = PfIconCatalog.GetCategoryIconId(entry.Category);
        if (!_icons.TryDrawGameIconAt(drawList, catIconId, new Vector2(x, line1Y), new Vector2(x + 24f, line1Y + 24f))) {
            drawList.AddRect(new Vector2(x, line1Y + 2f), new Vector2(x + 20f, line1Y + 22f), ImGui.GetColorU32(UiTheme.Muted), 3f);
        }
        x += 30f;

        // Right-aligned fixed columns from row right edge
        var secondaryTextColor = entry.IsLeaderBlocked
            ? UiTheme.BlockedText
            : (entry.IsDutyUnopened ? UiTheme.UnopenedText : UiTheme.Muted);
        var rightEdge = rowMin.X + rowWidth - 8f;
        var timeText = PfUiText.FormatRemainingTime(entry.SecondsRemaining, lang);
        var timeW = ImGui.CalcTextSize((ImU8String)timeText).X;
        drawList.AddText(new Vector2(rightEdge - timeW, line1Y + 3f), ImGui.GetColorU32(secondaryTextColor), (ImU8String)timeText);

        var leaderAreaWidth = 195f;
        var leaderX = MathF.Max(x + 220f, rightEdge - TimeColumnWidth - leaderAreaWidth - 10f);
        var leaderDisplay = string.IsNullOrWhiteSpace(entry.HomeWorldName)
            ? entry.LeaderName
            : $"{entry.LeaderName}@{entry.HomeWorldName}";
        var leaderClipped = ClipTextToWidth(leaderDisplay, leaderAreaWidth);
        drawList.AddText(new Vector2(leaderX, line1Y + 3f), ImGui.GetColorU32(secondaryTextColor), (ImU8String)leaderClipped);

        var ilText = entry.MinItemLevel > 0 ? $"i{entry.MinItemLevel}" : "-";
        var ilW = 46f;
        var ilX = leaderX - ilW - 8f;
        var ilTextW = ImGui.CalcTextSize((ImU8String)ilText).X;
        drawList.AddText(new Vector2(ilX + ((ilW - ilTextW) * 0.5f), line1Y + 3f), ImGui.GetColorU32(secondaryTextColor), (ImU8String)ilText);
        if (isListWindowHovered
            && ImGui.IsMouseHoveringRect(new Vector2(ilX, line1Y), new Vector2(ilX + ilW, line1Y + 22f))) {
            ImGui.SetTooltip(PfUiText.MinIlTooltip(lang, entry.MinItemLevel));
        }

        var slotBarX = ilX - PfSlotBarRenderer.StandardBarWidth - 10f;
        if (slotBarX > x + 90f) {
            ImGui.SetCursorScreenPos(new Vector2(slotBarX, line1Y + 2f));
            PfSlotBarRenderer.DrawMiniSlotBar($"bar_{entry.Id}", entry, _icons, playerClassJobId);

            // Duty name + objective pills clipped to available space before slotBarX
            var dutyMaxW = MathF.Max(60f, slotBarX - x - 8f);
            var dutyTitle = BuildListingTitleWithTags(entry, lang);
            var dutyClipped = ClipTextToWidth(dutyTitle, dutyMaxW);
            var titleColor = entry.IsLeaderBlocked
                ? UiTheme.BlockedText
                : (entry.IsDutyUnopened
                    ? UiTheme.UnopenedText
                    : (entry.IsHighEnd ? UiTheme.Accent : new Vector4(0.92f, 0.94f, 0.97f, 1.0f)));
            drawList.AddText(new Vector2(x, line1Y + 3f), ImGui.GetColorU32(titleColor), (ImU8String)dutyClipped);
        }

        // Line 2: Comment on the left, leader FFLogs/Tomestone badges aligned under leader on the right
        var commentMaxW = MathF.Max(100f, (leaderX - 10f) - x);
        var commentText = string.IsNullOrWhiteSpace(entry.Comment) ? PfUiText.NoComment(lang) : entry.Comment.Replace('\n', ' ');
        var commentClipped = ClipTextToWidth(commentText, commentMaxW);
        var commentColor = entry.IsLeaderBlocked
            ? UiTheme.BlockedText
            : (entry.IsDutyUnopened
                ? UiTheme.UnopenedText
                : (string.IsNullOrWhiteSpace(entry.Comment) ? UiTheme.Muted : new Vector4(0.82f, 0.84f, 0.88f, 1.0f)));
        drawList.AddText(new Vector2(x, line2Y), ImGui.GetColorU32(commentColor), (ImU8String)commentClipped);

        if (isListWindowHovered
            && ImGui.IsMouseHoveringRect(new Vector2(x, line2Y), new Vector2(MathF.Max(x + 12f, leaderX - 10f), rowMin.Y + rowHeight - 2f))
            && !string.IsNullOrWhiteSpace(entry.Comment)
            && !string.Equals(commentClipped, commentText, StringComparison.Ordinal)) {
            ImGui.SetTooltip(entry.Comment);
        }

        DrawLeaderLogBadges(entry, new Vector2(leaderX, line2Y - 1f), rightEdge - leaderX, drawList, isListWindowHovered, lang);
    }

    private void DrawCompactRowContents(
        PfListingEntry entry,
        Vector2 rowMin,
        float rowWidth,
        float rowHeight,
        ImDrawListPtr drawList,
        bool isListWindowHovered,
        uint? playerClassJobId
    ) {
        var lang = _icons.UiLanguage;
        var y = rowMin.Y + 4f;
        var x = rowMin.X + 6f;

        DrawRowStatusMarker(entry, new Vector2(x, y + 1f), drawList, isListWindowHovered, lang);
        x += 18f;

        var catIconId = PfIconCatalog.GetCategoryIconId(entry.Category);
        _icons.TryDrawGameIconAt(drawList, catIconId, new Vector2(x, y), new Vector2(x + 20f, y + 20f));
        x += 24f;

        var secondaryTextColor = entry.IsLeaderBlocked
            ? UiTheme.BlockedText
            : (entry.IsDutyUnopened ? UiTheme.UnopenedText : UiTheme.Muted);
        var rightEdge = rowMin.X + rowWidth - 8f;
        var timeText = PfUiText.FormatRemainingTime(entry.SecondsRemaining, lang);
        var timeW = ImGui.CalcTextSize((ImU8String)timeText).X;
        drawList.AddText(new Vector2(rightEdge - timeW, y + 2f), ImGui.GetColorU32(secondaryTextColor), (ImU8String)timeText);

        var leaderAreaW = 190f;
        var leaderX = MathF.Max(x + 300f, rightEdge - TimeColumnWidth - leaderAreaW - 8f);
        DrawCompactLeaderSummary(entry, new Vector2(leaderX, y + 1f), leaderAreaW, drawList, isListWindowHovered, lang);

        var ilText = entry.MinItemLevel > 0 ? $"i{entry.MinItemLevel}" : "-";
        var ilW = 46f;
        var ilX = leaderX - ilW - 8f;
        var ilTextW = ImGui.CalcTextSize((ImU8String)ilText).X;
        drawList.AddText(new Vector2(ilX + ((ilW - ilTextW) * 0.5f), y + 2f), ImGui.GetColorU32(secondaryTextColor), (ImU8String)ilText);
        if (isListWindowHovered
            && ImGui.IsMouseHoveringRect(new Vector2(ilX, y), new Vector2(ilX + ilW, y + 20f))) {
            ImGui.SetTooltip(PfUiText.MinIlTooltip(lang, entry.MinItemLevel));
        }

        var slotBarX = ilX - PfSlotBarRenderer.StandardBarWidth - 8f;
        var dutyMaxW = MathF.Max(60f, MathF.Min(210f, slotBarX - x - 6f));
        var dutyClipped = ClipTextToWidth(entry.DutyName, dutyMaxW);
        var dutyColor = entry.IsLeaderBlocked
            ? UiTheme.BlockedText
            : (entry.IsDutyUnopened
                ? UiTheme.UnopenedText
                : (entry.IsHighEnd ? UiTheme.Accent : new Vector4(0.92f, 0.94f, 0.97f, 1.0f)));
        drawList.AddText(new Vector2(x, y + 2f), ImGui.GetColorU32(dutyColor), (ImU8String)dutyClipped);

        var commentStartX = x + dutyMaxW + 8f;
        var commentMaxW = slotBarX - commentStartX - 8f;
        if (commentMaxW > 40f && !string.IsNullOrWhiteSpace(entry.Comment)) {
            var commentClipped = ClipTextToWidth(entry.Comment.Replace('\n', ' '), commentMaxW);
            drawList.AddText(new Vector2(commentStartX, y + 2f), ImGui.GetColorU32(secondaryTextColor), (ImU8String)commentClipped);
        }

        if (slotBarX > x + 70f) {
            ImGui.SetCursorScreenPos(new Vector2(slotBarX, y + 1f));
            PfSlotBarRenderer.DrawMiniSlotBar($"bar_cmp_{entry.Id}", entry, _icons, playerClassJobId);
        }
    }

    private void DrawRowStatusMarker(
        PfListingEntry entry,
        Vector2 pos,
        ImDrawListPtr drawList,
        bool allowHoverTooltip,
        PfUiLanguage lang
    ) {
        if ((entry.SearchArea & SearchAreaFlags.Private) == SearchAreaFlags.Private) {
            if (!_icons.TryDrawAtlasSpriteAt(drawList, PfIconCatalog.PartyFinderTexturePath, PfIconCatalog.UvPrivateLock, pos, new Vector2(pos.X + 14f, pos.Y + 16f))) {
                drawList.AddText(pos, ImGui.GetColorU32(UiTheme.Warning), (ImU8String)"P");
            }
            if (allowHoverTooltip && ImGui.IsMouseHoveringRect(pos, new Vector2(pos.X + 16f, pos.Y + 18f))) {
                ImGui.SetTooltip(PfUiText.PrivateStatusTooltip(lang));
            }
            return;
        }

        if (entry.IsDutyUnopened) {
            if (!_icons.TryDrawAtlasSpriteAt(drawList, PfIconCatalog.LfgTexturePath, PfIconCatalog.UvStatusCannotJoin, pos, new Vector2(pos.X + 16f, pos.Y + 16f))) {
                drawList.AddCircle(new Vector2(pos.X + 8f, pos.Y + 8f), 5f, ImGui.GetColorU32(UiTheme.UnopenedBadgeFg));
            }
            if (allowHoverTooltip && ImGui.IsMouseHoveringRect(pos, new Vector2(pos.X + 16f, pos.Y + 18f))) {
                ImGui.SetTooltip(PfUiText.UnopenedStatusTooltip(lang));
            }
            return;
        }

        if ((entry.Conditions & ConditionFlags.DutyComplete) == ConditionFlags.DutyComplete) {
            _icons.TryDrawAtlasSpriteAt(drawList, PfIconCatalog.LfgTexturePath, PfIconCatalog.UvStatusGoldCheck, pos, new Vector2(pos.X + 16f, pos.Y + 16f));
            if (allowHoverTooltip && ImGui.IsMouseHoveringRect(pos, new Vector2(pos.X + 16f, pos.Y + 18f))) {
                ImGui.SetTooltip(PfUiText.DutyCompleteStatusTooltip(lang));
            }
        }
    }

    private static float DrawLeaderLogBadges(
        PfListingEntry entry,
        Vector2 pos,
        float maxWidth,
        ImDrawListPtr drawList,
        bool allowHoverTooltip,
        PfUiLanguage lang
    ) {
        var x = pos.X;
        var maxX = pos.X + maxWidth;

        if (entry.IsLeaderBlocked) {
            var blockedLabel = PfUiText.BlockedBadgeLabel(lang);
            var blockedW = MeasureInlineBadgeWidth(blockedLabel);
            if (x + blockedW <= maxX) {
                x += DrawInlineBadge(drawList, new Vector2(x, pos.Y), blockedLabel, UiTheme.DangerHeaderBg, UiTheme.Danger) + 4f;
            }
        }

        if (entry.IsDutyUnopened) {
            var lockedLabel = PfUiText.UnopenedBadgeLabel(lang);
            var lockedW = MeasureInlineBadgeWidth(lockedLabel);
            if (x + lockedW <= maxX) {
                var renderedW = DrawInlineBadge(drawList, new Vector2(x, pos.Y), lockedLabel, UiTheme.UnopenedBadgeBg, UiTheme.UnopenedBadgeFg);
                if (allowHoverTooltip && ImGui.IsMouseHoveringRect(new Vector2(x, pos.Y), new Vector2(x + renderedW, pos.Y + ImGui.GetTextLineHeight() + 2f))) {
                    ImGui.SetTooltip(PfUiText.UnopenedBadgeTooltip(lang));
                }
                x += renderedW + 4f;
            }
        }

        if (entry.LeaderParse is { } parse) {
            if (parse.Hidden) {
                var hiddenText = PfUiText.HiddenBadge(lang);
                var hiddenW = MeasureInlineBadgeWidth(hiddenText);
                if (x + hiddenW <= maxX) {
                    x += DrawInlineBadge(drawList, new Vector2(x, pos.Y), hiddenText, UiTheme.PillBg, UiTheme.Muted) + 4f;
                }
            } else if (parse.HasVisiblePercentile && parse.PrimaryPercentile.HasValue) {
                var prefix = parse.Estimated ? "~" : string.Empty;
                var lockSuffix = parse.LockedIn ? string.Empty : "*";
                var text = parse.HasSecondary && parse.SecondaryPercentile.HasValue
                    ? $"{prefix}{parse.PrimaryPercentile.Value}/{parse.SecondaryPercentile.Value}{lockSuffix}"
                    : $"{prefix}{parse.PrimaryPercentile.Value}{lockSuffix}";
                var badgeW = MeasureInlineBadgeWidth(text);
                if (x + badgeW <= maxX) {
                    var border = parse.LockedIn
                        ? (Vector4?)null
                        : new Vector4(UiTheme.Warning.X, UiTheme.Warning.Y, UiTheme.Warning.Z, 0.70f);
                    var renderedW = DrawInlineBadge(
                        drawList,
                        new Vector2(x, pos.Y),
                        text,
                        UiTheme.PillBg,
                        UiTheme.GetParseColor(parse.PrimaryPercentile),
                        border);
                    if (allowHoverTooltip
                        && (!parse.LockedIn || parse.Estimated)
                        && ImGui.IsMouseHoveringRect(new Vector2(x, pos.Y), new Vector2(x + renderedW, pos.Y + ImGui.GetTextLineHeight() + 2f))) {
                        var tips = new List<string>(2);
                        if (!parse.LockedIn) {
                            tips.Add(PfUiText.ParseUnlockedTooltip(lang));
                        }
                        if (parse.Estimated) {
                            tips.Add(PfUiText.ParseEstimatedTooltip(lang));
                        }
                        ImGui.SetTooltip(string.Join("\n", tips));
                    }

                    x += renderedW + 4f;
                }
            }
        }

        var suppressClearTag = entry.LeaderParse is { HasVisiblePercentile: true };
        if (!suppressClearTag
            && entry.LeaderProgress is { } progress
            && !string.IsNullOrWhiteSpace(progress.SummaryLabel)
            && progress.SummaryLabel != "Hidden") {
            var progLabel = progress.SummaryLabel == "Cleared" ? PfUiText.ClearedBadge(lang) : progress.SummaryLabel;
            var progW = MeasureInlineBadgeWidth(progLabel);
            if (x + progW <= maxX) {
                var progColor = progress.Cleared ? UiTheme.Healthy : UiTheme.Info;
                x += DrawInlineBadge(drawList, new Vector2(x, pos.Y), progLabel, UiTheme.PillBg, progColor) + 5f;
            }
        }

        return x - pos.X;
    }

    private static void DrawCompactLeaderSummary(
        PfListingEntry entry,
        Vector2 pos,
        float maxWidth,
        ImDrawListPtr drawList,
        bool allowHoverTooltip,
        PfUiLanguage lang
    ) {
        var usedW = DrawLeaderLogBadges(entry, pos, maxWidth, drawList, allowHoverTooltip, lang);
        var x = pos.X + usedW;
        var remainingW = (pos.X + maxWidth) - x;
        if (remainingW > 24f) {
            var leaderDisplay = string.IsNullOrWhiteSpace(entry.HomeWorldName)
                ? entry.LeaderName
                : $"{entry.LeaderName}@{entry.HomeWorldName}";
            var clipped = ClipTextToWidth(leaderDisplay, remainingW);
            var leaderColor = entry.IsLeaderBlocked
                ? UiTheme.BlockedText
                : (entry.IsDutyUnopened ? UiTheme.UnopenedText : UiTheme.Muted);
            drawList.AddText(new Vector2(x, pos.Y + 1f), ImGui.GetColorU32(leaderColor), (ImU8String)clipped);
        }
    }

    private static float MeasureInlineBadgeWidth(string text) => ImGui.CalcTextSize((ImU8String)text).X + 8f;

    private static float DrawInlineBadge(
        ImDrawListPtr drawList,
        Vector2 pos,
        string text,
        Vector4 bg,
        Vector4 fg,
        Vector4? borderColor = null
    ) {
        var sz = ImGui.CalcTextSize((ImU8String)text);
        var w = sz.X + 8f;
        var h = sz.Y + 2f;
        var pMax = new Vector2(pos.X + w, pos.Y + h);
        drawList.AddRectFilled(pos, pMax, ImGui.GetColorU32(bg), 3f);
        if (borderColor.HasValue) {
            drawList.AddRect(pos, pMax, ImGui.GetColorU32(borderColor.Value), 3f, ImDrawFlags.None, 1f);
        }
        drawList.AddText(new Vector2(pos.X + 4f, pos.Y + 1f), ImGui.GetColorU32(fg), (ImU8String)text);
        return w;
    }

    private void DrawFooterBar(int shownCount, int pageLiveCount, PfNativeUiSnapshot nativeUi, bool isPluginOperational) {
        var lang = _icons.UiLanguage;
        var recruitText = PfUiText.RecruitButton(lang);
        var recruitW = MathF.Max(84f, ImGui.CalcTextSize((ImU8String)recruitText).X + 18f);
        var hideNativeText = PfUiText.HideNativePfButton(lang);
        var showNativeText = PfUiText.ShowNativePfButton(lang);
        var nativeToggleW = MathF.Max(
            114f,
            MathF.Max(ImGui.CalcTextSize((ImU8String)hideNativeText).X, ImGui.CalcTextSize((ImU8String)showNativeText).X) + 18f);
        var refreshLabel = PfUiText.RefreshButtonText(lang);

        using (UiTheme.PushColor(ImGuiCol.Button, UiTheme.SidebarSelectedBg))
        using (UiTheme.PushColor(ImGuiCol.ButtonHovered, UiTheme.RowHoverBg)) {
            if (ImGui.Button($"{recruitText}##PfRecruit", new Vector2(recruitW, 0f))) {
                _nativeBridge.QueueOpenRecruitCondition();
            }
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.RecruitTooltip(lang));
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        UiTheme.DrawColoredText(UiTheme.Muted, PfUiText.FooterShowing(lang, shownCount, pageLiveCount));

        if (nativeUi.HasServerData || nativeUi.IsNativeAgentInitialized) {
            ImGui.SameLine();
            using (ImRaii.Disabled(!nativeUi.HasPreviousPage)) {
                if (ImGui.Button("<##PfPrevPage", new Vector2(24f, 0f))) {
                    _scrollToTopRequested = true;
                    _nativeBridge.QueuePreviousPage();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                ImGui.SetTooltip(PfUiText.PreviousPageTooltip(lang));
            }

            ImGui.SameLine(0f, 6f);
            UiTheme.DrawColoredText(UiTheme.Info, FormatFooterPageLabel(nativeUi, pageLiveCount, lang));

            ImGui.SameLine(0f, 6f);
            using (ImRaii.Disabled(!nativeUi.HasNextPage)) {
                if (ImGui.Button(">##PfNextPage", new Vector2(24f, 0f))) {
                    _scrollToTopRequested = true;
                    _nativeBridge.QueueNextPage();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                ImGui.SetTooltip(PfUiText.NextPageTooltip(lang));
            }
        }

        ImGui.SameLine();
        UiTheme.DrawStatusBadge(
            isPluginOperational ? UiTheme.Healthy : UiTheme.Warning,
            isPluginOperational ? PfUiText.StatusActive(lang) : PfUiText.StatusPreview(lang),
            alignToFramePadding: true);

        var refreshW = ImGui.CalcTextSize((ImU8String)refreshLabel).X
                       + ImGui.GetFrameHeight()
                       + (ImGui.GetStyle().FramePadding.X * 2f);
        var totalRightW = refreshW + nativeToggleW + 10f;

        if (!string.IsNullOrWhiteSpace(_nativeBridge.LastStatusMessage)) {
            ImGui.SameLine();
            var statusAvail = MathF.Max(0f, ImGui.GetWindowContentRegionMax().X - ImGui.GetCursorPosX() - totalRightW - 12f);
            if (statusAvail > 50f) {
                var clippedStatus = ClipTextToWidth(_nativeBridge.LastStatusMessage, statusAvail);
                UiTheme.DrawColoredText(UiTheme.Muted, clippedStatus);
            }
        }

        var rightStart = MathF.Max(ImGui.GetCursorPosX() + 6f, ImGui.GetWindowContentRegionMax().X - totalRightW);
        ImGui.SameLine(rightStart);

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Sync, $"{refreshLabel}##PfRefresh")) {
            _nativeBridge.QueueRequestListingsUpdate();
        }
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(PfUiText.RefreshTooltip(lang));
        }

        ImGui.SameLine();
        if (nativeUi.IsNativeAddonVisible) {
            if (ImGui.Button($"{hideNativeText}##PfToggleNative", new Vector2(nativeToggleW, 0f))) {
                _nativeBridge.QueueHideNativePfWindow();
            }
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip(PfUiText.HideNativePfTooltip(lang));
            }
        } else {
            if (ImGui.Button($"{showNativeText}##PfToggleNative", new Vector2(nativeToggleW, 0f))) {
                _nativeBridge.QueueOpenNativePfWindow();
            }
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip(PfUiText.ShowNativePfTooltip(lang));
            }
        }
    }

    private string FormatFooterPageLabel(PfNativeUiSnapshot nativeUi, int pageLiveCount, PfUiLanguage lang) {
        if (!string.IsNullOrWhiteSpace(nativeUi.CurrentPageText) && !string.IsNullOrWhiteSpace(nativeUi.ResultsCountText)) {
            var parenIdx = nativeUi.ResultsCountText.IndexOf('(');
            return parenIdx >= 0
                ? $"{nativeUi.CurrentPageText} {nativeUi.ResultsCountText[parenIdx..]}"
                : $"{nativeUi.CurrentPageText} ({nativeUi.ResultsCountText})";
        }

        if (!string.IsNullOrWhiteSpace(nativeUi.CurrentPageText)) {
            return nativeUi.CurrentPageText;
        }

        if (!string.IsNullOrWhiteSpace(nativeUi.ResultsCountText)) {
            return nativeUi.ResultsCountText;
        }

        var total = GetActiveServerTotal(nativeUi);
        if (pageLiveCount > 0) {
            var start = (nativeUi.CurrentPageIndex * 50) + 1;
            var end = start + pageLiveCount - 1;
            return total >= 0 ? $"{start}-{end} ({total})" : $"{start}-{end}";
        }

        return total >= 0 ? $"0 ({total})" : PfUiText.PagePrefix(lang, nativeUi.CurrentPageIndex + 1);
    }

    private int GetActiveServerTotal(PfNativeUiSnapshot nativeUi) {
        if (nativeUi.CategoryTab < nativeUi.CategoryCounts.Count) {
            return nativeUi.CategoryCounts[nativeUi.CategoryTab];
        }

        var areaIdx = (int)_filter.AreaTab;
        if (areaIdx >= 0 && areaIdx < nativeUi.AreaCounts.Count) {
            return nativeUi.AreaCounts[areaIdx];
        }

        return -1;
    }

    private static string BuildListingTitleWithTags(PfListingEntry entry, PfUiLanguage lang) {
        var tags = new List<string>(3);
        if ((entry.Objective & ObjectiveFlags.DutyCompletion) == ObjectiveFlags.DutyCompletion) tags.Add(PfUiText.ObjectiveCompShort(lang));
        if ((entry.Objective & ObjectiveFlags.Practice) == ObjectiveFlags.Practice) tags.Add(PfUiText.ObjectivePracShort(lang));
        if ((entry.Objective & ObjectiveFlags.Loot) == ObjectiveFlags.Loot) tags.Add(PfUiText.ObjectiveLootShort(lang));
        if ((entry.Conditions & ConditionFlags.DutyComplete) == ConditionFlags.DutyComplete) tags.Add(PfUiText.ConditionClearedShort(lang));
        if ((entry.Conditions & ConditionFlags.DutyIncomplete) == ConditionFlags.DutyIncomplete) tags.Add(PfUiText.ConditionUnclearShort(lang));

        var prefix = entry.IsSample ? PfUiText.SampleTag(lang) : string.Empty;
        return tags.Count == 0
            ? $"{prefix}{entry.DutyName}"
            : $"{prefix}{entry.DutyName} [{string.Join("/", tags)}]";
    }

    private static string ClipTextToWidth(string text, float maxWidth) {
        if (string.IsNullOrEmpty(text) || maxWidth <= 12f) {
            return string.Empty;
        }

        if (ImGui.CalcTextSize((ImU8String)text).X <= maxWidth) {
            return text;
        }

        const string ellipsis = "...";
        var lo = 0;
        var hi = text.Length;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize((ImU8String)(text[..mid] + ellipsis)).X <= maxWidth) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        return lo <= 0 ? ellipsis : text[..lo] + ellipsis;
    }
}
