using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Gui.PartyFinder.Types;

namespace RemotePartyFinderReborn;

internal static class PartyFinderSlotFlagFormatter {
    private static readonly JobFlags[] OrderedJobFlags = Enum.GetValues<JobFlags>()
        .OrderBy(static flag => (ulong)flag)
        .ToArray();

    private static readonly JobRoleGroup[] RoleOrder = [
        JobRoleGroup.Tank,
        JobRoleGroup.Healer,
        JobRoleGroup.Dps,
        JobRoleGroup.Other,
    ];

    internal static string FormatAcceptingJobs(string rawSlotFlag) {
        if (!TryParseSlotFlag(rawSlotFlag, out var raw)) {
            return "Invalid";
        }

        var groups = new List<string>(4);
        var acceptedJobs = OrderedJobFlags
            .Where(job => (raw & (uint)job) != 0)
            .ToArray();

        foreach (var role in RoleOrder) {
            AppendGroup(groups, role, acceptedJobs);
        }

        return groups.Count == 0 ? "None" : string.Join("; ", groups);
    }

    private static bool TryParseSlotFlag(string rawSlotFlag, out ulong raw) {
        raw = 0;
        if (string.IsNullOrWhiteSpace(rawSlotFlag)) {
            return false;
        }

        var hex = rawSlotFlag.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            hex = hex[2..];
        }

        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw);
    }

    private static void AppendGroup(
        ICollection<string> groups,
        JobRoleGroup role,
        IReadOnlyCollection<JobFlags> jobs
    ) {
        var acceptedJobs = jobs
            .Where(job => RoleFor(job) == role)
            .Select(AbbreviationFor)
            .ToArray();

        if (acceptedJobs.Length == 0) {
            return;
        }

        groups.Add($"{LabelFor(role)}: {string.Join("/", acceptedJobs)}");
    }

    private static JobRoleGroup RoleFor(JobFlags job) {
        return job switch {
            JobFlags.Gladiator
                or JobFlags.Marauder
                or JobFlags.Paladin
                or JobFlags.Warrior
                or JobFlags.DarkKnight
                or JobFlags.Gunbreaker => JobRoleGroup.Tank,
            JobFlags.Conjurer
                or JobFlags.WhiteMage
                or JobFlags.Scholar
                or JobFlags.Astrologian
                or JobFlags.Sage => JobRoleGroup.Healer,
            JobFlags.Pugilist
                or JobFlags.Lancer
                or JobFlags.Archer
                or JobFlags.Thaumaturge
                or JobFlags.Monk
                or JobFlags.Dragoon
                or JobFlags.Bard
                or JobFlags.BlackMage
                or JobFlags.Arcanist
                or JobFlags.Summoner
                or JobFlags.Rogue
                or JobFlags.Ninja
                or JobFlags.Machinist
                or JobFlags.Samurai
                or JobFlags.RedMage
                or JobFlags.BlueMage
                or JobFlags.Dancer
                or JobFlags.Reaper
                or JobFlags.Viper
                or JobFlags.Pictomancer => JobRoleGroup.Dps,
            _ => JobRoleGroup.Other,
        };
    }

    private static string LabelFor(JobRoleGroup role) {
        return role switch {
            JobRoleGroup.Tank => "Tank",
            JobRoleGroup.Healer => "Healer",
            JobRoleGroup.Dps => "DPS",
            _ => "Other",
        };
    }

    private static string AbbreviationFor(JobFlags job) {
        return job switch {
            JobFlags.Gladiator => "GLD",
            JobFlags.Pugilist => "PGL",
            JobFlags.Marauder => "MRD",
            JobFlags.Lancer => "LNC",
            JobFlags.Archer => "ARC",
            JobFlags.Conjurer => "CNJ",
            JobFlags.Thaumaturge => "THM",
            JobFlags.Paladin => "PLD",
            JobFlags.Monk => "MNK",
            JobFlags.Warrior => "WAR",
            JobFlags.Dragoon => "DRG",
            JobFlags.Bard => "BRD",
            JobFlags.WhiteMage => "WHM",
            JobFlags.BlackMage => "BLM",
            JobFlags.Arcanist => "ACN",
            JobFlags.Summoner => "SMN",
            JobFlags.Scholar => "SCH",
            JobFlags.Rogue => "ROG",
            JobFlags.Ninja => "NIN",
            JobFlags.Machinist => "MCH",
            JobFlags.DarkKnight => "DRK",
            JobFlags.Astrologian => "AST",
            JobFlags.Samurai => "SAM",
            JobFlags.RedMage => "RDM",
            JobFlags.BlueMage => "BLU",
            JobFlags.Gunbreaker => "GNB",
            JobFlags.Dancer => "DNC",
            JobFlags.Reaper => "RPR",
            JobFlags.Sage => "SGE",
            JobFlags.Viper => "VPR",
            JobFlags.Pictomancer => "PCT",
            _ => job.ToString(),
        };
    }

    private enum JobRoleGroup {
        Tank,
        Healer,
        Dps,
        Other,
    }
}
