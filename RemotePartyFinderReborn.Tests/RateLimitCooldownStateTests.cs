using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class RateLimitCooldownStateTests
{
    [Fact]
    public void Cooldown_state_reports_remaining_until_expired()
    {
        var state = new RateLimitCooldownState();
        var now = new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc);

        state.Activate(now, TimeSpan.FromMinutes(10));

        Assert.Equal(now.AddMinutes(10), state.CooldownUntilUtc);
        Assert.True(state.TryGetRemaining(now.AddMinutes(3), out var remaining));
        Assert.Equal(TimeSpan.FromMinutes(7), remaining);
        Assert.False(state.TryGetRemaining(now.AddMinutes(10), out remaining));
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void Cooldown_state_activation_never_shortens_active_cooldown()
    {
        var state = new RateLimitCooldownState();
        var now = new DateTime(2026, 5, 12, 10, 30, 0, DateTimeKind.Utc);

        state.Activate(now, TimeSpan.FromMinutes(10));
        state.Activate(now.AddMinutes(2), TimeSpan.FromMinutes(1));

        Assert.Equal(now.AddMinutes(10), state.CooldownUntilUtc);

        state.Activate(now.AddMinutes(3), TimeSpan.FromMinutes(20));

        Assert.Equal(now.AddMinutes(23), state.CooldownUntilUtc);
    }

    [Fact]
    public void Cooldown_state_reset_clears_remaining_and_log_throttle()
    {
        var state = new RateLimitCooldownState();
        var now = new DateTime(2026, 5, 12, 11, 0, 0, DateTimeKind.Utc);

        state.Activate(now, TimeSpan.FromMinutes(10));
        Assert.True(state.ShouldLogSkip(now, TimeSpan.FromMinutes(1)));
        Assert.False(state.ShouldLogSkip(now.AddSeconds(30), TimeSpan.FromMinutes(1)));

        state.Reset();

        Assert.Equal(DateTime.MinValue, state.CooldownUntilUtc);
        Assert.False(state.TryGetRemaining(now.AddMinutes(1), out var remaining));
        Assert.Equal(TimeSpan.Zero, remaining);
        Assert.True(state.ShouldLogSkip(now.AddSeconds(30), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Cooldown_state_throttles_skip_logs_for_one_minute()
    {
        var state = new RateLimitCooldownState();
        var now = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(state.ShouldLogSkip(now, TimeSpan.FromMinutes(1)));
        Assert.False(state.ShouldLogSkip(now.AddSeconds(59), TimeSpan.FromMinutes(1)));
        Assert.True(state.ShouldLogSkip(now.AddMinutes(1), TimeSpan.FromMinutes(1)));
    }
}
