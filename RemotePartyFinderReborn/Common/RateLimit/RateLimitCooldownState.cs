using System;

namespace RemotePartyFinderReborn;

internal sealed class RateLimitCooldownState
{
    private readonly object _lock = new();
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private DateTime _lastSkipLogUtc = DateTime.MinValue;

    internal DateTime CooldownUntilUtc
    {
        get
        {
            lock (_lock)
            {
                return _cooldownUntilUtc;
            }
        }
    }

    internal void Activate(DateTime nowUtc, TimeSpan duration)
    {
        lock (_lock)
        {
            var proposedCooldownUntilUtc = nowUtc.Add(duration);
            if (proposedCooldownUntilUtc > _cooldownUntilUtc)
            {
                _cooldownUntilUtc = proposedCooldownUntilUtc;
            }

            _lastSkipLogUtc = DateTime.MinValue;
        }
    }

    internal bool TryGetRemaining(DateTime nowUtc, out TimeSpan remaining)
    {
        lock (_lock)
        {
            if (_cooldownUntilUtc <= nowUtc)
            {
                remaining = TimeSpan.Zero;
                return false;
            }

            remaining = _cooldownUntilUtc - nowUtc;
            return true;
        }
    }

    internal void Reset()
    {
        lock (_lock)
        {
            _cooldownUntilUtc = DateTime.MinValue;
            _lastSkipLogUtc = DateTime.MinValue;
        }
    }

    internal bool ShouldLogSkip(DateTime nowUtc, TimeSpan logInterval)
    {
        lock (_lock)
        {
            if ((nowUtc - _lastSkipLogUtc) < logInterval)
            {
                return false;
            }

            _lastSkipLogUtc = nowUtc;
            return true;
        }
    }
}
