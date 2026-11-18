using System;

namespace RemotePartyFinderReborn;

internal sealed class UploadCircuitState
{
    private readonly object _lock = new();
    private int _failureCount;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private bool _halfOpenProbeInFlight;

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
            _halfOpenProbeInFlight = false;
        }
    }

    internal void RecordFailure(DateTime utcNow)
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = utcNow;
            _halfOpenProbeInFlight = false;
        }
    }

    internal bool IsCircuitOpen(int failureThreshold, int breakDurationMinutes, DateTime utcNow)
    {
        var effectiveThreshold = Math.Max(1, failureThreshold);
        lock (_lock)
        {
            if (_failureCount < effectiveThreshold)
            {
                return false;
            }

            var elapsedSinceFailure = utcNow - _lastFailureTime;
            if (elapsedSinceFailure.TotalMinutes < breakDurationMinutes)
            {
                return true;
            }

            if (_halfOpenProbeInFlight)
            {
                return true;
            }

            _halfOpenProbeInFlight = true;
            return false;
        }
    }

    internal bool PeekIsCircuitOpen(int failureThreshold, int breakDurationMinutes, DateTime utcNow)
    {
        var effectiveThreshold = Math.Max(1, failureThreshold);
        lock (_lock)
        {
            if (_failureCount < effectiveThreshold)
            {
                return false;
            }

            var elapsedSinceFailure = utcNow - _lastFailureTime;
            return _halfOpenProbeInFlight || elapsedSinceFailure.TotalMinutes < breakDurationMinutes;
        }
    }
}
