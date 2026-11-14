using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Dalamud.IoC;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RemotePartyFinderReborn.Tomestone;

[assembly: InternalsVisibleTo("RemotePartyFinderReborn.Tests")]

namespace RemotePartyFinderReborn;

internal readonly record struct PartyDetailCaptureComposition(
    PartyDetailCaptureState CaptureState,
    PartyDetailCaptureRuntime CaptureRuntime,
    PartyDetailCollector PartyDetailCollector
);

public class Plugin : IDalamudPlugin {
    [PluginService]
    internal static IPluginLog Log { get; private set; }
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; }

    [PluginService]
    internal IFramework Framework { get; private init; }

    [PluginService]
    internal IPartyFinderGui PartyFinderGui { get; private init; }

    [PluginService]
    internal IObjectTable ObjectTable { get; private init; }

    [PluginService]
    internal IAgentLifecycle AgentLifecycle { get; private init; }

    [PluginService]
    internal IAddonLifecycle AddonLifecycle { get; private init; }

    [PluginService]
    internal IGameGui GameGui { get; private init; }

    [PluginService]
    internal IChatGui ChatGui { get; private init; }

    [PluginService]
    internal IToastGui ToastGui { get; private init; }

    [PluginService]
    internal ICommandManager CommandManager { get; private init; }

    [PluginService]
    internal IGameInteropProvider GameInteropProvider { get; private init; }

    [PluginService]
    internal IDataManager DataManager { get; private init; }

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("Remote Party Finder Reborn");
    private ConfigWindow ConfigWindow { get; init; }

    internal Gatherer Gatherer { get; }
    internal PlayerLocalDatabase PlayerDatabase { get; }
    private PlayerCollector PlayerCollector { get; }
    internal CharaCardResolver CharaCardResolver { get; }
    internal PartyDetailCaptureState PartyDetailCaptureState { get; }
    internal PartyDetailCaptureRuntime PartyDetailCaptureRuntime { get; }
    internal PartyDetailCollector PartyDetailCollector { get; }
    private IDisposable PartyDetailGameLogSuppressionRuntime { get; }
    internal DebugPfScanner DebugPfScanner { get; }
    internal ApiCredentialStatusStore ApiCredentialStatus { get; } = new();
    private FFLogsCollector FFLogsCollector { get; }
    internal TomestoneApiClient TomestoneApiClient { get; }

    internal int PendingPlayerUploadCount => this.PlayerCollector.PendingCount;
    internal int PendingDetailUploadCount => this.PartyDetailCollector.PendingQueueCount;
    internal DetailUploadQueueDebugState DetailUploadQueueDebugState => this.PartyDetailCollector.GetUploadQueueDebugState();
    internal bool IsPlayerUploadInProgress => this.PlayerCollector.IsUploadInProgress;
    internal DateTime FFLogsRateLimitCooldownUntilUtc => this.FFLogsCollector.RateLimitCooldownUntilUtc;
    internal DateTime TomestoneRateLimitCooldownUntilUtc => this.TomestoneApiClient.RateLimitCooldownUntilUtc;

    private readonly Stack<Action> _cleanupActions = new();
    private bool _disposed;

    public Plugin() {
        try {
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            if (Configuration.HasPendingSecretMigration) {
                Configuration.Save();
                Configuration.ClearPendingSecretMigration();
            }

            this.Gatherer = new Gatherer(this);
            _cleanupActions.Push(() => this.Gatherer.Dispose());

            this.PlayerDatabase = new PlayerLocalDatabase(Path.Combine(PluginInterface.ConfigDirectory.FullName, "player_cache.db"));
            _cleanupActions.Push(() => this.PlayerDatabase.Dispose());

            this.PlayerCollector = new PlayerCollector(this, this.PlayerDatabase);
            _cleanupActions.Push(() => this.PlayerCollector.Dispose());

            this.CharaCardResolver = new CharaCardResolver(
                this.PlayerDatabase,
                debugSink: message => Log.Debug(message),
                warningSink: message => Log.Warning(message),
                configuration: this.Configuration,
                selectOkDialogSuppressionRuntime: new DalamudSelectOkDialogSuppressionRuntime(
                    this.AddonLifecycle,
                    this.ChatGui,
                    this.ToastGui,
                    message => Log.Warning(message),
                    message => Log.Debug(message)),
                gameInteropProvider: this.GameInteropProvider
            );
            _cleanupActions.Push(() => this.CharaCardResolver.Dispose());

            this.Framework.Update += this.CharaCardResolver.OnFrameworkUpdate;
            _cleanupActions.Push(() => this.Framework.Update -= this.CharaCardResolver.OnFrameworkUpdate);

            var partyDetailCapture = ComposePartyDetailCapture(this, warningSink: message => Log.Warning(message));
            this.PartyDetailCaptureState = partyDetailCapture.CaptureState;
            this.PartyDetailCaptureRuntime = partyDetailCapture.CaptureRuntime;
            _cleanupActions.Push(() => this.PartyDetailCaptureRuntime.Dispose());
            this.PartyDetailCollector = partyDetailCapture.PartyDetailCollector;
            _cleanupActions.Push(() => this.PartyDetailCollector.Dispose());

            this.PartyDetailGameLogSuppressionRuntime = new DalamudGameLogSuppressionRuntime(
                this.ChatGui,
                this.PartyDetailCaptureRuntime.ShouldSuppressScannerGameLog,
                "PartyDetailCaptureRuntime",
                message => Log.Warning(message),
                message => Log.Debug(message)
            );
            _cleanupActions.Push(() => this.PartyDetailGameLogSuppressionRuntime.Dispose());

            this.DebugPfScanner = new DebugPfScanner(this, this.PartyDetailCollector, this.PartyDetailCaptureRuntime, this.Gatherer);
            _cleanupActions.Push(() => this.DebugPfScanner.Dispose());

            this.TomestoneApiClient = new TomestoneApiClient(this.Configuration, this.ApiCredentialStatus.ReportTomestone);
            _cleanupActions.Push(() => this.TomestoneApiClient.Dispose());

            this.FFLogsCollector = new FFLogsCollector(this);
            _cleanupActions.Push(() => this.FFLogsCollector.Dispose());

            ValidateGameLogMessageCatalog();

            ConfigWindow = new ConfigWindow(this);
            _cleanupActions.Push(() => ConfigWindow.Dispose());
            WindowSystem.AddWindow(ConfigWindow);
            _cleanupActions.Push(() => WindowSystem.RemoveAllWindows());

            PluginInterface.UiBuilder.Draw += DrawUI;
            _cleanupActions.Push(() => PluginInterface.UiBuilder.Draw -= DrawUI);
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
            _cleanupActions.Push(() => PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI);

            CommandManager.AddHandler("/rpr", new CommandInfo(OnCommand) {
                HelpMessage = "Open config UI. Args: players-upload, players-resync, details-upload"
            });
            _cleanupActions.Push(() => CommandManager.RemoveHandler("/rpr"));
        } catch {
            RunCleanupActions();
            throw;
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        RunCleanupActions();
    }

    private void RunCleanupActions() {
        while (_cleanupActions.Count > 0) {
            var action = _cleanupActions.Pop();
            try {
                action();
            } catch (Exception ex) {
                try {
                    Log?.Error(ex, "Plugin: failed to clean up resource during disposal or rollback.");
                } catch {
                    // Ignore logging failures during teardown.
                }
            }
        }
    }

    private void OnCommand(string command, string args) {
        var normalizedArgs = (args ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedArgs == "players-upload") {
            TriggerPlayerUploadNow();
            return;
        }

        if (normalizedArgs == "players-resync") {
            _ = TriggerPlayerFullResyncUploadNow();
            return;
        }

        if (normalizedArgs == "details-upload") {
            _ = TriggerPendingDetailUploadNow();
            return;
        }

        ToggleConfigUI();
    }

    internal void TriggerPlayerUploadNow() {
        this.PlayerCollector.TriggerManualUploadNow();
    }

    internal int TriggerPlayerFullResyncUploadNow() {
        return this.PlayerCollector.TriggerManualFullResyncUploadNow();
    }

    internal int TriggerPendingDetailUploadNow() {
        return this.PartyDetailCollector.TriggerPendingDetailUploadNow();
    }

    internal DetailUploadRetryTriggerResult TriggerPendingDetailUploadNowWithState() {
        return this.PartyDetailCollector.TriggerPendingDetailUploadNowWithState();
    }

    internal bool TryGetFFLogsRateLimitCooldownRemaining(out TimeSpan remaining) {
        return this.FFLogsCollector.TryGetRateLimitCooldownRemaining(out remaining);
    }

    internal void ResetFFLogsRateLimitCooldown() {
        this.FFLogsCollector.ResetRateLimitCooldown();
    }

    internal bool TryGetTomestoneRateLimitCooldownRemaining(out TimeSpan remaining) {
        return this.TomestoneApiClient.TryGetRateLimitRemaining(out remaining);
    }

    internal void ResetTomestoneRateLimitCooldown() {
        this.TomestoneApiClient.ResetRateLimitCooldown();
    }

    private void ValidateGameLogMessageCatalog() {
        try {
            var lookup = new LuminaLogMessageLookup(this.DataManager);
            var missingKnownIds = GameLogMessageCatalog.FindMissingKnownIds(lookup);
            if (missingKnownIds.Count > 0) {
                Log.Warning($"GameLogMessageCatalog: known suppression LogMessage ids missing from Lumina: {string.Join(", ", missingKnownIds)}");
            }
        } catch (Exception exception) {
            Log.Warning($"GameLogMessageCatalog: failed to validate known LogMessage ids. {exception.Message}");
        }
    }

    internal static PartyDetailCaptureComposition ComposePartyDetailCapture(
        Plugin plugin,
        Func<PartyDetailCaptureState, PartyDetailCaptureRuntime> runtimeFactory = null,
        Func<PartyDetailCaptureState, PartyDetailCollector> collectorFactory = null,
        Action<string> warningSink = null
    ) {
        var captureState = new PartyDetailCaptureState();
        var captureRuntime = runtimeFactory?.Invoke(captureState)
                             ?? new PartyDetailCaptureRuntime(
                                 captureState,
                                 plugin.GameInteropProvider,
                                 warningSink,
                                 ScannerHeadlessBackendKind.NativeSuppression
                             );
        var collector = collectorFactory?.Invoke(captureState)
                        ?? new PartyDetailCollector(
                            plugin,
                            captureState
                        );

        return new PartyDetailCaptureComposition(captureState, captureRuntime, collector);
    }

    public void DrawUI() => WindowSystem.Draw();

    public void ToggleConfigUI() => ConfigWindow.Toggle();
}
