using System;
using System.Collections.Immutable;
using Dalamud.Configuration;

namespace RemotePartyFinderReborn;

[Serializable]
public class Configuration : IPluginConfiguration {
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
    public int AutoDetailScanRetryCount { get; set; } = 1;
    
    [Newtonsoft.Json.JsonIgnore]
    private readonly object _uploadUrlsSync = new();
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
    public int TomestoneRequestIntervalMs { get; set; } = 750;
    public int TomestoneMaxConcurrency { get; set; } = 1;

    // Ingest security metadata
    public string IngestClientId { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonConverter(typeof(DpapiProtectedStringJsonConverter))]
    public string IngestSharedSecret { get; set; } = "rpf-reborn-public-ingest-v1";

    // Debug scanner: auto-open PF detail windows
    public bool EnableAutoDetailScanDebug { get; set; } = false;
    public bool EnableManualPageCollectionDebug { get; set; } = false;
    public int AutoDetailScanActionIntervalMs { get; set; } = 400;
    public int AutoDetailScanDetailTimeoutMs { get; set; } = 3500;
    public int AutoDetailScanMinDwellMs { get; set; } = 800;
    public int AutoDetailScanPostListingCooldownMs { get; set; } = 300;
    public int AutoDetailScanDedupTtlSeconds { get; set; } = 600;
    public int AutoDetailScanMaxConsecutiveFailures { get; set; } = 5;
    public int AutoDetailScanMaxPerRun { get; set; } = 0;
    
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
        TomestoneRequestIntervalMs = 750;
        TomestoneMaxConcurrency = 1;
    }

    internal void ResetScannerDefaults() {
        AutoDetailScanActionIntervalMs = 400;
        AutoDetailScanDetailTimeoutMs = 3500;
        AutoDetailScanMinDwellMs = 800;
        AutoDetailScanPostListingCooldownMs = 300;
        AutoDetailScanDedupTtlSeconds = 600;
        AutoDetailScanMaxConsecutiveFailures = 5;
        AutoDetailScanRetryCount = 1;
        AutoDetailScanMaxPerRun = 0;
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

    internal static int NormalizeTomestoneRequestIntervalMs(int configured)
        => Math.Clamp(configured, 0, 60_000);

    internal static int NormalizeTomestoneMaxConcurrency(int configured)
        => Math.Clamp(configured <= 0 ? 1 : configured, 1, 3);

    internal static int NormalizeScanActionIntervalMs(int configured)
        => Math.Clamp(configured, 100, 2_000);

    internal static int NormalizeScanDetailTimeoutMs(int configured)
        => Math.Clamp(configured, 500, 10_000);

    internal static int NormalizeScanMinDwellMs(int configured)
        => Math.Clamp(configured, 100, 3_000);

    internal static int NormalizeScanPostCooldownMs(int configured)
        => Math.Clamp(configured, 50, 3_000);

    internal static int NormalizeScanDedupTtlSeconds(int configured)
        => Math.Clamp(configured, 30, 3_600);

    internal static int NormalizeScanMaxFailures(int configured)
        => Math.Max(configured, 1);

    internal static int NormalizeScanMaxPerRun(int configured)
        => Math.Max(configured, 0);

    public void Save() {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
