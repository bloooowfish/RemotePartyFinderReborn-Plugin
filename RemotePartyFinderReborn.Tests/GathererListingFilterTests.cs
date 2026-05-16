using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class GathererListingFilterTests {
    [Fact]
    public void Upload_filter_keeps_only_client_high_end_duty_listings() {
        Assert.True(Gatherer.ShouldUploadListing(CreateListing(clientHighEndDuty: true)));
        Assert.False(Gatherer.ShouldUploadListing(CreateListing(clientHighEndDuty: false)));
    }

    private static UploadableListing CreateListing(bool clientHighEndDuty) {
        var listing = (UploadableListing)RuntimeHelpers.GetUninitializedObject(typeof(UploadableListing));
        var field = typeof(UploadableListing).GetField(
            "<ClientHighEndDuty>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(listing, clientHighEndDuty);
        return listing;
    }
}
