using System;

namespace RemotePartyFinderReborn;

internal sealed class TimedWindowBudget {
    private int _remainingBudget;
    private DateTime _suppressUntilUtc = DateTime.MinValue;

    internal void Arm(int budget, DateTime suppressUntilUtc) {
        if (budget <= 0) {
            _remainingBudget = 0;
            _suppressUntilUtc = DateTime.MinValue;
            return;
        }

        _remainingBudget = budget;
        _suppressUntilUtc = suppressUntilUtc;
    }

    internal SuppressionConsumption TryConsume(DateTime nowUtc) {
        if (nowUtc > _suppressUntilUtc) {
            _remainingBudget = 0;
        }

        if (_remainingBudget > 0) {
            _remainingBudget--;
            return SuppressionConsumption.WindowBudget;
        }

        return SuppressionConsumption.None;
    }

    internal bool IsActive(DateTime nowUtc) {
        if (nowUtc > _suppressUntilUtc) {
            _remainingBudget = 0;
            return false;
        }

        return _suppressUntilUtc != DateTime.MinValue;
    }
}
