using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyDetailEnqueueTests {
    [Fact]
    public void BuildDetailEnqueueSet_includes_leader_and_nonzero_members() {
        var payload = new UploadablePartyDetail {
            LeaderContentId = 111UL,
            MemberContentIds = [0UL, 222UL, 333UL, 222UL, 0UL],
        };

        var contentIds = PartyDetailContentIdEnrichment.BuildContentIdsForResolve(payload);

        Assert.Equal(new HashSet<ulong> { 111UL, 222UL, 333UL }, contentIds.ToHashSet());
    }

    [Fact]
    public void BuildDetailEnqueueSet_includes_alliance_members_without_truncating_to_eight() {
        var memberContentIds = Enumerable.Range(0, 24)
            .Select(index => 10_000UL + (ulong)index)
            .ToList();

        var payload = new UploadablePartyDetail {
            LeaderContentId = 10_000UL,
            MemberContentIds = memberContentIds,
        };

        var contentIds = PartyDetailContentIdEnrichment.BuildContentIdsForResolve(payload);

        Assert.DoesNotContain(0UL, contentIds);
        Assert.Contains(10_008UL, contentIds);
        Assert.Contains(10_023UL, contentIds);
        Assert.Equal(24, contentIds.Count);
    }

    [Fact]
    public void DetailUpload_path_continues_when_enrichment_is_disabled() {
        var payload = new UploadablePartyDetail {
            LeaderContentId = 111UL,
            MemberContentIds = [0UL, 222UL],
        };

        List<ulong>? captured = null;
        var exception = Record.Exception(() => {
            var enqueued = PartyDetailContentIdEnrichment.TryEnqueueForResolve(payload, contentIds => {
                captured = contentIds.ToList();
                throw new InvalidOperationException("enrichment disabled");
            });

            Assert.False(enqueued);
        });

        Assert.Null(exception);
        Assert.Equal([111UL, 222UL], captured);
    }

    [Fact]
    public void SnapshotValidity_requires_nonzero_member_content() {
        var payload = new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 111UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 77,
            MemberContentIds = [0UL, 0UL],
            MemberJobs = [19, 24],
            SlotFlags = ["0x0", "0x0"],
        };

        Assert.False(PartyDetailUploadPolicy.IsSnapshotReadyForEnqueue(payload));
    }

    [Theory]
    [InlineData((ushort)0, false)]
    [InlineData((ushort)1, true)]
    [InlineData((ushort)77, true)]
    [InlineData((ushort)999, true)]
    [InlineData((ushort)1000, false)]
    [InlineData((ushort)65535, false)]
    public void SnapshotValidity_requires_valid_created_world(ushort createdWorld, bool expectedValid) {
        var payload = new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 111UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = createdWorld,
            MemberContentIds = [111UL, 0UL],
            MemberJobs = [19, 24],
            SlotFlags = ["0x0", "0x0"],
        };

        Assert.Equal(expectedValid, PartyDetailUploadPolicy.IsSnapshotReadyForEnqueue(payload));
    }

    [Fact]
    public void ComputeFingerprint_includes_leader_and_world_metadata() {
        var baseline = new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 111UL,
            LeaderName = "Leader Alpha",
            HomeWorld = 77,
            CreatedWorld = 74,
            MemberContentIds = [111UL, 222UL],
            MemberJobs = [19, 24],
            SlotFlags = ["0x1", "0x2"],
        };

        var baselineHash = PartyDetailUploadPolicy.ComputeFingerprint(baseline);

        Assert.NotEqual(baselineHash, PartyDetailUploadPolicy.ComputeFingerprint(new UploadablePartyDetail {
            ListingId = baseline.ListingId,
            LeaderContentId = 999UL,
            LeaderName = baseline.LeaderName,
            HomeWorld = baseline.HomeWorld,
            CreatedWorld = baseline.CreatedWorld,
            MemberContentIds = baseline.MemberContentIds,
            MemberJobs = baseline.MemberJobs,
            SlotFlags = baseline.SlotFlags,
        }));
        Assert.NotEqual(baselineHash, PartyDetailUploadPolicy.ComputeFingerprint(new UploadablePartyDetail {
            ListingId = baseline.ListingId,
            LeaderContentId = baseline.LeaderContentId,
            LeaderName = "Leader Beta",
            HomeWorld = baseline.HomeWorld,
            CreatedWorld = baseline.CreatedWorld,
            MemberContentIds = baseline.MemberContentIds,
            MemberJobs = baseline.MemberJobs,
            SlotFlags = baseline.SlotFlags,
        }));
        Assert.NotEqual(baselineHash, PartyDetailUploadPolicy.ComputeFingerprint(new UploadablePartyDetail {
            ListingId = baseline.ListingId,
            LeaderContentId = baseline.LeaderContentId,
            LeaderName = baseline.LeaderName,
            HomeWorld = 78,
            CreatedWorld = baseline.CreatedWorld,
            MemberContentIds = baseline.MemberContentIds,
            MemberJobs = baseline.MemberJobs,
            SlotFlags = baseline.SlotFlags,
        }));
        Assert.NotEqual(baselineHash, PartyDetailUploadPolicy.ComputeFingerprint(new UploadablePartyDetail {
            ListingId = baseline.ListingId,
            LeaderContentId = baseline.LeaderContentId,
            LeaderName = baseline.LeaderName,
            HomeWorld = baseline.HomeWorld,
            CreatedWorld = 75,
            MemberContentIds = baseline.MemberContentIds,
            MemberJobs = baseline.MemberJobs,
            SlotFlags = baseline.SlotFlags,
        }));
    }
}
