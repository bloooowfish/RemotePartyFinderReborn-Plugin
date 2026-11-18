#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace RemotePartyFinderReborn;

/// <summary>
/// Scoped circuit attempt lease that releases a claimed Half-Open probe slot on disposal
/// if the attempt finishes without recording success or failure.
/// </summary>
internal sealed class UploadAttemptLease : IDisposable
{
    private readonly UploadUrl _uploadUrl;
    private readonly long _halfOpenProbeToken;
    private int _settledOrDisposed;

    internal UploadAttemptLease(UploadUrl uploadUrl, long halfOpenProbeToken)
    {
        _uploadUrl = uploadUrl ?? throw new ArgumentNullException(nameof(uploadUrl));
        _halfOpenProbeToken = halfOpenProbeToken;
    }

    internal UploadUrl UploadUrl => _uploadUrl;
    internal bool ClaimedHalfOpenProbe => _halfOpenProbeToken != 0;

    internal void RecordSuccess()
    {
        Interlocked.Exchange(ref _settledOrDisposed, 1);
        UploadAttemptRecorder.RecordSuccess(_uploadUrl);
    }

    internal void RecordFailure(DateTime utcNow)
    {
        Interlocked.Exchange(ref _settledOrDisposed, 1);
        UploadAttemptRecorder.RecordFailure(_uploadUrl, utcNow);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _settledOrDisposed, 1) == 0)
        {
            _uploadUrl.ReleaseHalfOpenProbe(_halfOpenProbeToken);
        }
    }
}

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

    internal static bool TryBeginAttempt(
        UploadUrl uploadUrl,
        Configuration configuration,
        DateTime utcNow,
        [NotNullWhen(true)] out UploadAttemptLease? lease,
        TimeSpan? halfOpenProbeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(configuration);

        if (uploadUrl.TryBeginAttempt(
                configuration.CircuitBreakerFailureThreshold,
                configuration.CircuitBreakerBreakDurationMinutes,
                utcNow,
                out var halfOpenProbeToken,
                halfOpenProbeTimeout))
        {
            lease = new UploadAttemptLease(uploadUrl, halfOpenProbeToken);
            return true;
        }

        lease = null;
        return false;
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

    internal static bool PeekIsCircuitOpen(
        UploadUrl uploadUrl,
        Configuration configuration,
        DateTime utcNow,
        TimeSpan? halfOpenProbeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(configuration);

        return uploadUrl.PeekIsCircuitOpen(
            configuration.CircuitBreakerFailureThreshold,
            configuration.CircuitBreakerBreakDurationMinutes,
            utcNow,
            halfOpenProbeTimeout);
    }

    internal static CircuitStatus GetCircuitStatus(UploadUrl uploadUrl, Configuration configuration, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(configuration);

        var effectiveThreshold = Math.Max(1, configuration.CircuitBreakerFailureThreshold);
        var isOpen = PeekIsCircuitOpen(uploadUrl, configuration, utcNow);
        var remaining = TimeSpan.Zero;
        if (uploadUrl.FailureCount >= effectiveThreshold)
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
