using System;
using System.Collections.Immutable;
using Dalamud.Configuration;

namespace RemotePartyFinderReborn;

[Serializable]
public class Configuration : IPluginConfiguration {
    public const int SafeMinTomestoneRequestIntervalMs = 1000;
    public const int SafeMaxTomestoneRequestIntervalMs = 60_000;
    public const int SafeMaxTomestoneConcurrency = 1;
    public const int DebugMinTomestoneRequestIntervalMs = 0;
    public const int DebugMaxTomestoneRequestIntervalMs = 60_000;
    public const int DebugMaxTomestoneConcurrency = 10;

    public const int DefaultScanActionIntervalMs = 150;
    public const int DefaultScanPostListingCooldownMs = 50;
    public const int DefaultScanDetailTimeoutMs = 1500;
    public const int DefaultScanRetryCount = 1;

    public int Version { get; set; } = 1;
    public bool AdvancedSettingsEnabled = false;
    
    // Circuit Breaker Settings
    public int CircuitBreakerFailureThreshold { get; set; } = 3;
    public int CircuitBreakerBreakDurationMinutes { get; set; } = 1;
    public int DetailUploadRetryCount { get; set; } = 1;
    public int DetailUploadTimeoutMs { get; set; } = 5000;
    public int DetailUploadMaxConcurrency { get; set; } = 2;
    public int DetailUploadBatchSize { get; set; } = 8;
    public int CharaCardResolveRetryCount { get; set; } = 1;
    
    [Newtonsoft.Json.JsonIgnore]
    private readonly object _uploadUrlsSync = new();
    [Newtonsoft.Json.JsonIgnore]
    private readonly object _saveSync = new();
    private ImmutableList<UploadUrl> _uploadUrls = DefaultUploadUrls();

    public ImmutableList<UploadUrl> UploadUrls {
        get {
            lock (_uploadUrlsSync) {
                return _uploadUrls;
            }
        }
        set {
            lock (_uploadUrlsSync) {
                _uploadUrls = value ?? ImmutableList<UploadUrl>.Empty;
            }
        }
    }

    [Newtonsoft.Json.JsonIgnore]
    internal bool HasPendingSecretMigration { get; private set; }

    // FFLogs API Settings
    public string FFLogsClientId { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonConverter(typeof(DpapiProtectedStringJsonConverter))]
    public string FFLogsClientSecret { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonConverter(typeof(DpapiProtectedStringJsonConverter))]
    public string VerifiedFFLogsCredentialFingerprint { get; set; } = string.Empty;

    public bool EnableFFLogsWorker { get; set; } = true;
    public int FFLogsWorkerBaseDelayMs { get; set; } = 5000;
    public int FFLogsWorkerIdleDelayMs { get; set; } = 10000;
    public int FFLogsWorkerMaxBackoffDelayMs { get; set; } = 60000;
    public int FFLogsWorkerJitterMs { get; set; } = 2000;

    // Tomestone API Settings
    [Newtonsoft.Json.JsonConverter(typeof(DpapiProtectedStringJsonConverter))]
    public string TomestoneApiKey { get; set; } = string.Empty;
    public bool EnableTomestoneProgressEnrichment { get; set; } = false;
    public int TomestoneRequestIntervalMs { get; set; } = SafeMinTomestoneRequestIntervalMs;
    public int TomestoneMaxConcurrency { get; set; } = SafeMaxTomestoneConcurrency;

    [Newtonsoft.Json.JsonIgnore]
    internal bool BypassTomestonePacingClampDebug { get; set; }

    // Auto Scanner Speed & Retry Settings
    public int AutoDetailScanActionIntervalMs { get; set; } = DefaultScanActionIntervalMs;
    public int AutoDetailScanPostListingCooldownMs { get; set; } = DefaultScanPostListingCooldownMs;
    public int AutoDetailScanDetailTimeoutMs { get; set; } = DefaultScanDetailTimeoutMs;
    public int AutoDetailScanRetryCount { get; set; } = DefaultScanRetryCount;

    // Ingest security metadata
    public string IngestClientId { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonConverter(typeof(DpapiProtectedStringJsonConverter))]
    public string IngestSharedSecret { get; set; } = "rpf-reborn-public-ingest-v1";
    
    internal ImmutableList<UploadUrl> GetUploadUrlsSnapshot() {
        lock (_uploadUrlsSync) {
            return _uploadUrls;
        }
    }

    internal void UpdateUploadUrls(Func<ImmutableList<UploadUrl>, ImmutableList<UploadUrl>> updater) {
        ArgumentNullException.ThrowIfNull(updater);
        lock (_uploadUrlsSync) {
            _uploadUrls = updater(_uploadUrls) ?? ImmutableList<UploadUrl>.Empty;
        }
    }

    internal void ClearPendingSecretMigration() {
        HasPendingSecretMigration = false;
    }

    [Newtonsoft.Json.JsonIgnore]
    internal bool HasFFLogsCredentialsConfigured
        => !string.IsNullOrWhiteSpace(FFLogsClientId) && !string.IsNullOrWhiteSpace(FFLogsClientSecret);

    [Newtonsoft.Json.JsonIgnore]
    internal bool HasTomestoneApiKeyConfigured
        => !string.IsNullOrWhiteSpace(TomestoneApiKey);

    internal void ResetUploadBehaviorDefaults() {
        CircuitBreakerFailureThreshold = 3;
        CircuitBreakerBreakDurationMinutes = 1;
        DetailUploadRetryCount = 1;
        CharaCardResolveRetryCount = 1;
    }

    internal void ResetFFLogsWorkerTimingDefaults() {
        FFLogsWorkerBaseDelayMs = 5000;
        FFLogsWorkerIdleDelayMs = 10000;
        FFLogsWorkerMaxBackoffDelayMs = 60000;
        FFLogsWorkerJitterMs = 2000;
    }

    internal void ResetTomestoneAdvancedDefaults() {
        TomestoneRequestIntervalMs = SafeMinTomestoneRequestIntervalMs;
        TomestoneMaxConcurrency = SafeMaxTomestoneConcurrency;
    }

    internal void ResetScannerDefaults() {
        AutoDetailScanActionIntervalMs = DefaultScanActionIntervalMs;
        AutoDetailScanPostListingCooldownMs = DefaultScanPostListingCooldownMs;
        AutoDetailScanDetailTimeoutMs = DefaultScanDetailTimeoutMs;
        AutoDetailScanRetryCount = DefaultScanRetryCount;
    }

    [System.Runtime.Serialization.OnDeserializing]
    private void OnDeserializing(System.Runtime.Serialization.StreamingContext context) {
        DpapiSecretProtector.BeginDeserialization();
    }

    [System.Runtime.Serialization.OnDeserialized]
    private void OnDeserialized(System.Runtime.Serialization.StreamingContext context) {
        HasPendingSecretMigration = DpapiSecretProtector.EndDeserialization();
    }

    public static ImmutableList<UploadUrl> DefaultUploadUrls() => [
        new("http://127.0.0.1:8000") { IsDefault = true }
    ];

    internal int GetEffectiveTomestoneRequestIntervalMs()
        => NormalizeTomestoneRequestIntervalMs(TomestoneRequestIntervalMs, BypassTomestonePacingClampDebug);

    internal int GetEffectiveTomestoneMaxConcurrency()
        => NormalizeTomestoneMaxConcurrency(TomestoneMaxConcurrency, BypassTomestonePacingClampDebug);

    internal static int NormalizeTomestoneRequestIntervalMs(int configured, bool bypassClamp = false)
        => bypassClamp
            ? Math.Clamp(configured, DebugMinTomestoneRequestIntervalMs, DebugMaxTomestoneRequestIntervalMs)
            : Math.Clamp(configured, SafeMinTomestoneRequestIntervalMs, SafeMaxTomestoneRequestIntervalMs);

    internal static int NormalizeTomestoneMaxConcurrency(int configured, bool bypassClamp = false) {
        var positive = configured <= 0 ? 1 : configured;
        return bypassClamp
            ? Math.Clamp(positive, 1, DebugMaxTomestoneConcurrency)
            : Math.Clamp(positive, 1, SafeMaxTomestoneConcurrency);
    }

    internal static int NormalizeScanActionIntervalMs(int configured)
        => Math.Clamp(configured, 50, 2_000);

    internal static int NormalizeScanPostCooldownMs(int configured)
        => Math.Clamp(configured, 0, 2_000);

    internal static int NormalizeScanDetailTimeoutMs(int configured)
        => Math.Clamp(configured, 500, 10_000);

    internal static int NormalizeScanRetryCount(int configured)
        => Math.Clamp(configured, 0, 5);

    public void Save() {
        lock (_saveSync) {
            Plugin.PluginInterface.SavePluginConfig(this);
        }
    }
}
