using System;

namespace RemotePartyFinderReborn;

internal static class UploadAttemptRecorder
{
    internal readonly record struct CircuitStatus(
        int FailureCount,
        bool IsOpen,
        TimeSpan RemainingBreakDuration);

    internal static void RecordSuccess(UploadUrl uploadUrl)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        uploadUrl.ResetFailureState();
    }

    internal static void RecordFailure(UploadUrl uploadUrl, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        uploadUrl.RecordFailure(utcNow);
    }

    internal static bool IsCircuitOpen(UploadUrl uploadUrl, Configuration configuration, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(configuration);

        return uploadUrl.IsCircuitOpen(
            configuration.CircuitBreakerFailureThreshold,
            configuration.CircuitBreakerBreakDurationMinutes,
            utcNow);
    }

    internal static CircuitStatus GetCircuitStatus(UploadUrl uploadUrl, Configuration configuration, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(configuration);

        var isOpen = IsCircuitOpen(uploadUrl, configuration, utcNow);
        var remaining = TimeSpan.Zero;
        if (uploadUrl.FailureCount >= configuration.CircuitBreakerFailureThreshold)
        {
            var breakDuration = TimeSpan.FromMinutes(configuration.CircuitBreakerBreakDurationMinutes);
            var elapsed = utcNow - uploadUrl.LastFailureTime;
            remaining = breakDuration - elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }
        }

        return new CircuitStatus(uploadUrl.FailureCount, isOpen, remaining);
    }
}
