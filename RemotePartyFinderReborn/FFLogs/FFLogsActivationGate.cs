#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal enum PluginActivationState {
    MissingCredentials,
    Verifying,
    Active,
    Rejected,
    VerificationFailed,
}

internal readonly record struct PluginActivationSnapshot(
    PluginActivationState State,
    bool IsOperational,
    string StatusMessage,
    DateTime UpdatedAtUtc
);

internal sealed class FFLogsActivationGate : IDisposable {
    private readonly Configuration _configuration;
    private readonly ApiCredentialStatusStore _credentialStatusStore;
    private readonly Action _saveConfiguration;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _verifyLock = new(1, 1);

    private string _lastAcceptedFingerprint = string.Empty;
    private int _verificationInFlightCount;

    internal FFLogsActivationGate(
        Configuration configuration,
        ApiCredentialStatusStore credentialStatusStore,
        Action? saveConfiguration = null
    ) {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _credentialStatusStore = credentialStatusStore ?? throw new ArgumentNullException(nameof(credentialStatusStore));
        _saveConfiguration = saveConfiguration ?? configuration.Save;
    }

    internal bool IsVerificationInFlight => Volatile.Read(ref _verificationInFlightCount) > 0;

    public void Dispose() {
        _verifyLock.Dispose();
    }

    internal static string ComputeCredentialFingerprint(string? clientId, string? clientSecret) {
        var trimmedId = clientId?.Trim() ?? string.Empty;
        var trimmedSecret = clientSecret?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedId) || string.IsNullOrWhiteSpace(trimmedSecret)) {
            return string.Empty;
        }

        var payload = $"rpf-fflogs-v1:{trimmedId}\n{trimmedSecret}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    internal static bool HasMatchingVerifiedFingerprint(Configuration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.HasFFLogsCredentialsConfigured) {
            return false;
        }

        var currentFingerprint = ComputeCredentialFingerprint(configuration.FFLogsClientId, configuration.FFLogsClientSecret);
        var storedFingerprint = configuration.VerifiedFFLogsCredentialFingerprint ?? string.Empty;
        if (string.IsNullOrEmpty(currentFingerprint) || string.IsNullOrEmpty(storedFingerprint)) {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedFingerprint),
            Encoding.UTF8.GetBytes(currentFingerprint));
    }

    internal PluginActivationSnapshot GetSnapshot() {
        var nowUtc = DateTime.UtcNow;
        if (!_configuration.HasFFLogsCredentialsConfigured) {
            return new PluginActivationSnapshot(
                PluginActivationState.MissingCredentials,
                IsOperational: false,
                "FFLogs v2 Client ID and Client Secret are required to unlock the plugin.",
                DateTime.MinValue);
        }

        var fflogsStatus = _credentialStatusStore.FFLogs;
        var currentFingerprint = ComputeCredentialFingerprint(_configuration.FFLogsClientId, _configuration.FFLogsClientSecret);
        string lastAcceptedFingerprint;
        bool isVerificationInFlight;
        lock (_gate) {
            lastAcceptedFingerprint = _lastAcceptedFingerprint;
            isVerificationInFlight = _verificationInFlightCount > 0;
        }

        if (fflogsStatus.State == ApiCredentialValidationState.Rejected) {
            var rejectedMessage = string.IsNullOrWhiteSpace(fflogsStatus.Message)
                ? "FFLogs rejected the Client ID or Client Secret."
                : fflogsStatus.Message;
            return new PluginActivationSnapshot(
                PluginActivationState.Rejected,
                IsOperational: false,
                rejectedMessage,
                fflogsStatus.UpdatedAtUtc);
        }

        if (fflogsStatus.State == ApiCredentialValidationState.Accepted
            && !string.IsNullOrEmpty(currentFingerprint)
            && string.Equals(currentFingerprint, lastAcceptedFingerprint, StringComparison.Ordinal)) {
            var acceptedMessage = string.IsNullOrWhiteSpace(fflogsStatus.Message)
                ? "FFLogs v2 API credentials verified."
                : fflogsStatus.Message;
            return new PluginActivationSnapshot(
                PluginActivationState.Active,
                IsOperational: true,
                acceptedMessage,
                fflogsStatus.UpdatedAtUtc);
        }

        if (HasMatchingVerifiedFingerprint(_configuration)) {
            var activeMessage = isVerificationInFlight
                ? "Plugin active (verifying saved FFLogs credentials...)"
                : fflogsStatus.State == ApiCredentialValidationState.RequestFailed && !string.IsNullOrWhiteSpace(fflogsStatus.Message)
                    ? $"Plugin active (verified credentials; {fflogsStatus.Message})"
                    : "FFLogs v2 API credentials verified.";
            var updatedAt = fflogsStatus.UpdatedAtUtc != DateTime.MinValue ? fflogsStatus.UpdatedAtUtc : nowUtc;
            return new PluginActivationSnapshot(
                PluginActivationState.Active,
                IsOperational: true,
                activeMessage,
                updatedAt);
        }

        if (isVerificationInFlight) {
            return new PluginActivationSnapshot(
                PluginActivationState.Verifying,
                IsOperational: false,
                "Verifying FFLogs v2 API credentials...",
                nowUtc);
        }

        if (fflogsStatus.State == ApiCredentialValidationState.RequestFailed) {
            var failedMessage = string.IsNullOrWhiteSpace(fflogsStatus.Message)
                ? "FFLogs credential verification failed."
                : fflogsStatus.Message;
            return new PluginActivationSnapshot(
                PluginActivationState.VerificationFailed,
                IsOperational: false,
                failedMessage,
                fflogsStatus.UpdatedAtUtc);
        }

        return new PluginActivationSnapshot(
            PluginActivationState.MissingCredentials,
            IsOperational: false,
            "Click 'Verify & Unlock Plugin' to validate your FFLogs credentials.",
            fflogsStatus.UpdatedAtUtc);
    }

    internal void OnFFLogsCredentialStatusReported(ApiCredentialValidationState state, string message) {
        _credentialStatusStore.ReportFFLogs(state, message);

        lock (_gate) {
            if (state == ApiCredentialValidationState.Accepted) {
                var fingerprint = ComputeCredentialFingerprint(_configuration.FFLogsClientId, _configuration.FFLogsClientSecret);
                if (!string.IsNullOrEmpty(fingerprint)) {
                    _lastAcceptedFingerprint = fingerprint;
                    if (!string.Equals(_configuration.VerifiedFFLogsCredentialFingerprint, fingerprint, StringComparison.Ordinal)
                        || !_configuration.EnableFFLogsWorker) {
                        _configuration.VerifiedFFLogsCredentialFingerprint = fingerprint;
                        _configuration.EnableFFLogsWorker = true;
                        _saveConfiguration();
                    }
                }
            } else if (state == ApiCredentialValidationState.Rejected) {
                _lastAcceptedFingerprint = string.Empty;
                if (!string.IsNullOrEmpty(_configuration.VerifiedFFLogsCredentialFingerprint)) {
                    _configuration.VerifiedFFLogsCredentialFingerprint = string.Empty;
                    _saveConfiguration();
                }
            }
        }
    }

    internal async Task<OperationOutcome<bool>> VerifyAsync(
        Func<CancellationToken, Task<OperationOutcome<bool>>> verifyAsync,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(verifyAsync);

        await _verifyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _verificationInFlightCount);
        try {
            _credentialStatusStore.ResetFFLogs();
            return await verifyAsync(cancellationToken).ConfigureAwait(false);
        } finally {
            Interlocked.Decrement(ref _verificationInFlightCount);
            _verifyLock.Release();
        }
    }

    internal void ClearCredentialsAndLock() {
        lock (_gate) {
            _lastAcceptedFingerprint = string.Empty;
            _configuration.FFLogsClientId = string.Empty;
            _configuration.FFLogsClientSecret = string.Empty;
            _configuration.VerifiedFFLogsCredentialFingerprint = string.Empty;
            _credentialStatusStore.ResetFFLogs();
            _saveConfiguration();
        }
    }
}
