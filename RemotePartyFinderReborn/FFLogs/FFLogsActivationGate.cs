#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly TimeSpan DefaultShutdownWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly Configuration _configuration;
    private readonly ApiCredentialStatusStore _credentialStatusStore;
    private readonly Action _saveConfiguration;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _verifyLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly HashSet<Task> _activeVerificationTasks = new();

    private string _lastAcceptedFingerprint = string.Empty;
    private int _verificationInFlightCount;
    private int _shutdownSignaled;
    private int _disposed;

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

    internal void BeginShutdown() {
        if (Interlocked.Exchange(ref _shutdownSignaled, 1) != 0) {
            return;
        }

        try {
            _shutdownCts.Cancel();
        } catch (ObjectDisposedException) {
        }
    }

    internal void WaitForPendingVerifications(TimeSpan? timeout = null) {
        Task[] activeTasks;
        lock (_gate) {
            activeTasks = _activeVerificationTasks.ToArray();
        }

        if (activeTasks.Length == 0) {
            return;
        }

        try {
            Task.WhenAll(activeTasks).Wait(timeout ?? DefaultShutdownWaitTimeout);
        } catch (Exception) {
            // Cancellation/timeout during shutdown is expected; callers have already signaled shutdown.
        }
    }

    public void Dispose() {
        BeginShutdown();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        WaitForPendingVerifications(DefaultShutdownWaitTimeout);
        _verifyLock.Dispose();
        _shutdownCts.Dispose();
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

    internal Task<OperationOutcome<bool>> VerifyAsync(
        Func<CancellationToken, Task<OperationOutcome<bool>>> verifyAsync,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(verifyAsync);

        lock (_gate) {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _shutdownSignaled) != 0) {
                return cancellationToken.IsCancellationRequested
                    ? Task.FromCanceled<OperationOutcome<bool>>(cancellationToken)
                    : Task.FromResult(OperationOutcome<bool>.Failure(false, "Plugin is shutting down."));
            }

            var task = VerifyCoreAsync(verifyAsync, cancellationToken);
            if (!task.IsCompleted) {
                _activeVerificationTasks.Add(task);
                _ = task.ContinueWith(
                    static (completed, state) => {
                        var owner = (FFLogsActivationGate)state!;
                        lock (owner._gate) {
                            owner._activeVerificationTasks.Remove(completed);
                        }
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return task;
        }
    }

    private async Task<OperationOutcome<bool>> VerifyCoreAsync(
        Func<CancellationToken, Task<OperationOutcome<bool>>> verifyAsync,
        CancellationToken cancellationToken
    ) {
        CancellationTokenSource linkedCts;
        try {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        } catch (ObjectDisposedException) {
            return OperationOutcome<bool>.Failure(false, "Plugin is shutting down.");
        }

        using (linkedCts) {
            var lockAcquired = false;
            try {
                await _verifyLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                lockAcquired = true;
                Interlocked.Increment(ref _verificationInFlightCount);
                try {
                    _credentialStatusStore.ResetFFLogs();
                    return await verifyAsync(linkedCts.Token).ConfigureAwait(false);
                } finally {
                    Interlocked.Decrement(ref _verificationInFlightCount);
                }
            } catch (OperationCanceledException) when (Volatile.Read(ref _shutdownSignaled) != 0 && !cancellationToken.IsCancellationRequested) {
                return OperationOutcome<bool>.Failure(false, "Plugin is shutting down.");
            } catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) {
                return OperationOutcome<bool>.Failure(false, "Plugin is shutting down.");
            } finally {
                if (lockAcquired) {
                    try {
                        _verifyLock.Release();
                    } catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) {
                    }
                }
            }
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
