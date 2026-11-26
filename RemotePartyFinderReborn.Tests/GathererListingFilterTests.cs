using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class GathererListingFilterTests {
    [Fact]
    public void Upload_filter_keeps_only_client_high_end_duty_listings() {
        Assert.True(Gatherer.ShouldUploadListing(CreateListing(clientHighEndDuty: true)));
        Assert.False(Gatherer.ShouldUploadListing(CreateListing(clientHighEndDuty: false)));
    }

    [Fact]
    public void ForwardListingLeaderForTesting_forwards_valid_64bit_leader_and_preserves_existing_account_id() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rpfr-gatherer-leader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var dbPath = Path.Combine(tempDir, "players.db");
            using var db = new PlayerLocalDatabase(dbPath);
            using var collector = new PlayerCollector(new Configuration(), db, isOperational: () => true);

            // Seed an overworld observation with a known AccountId, then mark it clean.
            const ulong leaderContentId = 0x0400_0000_0012_3456UL;
            db.UpsertObservedPlayers([
                new UploadablePlayer {
                    ContentId = leaderContentId,
                    Name = "Known Leader",
                    HomeWorld = 74,
                    CurrentWorld = 74,
                    AccountId = 888999UL,
                },
            ]);
            db.MarkBatchUploaded(db.TakePendingBatch(10));
            Assert.Equal(0, db.PendingCount);

            using var gatherer = new Gatherer(
                new Configuration(),
                isOperational: () => true,
                onLeaderObserved: collector.EnqueueObservedPlayer);

            // Invalid leader inputs are ignored.
            gatherer.ForwardListingLeaderForTesting(0UL, "Zero Id", 74);
            gatherer.ForwardListingLeaderForTesting((ulong)long.MaxValue + 1UL, "Overflow Id", 74);
            gatherer.ForwardListingLeaderForTesting(123UL, "   ", 74);
            gatherer.ForwardListingLeaderForTesting(123UL, "Invalid World", 0);
            gatherer.ForwardListingLeaderForTesting(123UL, "Invalid World", 1000);

            // Existing clean leader keeps AccountId and does not re-dirty.
            gatherer.ForwardListingLeaderForTesting(leaderContentId, "Known Leader", 74);

            // New 64-bit leader from PF listing is queued for upload.
            const ulong newLeaderId = 0x0400_0000_00AB_CDEFUL;
            gatherer.ForwardListingLeaderForTesting(newLeaderId, "  Fresh PF Leader  ", 79);

            // Wait for background PlayerObservationWriter to flush to PlayerLocalDatabase.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (db.PendingCount == 0 && DateTime.UtcNow < deadline) {
                Thread.Sleep(20);
            }

            var pending = db.TakePendingBatch(10);
            var forwarded = Assert.Single(pending);
            Assert.Equal(newLeaderId, forwarded.ContentId);
            Assert.Equal("Fresh PF Leader", forwarded.Name);
            Assert.Equal((ushort)79, forwarded.HomeWorld);
            Assert.Equal((ushort)79, forwarded.CurrentWorld);
            Assert.Equal(0UL, forwarded.AccountId);

            // Re-verify existing player kept its AccountId=888999.
            db.MarkAllPlayersDirty();
            var allPlayers = db.TakePendingBatch(10);
            var existing = Assert.Single(allPlayers, p => p.ContentId == leaderContentId);
            Assert.Equal(888999UL, existing.AccountId);
        } finally {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
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
