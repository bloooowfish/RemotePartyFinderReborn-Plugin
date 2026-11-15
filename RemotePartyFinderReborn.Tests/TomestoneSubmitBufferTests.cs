using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneSubmitBufferTests
{
    [Fact]
    public void Buffer_keys_by_content_zone_difficulty_and_encounter_preserving_sibling_encounters()
    {
        var buffer = new TomestoneSubmitBuffer();

        buffer.QueueSubmitResults([
            CreateResult(contentId: 1001, zoneId: 73, encounterId: 101, bossPct: 40.0),
            CreateResult(contentId: 1001, zoneId: 73, encounterId: 102, bossPct: 25.0),
            CreateResult(contentId: 1001, zoneId: 73, encounterId: 101, bossPct: 12.5),
        ]);

        Assert.True(buffer.HasPendingResults);
        Assert.Equal(2, buffer.PendingCount);

        var batch = buffer.BuildSubmitBatch([]);
        Assert.Equal(2, batch.Count);
        Assert.False(buffer.HasPendingResults);
        Assert.Equal(12.5, Assert.Single(batch, r => r.EncounterId == 101).BossPercentages[101]);
        Assert.Equal(25.0, Assert.Single(batch, r => r.EncounterId == 102).BossPercentages[102]);
    }

    [Fact]
    public void Requeue_and_drain_roundtrip_pending_results()
    {
        var buffer = new TomestoneSubmitBuffer();
        var item = CreateResult(contentId: 2002, zoneId: 65, encounterId: 1079, bossPct: null);

        buffer.RequeueSubmitBatch([item]);
        Assert.Equal(1, buffer.PendingCount);

        var drained = buffer.DrainPendingResults();
        Assert.Same(item, Assert.Single(drained));
        Assert.False(buffer.HasPendingResults);
        Assert.Empty(buffer.DrainPendingResults());
    }

    private static TomestoneResult CreateResult(ulong contentId, uint zoneId, uint encounterId, double? bossPct)
    {
        var bosses = new Dictionary<int, double>();
        if (bossPct.HasValue)
        {
            bosses[(int)encounterId] = bossPct.Value;
        }

        return new TomestoneResult
        {
            ContentId = contentId,
            ZoneId = zoneId,
            DifficultyId = 0,
            EncounterId = encounterId,
            BossPercentages = bosses,
            TomestoneQueriedEncounters = [(int)encounterId],
            MatchedServer = "Tonberry",
            SourceKind = "tomestone_api",
            LeaseToken = $"lease-{contentId}-{encounterId}",
        };
    }
}
