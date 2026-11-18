using System;
using System.Collections.Generic;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class ExpiringBudgetGate<TKey> where TKey : notnull {
    private readonly Dictionary<TKey, Budget> _budgets = [];
    private DateTime _latestNowUtc = DateTime.MinValue;

    internal int Count => _budgets.Count;

    internal void Arm(TKey key, int budget, DateTime expiresAtUtc, DateTime? nowUtc = null) {
        if (nowUtc.HasValue && nowUtc.Value > _latestNowUtc) {
            _latestNowUtc = nowUtc.Value;
        }

        var sweepCutoffUtc = nowUtc ?? (_latestNowUtc != DateTime.MinValue ? _latestNowUtc : (DateTime?)null);
        if (sweepCutoffUtc.HasValue) {
            SweepExpired(sweepCutoffUtc.Value);
        }

        if (budget <= 0) {
            Clear(key);
            return;
        }

        _budgets[key] = new Budget(budget, expiresAtUtc);
    }

    internal bool TryConsume(TKey key, DateTime nowUtc) {
        if (nowUtc > _latestNowUtc) {
            _latestNowUtc = nowUtc;
        }

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

    private void SweepExpired(DateTime nowUtc) {
        List<TKey>? expiredKeys = null;
        foreach (var (candidateKey, candidateBudget) in _budgets) {
            if (nowUtc > candidateBudget.ExpiresAtUtc) {
                (expiredKeys ??= []).Add(candidateKey);
            }
        }

        if (expiredKeys is null) {
            return;
        }

        foreach (var expiredKey in expiredKeys) {
            _budgets.Remove(expiredKey);
        }
    }

    internal void Clear(TKey key) {
        _budgets.Remove(key);
    }

    private readonly record struct Budget(int Remaining, DateTime ExpiresAtUtc);
}
