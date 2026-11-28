#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RemotePartyFinderReborn;

internal enum NativePfActionKind {
    OpenListing,
    InspectListingHeadless,
    RequestListingsUpdate,
    RequestCategoryListings,
    OpenNativePfWindow,
    HideNativePfWindow,
    EnsureHiddenNativePfHost,
    ReleaseHiddenNativePfHost,
    SelectAreaTab,
    NextPage,
    PreviousPage,
    OpenRecruitCondition,
    OpenDetailedSearch,
}

internal readonly record struct NativePfAction(
    NativePfActionKind Kind,
    ulong ListingId = 0,
    byte Category = 0,
    byte AreaTab = 0,
    ulong LeaderContentId = 0);

internal interface INativePfHeadlessInspectDriver {
    bool IsDetailAddonOpen { get; }
    bool IsScannerRunning { get; }
    bool IsScannerCaptureReady { get; }
    void ArmScannerRequest(Guid attemptId, ulong listingId, ulong leaderContentId, bool allowHeadless);
    void BeginScannerOpenAttempt(Guid attemptId);
    bool OpenListing(ulong listingId);
    void EndScannerOpenAttempt(Guid attemptId);
    void ClearScannerRequest(Guid attemptId);
}

/// <summary>
/// Marshals UI-triggered Party Finder actions safely onto Dalamud's main framework thread,
/// delegating refresh, category/area tabs, paging, detail inspection, and recruitment
/// to native FFXIV AgentLookingForGroup / AddonLookingForGroup.
/// </summary>
internal sealed unsafe class NativePfBridge : IDisposable {
    private static readonly TimeSpan PendingHiddenSetupTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RehostDelayAfterManualClose = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HeadlessInspectDebounceDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HeadlessInspectTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly Plugin? _plugin;
    private readonly PfLocalMirrorStore? _mirrorStore;
    private readonly ConcurrentQueue<NativePfAction> _queue = new();
    private readonly Action<NativePfAction> _executor;
    private readonly Action<string> _warningLog;
    private readonly Func<DateTime> _utcNow;
    private readonly INativePfHeadlessInspectDriver? _headlessDriver;
    private readonly object _headlessInspectGate = new();

    private int _refreshQueued;
    private int _disposed;
    private bool _hostingHiddenNativeAddon;
    private bool _pendingHiddenSetup;
    private bool _closeHiddenOnPostSetup;
    private bool _browserWindowOpen;
    private bool _closingOurHiddenHost;
    private DateTime _pendingHiddenSetupDeadlineUtc = DateTime.MinValue;
    private DateTime _pendingRehostAtUtc = DateTime.MinValue;
    private NativePfAction? _deferredActionAfterHiddenSetup;
    private ulong _pendingHeadlessListingId;
    private ulong _pendingHeadlessLeaderContentId;
    private DateTime _pendingHeadlessDueUtc = DateTime.MinValue;
    private Guid? _activeUiHeadlessAttemptId;
    private ulong _activeUiHeadlessListingId;
    private DateTime _activeUiHeadlessDeadlineUtc = DateTime.MinValue;

    internal string LastStatusMessage { get; private set; } = string.Empty;
    internal bool IsHostingHiddenNativeAddon => _hostingHiddenNativeAddon;
    internal bool IsPendingHiddenSetup => _pendingHiddenSetup;
    internal Guid? ActiveUiHeadlessAttemptIdForTesting {
        get {
            lock (_headlessInspectGate) {
                return _activeUiHeadlessAttemptId;
            }
        }
    }
    internal ulong PendingHeadlessListingIdForTesting {
        get {
            lock (_headlessInspectGate) {
                return _pendingHeadlessListingId;
            }
        }
    }
    private PfUiLanguage UiLanguage => _plugin?.PfIconCatalog?.UiLanguage ?? PfUiLanguage.English;

    internal NativePfBridge(Plugin plugin, PfLocalMirrorStore mirrorStore) {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _mirrorStore = mirrorStore ?? throw new ArgumentNullException(nameof(mirrorStore));
        _executor = ExecuteOnGameMainThread;
        _warningLog = static msg => Plugin.Log?.Warning(msg);
        _utcNow = static () => DateTime.UtcNow;
        _headlessDriver = new PluginHeadlessInspectDriver(_plugin);
        _plugin.Framework.Update += OnFrameworkUpdate;
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "LookingForGroup", OnLfgPreSetup);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroup", OnLfgPostSetup);
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnLfgPreFinalize);
    }

    internal NativePfBridge(
        Action<NativePfAction> executor,
        Action<string>? warningLog = null,
        PfLocalMirrorStore? mirrorStore = null,
        Func<DateTime>? utcNow = null,
        INativePfHeadlessInspectDriver? headlessDriver = null
    ) {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _warningLog = warningLog ?? (static _ => { });
        _mirrorStore = mirrorStore;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _headlessDriver = headlessDriver;
    }

    internal int QueuedCount => _queue.Count;

    internal void QueueOpenListing(ulong listingId) {
        if (listingId == 0 || Volatile.Read(ref _disposed) != 0) {
            return;
        }

        lock (_headlessInspectGate) {
            _pendingHeadlessListingId = 0;
            _pendingHeadlessLeaderContentId = 0;
            _pendingHeadlessDueUtc = DateTime.MinValue;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.OpenListing, ListingId: listingId));
    }

    internal void QueueInspectListingHeadless(ulong listingId, ulong leaderContentId = 0) {
        if (listingId == 0 || Volatile.Read(ref _disposed) != 0) {
            return;
        }

        lock (_headlessInspectGate) {
            _pendingHeadlessListingId = listingId;
            _pendingHeadlessLeaderContentId = leaderContentId;
            _pendingHeadlessDueUtc = _utcNow() + HeadlessInspectDebounceDelay;
        }
    }

    internal void NotifyDetailCaptured(ulong listingId) {
        if (listingId == 0) {
            return;
        }

        Guid? attemptToClear = null;
        lock (_headlessInspectGate) {
            if (_activeUiHeadlessAttemptId is { } activeId && _activeUiHeadlessListingId == listingId) {
                attemptToClear = activeId;
                _activeUiHeadlessAttemptId = null;
                _activeUiHeadlessListingId = 0;
                _activeUiHeadlessDeadlineUtc = DateTime.MinValue;
            }
        }

        if (attemptToClear is { } attemptId) {
            _headlessDriver?.ClearScannerRequest(attemptId);
        }
    }

    internal void ClearUiHeadlessInspect() {
        Guid? attemptToClear = null;
        lock (_headlessInspectGate) {
            _pendingHeadlessListingId = 0;
            _pendingHeadlessLeaderContentId = 0;
            _pendingHeadlessDueUtc = DateTime.MinValue;

            if (_activeUiHeadlessAttemptId is { } activeId) {
                attemptToClear = activeId;
                _activeUiHeadlessAttemptId = null;
                _activeUiHeadlessListingId = 0;
                _activeUiHeadlessDeadlineUtc = DateTime.MinValue;
            }
        }

        if (attemptToClear is { } attemptId) {
            _headlessDriver?.ClearScannerRequest(attemptId);
        }
    }

    internal void ProcessPendingHeadlessInspectForTesting() {
        ProcessHeadlessInspectLifecycle(_utcNow());
    }

    internal bool QueueRequestListingsUpdate() {
        if (Volatile.Read(ref _disposed) != 0) {
            return false;
        }

        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0) {
            return false;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.RequestListingsUpdate));
        return true;
    }

    internal void QueueRequestCategoryListings(byte category) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.RequestCategoryListings, Category: category));
    }

    internal void QueueSelectAreaTab(byte areaTab) {
        if (areaTab > 2 || Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.SelectAreaTab, AreaTab: areaTab));
    }

    internal void QueueNextPage() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.NextPage));
    }

    internal void QueuePreviousPage() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.PreviousPage));
    }

    internal void QueueOpenNativePfWindow() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.OpenNativePfWindow));
    }

    internal void QueueHideNativePfWindow() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.HideNativePfWindow));
    }

    internal void QueueEnsureHiddenNativePfHost() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.EnsureHiddenNativePfHost));
    }

    internal void QueueReleaseHiddenNativePfHost() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.ReleaseHiddenNativePfHost));
    }

    internal void QueueOpenRecruitCondition() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.OpenRecruitCondition));
    }

    internal void QueueOpenDetailedSearch() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        _queue.Enqueue(new NativePfAction(NativePfActionKind.OpenDetailedSearch));
    }

    internal int DrainQueue() {
        var executed = 0;
        while (_queue.TryDequeue(out var action)) {
            if (action.Kind == NativePfActionKind.RequestListingsUpdate) {
                Interlocked.Exchange(ref _refreshQueued, 0);
            }

            try {
                if (_plugin == null) {
                    if (action.Kind == NativePfActionKind.OpenListing) {
                        ClearUiHeadlessInspect();
                    } else if (action.Kind == NativePfActionKind.InspectListingHeadless && _headlessDriver != null) {
                        ExecuteInspectListingHeadless(action);
                    }
                }

                _executor(action);
                executed++;
            } catch (Exception ex) {
                LastStatusMessage = PfUiText.BridgeActionFailed(UiLanguage, ex.Message);
                _warningLog($"NativePfBridge: action {action.Kind} failed: {ex.Message}");
            }
        }

        return executed;
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        ClearUiHeadlessInspect();

        try {
            ReleaseHiddenNativePfHostCore();
        } catch (Exception ex) {
            _warningLog($"NativePfBridge: failed releasing hidden LookingForGroup host on dispose: {ex.Message}");
        }

        if (_plugin is not null) {
            _plugin.Framework.Update -= OnFrameworkUpdate;
            _plugin.AddonLifecycle.UnregisterListener(OnLfgPreSetup);
            _plugin.AddonLifecycle.UnregisterListener(OnLfgPostSetup);
            _plugin.AddonLifecycle.UnregisterListener(OnLfgPreFinalize);
        }
    }

    private void OnFrameworkUpdate(IFramework framework) {
        var nowUtc = _utcNow();
        if ((_pendingHiddenSetup || _closeHiddenOnPostSetup) && nowUtc > _pendingHiddenSetupDeadlineUtc) {
            _pendingHiddenSetup = false;
            _closeHiddenOnPostSetup = false;
            _deferredActionAfterHiddenSetup = null;
            if (!TryGetOpenLfgAddon(out _)) {
                _hostingHiddenNativeAddon = false;
            }
        }

        if (_hostingHiddenNativeAddon
            && !_pendingHiddenSetup
            && TryGetOpenLfgAddon(out var lfg)) {
            var unit = (AtkUnitBase*)lfg;
            if (unit->IsVisible) {
                RestoreNativeAddonFlags(unit);
                _hostingHiddenNativeAddon = false;
            }
        }

        if (_browserWindowOpen
            && _pendingRehostAtUtc != DateTime.MinValue
            && nowUtc >= _pendingRehostAtUtc) {
            _pendingRehostAtUtc = DateTime.MinValue;
            if (_plugin != null
                && _plugin.ObjectTable.LocalPlayer != null
                && !TryGetOpenLfgAddon(out _)
                && AgentLookingForGroup.Instance() != null) {
                QueueEnsureHiddenNativePfHost();
            }
        }

        ProcessHeadlessInspectLifecycle(nowUtc);
        DrainQueue();
    }

    private void ExecuteOnGameMainThread(NativePfAction action) {
        var lang = UiLanguage;
        var agent = AgentLookingForGroup.Instance();
        if (agent == null) {
            LastStatusMessage = PfUiText.BridgeAgentUnavailable(lang);
            return;
        }

        switch (action.Kind) {
            case NativePfActionKind.OpenListing:
                ClearUiHeadlessInspect();
                LastStatusMessage = agent->OpenListing(action.ListingId)
                    ? PfUiText.BridgeOpenedDetail(lang, action.ListingId)
                    : PfUiText.BridgeOpenDetailFailed(lang, action.ListingId);
                break;

            case NativePfActionKind.InspectListingHeadless:
                ExecuteInspectListingHeadless(action);
                break;

            case NativePfActionKind.RequestListingsUpdate:
                if (!TryGetOpenLfgAddon(out _) || agent->InfoProxyCrossRealm == null) {
                    StartHiddenHost(agent);
                    LastStatusMessage = PfUiText.BridgeReinitializing(lang);
                    break;
                }

                _mirrorStore?.MarkPendingPageRefresh();
                LastStatusMessage = agent->RequestListingsUpdate()
                    ? PfUiText.BridgeRefreshed(lang)
                    : PfUiText.BridgeRefreshBusy(lang);
                break;

            case NativePfActionKind.RequestCategoryListings:
                if (action.Category >= 17) {
                    LastStatusMessage = $"Unsupported PF category index {action.Category}.";
                    break;
                }

                if (!TryGetOpenLfgAddon(out var categoryAddon) || agent->InfoProxyCrossRealm == null) {
                    StartHiddenHost(agent, action);
                    LastStatusMessage = PfUiText.BridgeReinitializing(lang);
                    break;
                }

                _mirrorStore?.MarkPendingPageRefresh();
                if (!FireAddonCallback((AtkUnitBase*)categoryAddon, 21, action.Category)) {
                    DispatchAgentEvent(agent, 21, action.Category);
                }

                LastStatusMessage = PfUiText.BridgeRequestedCategory(lang, action.Category);
                break;

            case NativePfActionKind.SelectAreaTab:
                if (action.AreaTab > 2) {
                    LastStatusMessage = $"Unsupported PF area tab {action.AreaTab}.";
                    break;
                }

                if (!TryGetOpenLfgAddon(out var areaAddon) || agent->InfoProxyCrossRealm == null) {
                    StartHiddenHost(agent, action);
                    LastStatusMessage = PfUiText.BridgeReinitializing(lang);
                    break;
                }

                _mirrorStore?.MarkPendingPageRefresh();
                if (!FireAddonCallback((AtkUnitBase*)areaAddon, 20, action.AreaTab)) {
                    DispatchAgentEvent(agent, 20, action.AreaTab);
                }

                LastStatusMessage = PfUiText.BridgeSwitchedArea(lang, action.AreaTab);
                break;

            case NativePfActionKind.NextPage: {
                if (!TryGetOpenLfgAddon(out var nextAddon) || agent->InfoProxyCrossRealm == null) {
                    StartHiddenHost(agent, action);
                    LastStatusMessage = PfUiText.BridgeReinitializing(lang);
                    break;
                }

                var stage = AtkStage.Instance();
                var numArr = stage != null ? stage->GetNumberArrayData(NumberArrayType.LookingForGroup) : null;
                var hasValidNumArr = numArr != null && numArr->IntArray != null && numArr->Size > 428;
                var hasNext = hasValidNumArr
                    ? numArr->IntArray[426] > 0
                    : (((AtkUnitBase*)nextAddon)->IsVisible
                       && nextAddon->NextPageButton != null
                       && nextAddon->NextPageButton->IsEnabled);

                if (!hasNext) {
                    LastStatusMessage = PfUiText.BridgeNoNextPage(lang);
                    break;
                }

                _mirrorStore?.MarkPendingPageRefresh();
                if (!FireAddonCallback((AtkUnitBase*)nextAddon, 22)) {
                    DispatchAgentEvent(agent, 22);
                }

                LastStatusMessage = PfUiText.BridgeRequestedNextPage(lang);
                break;
            }

            case NativePfActionKind.PreviousPage: {
                if (!TryGetOpenLfgAddon(out var prevAddon) || agent->InfoProxyCrossRealm == null) {
                    StartHiddenHost(agent, action);
                    LastStatusMessage = PfUiText.BridgeReinitializing(lang);
                    break;
                }

                var stage = AtkStage.Instance();
                var numArr = stage != null ? stage->GetNumberArrayData(NumberArrayType.LookingForGroup) : null;
                var hasValidNumArr = numArr != null && numArr->IntArray != null && numArr->Size > 428;
                var hasPrev = hasValidNumArr
                    ? numArr->IntArray[427] >= 0
                    : (((AtkUnitBase*)prevAddon)->IsVisible
                       && prevAddon->PreviousPageButton != null
                       && prevAddon->PreviousPageButton->IsEnabled);

                if (!hasPrev) {
                    LastStatusMessage = PfUiText.BridgeNoPrevPage(lang);
                    break;
                }

                _mirrorStore?.MarkPendingPageRefresh();
                if (!FireAddonCallback((AtkUnitBase*)prevAddon, 23)) {
                    DispatchAgentEvent(agent, 23);
                }

                LastStatusMessage = PfUiText.BridgeRequestedPrevPage(lang);
                break;
            }

            case NativePfActionKind.OpenRecruitCondition:
                if (TryGetOpenLfgAddon(out var recruitAddon) && agent->InfoProxyCrossRealm != null) {
                    var ok = FireAddonCallback((AtkUnitBase*)recruitAddon, 14);
                    LastStatusMessage = ok
                        ? PfUiText.BridgeOpenedRecruit(lang)
                        : PfUiText.BridgeOpenRecruitFailed(lang);
                } else {
                    StartHiddenHost(agent, action);
                    LastStatusMessage = PfUiText.BridgeInitializingRecruit(lang);
                }
                break;

            case NativePfActionKind.OpenDetailedSearch:
                if (TryGetOpenLfgAddon(out var searchAddon) && agent->InfoProxyCrossRealm != null) {
                    var ok = FireAddonCallback((AtkUnitBase*)searchAddon, 15);
                    LastStatusMessage = ok
                        ? PfUiText.BridgeOpenedDetailedSearch(lang)
                        : PfUiText.BridgeOpenDetailedSearchFailed(lang);
                } else {
                    StartHiddenHost(agent, action);
                    LastStatusMessage = PfUiText.BridgeInitializingDetailedSearch(lang);
                }
                break;

            case NativePfActionKind.EnsureHiddenNativePfHost:
                _browserWindowOpen = true;
                _closeHiddenOnPostSetup = false;
                _pendingRehostAtUtc = DateTime.MinValue;
                if (TryGetOpenLfgAddon(out _)) {
                    if (agent->InfoProxyCrossRealm != null) {
                        _mirrorStore?.MarkPendingPageRefresh();
                        LastStatusMessage = agent->RequestListingsUpdate()
                            ? PfUiText.BridgeRefreshed(lang)
                            : PfUiText.BridgeOpenedAlready(lang);
                    }
                    _mirrorStore?.RequestNativeSnapshotRefresh();
                } else {
                    StartHiddenHost(agent);
                    LastStatusMessage = PfUiText.BridgeInitializing(lang);
                }
                break;

            case NativePfActionKind.ReleaseHiddenNativePfHost:
                ReleaseHiddenNativePfHostCore();
                break;

            case NativePfActionKind.OpenNativePfWindow:
                _hostingHiddenNativeAddon = false;
                _pendingHiddenSetup = false;
                _closeHiddenOnPostSetup = false;
                _pendingRehostAtUtc = DateTime.MinValue;
                _deferredActionAfterHiddenSetup = null;
                if (TryGetOpenLfgAddon(out var openAddon)) {
                    var unit = (AtkUnitBase*)openAddon;
                    RestoreNativeAddonFlags(unit);
                    if (!unit->IsVisible) {
                        unit->Show(false, 0);
                    }
                } else {
                    agent->Show();
                }

                _mirrorStore?.RequestNativeSnapshotRefresh();
                LastStatusMessage = PfUiText.BridgeShowingNative(lang);
                break;

            case NativePfActionKind.HideNativePfWindow:
                _pendingHiddenSetup = false;
                _closeHiddenOnPostSetup = false;
                _pendingRehostAtUtc = DateTime.MinValue;
                if (TryGetOpenLfgAddon(out var hideAddon)) {
                    var unit = (AtkUnitBase*)hideAddon;
                    ApplyHiddenHostFlags(unit);
                    if (unit->IsVisible) {
                        unit->Hide(true, false, 0);
                    }
                    _hostingHiddenNativeAddon = true;
                    LastStatusMessage = PfUiText.BridgeHiddenNative(lang);
                } else {
                    LastStatusMessage = PfUiText.BridgeAlreadyClosed(lang);
                }

                _mirrorStore?.RequestNativeSnapshotRefresh();
                break;
        }
    }

    private void StartHiddenHost(AgentLookingForGroup* agent, NativePfAction? deferredAction = null) {
        _hostingHiddenNativeAddon = true;
        _pendingHiddenSetup = true;
        _closeHiddenOnPostSetup = false;
        _pendingRehostAtUtc = DateTime.MinValue;
        _pendingHiddenSetupDeadlineUtc = DateTime.UtcNow + PendingHiddenSetupTimeout;
        _deferredActionAfterHiddenSetup = deferredAction;
        _mirrorStore?.MarkPendingPageRefresh();
        agent->Show();
    }

    private void OnLfgPreSetup(AddonEvent type, AddonArgs args) {
        if (Volatile.Read(ref _disposed) != 0 || args.Addon.Address == 0) {
            return;
        }

        if ((_pendingHiddenSetup || _closeHiddenOnPostSetup) && DateTime.UtcNow <= _pendingHiddenSetupDeadlineUtc) {
            ApplyHiddenHostFlags((AtkUnitBase*)args.Addon.Address);
        }
    }

    private void OnLfgPostSetup(AddonEvent type, AddonArgs args) {
        if (Volatile.Read(ref _disposed) != 0 || args.Addon.Address == 0) {
            return;
        }

        var unit = (AtkUnitBase*)args.Addon.Address;
        if (_closeHiddenOnPostSetup) {
            _closeHiddenOnPostSetup = false;
            _pendingHiddenSetup = false;
            _hostingHiddenNativeAddon = false;
            _closingOurHiddenHost = true;
            unit->DisableShowHideSoundEffects = true;
            unit->DisableHideTransition = true;
            unit->Close(true);
            return;
        }

        if (_hostingHiddenNativeAddon && _pendingHiddenSetup) {
            _pendingHiddenSetup = false;
            unit->Hide(true, false, 0);
            _mirrorStore?.MarkPendingPageRefresh();
            if (!unit->FireCallbackInt(16)) {
                var agent = AgentLookingForGroup.Instance();
                if (agent != null && agent->InfoProxyCrossRealm != null) {
                    agent->RequestListingsUpdate();
                }
            }

            _mirrorStore?.RequestNativeSnapshotRefresh();
            LastStatusMessage = PfUiText.BridgeLoadedData(UiLanguage);

            if (_deferredActionAfterHiddenSetup is { } deferred) {
                _deferredActionAfterHiddenSetup = null;
                _queue.Enqueue(deferred);
            }
        }
    }

    private void OnLfgPreFinalize(AddonEvent type, AddonArgs args) {
        var wasOurClose = _closingOurHiddenHost;
        _closingOurHiddenHost = false;
        _hostingHiddenNativeAddon = false;
        _pendingHiddenSetup = false;
        _closeHiddenOnPostSetup = false;
        _deferredActionAfterHiddenSetup = null;

        if (!wasOurClose && _browserWindowOpen && Volatile.Read(ref _disposed) == 0) {
            _pendingRehostAtUtc = DateTime.UtcNow + RehostDelayAfterManualClose;
        }
    }

    private void ReleaseHiddenNativePfHostCore() {
        ClearUiHeadlessInspect();
        _browserWindowOpen = false;
        _pendingRehostAtUtc = DateTime.MinValue;
        _deferredActionAfterHiddenSetup = null;

        if (!_hostingHiddenNativeAddon) {
            return;
        }

        if (TryGetOpenLfgAddon(out var hiddenAddon)) {
            var unit = (AtkUnitBase*)hiddenAddon;
            if (!unit->IsVisible) {
                _closingOurHiddenHost = true;
                unit->DisableShowHideSoundEffects = true;
                unit->DisableHideTransition = true;
                unit->Close(true);
            }

            _hostingHiddenNativeAddon = false;
            _pendingHiddenSetup = false;
            _closeHiddenOnPostSetup = false;
        } else if (_pendingHiddenSetup) {
            _hostingHiddenNativeAddon = false;
            _closeHiddenOnPostSetup = true;
        } else {
            _hostingHiddenNativeAddon = false;
        }
    }

    private static void ApplyHiddenHostFlags(AtkUnitBase* unit) {
        unit->DisableShowOnOpen = true;
        unit->DisableFocusOnShow = true;
        unit->DisableShowHideSoundEffects = true;
        unit->DisableUnfocusedCloseOnEsc = true;
        unit->DisableHideTransition = true;
    }

    private static void RestoreNativeAddonFlags(AtkUnitBase* unit) {
        unit->DisableShowOnOpen = false;
        unit->DisableFocusOnShow = false;
        unit->DisableShowHideSoundEffects = false;
        unit->DisableUnfocusedCloseOnEsc = false;
        unit->DisableHideTransition = false;
    }

    private bool TryGetOpenLfgAddon(out AddonLookingForGroup* addon) {
        addon = null;
        if (_plugin == null) {
            return false;
        }

        var addonPtr = _plugin.GameGui.GetAddonByName("LookingForGroup", 1);
        if (addonPtr.IsNull) {
            return false;
        }

        addon = (AddonLookingForGroup*)addonPtr.Address;
        return addon != null;
    }

    private static bool FireAddonCallback(AtkUnitBase* addon, int callbackId, uint? arg = null) {
        if (addon == null) {
            return false;
        }

        if (!arg.HasValue) {
            return addon->FireCallbackInt(callbackId);
        }

        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = callbackId;
        values[1].Type = AtkValueType.UInt;
        values[1].UInt = arg.Value;
        return addon->FireCallback(2, values, close: false);
    }

    private static void DispatchAgentEvent(AgentLookingForGroup* agent, int callbackId, uint? arg = null) {
        var count = arg.HasValue ? 2u : 1u;
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = callbackId;
        if (arg.HasValue) {
            values[1].Type = AtkValueType.UInt;
            values[1].UInt = arg.Value;
        }

        var ret = stackalloc AtkValue[1];
        ret->Type = AtkValueType.Undefined;
        agent->AgentInterface.ReceiveEvent(ret, values, count, 1);
    }

    private void ProcessHeadlessInspectLifecycle(DateTime nowUtc) {
        Guid? timedOutAttemptId = null;
        ulong pendingListingId = 0;
        ulong pendingLeaderContentId = 0;

        lock (_headlessInspectGate) {
            if (_activeUiHeadlessAttemptId is { } activeId && nowUtc >= _activeUiHeadlessDeadlineUtc) {
                timedOutAttemptId = activeId;
                _activeUiHeadlessAttemptId = null;
                _activeUiHeadlessListingId = 0;
                _activeUiHeadlessDeadlineUtc = DateTime.MinValue;
            }

            if (_pendingHeadlessListingId != 0 && nowUtc >= _pendingHeadlessDueUtc) {
                pendingListingId = _pendingHeadlessListingId;
                pendingLeaderContentId = _pendingHeadlessLeaderContentId;
                _pendingHeadlessListingId = 0;
                _pendingHeadlessLeaderContentId = 0;
                _pendingHeadlessDueUtc = DateTime.MinValue;
            }
        }

        if (timedOutAttemptId is { } expiredId) {
            _headlessDriver?.ClearScannerRequest(expiredId);
        }

        if (pendingListingId != 0 && Volatile.Read(ref _disposed) == 0) {
            _queue.Enqueue(new NativePfAction(
                NativePfActionKind.InspectListingHeadless,
                ListingId: pendingListingId,
                LeaderContentId: pendingLeaderContentId));
        }
    }

    private void ExecuteInspectListingHeadless(NativePfAction action) {
        if (action.ListingId == 0 || Volatile.Read(ref _disposed) != 0) {
            return;
        }

        var driver = _headlessDriver;
        if (driver == null) {
            return;
        }

        if (driver.IsDetailAddonOpen || driver.IsScannerRunning || !driver.IsScannerCaptureReady) {
            return;
        }

        ClearActiveUiHeadlessAttempt(driver);

        var attemptId = Guid.NewGuid();
        lock (_headlessInspectGate) {
            _activeUiHeadlessAttemptId = attemptId;
            _activeUiHeadlessListingId = action.ListingId;
            _activeUiHeadlessDeadlineUtc = _utcNow() + HeadlessInspectTimeout;
        }

        var opened = false;
        try {
            driver.ArmScannerRequest(attemptId, action.ListingId, action.LeaderContentId, allowHeadless: true);
            driver.BeginScannerOpenAttempt(attemptId);
            opened = driver.OpenListing(action.ListingId);
        } finally {
            driver.EndScannerOpenAttempt(attemptId);
        }

        if (!opened) {
            ClearActiveUiHeadlessAttemptIfMatches(attemptId, driver);
        }
    }

    private void ClearActiveUiHeadlessAttempt(INativePfHeadlessInspectDriver driver) {
        Guid? previousId = null;
        lock (_headlessInspectGate) {
            if (_activeUiHeadlessAttemptId is { } activeId) {
                previousId = activeId;
                _activeUiHeadlessAttemptId = null;
                _activeUiHeadlessListingId = 0;
                _activeUiHeadlessDeadlineUtc = DateTime.MinValue;
            }
        }

        if (previousId is { } attemptId) {
            driver.ClearScannerRequest(attemptId);
        }
    }

    private void ClearActiveUiHeadlessAttemptIfMatches(Guid attemptId, INativePfHeadlessInspectDriver driver) {
        var matched = false;
        lock (_headlessInspectGate) {
            if (_activeUiHeadlessAttemptId is { } activeId && activeId == attemptId) {
                matched = true;
                _activeUiHeadlessAttemptId = null;
                _activeUiHeadlessListingId = 0;
                _activeUiHeadlessDeadlineUtc = DateTime.MinValue;
            }
        }

        if (matched) {
            driver.ClearScannerRequest(attemptId);
        }
    }

    private sealed class PluginHeadlessInspectDriver(Plugin plugin) : INativePfHeadlessInspectDriver {
        public bool IsDetailAddonOpen => !plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1).IsNull;
        public bool IsScannerRunning => plugin.DebugPfScanner.IsRunning;
        public bool IsScannerCaptureReady => plugin.PartyDetailCaptureRuntime.IsScannerCaptureReady;

        public void ArmScannerRequest(Guid attemptId, ulong listingId, ulong leaderContentId, bool allowHeadless)
            => plugin.PartyDetailCaptureRuntime.ArmScannerRequest(attemptId, listingId, leaderContentId, allowHeadless);

        public void BeginScannerOpenAttempt(Guid attemptId)
            => plugin.PartyDetailCaptureRuntime.BeginScannerOpenAttempt(attemptId);

        public bool OpenListing(ulong listingId) {
            var agent = AgentLookingForGroup.Instance();
            return agent != null && agent->OpenListing(listingId);
        }

        public void EndScannerOpenAttempt(Guid attemptId)
            => plugin.PartyDetailCaptureRuntime.EndScannerOpenAttempt(attemptId);

        public void ClearScannerRequest(Guid attemptId)
            => plugin.PartyDetailCaptureRuntime.ClearScannerRequest(attemptId);
    }
}
