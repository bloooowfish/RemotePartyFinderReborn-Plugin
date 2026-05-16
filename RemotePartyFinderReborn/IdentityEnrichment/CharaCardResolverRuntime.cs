using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Toast;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

#nullable enable

namespace RemotePartyFinderReborn;

internal interface ICharaCardResolverRuntime : IDisposable {
    ResolverPreflightResult CheckAvailability();
    void Initialize(
        Func<CharaCardPacketModel, bool> packetHandler,
        Func<CharaCardPacketModel, bool> agentPacketHandler,
        Func<BannerHelperResponseModel, bool> responseDispatcherHandler,
        Func<SelectOkStateTransitionModel, bool> selectOkStateTransitionHandler,
        Func<GameUiMessageModel, bool> simpleGameUiMessageHandler,
        Func<GameUiMessageModel, bool> parameterizedGameUiMessageHandler,
        Func<SelectOkDialogRequestModel, bool> selectOkDialogHandler
    );
    bool TryRequest(ulong contentId);
}

internal interface ISelectOkDialogSuppressionRuntime : IDisposable {
    void Initialize(Func<string, bool> selectOkHandler);
}

internal sealed class DalamudSelectOkDialogSuppressionRuntime : ISelectOkDialogSuppressionRuntime {
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IChatGui _chatGui;
    private readonly IToastGui _toastGui;
    private readonly Action<string>? _warningSink;
    private readonly Action<string>? _debugSink;
    private readonly IAddonLifecycle.AddonEventDelegate _addonEventHandler;
    private readonly IChatGui.OnLogMessageDelegate _logMessageHandler;
    private readonly IToastGui.OnNormalToastDelegate _normalToastHandler;
    private readonly IToastGui.OnQuestToastDelegate _questToastHandler;
    private readonly IToastGui.OnErrorToastDelegate _errorToastHandler;
    private readonly List<AddonEvent> _registeredAddonEvents = [];
    private Func<string, bool>? _selectOkHandler;
    private bool _disposed;

    internal DalamudSelectOkDialogSuppressionRuntime(
        IAddonLifecycle addonLifecycle,
        IChatGui chatGui,
        IToastGui toastGui,
        Action<string>? warningSink = null,
        Action<string>? debugSink = null
    ) {
        _addonLifecycle = addonLifecycle ?? throw new ArgumentNullException(nameof(addonLifecycle));
        _chatGui = chatGui ?? throw new ArgumentNullException(nameof(chatGui));
        _toastGui = toastGui ?? throw new ArgumentNullException(nameof(toastGui));
        _warningSink = warningSink;
        _debugSink = debugSink;
        _addonEventHandler = OnSelectOkAddonEvent;
        _logMessageHandler = OnLogMessage;
        _normalToastHandler = OnNormalToast;
        _questToastHandler = OnQuestToast;
        _errorToastHandler = OnErrorToast;
    }

    public void Initialize(Func<string, bool> selectOkHandler) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _selectOkHandler = selectOkHandler ?? throw new ArgumentNullException(nameof(selectOkHandler));

        foreach (var eventType in CharaCardResolver.SelectOkSuppressionEvents) {
            _addonLifecycle.RegisterListener(eventType, CharaCardResolver.SelectOkAddonNames, _addonEventHandler);
            _registeredAddonEvents.Add(eventType);
        }

        _chatGui.LogMessage += _logMessageHandler;
        _toastGui.Toast += _normalToastHandler;
        _toastGui.QuestToast += _questToastHandler;
        _toastGui.ErrorToast += _errorToastHandler;
        _debugSink?.Invoke("CharaCardResolver: SelectOk addon/chat/toast suppression runtime initialized.");
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        _chatGui.LogMessage -= _logMessageHandler;
        _toastGui.Toast -= _normalToastHandler;
        _toastGui.QuestToast -= _questToastHandler;
        _toastGui.ErrorToast -= _errorToastHandler;

        foreach (var eventType in _registeredAddonEvents) {
            _addonLifecycle.UnregisterListener(eventType, CharaCardResolver.SelectOkAddonNames, _addonEventHandler);
        }

        _registeredAddonEvents.Clear();
    }

    private void OnSelectOkAddonEvent(AddonEvent type, AddonArgs args) {
        try {
            var addonName = string.IsNullOrWhiteSpace(args.AddonName) ? "SelectOk" : args.AddonName;
            if (_selectOkHandler?.Invoke($"{addonName}.{type}") == true) {
                args.PreventOriginal();
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process SelectOk addon suppression event. {exception.Message}");
        }
    }

    private void OnLogMessage(ILogMessage message) {
        try {
            if (_selectOkHandler?.Invoke($"ChatLog.{message.LogMessageId}") == true) {
                message.PreventOriginal();
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process chat log suppression event. {exception.Message}");
        }
    }

    private void OnNormalToast(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref ToastOptions options, ref bool isHandled) {
        try {
            if (_selectOkHandler?.Invoke("Toast.Normal") == true) {
                isHandled = true;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process normal toast suppression event. {exception.Message}");
        }
    }

    private void OnQuestToast(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref QuestToastOptions options, ref bool isHandled) {
        try {
            if (_selectOkHandler?.Invoke("Toast.Quest") == true) {
                isHandled = true;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process quest toast suppression event. {exception.Message}");
        }
    }

    private void OnErrorToast(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref bool isHandled) {
        try {
            if (_selectOkHandler?.Invoke("Toast.Error") == true) {
                isHandled = true;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process error toast suppression event. {exception.Message}");
        }
    }
}

internal unsafe sealed class DalamudCharaCardResolverRuntime : ICharaCardResolverRuntime {
    private const int CharaCardPacketNameOffset = 0x1A9;
    private const int CharaCardPacketNameLength = 32;

    // These helpers are not exposed as generated interop yet, so resolve them by signature.
    private const string HandleCurrentCharaCardDataPacketSignature =
        "40 53 48 83 EC ?? 8B 05 ?? ?? ?? ?? 48 8B DA";
    private const string CharaCardUpdateResponseDispatcherSignature =
        "48 89 5C 24 08 57 48 83 EC 20 41 8B F8 8B DA 85 D2 0F 85 84 01 00 00 38 51 68 0F 84 3B 02 00 00 44 0F B6 81 E0 01 00 00 45 33 D2 41 F6 C0 04";
    private const string AgentOpenCharaCardForPacketSignature =
        "40 55 53 57 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC B8 04 00 00";
    private const string SelectOkStateTransitionSignature =
        "40 55 53 56 57 48 8D 6C 24 88 48 81 EC 78 01 00 00 48 8B 05 50 C2 A7 01 48 33 C4 48 89 45 60 8B 85 C0 00 00 00 49 8B F8 48 8B F2 48 8B D9 83 F8 01 75 17";
    private const string GameUiMessageSignature =
        "48 89 5C 24 10 48 89 74 24 18 48 89 7C 24 20 55 48 8D AC 24 80 FE FF FF 48 81 EC 80 02 00 00 48 8B 05 22 F9 E4 01 48 33 C4 48 89 85 70 01 00 00";
    private const string ParameterizedGameUiMessageSignature =
        "48 89 5C 24 10 48 89 74 24 18 55 57 41 56 48 8D AC 24 20 FE FF FF 48 81 EC E0 02 00 00 48 8B 05 C4 F6 E4 01 48 33 C4 48 89 85 D0 01 00 00 45 33 F6";
    private const string CreateSelectOkDialogSignature =
        "40 53 55 56 57 41 56 48 81 EC 90 00 00 00 48 8B 05 23 86 A7 01 48 33 C4 48 89 84 24 80 00 00 00 BF 04 00 00 00 41 8B E8 44 8B CF 48 8D 44 24 40";

    private readonly IGameInteropProvider _interopProvider;
    private readonly delegate*<CharaCard*> _instanceResolver;
    private readonly Action<string>? _warningSink;
    private readonly Action<string>? _debugSink;
    private Hook<HandleCurrentCharaCardDataPacketDelegate>? _packetHook;
    private Hook<CharaCardUpdateResponseDispatcherDelegate>? _responseStatusDispatcherHook;
    private Hook<AgentOpenCharaCardForPacketDelegate>? _agentPacketHook;
    private Hook<SelectOkStateTransitionDelegate>? _selectOkStateTransitionHook;
    private Hook<GameUiMessageDelegate>? _gameUiMessageHook;
    private Hook<ParameterizedGameUiMessageDelegate>? _parameterizedGameUiMessageHook;
    private Hook<CreateSelectOkDialogDelegate>? _createSelectOkDialogHook;
    private InteropHookBundle? _hookBundle;
    private bool _loggedPacketDetourHit;
    private bool _loggedResponseStatusDetourHit;
    private bool _loggedSelectOkStateTransitionDetourHit;
    private bool _loggedSimpleGameUiMessageDetourHit;
    private bool _loggedParameterizedGameUiMessageDetourHit;
    private bool _loggedFinalSelectOkDialogDetourHit;
    private Func<CharaCardPacketModel, bool>? _packetHandler;
    private Func<CharaCardPacketModel, bool>? _agentPacketHandler;
    private Func<BannerHelperResponseModel, bool>? _responseDispatcherHandler;
    private Func<SelectOkStateTransitionModel, bool>? _selectOkStateTransitionHandler;
    private Func<GameUiMessageModel, bool>? _simpleGameUiMessageHandler;
    private Func<GameUiMessageModel, bool>? _parameterizedGameUiMessageHandler;
    private Func<SelectOkDialogRequestModel, bool>? _selectOkDialogHandler;

    private delegate void HandleCurrentCharaCardDataPacketDelegate(CharaCard* thisPtr, CharaCardPacket* packet);
    private delegate void CharaCardUpdateResponseDispatcherDelegate(nint thisPtr, int responseCode, uint responseDetail);
    private delegate void AgentOpenCharaCardForPacketDelegate(AgentCharaCard* thisPtr, CharaCardPacket* packet, bool a3);
    private delegate nint SelectOkStateTransitionDelegate(nint thisPtr, nint statePtr, nint eventPtr, nint arg4, int action);
    private delegate void GameUiMessageDelegate(nint thisPtr, uint messageId);
    private delegate void ParameterizedGameUiMessageDelegate(nint thisPtr, uint messageId, uint param);
    private delegate uint CreateSelectOkDialogDelegate(nint thisPtr, uint messageId, nuint variant);

    internal static DalamudCharaCardResolverRuntime Create(
        IGameInteropProvider interopProvider,
        Action<string>? warningSink = null,
        Action<string>? debugSink = null
    ) {
        return new DalamudCharaCardResolverRuntime(
            interopProvider,
            &CharaCard.Instance,
            warningSink,
            debugSink
        );
    }

    internal DalamudCharaCardResolverRuntime(
        IGameInteropProvider interopProvider,
        delegate*<CharaCard*> instanceResolver,
        Action<string>? warningSink = null,
        Action<string>? debugSink = null
    ) {
        _interopProvider = interopProvider ?? throw new ArgumentNullException(nameof(interopProvider));
        _instanceResolver = instanceResolver;
        _warningSink = warningSink;
        _debugSink = debugSink;
    }

    public ResolverPreflightResult CheckAvailability() => CharaCardResolver.CheckAvailability();

    public void Initialize(
        Func<CharaCardPacketModel, bool> packetHandler,
        Func<CharaCardPacketModel, bool> agentPacketHandler,
        Func<BannerHelperResponseModel, bool> responseDispatcherHandler,
        Func<SelectOkStateTransitionModel, bool> selectOkStateTransitionHandler,
        Func<GameUiMessageModel, bool> simpleGameUiMessageHandler,
        Func<GameUiMessageModel, bool> parameterizedGameUiMessageHandler,
        Func<SelectOkDialogRequestModel, bool> selectOkDialogHandler
    ) {
        ArgumentNullException.ThrowIfNull(packetHandler);
        ArgumentNullException.ThrowIfNull(agentPacketHandler);
        ArgumentNullException.ThrowIfNull(responseDispatcherHandler);
        ArgumentNullException.ThrowIfNull(selectOkStateTransitionHandler);
        ArgumentNullException.ThrowIfNull(simpleGameUiMessageHandler);
        ArgumentNullException.ThrowIfNull(parameterizedGameUiMessageHandler);
        ArgumentNullException.ThrowIfNull(selectOkDialogHandler);

        _packetHandler = packetHandler;
        _agentPacketHandler = agentPacketHandler;
        _responseDispatcherHandler = responseDispatcherHandler;
        _selectOkStateTransitionHandler = selectOkStateTransitionHandler;
        _simpleGameUiMessageHandler = simpleGameUiMessageHandler;
        _parameterizedGameUiMessageHandler = parameterizedGameUiMessageHandler;
        _selectOkDialogHandler = selectOkDialogHandler;
        var hooks = new InteropHookBundle();
        try {
            _packetHook = CreateRequiredHook<HandleCurrentCharaCardDataPacketDelegate>(
                "HandleCurrentCharaCardDataPacket",
                HandleCurrentCharaCardDataPacketSignature,
                HandleCurrentCharaCardDataPacketDetour
            );
            hooks.Add(_packetHook);
            _responseStatusDispatcherHook = TryCreateOptionalHook<CharaCardUpdateResponseDispatcherDelegate>(
                "CharaCardUpdateResponseDispatcher",
                CharaCardUpdateResponseDispatcherSignature,
                CharaCardUpdateResponseDispatcherDetour
            );
            AddOptionalHook(hooks, _responseStatusDispatcherHook);
            _agentPacketHook = TryCreateOptionalHook<AgentOpenCharaCardForPacketDelegate>(
                "AgentOpenCharaCardForPacket",
                AgentOpenCharaCardForPacketSignature,
                AgentOpenCharaCardForPacketDetour
            );
            AddOptionalHook(hooks, _agentPacketHook);
            TryInitializeOptionalSelectOkStateTransitionHook();
            AddOptionalHook(hooks, _selectOkStateTransitionHook);
            TryInitializeOptionalGameUiMessageHooks();
            AddOptionalHook(hooks, _gameUiMessageHook);
            AddOptionalHook(hooks, _parameterizedGameUiMessageHook);
            _createSelectOkDialogHook = TryCreateOptionalHook<CreateSelectOkDialogDelegate>(
                "CreateSelectOkDialog",
                CreateSelectOkDialogSignature,
                CreateSelectOkDialogDetour
            );
            AddOptionalHook(hooks, _createSelectOkDialogHook);
            _packetHook.Enable();
            _responseStatusDispatcherHook?.Enable();
            _agentPacketHook?.Enable();
            _selectOkStateTransitionHook?.Enable();
            _gameUiMessageHook?.Enable();
            _parameterizedGameUiMessageHook?.Enable();
            _createSelectOkDialogHook?.Enable();
            _hookBundle = hooks;
        } catch {
            hooks.Dispose();
            ClearHookReferences();
            throw;
        }
        _debugSink?.Invoke(
            "CharaCardResolver: hook init " +
            $"packet=true responseDispatcher={_responseStatusDispatcherHook is not null} agent={_agentPacketHook is not null} " +
            $"stateTransition={_selectOkStateTransitionHook is not null} " +
            $"simpleMessage={_gameUiMessageHook is not null} parameterizedMessage={_parameterizedGameUiMessageHook is not null} " +
            $"finalSelectOk={_createSelectOkDialogHook is not null} bannerLog=false bannerPacket=false"
        );
    }

    public bool TryRequest(ulong contentId) {
        var charaCard = _instanceResolver();
        if (charaCard == null) {
            return false;
        }

        charaCard->RequestCharaCardForContentId(contentId);
        return true;
    }

    public void Dispose() {
        _packetHandler = null;
        _agentPacketHandler = null;
        _responseDispatcherHandler = null;
        _selectOkStateTransitionHandler = null;
        _simpleGameUiMessageHandler = null;
        _parameterizedGameUiMessageHandler = null;
        _selectOkDialogHandler = null;
        _hookBundle?.Dispose();
        _hookBundle = null;
        ClearHookReferences();
    }

    private static void AddOptionalHook(InteropHookBundle hooks, IDisposable? hook) {
        if (hook is not null) {
            hooks.Add(hook);
        }
    }

    private void ClearHookReferences() {
        _packetHook = null;
        _responseStatusDispatcherHook = null;
        _agentPacketHook = null;
        _selectOkStateTransitionHook = null;
        _gameUiMessageHook = null;
        _parameterizedGameUiMessageHook = null;
        _createSelectOkDialogHook = null;
    }

    private void TryInitializeOptionalSelectOkStateTransitionHook() {
        _selectOkStateTransitionHook = TryCreateOptionalHook<SelectOkStateTransitionDelegate>(
            "SelectOkStateTransition",
            SelectOkStateTransitionSignature,
            SelectOkStateTransitionDetour
        );
    }

    private void TryInitializeOptionalGameUiMessageHooks() {
        _gameUiMessageHook = TryCreateOptionalHook<GameUiMessageDelegate>(
            "GameUiMessage",
            GameUiMessageSignature,
            GameUiMessageDetour
        );

        _parameterizedGameUiMessageHook = TryCreateOptionalHook<ParameterizedGameUiMessageDelegate>(
            "ParameterizedGameUiMessage",
            ParameterizedGameUiMessageSignature,
            ParameterizedGameUiMessageDetour
        );
    }

    private Hook<T> CreateRequiredHook<T>(string name, string signature, T detour) where T : Delegate {
        try {
            var hook = _interopProvider.HookFromSignature(signature, detour);
            _debugSink?.Invoke($"CharaCardResolver: initialized required {name} hook.");
            return hook;
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to initialize required {name} hook. {exception.Message}");
            throw;
        }
    }

    private Hook<T>? TryCreateOptionalHook<T>(string name, string signature, T detour) where T : Delegate {
        try {
            var hook = _interopProvider.HookFromSignature(signature, detour);
            _debugSink?.Invoke($"CharaCardResolver: initialized optional {name} hook.");
            return hook;
        } catch (Exception exception) {
            _debugSink?.Invoke($"CharaCardResolver: optional {name} hook unavailable. {exception.Message}");
            return null;
        }
    }

    private void HandleCurrentCharaCardDataPacketDetour(CharaCard* thisPtr, CharaCardPacket* packet) {
        var shouldPropagateOriginal = true;
        try {
            if (!_loggedPacketDetourHit) {
                _loggedPacketDetourHit = true;
                _debugSink?.Invoke("CharaCardResolver: hit HandleCurrentCharaCardDataPacket detour.");
            }
            if (packet != null) {
                shouldPropagateOriginal = _packetHandler?.Invoke(CreatePacketModel(packet)) ?? true;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process packet. {exception.Message}");
        } finally {
            if (shouldPropagateOriginal) {
                _packetHook?.Original(thisPtr, packet);
            }
        }
    }

    private void CharaCardUpdateResponseDispatcherDetour(nint thisPtr, int responseCode, uint responseDetail) {
        try {
            if (!_loggedResponseStatusDetourHit) {
                _loggedResponseStatusDetourHit = true;
                _debugSink?.Invoke(
                    $"CharaCardResolver: hit CharaCard update response dispatcher detour code={responseCode} detail={responseDetail}."
                );
            }
            if (_responseDispatcherHandler?.Invoke(new BannerHelperResponseModel(responseCode, responseDetail)) == true) {
                return;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process CharaCard update response dispatcher. {exception.Message}");
        }

        _responseStatusDispatcherHook?.Original(thisPtr, responseCode, responseDetail);
    }

    private void AgentOpenCharaCardForPacketDetour(AgentCharaCard* thisPtr, CharaCardPacket* packet, bool a3) {
        var shouldPropagateOriginal = true;
        try {
            if (packet != null) {
                shouldPropagateOriginal = _agentPacketHandler?.Invoke(CreatePacketModel(packet)) ?? true;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process agent packet. {exception.Message}");
        } finally {
            if (shouldPropagateOriginal) {
                _agentPacketHook?.Original(thisPtr, packet, a3);
            }
        }
    }

    private void GameUiMessageDetour(nint thisPtr, uint messageId) {
        try {
            if (CharaCardResolver.PlateFailureMessageIds.Contains(messageId) && !_loggedSimpleGameUiMessageDetourHit) {
                _loggedSimpleGameUiMessageDetourHit = true;
                _debugSink?.Invoke($"CharaCardResolver: hit simple game UI message detour messageId=0x{messageId:X}.");
            }
            if (_simpleGameUiMessageHandler?.Invoke(new GameUiMessageModel(messageId)) == false) {
                return;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process simple game UI message. {exception.Message}");
        }

        _gameUiMessageHook?.Original(thisPtr, messageId);
    }

    private void ParameterizedGameUiMessageDetour(nint thisPtr, uint messageId, uint param) {
        try {
            if (CharaCardResolver.PlateFailureMessageIds.Contains(messageId) && !_loggedParameterizedGameUiMessageDetourHit) {
                _loggedParameterizedGameUiMessageDetourHit = true;
                _debugSink?.Invoke(
                    $"CharaCardResolver: hit parameterized game UI message detour messageId=0x{messageId:X} param={param}."
                );
            }
            if (_parameterizedGameUiMessageHandler?.Invoke(new GameUiMessageModel(messageId, param, true)) == false) {
                return;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process parameterized game UI message. {exception.Message}");
        }

        _parameterizedGameUiMessageHook?.Original(thisPtr, messageId, param);
    }

    private nint SelectOkStateTransitionDetour(nint thisPtr, nint statePtr, nint eventPtr, nint arg4, int action) {
        try {
            var storage = GetCharaCardStorage(thisPtr);
            if (storage != null && !_loggedSelectOkStateTransitionDetourHit) {
                _loggedSelectOkStateTransitionDetourHit = true;
                _debugSink?.Invoke(
                    $"CharaCardResolver: hit SelectOk state transition detour action={action} contentId={storage->ContentId}."
                );
            }
            if (storage != null
                && _selectOkStateTransitionHandler?.Invoke(new SelectOkStateTransitionModel(
                    storage->ContentId,
                    action,
                    storage->CanEdit,
                    storage->IsNotCreated,
                    storage->WasResetDueToFantasia
                )) == false) {
                storage->SelectOkAddonId = 0;
                if (statePtr != 0) {
                    *(int*)statePtr = 2;
                    *((byte*)statePtr + 8) = 0;
                }

                return statePtr;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process SelectOk state transition. {exception.Message}");
        }

        return _selectOkStateTransitionHook?.Original(thisPtr, statePtr, eventPtr, arg4, action) ?? statePtr;
    }

    private uint CreateSelectOkDialogDetour(nint thisPtr, uint messageId, nuint variant) {
        try {
            var storage = GetCharaCardStorage(thisPtr);
            if (storage != null && !_loggedFinalSelectOkDialogDetourHit) {
                _loggedFinalSelectOkDialogDetourHit = true;
                _debugSink?.Invoke(
                    $"CharaCardResolver: hit final SelectOk dialog detour messageId=0x{messageId:X} variant={(int)variant} contentId={storage->ContentId}."
                );
            }
            if (storage != null
                && _selectOkDialogHandler?.Invoke(new SelectOkDialogRequestModel(
                    storage->ContentId,
                    messageId,
                    (int)variant,
                    storage->IsNotCreated,
                    storage->WasResetDueToFantasia
                )) == false) {
                return 0;
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"CharaCardResolver: failed to process final SelectOk dialog creation. {exception.Message}");
        }

        return _createSelectOkDialogHook?.Original(thisPtr, messageId, variant) ?? 0;
    }

    private static AgentCharaCard.Storage* GetCharaCardStorage(nint thisPtr) {
        return thisPtr == 0 ? null : ((AgentCharaCard*)thisPtr)->Data;
    }

    private static CharaCardPacketModel CreatePacketModel(CharaCardPacket* packet) {
        return new CharaCardPacketModel(
            packet->ContentId,
            packet->WorldId,
            ReadPacketName(packet),
            packet->SomeState,
            packet->CharaCardData.Data8.Version,
            packet->CharaCardData.Data8.Flags,
            packet->CharaCardData.Data8.PrivacyFlags
        );
    }

    private static string ReadPacketName(CharaCardPacket* packet) {
        // API15 client code reads the CharaCard packet name at 0x1A9. The current generated
        // FFXIVClientStructs field is four bytes early, so packet->NameString is empty.
        var bytes = new ReadOnlySpan<byte>((byte*)packet + CharaCardPacketNameOffset, CharaCardPacketNameLength);
        return DecodeNullTerminatedUtf8(bytes);
    }

    internal static string DecodeNullTerminatedUtf8(ReadOnlySpan<byte> bytes) {
        var terminatorIndex = bytes.IndexOf((byte)0);
        var nameBytes = terminatorIndex >= 0 ? bytes[..terminatorIndex] : bytes;
        return nameBytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(nameBytes);
    }
}
