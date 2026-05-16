using System;
using System.Collections.Generic;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class ExpiringBudgetGate<TKey> where TKey : notnull {
    private readonly Dictionary<TKey, Budget> _budgets = [];

    internal void Arm(TKey key, int budget, DateTime expiresAtUtc) {
        if (budget <= 0) {
            Clear(key);
            return;
        }

        _budgets[key] = new Budget(budget, expiresAtUtc);
    }

    internal bool TryConsume(TKey key, DateTime nowUtc) {
        if (!_budgets.TryGetValue(key, out var budget) || budget.Remaining <= 0) {
            return false;
        }

        if (nowUtc > budget.ExpiresAtUtc) {
            _budgets.Remove(key);
            return false;
        }

        if (budget.Remaining == 1) {
            _budgets.Remove(key);
            return true;
        }

        _budgets[key] = budget with {
            Remaining = budget.Remaining - 1,
        };
        return true;
    }

    internal void Clear(TKey key) {
        _budgets.Remove(key);
    }

    private readonly record struct Budget(int Remaining, DateTime ExpiresAtUtc);
}
