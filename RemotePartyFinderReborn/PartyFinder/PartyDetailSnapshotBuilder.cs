using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

#nullable enable

namespace RemotePartyFinderReborn;

internal static class PartyDetailSnapshotBuilder {
    internal static unsafe bool TryBuildFromAgent(
        out UploadablePartyDetail snapshot,
        out PartyDetailDebugSnapshot? debugSnapshot
    ) {
        snapshot = new UploadablePartyDetail();
        debugSnapshot = null;

        var lookingForGroupAgent = AgentLookingForGroup.Instance();
        if (lookingForGroupAgent == null) {
            return false;
        }

        ref var viewedListing = ref lookingForGroupAgent->LastViewedListing;
        if (viewedListing.ListingId == 0) {
            return false;
        }

        if (!UploadableListing.TryNormalizeListingIdForUpload(viewedListing.ListingId, out var listingId)) {
            return false;
        }

        var effectiveParties = Math.Max(1, (int)viewedListing.NumberOfParties);
        var declaredSlots = Math.Max((int)viewedListing.TotalSlots, effectiveParties * 8);
        var slotCount = Math.Clamp(declaredSlots, 0, 48);
        if (slotCount <= 0) {
            return false;
        }

        var memberContentIds = new List<ulong>(slotCount);
        var memberJobs = new List<byte>(slotCount);
        var slotFlags = new List<string>(slotCount);
        var debugMembers = new List<PartyDetailDebugMember>(slotCount);
        var nonZeroMemberCount = 0;

        for (var slotIndex = 0; slotIndex < slotCount; slotIndex++) {
            var memberContentId = viewedListing.MemberContentIds[slotIndex];
            var memberJob = viewedListing.Jobs[slotIndex];
            var rawSlotFlag = Convert.ToUInt64(viewedListing.SlotFlags[slotIndex]);
            var formattedSlotFlag = $"0x{rawSlotFlag:X16}";

            memberContentIds.Add(memberContentId);
            memberJobs.Add(memberJob);
            slotFlags.Add(formattedSlotFlag);
            debugMembers.Add(new PartyDetailDebugMember(slotIndex, memberContentId, memberJob, formattedSlotFlag));
            if (memberContentId != 0) {
                nonZeroMemberCount++;
            }
        }

        snapshot = new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = viewedListing.LeaderContentId,
            LeaderName = lookingForGroupAgent->LastLeader.ToString(),
            HomeWorld = viewedListing.HomeWorld,
            MemberContentIds = memberContentIds,
            MemberJobs = memberJobs,
            SlotFlags = slotFlags,
        };

        var objectiveRaw = (byte)viewedListing.Objective;
        debugSnapshot = new PartyDetailDebugSnapshot {
            CapturedAtUtc = DateTime.UtcNow,
            RawListingId = viewedListing.ListingId,
            UploadListingId = listingId,
            LeaderAccountId = viewedListing.LeaderAccountId,
            LeaderContentId = viewedListing.LeaderContentId,
            LeaderName = lookingForGroupAgent->LastLeader.ToString(),
            Comment = lookingForGroupAgent->LastComment.ToString(),
            Category = (uint)viewedListing.Category,
            DutyId = viewedListing.DutyId,
            World = viewedListing.World,
            HomeWorld = viewedListing.HomeWorld,
            CurrentWorld = viewedListing.CurrentWorld,
            ObjectiveRaw = objectiveRaw,
            ObjectiveName = viewedListing.Objective.ToString(),
            BeginnerFriendly = viewedListing.BeginnerFriendly,
            CompletionStatusRaw = (byte)viewedListing.CompletionStatus,
            CompletionStatusName = viewedListing.CompletionStatus.ToString(),
            DutyFinderSettingRaw = (byte)viewedListing.DutyFinderSettingFlags,
            DutyFinderSettingName = viewedListing.DutyFinderSettingFlags.ToString(),
            LootRuleRaw = (byte)viewedListing.LootRule,
            LootRuleName = viewedListing.LootRule.ToString(),
            LastPatchHotfixTimestamp = viewedListing.LastPatchHotfixTimestamp,
            TimeLeft = viewedListing.TimeLeft,
            AvgItemLevel = viewedListing.AvgItemLv,
            LeaderClientLanguageRaw = (byte)viewedListing.LeaderClientLanguage,
            LanguageFlagsRaw = (byte)viewedListing.LanguageFlags,
            TotalSlots = viewedListing.TotalSlots,
            SlotsFilled = viewedListing.SlotsFilled,
            JoinConditionRaw = (byte)viewedListing.JoinConditionFlags,
            JoinConditionName = viewedListing.JoinConditionFlags.ToString(),
            IsAlliance = viewedListing.IsAlliance,
            NumberOfParties = viewedListing.NumberOfParties,
            NonZeroMemberCount = nonZeroMemberCount,
            Members = debugMembers,
        };

        return true;
    }
}
