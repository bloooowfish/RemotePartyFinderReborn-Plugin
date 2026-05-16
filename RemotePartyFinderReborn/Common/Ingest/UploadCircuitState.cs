using System;

namespace RemotePartyFinderReborn;

internal sealed class UploadCircuitState
{
    private readonly object _lock = new();
    private int _failureCount;
    private DateTime _lastFailureTime = DateTime.MinValue;

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
        }
    }

    internal void RecordFailure(DateTime utcNow)
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = utcNow;
        }
    }

    internal bool IsCircuitOpen(int failureThreshold, int breakDurationMinutes, DateTime utcNow)
    {
        lock (_lock)
        {
            if (_failureCount < failureThreshold)
            {
                return false;
            }

            var elapsedSinceFailure = utcNow - _lastFailureTime;
            return elapsedSinceFailure.TotalMinutes < breakDurationMinutes;
        }
    }
}
