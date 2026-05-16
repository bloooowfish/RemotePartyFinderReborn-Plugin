using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class SuppressionPrimitiveTests {
    [Fact]
    public void Expiring_budget_gate_consumes_keyed_budget_until_empty() {
        var nowUtc = new DateTime(2026, 5, 12, 1, 0, 0, DateTimeKind.Utc);
        var gate = new ExpiringBudgetGate<ulong>();

        gate.Arm(9001UL, budget: 2, expiresAtUtc: nowUtc.AddSeconds(1));

        Assert.True(gate.TryConsume(9001UL, nowUtc));
        Assert.True(gate.TryConsume(9001UL, nowUtc));
        Assert.False(gate.TryConsume(9001UL, nowUtc));
    }

    [Fact]
    public void Expiring_budget_gate_expires_keyed_budget_by_clock() {
        var nowUtc = new DateTime(2026, 5, 12, 1, 5, 0, DateTimeKind.Utc);
        var gate = new ExpiringBudgetGate<ulong>();

        gate.Arm(9002UL, budget: 2, expiresAtUtc: nowUtc.AddSeconds(1));

        Assert.False(gate.TryConsume(9002UL, nowUtc.AddSeconds(2)));
        Assert.False(gate.TryConsume(9002UL, nowUtc));
    }

    [Fact]
    public void Timed_window_budget_consumes_until_budget_empty() {
        var nowUtc = new DateTime(2026, 5, 12, 1, 10, 0, DateTimeKind.Utc);
        var budget = new TimedWindowBudget();

        budget.Arm(budget: 2, suppressUntilUtc: nowUtc.AddSeconds(3));

        Assert.True(budget.IsActive(nowUtc));
        Assert.Equal(SuppressionConsumption.WindowBudget, budget.TryConsume(nowUtc));
        Assert.Equal(SuppressionConsumption.WindowBudget, budget.TryConsume(nowUtc));
        Assert.Equal(SuppressionConsumption.None, budget.TryConsume(nowUtc));
    }

    [Fact]
    public void Timed_window_budget_expires_and_resets_remaining_budget() {
        var nowUtc = new DateTime(2026, 5, 12, 1, 15, 0, DateTimeKind.Utc);
        var budget = new TimedWindowBudget();

        budget.Arm(budget: 2, suppressUntilUtc: nowUtc.AddSeconds(1));

        Assert.False(budget.IsActive(nowUtc.AddSeconds(2)));
        Assert.Equal(SuppressionConsumption.None, budget.TryConsume(nowUtc.AddSeconds(2)));
        Assert.Equal(SuppressionConsumption.None, budget.TryConsume(nowUtc));
    }

    [Fact]
    public void Timed_window_budget_zero_budget_arm_is_inactive() {
        var nowUtc = new DateTime(2026, 5, 12, 1, 20, 0, DateTimeKind.Utc);
        var budget = new TimedWindowBudget();

        budget.Arm(budget: 0, suppressUntilUtc: nowUtc.AddSeconds(3));

        Assert.False(budget.IsActive(nowUtc));
        Assert.Equal(SuppressionConsumption.None, budget.TryConsume(nowUtc));
    }
}
