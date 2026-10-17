using System;

namespace RemotePartyFinderReborn;

internal enum ApiCredentialValidationState {
    NotChecked,
    Accepted,
    Rejected,
    RequestFailed,
}

internal sealed record ApiCredentialStatusSnapshot(
    ApiCredentialValidationState State,
    string Message,
    DateTime UpdatedAtUtc);

internal sealed class ApiCredentialStatusStore {
    private readonly object _gate = new();
    private ApiCredentialStatusSnapshot _fflogs = NotChecked("Credentials have not been checked yet.");
    private ApiCredentialStatusSnapshot _tomestone = NotChecked("API key has not been checked yet.");

    internal ApiCredentialStatusSnapshot FFLogs {
        get {
            lock (_gate) {
                return _fflogs;
            }
        }
    }

    internal ApiCredentialStatusSnapshot Tomestone {
        get {
            lock (_gate) {
                return _tomestone;
            }
        }
    }

    internal void ReportFFLogs(ApiCredentialValidationState state, string message) {
        lock (_gate) {
            _fflogs = CreateSnapshot(state, message);
        }
    }

    internal void ReportTomestone(ApiCredentialValidationState state, string message) {
        lock (_gate) {
            _tomestone = CreateSnapshot(state, message);
        }
    }

    internal void ResetFFLogs() {
        lock (_gate) {
            _fflogs = NotChecked("Credentials changed; waiting for the next FFLogs request.");
        }
    }

    internal void ResetTomestone() {
        lock (_gate) {
            _tomestone = NotChecked("API key changed; waiting for the next Tomestone request.");
        }
    }

    private static ApiCredentialStatusSnapshot CreateSnapshot(ApiCredentialValidationState state, string message) =>
        new(state, message ?? string.Empty, DateTime.UtcNow);

    private static ApiCredentialStatusSnapshot NotChecked(string message) =>
        new(ApiCredentialValidationState.NotChecked, message, DateTime.MinValue);
}
