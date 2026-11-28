#nullable enable

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RemotePartyFinderReborn;

/// <summary>
/// Standalone ImGui Party Finder browser window (Phase 1 prototype mirroring LFG.uld dimensions).
/// </summary>
internal sealed class PfBrowserWindow : Window, IDisposable {
    private readonly Plugin _plugin;
    private readonly PfListPane _listPane;
    private readonly PfDetailWindow _detailWindow;

    internal PfBrowserWindow(
        Plugin plugin,
        PfListPane listPane,
        PfDetailWindow detailWindow
    ) : base(PfUiText.BrowserWindowTitle(plugin.PfIconCatalog.UiLanguage)) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _listPane = listPane ?? throw new ArgumentNullException(nameof(listPane));
        _detailWindow = detailWindow ?? throw new ArgumentNullException(nameof(detailWindow));

        Size = new Vector2(900f, 680f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(760f, 480f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        _listPane.ListingActivated += OnListingActivated;
    }

    public override void OnOpen() {
        _listPane.OnWindowOpened();
    }

    public override void OnClose() {
        _listPane.OnWindowClosed();
    }

    public override void Draw() {
        var playerClassJobId = _plugin.GetLocalPlayerClassJobId();
        var currentWorldId = (ushort?)_plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId;
        _listPane.Draw(playerClassJobId, _plugin.IsPluginOperational, currentWorldId);
    }

    public void Dispose() {
        _listPane.ListingActivated -= OnListingActivated;
    }

    private void OnListingActivated(ulong listingId, bool doubleClick) {
        _detailWindow.OpenListing(listingId, _listPane.Filter);
        var entry = _plugin.PfMirrorStore.TryGetListing(listingId, includeSamples: false, useSampleEnrichment: false);
        if (entry != null && !entry.IsSample) {
            _plugin.NativePfBridge.QueueInspectListingHeadless(listingId, entry.LeaderContentId);
        }
    }
}
