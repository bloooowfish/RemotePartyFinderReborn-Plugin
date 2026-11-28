#nullable enable

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RemotePartyFinderReborn;

/// <summary>
/// Standalone ImGui redesigned Party Finder detail window paired with PfBrowserWindow.
/// </summary>
internal sealed class PfDetailWindow : Window, IDisposable {
    private readonly Plugin _plugin;
    private readonly PfDetailPane _detailPane;
    private PfFilterState _activeFilter = new();

    internal PfDetailWindow(Plugin plugin, PfDetailPane detailPane)
        : base(PfUiText.DetailWindowTitle(plugin.PfIconCatalog.UiLanguage)) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _detailPane = detailPane ?? throw new ArgumentNullException(nameof(detailPane));

        Size = new Vector2(700f, 560f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(600f, 420f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    internal ulong SelectedListingId { get; private set; }

    internal void OpenListing(ulong listingId, PfFilterState filter) {
        SelectedListingId = listingId;
        _activeFilter = filter ?? _activeFilter;
        IsOpen = true;
        _detailPane.NotifyListingOpened(listingId, _activeFilter, _plugin.IsPluginOperational);
    }

    internal void NotifyLiveDetailArrived(UploadablePartyDetail detail) {
        if (IsOpen && SelectedListingId == detail.ListingId) {
            _detailPane.RequestPartyEnrichmentForListing(
                detail.ListingId,
                _activeFilter,
                _plugin.IsPluginOperational,
                forceRefresh: false);
        }
    }

    public override void OnClose() {
        _detailPane.NotifyWindowClosed();
    }

    public override void Draw() {
        var playerClassJobId = _plugin.GetLocalPlayerClassJobId();
        _detailPane.Draw(
            SelectedListingId,
            _activeFilter,
            playerClassJobId,
            onCloseRequested: () => IsOpen = false,
            isPluginOperational: _plugin.IsPluginOperational);
    }

    public void Dispose() {
    }
}
