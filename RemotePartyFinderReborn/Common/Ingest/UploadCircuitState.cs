#nullable enable

using System;

namespace RemotePartyFinderReborn;

internal sealed class UploadCircuitState
{
    internal static readonly TimeSpan DefaultHalfOpenProbeTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan DefaultRouteNotFoundCooldown = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private int _failureCount;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private DateTime _routeNotFoundCooldownUntilUtc = DateTime.MinValue;
    private bool _halfOpenProbeInFlight;
    private DateTime _halfOpenProbeStartedAtUtc = DateTime.MinValue;
    private long _halfOpenProbeToken;

    internal int FailureCount
    {
        get
        {
            lock (_lock)
            {
                return _failureCount;
            }
        }
        set
        {
            lock (_lock)
            {
                _failureCount = value;
            }
        }
    }

    internal DateTime LastFailureTime
    {
        get
        {
            lock (_lock)
            {
                return _lastFailureTime;
            }
        }
        set
        {
            lock (_lock)
            {
                _lastFailureTime = value;
            }
        }
    }

    internal void ResetFailureState()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _lastFailureTime = DateTime.MinValue;
            _routeNotFoundCooldownUntilUtc = DateTime.MinValue;
            _halfOpenProbeInFlight = false;
            _halfOpenProbeStartedAtUtc = DateTime.MinValue;
            _halfOpenProbeToken++;
        }
    }

    internal void RecordFailure(DateTime utcNow)
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = utcNow;
            _halfOpenProbeInFlight = false;
            _halfOpenProbeStartedAtUtc = DateTime.MinValue;
            _halfOpenProbeToken++;
        }
    }

    internal void RecordRouteNotFoundFailure(DateTime utcNow, TimeSpan? cooldownDuration = null)
    {
        var effectiveCooldown = ResolveRouteNotFoundCooldown(cooldownDuration);
        var cooldownUntilUtc = utcNow + effectiveCooldown;
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = utcNow;
            if (cooldownUntilUtc > _routeNotFoundCooldownUntilUtc)
            {
                _routeNotFoundCooldownUntilUtc = cooldownUntilUtc;
            }
            _halfOpenProbeInFlight = false;
            _halfOpenProbeStartedAtUtc = DateTime.MinValue;
            _halfOpenProbeToken++;
        }
    }

    internal bool TryBeginAttempt(
        int failureThreshold,
        int breakDurationMinutes,
        DateTime utcNow,
        out long halfOpenProbeToken,
        TimeSpan? halfOpenProbeTimeout = null)
    {
        var effectiveThreshold = Math.Max(1, failureThreshold);
        var effectiveProbeTimeout = ResolveProbeTimeout(halfOpenProbeTimeout);
        lock (_lock)
        {
            if (utcNow < _routeNotFoundCooldownUntilUtc)
            {
                halfOpenProbeToken = 0;
                return false;
            }

            if (_failureCount < effectiveThreshold)
            {
                halfOpenProbeToken = 0;
                return true;
            }

            var elapsedSinceFailure = utcNow - _lastFailureTime;
            if (elapsedSinceFailure.TotalMinutes < breakDurationMinutes)
            {
                halfOpenProbeToken = 0;
                return false;
            }

            if (_halfOpenProbeInFlight && !IsHalfOpenProbeStaleLocked(utcNow, effectiveProbeTimeout))
            {
                halfOpenProbeToken = 0;
                return false;
            }

            _halfOpenProbeInFlight = true;
            _halfOpenProbeStartedAtUtc = utcNow;
            _halfOpenProbeToken++;
            halfOpenProbeToken = _halfOpenProbeToken;
            return true;
        }
    }

    internal void ReleaseHalfOpenProbe(long halfOpenProbeToken)
    {
        if (halfOpenProbeToken == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_halfOpenProbeInFlight && _halfOpenProbeToken == halfOpenProbeToken)
            {
                _halfOpenProbeInFlight = false;
                _halfOpenProbeStartedAtUtc = DateTime.MinValue;
                _halfOpenProbeToken++;
            }
        }
    }

    internal bool IsCircuitOpen(int failureThreshold, int breakDurationMinutes, DateTime utcNow)
    {
        return !TryBeginAttempt(failureThreshold, breakDurationMinutes, utcNow, out _);
    }

    internal bool PeekIsCircuitOpen(
        int failureThreshold,
        int breakDurationMinutes,
        DateTime utcNow,
        TimeSpan? halfOpenProbeTimeout = null)
    {
        var effectiveThreshold = Math.Max(1, failureThreshold);
        var effectiveProbeTimeout = ResolveProbeTimeout(halfOpenProbeTimeout);
        lock (_lock)
        {
            if (utcNow < _routeNotFoundCooldownUntilUtc)
            {
                return true;
            }

            if (_failureCount < effectiveThreshold)
            {
                return false;
            }

            var elapsedSinceFailure = utcNow - _lastFailureTime;
            if (elapsedSinceFailure.TotalMinutes < breakDurationMinutes)
            {
                return true;
            }

            return _halfOpenProbeInFlight && !IsHalfOpenProbeStaleLocked(utcNow, effectiveProbeTimeout);
        }
    }

    internal TimeSpan GetRemainingBreakDuration(int failureThreshold, int breakDurationMinutes, DateTime utcNow)
    {
        var effectiveThreshold = Math.Max(1, failureThreshold);
        lock (_lock)
        {
            var remaining = TimeSpan.Zero;
            if (_routeNotFoundCooldownUntilUtc > utcNow)
            {
                remaining = _routeNotFoundCooldownUntilUtc - utcNow;
            }

            if (_failureCount >= effectiveThreshold)
            {
                var breakDuration = TimeSpan.FromMinutes(breakDurationMinutes);
                var elapsed = utcNow - _lastFailureTime;
                var standardRemaining = breakDuration - elapsed;
                if (standardRemaining > remaining)
                {
                    remaining = standardRemaining;
                }
            }

            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private bool IsHalfOpenProbeStaleLocked(DateTime utcNow, TimeSpan effectiveProbeTimeout)
    {
        return utcNow - _halfOpenProbeStartedAtUtc >= effectiveProbeTimeout;
    }

    private static TimeSpan ResolveProbeTimeout(TimeSpan? halfOpenProbeTimeout)
    {
        return halfOpenProbeTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultHalfOpenProbeTimeout;
    }

    private static TimeSpan ResolveRouteNotFoundCooldown(TimeSpan? cooldownDuration)
    {
        return cooldownDuration is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultRouteNotFoundCooldown;
    }
}
