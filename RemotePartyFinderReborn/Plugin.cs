using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
    internal PlayerCollector PlayerCollector { get; }
    internal CharaCardResolver CharaCardResolver { get; }
    internal PartyDetailCaptureState PartyDetailCaptureState { get; }
    internal PartyDetailCaptureRuntime PartyDetailCaptureRuntime { get; }
    internal PartyDetailCollector PartyDetailCollector { get; }
    private IDisposable PartyDetailGameLogSuppressionRuntime { get; }
    internal DebugPfScanner DebugPfScanner { get; }
    internal ApiCredentialStatusStore ApiCredentialStatus { get; } = new();
    internal FFLogsActivationGate ActivationGate { get; }
    private DalamudFFLogsWorkerRegionProvider WorkerRegionProvider { get; }
    private FFLogsCollector FFLogsCollector { get; }
    internal TomestoneApiClient TomestoneApiClient { get; }
    private TomestoneCollector TomestoneCollector { get; }

    internal bool IsPluginOperational => this.ActivationGate.GetSnapshot().IsOperational;
    internal bool IsTomestoneWorkerEligible
        => this.IsPluginOperational
           && this.Configuration.EnableTomestoneProgressEnrichment
           && this.Configuration.HasTomestoneApiKeyConfigured
           && this.ApiCredentialStatus.Tomestone.State != ApiCredentialValidationState.Rejected
           && !this.TomestoneApiClient.TryGetRateLimitRemaining(out _);
    internal int PendingPlayerUploadCount => this.PlayerCollector.PendingCount;
    internal int PendingDetailUploadCount => this.PartyDetailCollector.PendingQueueCount;
    internal DetailUploadQueueDebugState DetailUploadQueueDebugState => this.PartyDetailCollector.GetUploadQueueDebugState();
    internal bool IsPlayerUploadInProgress => this.PlayerCollector.IsUploadInProgress;
    internal DateTime FFLogsRateLimitCooldownUntilUtc => this.FFLogsCollector.RateLimitCooldownUntilUtc;
    internal DateTime TomestoneRateLimitCooldownUntilUtc => this.TomestoneApiClient.RateLimitCooldownUntilUtc;

    private static readonly TimeSpan ShutdownWaitBudget = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Action> _shutdownSignalActions = [];
    private readonly Stack<Action<TimeSpan>> _cleanupActions = new();
    private int _disposed;

    public Plugin() {
        try {
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            var shouldSaveConfiguration = Configuration.HasPendingSecretMigration;
            if (Configuration.HasPendingSecretMigration) {
                Configuration.ClearPendingSecretMigration();
            }

            if (!Configuration.EnableFFLogsWorker) {
                Configuration.EnableFFLogsWorker = true;
                shouldSaveConfiguration = true;
            }

            if (shouldSaveConfiguration) {
                Configuration.Save();
            }

            _cleanupActions.Push(_ => _shutdownCts.Dispose());
            _shutdownSignalActions.Add(() => _shutdownCts.Cancel());
            this.ActivationGate = new FFLogsActivationGate(Configuration, ApiCredentialStatus, Configuration.Save);
            _shutdownSignalActions.Add(() => this.ActivationGate.BeginShutdown());
            _cleanupActions.Push(remainingTimeout => this.ActivationGate.Dispose(remainingTimeout));

            this.PlayerDatabase = new PlayerLocalDatabase(Path.Combine(PluginInterface.ConfigDirectory.FullName, "player_cache.db"));
            _cleanupActions.Push(remainingTimeout => this.PlayerDatabase.Dispose(remainingTimeout));

            this.PlayerCollector = new PlayerCollector(this, this.PlayerDatabase);
            _shutdownSignalActions.Add(() => this.PlayerCollector.BeginShutdown());
            _cleanupActions.Push(remainingTimeout => this.PlayerCollector.Dispose(remainingTimeout));

            this.Gatherer = new Gatherer(this);
            _shutdownSignalActions.Add(() => this.Gatherer.BeginShutdown());
            _cleanupActions.Push(remainingTimeout => this.Gatherer.Dispose(remainingTimeout));

            this.CharaCardResolver = new CharaCardResolver(
                this.PlayerDatabase,
                debugSink: message => Log.Debug(message),
                warningSink: message => Log.Warning(message),
                configuration: this.Configuration,
                selectOkDialogSuppressionRuntime: new DalamudSelectOkDialogSuppressionRuntime(
                    this.ChatGui,
                    this.ToastGui,
                    this.DataManager,
                    message => Log.Warning(message),
                    message => Log.Debug(message)),
                gameInteropProvider: this.GameInteropProvider,
                isOperational: () => this.IsPluginOperational
            );
            _shutdownSignalActions.Add(() => this.CharaCardResolver.BeginShutdown());
            _cleanupActions.Push(remainingTimeout => this.CharaCardResolver.Dispose(remainingTimeout));

            this.Framework.Update += this.CharaCardResolver.OnFrameworkUpdate;
            _cleanupActions.Push(_ => this.Framework.Update -= this.CharaCardResolver.OnFrameworkUpdate);

            var partyDetailCapture = ComposePartyDetailCapture(this, warningSink: message => Log.Warning(message));
            this.PartyDetailCaptureState = partyDetailCapture.CaptureState;
            this.PartyDetailCaptureRuntime = partyDetailCapture.CaptureRuntime;
            _cleanupActions.Push(_ => this.PartyDetailCaptureRuntime.Dispose());
            this.PartyDetailCollector = partyDetailCapture.PartyDetailCollector;
            _shutdownSignalActions.Add(() => this.PartyDetailCollector.BeginShutdown());
            _cleanupActions.Push(remainingTimeout => this.PartyDetailCollector.Dispose(remainingTimeout));

            this.PartyDetailGameLogSuppressionRuntime = new DalamudGameLogSuppressionRuntime(
                this.ChatGui,
                this.PartyDetailCaptureRuntime.ShouldSuppressScannerGameLog,
                "PartyDetailCaptureRuntime",
                message => Log.Warning(message),
                message => Log.Debug(message)
            );
            _cleanupActions.Push(_ => this.PartyDetailGameLogSuppressionRuntime.Dispose());

            this.DebugPfScanner = new DebugPfScanner(this, this.PartyDetailCollector, this.PartyDetailCaptureRuntime, this.Gatherer);
            _cleanupActions.Push(_ => this.DebugPfScanner.Dispose());

            this.TomestoneApiClient = new TomestoneApiClient(this.Configuration, this.ApiCredentialStatus.ReportTomestone);
            _cleanupActions.Push(_ => this.TomestoneApiClient.Dispose());

            this.WorkerRegionProvider = new DalamudFFLogsWorkerRegionProvider(
                this.Framework,
                this.ObjectTable,
                this.DataManager,
                message => Log.Warning(message));
            _cleanupActions.Push(_ => this.WorkerRegionProvider.Dispose());

            this.TomestoneCollector = new TomestoneCollector(this, this.WorkerRegionProvider);
            _shutdownSignalActions.Add(() => this.TomestoneCollector.BeginShutdown());
            _cleanupActions.Push(remainingTimeout => this.TomestoneCollector.Dispose(remainingTimeout));

            this.FFLogsCollector = new FFLogsCollector(this, this.WorkerRegionProvider);
            _shutdownSignalActions.Add(() => this.FFLogsCollector.BeginShutdown());
            _cleanupActions.Push(remainingTimeout => this.FFLogsCollector.Dispose(remainingTimeout));

            ValidateGameLogMessageCatalog();

            ConfigWindow = new ConfigWindow(this);
            _cleanupActions.Push(_ => ConfigWindow.Dispose());
            WindowSystem.AddWindow(ConfigWindow);
            _cleanupActions.Push(_ => WindowSystem.RemoveAllWindows());

            PluginInterface.UiBuilder.Draw += DrawUI;
            _cleanupActions.Push(_ => PluginInterface.UiBuilder.Draw -= DrawUI);
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
            _cleanupActions.Push(_ => PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI);

            CommandManager.AddHandler("/rpr", new CommandInfo(OnCommand) {
                HelpMessage = "Open config UI. Args: players-upload, players-resync, details-upload"
            });
            _cleanupActions.Push(_ => CommandManager.RemoveHandler("/rpr"));

            if (!Configuration.HasFFLogsCredentialsConfigured) {
                ConfigWindow.IsOpen = true;
            } else {
                _ = VerifyFFLogsCredentialsAsync();
            }
        } catch {
            RunCleanupActions();
            throw;
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        RunCleanupActions();
    }

    private void RunCleanupActions() {
        for (var i = _shutdownSignalActions.Count - 1; i >= 0; i--) {
            try {
                _shutdownSignalActions[i]();
            } catch (Exception ex) {
                try {
                    Log?.Error(ex, "Plugin: failed to signal shutdown during disposal or rollback.");
                } catch {
                    // Ignore logging failures during teardown.
                }
            }
        }

        var shutdownStopwatch = Stopwatch.StartNew();
        var workerTasks = new List<Task>();
        this.ActivationGate?.CollectShutdownTasks(workerTasks);
        this.FFLogsCollector?.CollectShutdownTasks(workerTasks);
        this.TomestoneCollector?.CollectShutdownTasks(workerTasks);
        this.PartyDetailCollector?.CollectShutdownTasks(workerTasks);
        this.CharaCardResolver?.CollectShutdownTasks(workerTasks);
        this.PlayerCollector?.CollectShutdownTasks(workerTasks);
        this.Gatherer?.CollectShutdownTasks(workerTasks);

        var remainingTimeout = WaitForShutdownTasksConcurrently(workerTasks, ShutdownWaitBudget, shutdownStopwatch);

        try {
            this.PlayerDatabase?.BeginShutdown();
            var dbTasks = new List<Task>();
            this.PlayerDatabase?.CollectShutdownTasks(dbTasks);
            remainingTimeout = WaitForShutdownTasksConcurrently(dbTasks, ShutdownWaitBudget, shutdownStopwatch);
        } catch (Exception ex) {
            try {
                Log?.Error(ex, "Plugin: failed waiting for player database shutdown.");
            } catch {
                // Ignore logging failures during teardown.
            }
        }

        while (_cleanupActions.Count > 0) {
            var action = _cleanupActions.Pop();
            try {
                action(remainingTimeout);
            } catch (Exception ex) {
                try {
                    Log?.Error(ex, "Plugin: failed to clean up resource during disposal or rollback.");
                } catch {
                    // Ignore logging failures during teardown.
                }
            }
        }
    }

    internal static TimeSpan WaitForShutdownTasksConcurrently(
        IReadOnlyList<Task> tasks,
        TimeSpan totalBudget,
        Stopwatch elapsedStopwatch = null
    ) {
        elapsedStopwatch ??= Stopwatch.StartNew();
        var remaining = totalBudget - elapsedStopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero) {
            return TimeSpan.Zero;
        }

        if (tasks.Count > 0) {
            try {
                Task.WhenAll(tasks).Wait(remaining);
            } catch (Exception) {
                // Cancellation/timeout during shutdown is expected; callers have already signaled shutdown.
            }
        }

        var afterWait = totalBudget - elapsedStopwatch.Elapsed;
        return afterWait > TimeSpan.Zero ? afterWait : TimeSpan.Zero;
    }

    private void OnCommand(string command, string args) {
        var normalizedArgs = (args ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedArgs == "players-upload") {
            if (!IsPluginOperational) {
                NotifyLockedAndOpenSettings();
                return;
            }

            TriggerPlayerUploadNow();
            return;
        }

        if (normalizedArgs == "players-resync") {
            if (!IsPluginOperational) {
                NotifyLockedAndOpenSettings();
                return;
            }

            _ = TriggerPlayerFullResyncUploadNow();
            return;
        }

        if (normalizedArgs == "details-upload") {
            if (!IsPluginOperational) {
                NotifyLockedAndOpenSettings();
                return;
            }

            _ = TriggerPendingDetailUploadNow();
            return;
        }

        ToggleConfigUI();
    }

    private void NotifyLockedAndOpenSettings() {
        ChatGui.PrintError("[Remote Party Finder Reborn] Plugin is locked. Register valid FFLogs v2 API credentials first (/rpr).");
        ConfigWindow.IsOpen = true;
    }

    internal async Task<OperationOutcome<bool>> VerifyFFLogsCredentialsAsync(CancellationToken cancellationToken = default) {
        if (Volatile.Read(ref _disposed) != 0) {
            return OperationOutcome<bool>.Failure(false, "Plugin is shutting down.");
        }

        CancellationToken shutdownToken;
        CancellationTokenSource linkedCts;
        try {
            shutdownToken = _shutdownCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
        } catch (ObjectDisposedException) {
            return OperationOutcome<bool>.Failure(false, "Plugin is shutting down.");
        }

        using (linkedCts) {
            try {
                return await ActivationGate.VerifyAsync(
                    ct => FFLogsCollector.VerifyCredentialsAsync(ct),
                    linkedCts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
                return OperationOutcome<bool>.Failure(false, "Plugin is shutting down.");
            } catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) {
                return OperationOutcome<bool>.Failure(false, "Plugin is shutting down.");
            }
        }
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
                                 ScannerHeadlessBackendKind.NativeSuppression,
                                 isCaptureEnabled: plugin is null ? null : () => plugin.IsPluginOperational
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
