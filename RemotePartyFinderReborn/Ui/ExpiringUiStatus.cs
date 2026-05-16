using System;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class ExpiringUiStatus {
    private string _message = string.Empty;
    private DateTime _expiresAtUtc = DateTime.MinValue;

    internal void Set(string message, DateTime nowUtc, TimeSpan duration) {
        _message = message ?? string.Empty;
        _expiresAtUtc = string.IsNullOrEmpty(_message)
            ? DateTime.MinValue
            : nowUtc.Add(duration);
    }

    internal string Current(DateTime nowUtc) {
        if (string.IsNullOrEmpty(_message)) {
            return string.Empty;
        }

        if (nowUtc < _expiresAtUtc) {
            return _message;
        }

        _message = string.Empty;
        _expiresAtUtc = DateTime.MinValue;
        return string.Empty;
    }
}
