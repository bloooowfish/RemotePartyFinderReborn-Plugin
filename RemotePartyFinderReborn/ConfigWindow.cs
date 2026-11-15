#nullable enable

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace RemotePartyFinderReborn;

public class ConfigWindow : Window, IDisposable {
    private const float SidebarWidth = 175f;

    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private readonly UiNavigationState _navState = new();
    private readonly OnboardingGateRenderer _onboardingGateRenderer;
    private readonly StatusBannerRenderer _statusBannerRenderer;
    private readonly EndpointsPageRenderer _endpointsPageRenderer;
    private readonly IntegrationsPageRenderer _integrationsPageRenderer;
    private readonly AdvancedPageRenderer _advancedPageRenderer;
    private readonly DebugPageRenderer _debugPageRenderer;

    public ConfigWindow(Plugin plugin) : base("Remote Party Finder Reborn") {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _configuration = plugin.Configuration;
        _onboardingGateRenderer = new OnboardingGateRenderer(plugin);
        _statusBannerRenderer = new StatusBannerRenderer(plugin);
        _endpointsPageRenderer = new EndpointsPageRenderer(plugin);
        _integrationsPageRenderer = new IntegrationsPageRenderer(plugin);
        _advancedPageRenderer = new AdvancedPageRenderer(plugin);
        _debugPageRenderer = new DebugPageRenderer(plugin, _navState);

        Size = new Vector2(760, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(640, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() {
    }

    public override void OnClose() {
        _configuration.Save();
    }

    public override void Draw() {
        if (!_plugin.IsPluginOperational) {
            var gateOpen = ImGui.BeginChild(
                (ImU8String)"OnboardingGateScroll",
                Vector2.Zero,
                false,
                ImGuiWindowFlags.None);
            try {
                if (gateOpen) {
                    _onboardingGateRenderer.Draw();
                }
            } finally {
                ImGui.EndChild();
            }

            return;
        }

        _statusBannerRenderer.Draw();
        ImGui.Separator();

        var sidebarOpen = ImGui.BeginChild(
            (ImU8String)"NavSidebar",
            new Vector2(SidebarWidth, 0f),
            true,
            ImGuiWindowFlags.None);
        try {
            if (sidebarOpen) {
                DrawSidebar();
            }
        } finally {
            ImGui.EndChild();
        }

        ImGui.SameLine();

        var contentOpen = ImGui.BeginChild(
            (ImU8String)"PageContent",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.None);
        try {
            if (contentOpen) {
                var activePage = _navState.GetEffectivePage(_configuration.AdvancedSettingsEnabled);
                switch (activePage) {
                    case ConfigNavPage.Endpoints:
                        _endpointsPageRenderer.Draw();
                        break;
                    case ConfigNavPage.Integrations:
                        _integrationsPageRenderer.Draw();
                        break;
                    case ConfigNavPage.Advanced:
                        _advancedPageRenderer.Draw();
                        break;
                    case ConfigNavPage.Debug:
                        _debugPageRenderer.Draw();
                        break;
                }
            }
        } finally {
            ImGui.EndChild();
        }
    }

    private void DrawSidebar() {
        var activePage = _navState.GetEffectivePage(_configuration.AdvancedSettingsEnabled);

        DrawNavItem("Endpoints & Sync", ConfigNavPage.Endpoints, activePage);
        DrawNavItem("Integrations", ConfigNavPage.Integrations, activePage);

        if (_configuration.AdvancedSettingsEnabled) {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            UiTheme.DrawColoredText(UiTheme.Muted, "Developer");
            DrawNavItem("Advanced", ConfigNavPage.Advanced, activePage);
            DrawNavItem("Debug", ConfigNavPage.Debug, activePage);
        }

        var style = ImGui.GetStyle();
        var footerHeight = ImGui.GetFrameHeight() + (style.ItemSpacing.Y * 2f) + 2f;
        var remainingY = ImGui.GetContentRegionAvail().Y;
        if (remainingY > footerHeight) {
            ImGui.Dummy(new Vector2(0f, remainingY - footerHeight));
        }

        ImGui.Separator();
        var advancedEnabled = _configuration.AdvancedSettingsEnabled;
        if (ImGui.Checkbox("Advanced & Debug", ref advancedEnabled)) {
            _configuration.AdvancedSettingsEnabled = advancedEnabled;
            _configuration.Save();
            _navState.GetEffectivePage(advancedEnabled);
        }
    }

    private void DrawNavItem(string label, ConfigNavPage page, ConfigNavPage activePage) {
        var isSelected = activePage == page;
        using var headerColor = UiTheme.PushColor(ImGuiCol.Header, UiTheme.SidebarSelectedBg, isSelected);
        using var alignStyle = UiTheme.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.04f, 0.5f));
        if (ImGui.Selectable(
                (ImU8String)label,
                isSelected,
                ImGuiSelectableFlags.None,
                new Vector2(0f, ImGui.GetFrameHeight() + 4f))) {
            _navState.SelectedPage = page;
        }
    }
}
