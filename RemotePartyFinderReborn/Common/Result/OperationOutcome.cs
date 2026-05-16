#nullable enable

namespace RemotePartyFinderReborn;

internal sealed record OperationOutcome<T>(
    bool Succeeded,
    T? Value,
    bool TransientFailure,
    string? ErrorMessage = null)
{
    internal static OperationOutcome<T> Success(T value)
        => new(true, value, false);

    internal static OperationOutcome<T> Failure(bool transientFailure, string? errorMessage = null)
        => new(false, default, transientFailure, errorMessage);
}
