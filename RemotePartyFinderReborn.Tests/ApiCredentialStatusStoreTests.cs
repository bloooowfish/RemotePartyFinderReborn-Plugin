using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class ApiCredentialStatusStoreTests {
    [Fact]
    public void Provider_statuses_are_independent_and_reset_after_credential_changes() {
        var status = new ApiCredentialStatusStore();

        status.ReportFFLogs(ApiCredentialValidationState.Rejected, "FFLogs rejected credentials.");
        status.ReportTomestone(ApiCredentialValidationState.Accepted, "Tomestone accepted request.");

        Assert.Equal(ApiCredentialValidationState.Rejected, status.FFLogs.State);
        Assert.Equal(ApiCredentialValidationState.Accepted, status.Tomestone.State);

        status.ResetFFLogs();
        Assert.Equal(ApiCredentialValidationState.NotChecked, status.FFLogs.State);
        Assert.Equal(DateTime.MinValue, status.FFLogs.UpdatedAtUtc);
        Assert.Equal("Tomestone accepted request.", status.Tomestone.Message);
    }
}
