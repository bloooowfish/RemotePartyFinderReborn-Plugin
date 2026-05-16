using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class ExpiringUiStatusTests {
    [Fact]
    public void Expiring_status_clears_after_duration() {
        var status = new ExpiringUiStatus();
        var nowUtc = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

        status.Set("Endpoint already exists.", nowUtc, TimeSpan.FromSeconds(5));

        Assert.Equal("Endpoint already exists.", status.Current(nowUtc.AddSeconds(4)));
        Assert.Equal(string.Empty, status.Current(nowUtc.AddSeconds(5)));
        Assert.Equal(string.Empty, status.Current(nowUtc.AddSeconds(6)));
    }
}
