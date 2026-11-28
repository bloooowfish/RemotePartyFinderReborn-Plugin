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

    private const ulong StandardTanksMask = (ulong)(
        JobFlags.Paladin | JobFlags.Warrior | JobFlags.DarkKnight | JobFlags.Gunbreaker);
    private const ulong TankClassesMask = (ulong)(JobFlags.Gladiator | JobFlags.Marauder);

    private const ulong StandardHealersMask = (ulong)(
        JobFlags.WhiteMage | JobFlags.Scholar | JobFlags.Astrologian | JobFlags.Sage);
    private const ulong HealerClassesMask = (ulong)JobFlags.Conjurer;

    private const ulong StandardMeleeMask = (ulong)(
        JobFlags.Monk | JobFlags.Dragoon | JobFlags.Ninja | JobFlags.Samurai | JobFlags.Reaper | JobFlags.Viper);
    private const ulong MeleeClassesMask = (ulong)(JobFlags.Pugilist | JobFlags.Lancer | JobFlags.Rogue);

    private const ulong StandardPhysRangedMask = (ulong)(
        JobFlags.Bard | JobFlags.Machinist | JobFlags.Dancer);
    private const ulong PhysRangedClassesMask = (ulong)JobFlags.Archer;

    private const ulong StandardCastersMask = (ulong)(
        JobFlags.BlackMage | JobFlags.Summoner | JobFlags.RedMage | JobFlags.Pictomancer);
    private const ulong CasterClassesMask = (ulong)(JobFlags.Thaumaturge | JobFlags.Arcanist);

    private const ulong AllStandardDpsMask = StandardMeleeMask | StandardPhysRangedMask | StandardCastersMask;
    private const ulong AllStandardCombatMask = StandardTanksMask | StandardHealersMask | AllStandardDpsMask;
    private static readonly ulong AllKnownJobFlagsMask = OrderedJobFlags.Aggregate(0UL, static (acc, flag) => acc | (ulong)flag);

    private static readonly (JobFlags Job, JobFlags Class)[] JobBaseClassPairs = [
        (JobFlags.Paladin, JobFlags.Gladiator),
        (JobFlags.Warrior, JobFlags.Marauder),
        (JobFlags.WhiteMage, JobFlags.Conjurer),
        (JobFlags.Monk, JobFlags.Pugilist),
        (JobFlags.Dragoon, JobFlags.Lancer),
        (JobFlags.Ninja, JobFlags.Rogue),
        (JobFlags.Bard, JobFlags.Archer),
        (JobFlags.BlackMage, JobFlags.Thaumaturge),
        (JobFlags.Summoner, JobFlags.Arcanist),
        (JobFlags.Scholar, JobFlags.Arcanist),
    ];

    private static readonly JobRoleGroup[] RoleOrder = [
        JobRoleGroup.Tank,
        JobRoleGroup.Healer,
        JobRoleGroup.Dps,
        JobRoleGroup.Other,
    ];

    internal static string FormatAcceptingJobs(string rawSlotFlag, PfUiLanguage language = PfUiLanguage.English) {
        if (!TryParseSlotFlag(rawSlotFlag, out var raw)) {
            return "Invalid";
        }

        var groups = new List<string>(4);
        var acceptedJobs = OrderedJobFlags
            .Where(job => (raw & (ulong)job) != 0)
            .ToArray();

        foreach (var role in RoleOrder) {
            AppendGroup(groups, role, acceptedJobs, language);
        }

        return groups.Count == 0 ? PfUiText.RoleNone(language) : string.Join("; ", groups);
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
        IReadOnlyCollection<JobFlags> jobs,
        PfUiLanguage language
    ) {
        var acceptedJobs = jobs
            .Where(job => RoleFor(job) == role)
            .Select(AbbreviationFor)
            .ToArray();

        if (acceptedJobs.Length == 0) {
            return;
        }

        groups.Add($"{LabelFor(role, language)}: {string.Join("/", acceptedJobs)}");
    }

    internal static JobRoleGroup RoleFor(JobFlags job) {
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
                or JobFlags.Pictomancer
                or JobFlags.Beastmaster => JobRoleGroup.Dps,
            _ => JobRoleGroup.Other,
        };
    }

    internal static JobRoleFlags SummarizeRoles(ulong acceptingMask) {
        var roles = JobRoleFlags.None;
        foreach (var flag in OrderedJobFlags) {
            if ((acceptingMask & (ulong)flag) == 0) {
                continue;
            }

            roles |= RoleFor(flag) switch {
                JobRoleGroup.Tank => JobRoleFlags.Tank,
                JobRoleGroup.Healer => JobRoleFlags.Healer,
                JobRoleGroup.Dps => JobRoleFlags.Dps,
                _ => JobRoleFlags.None,
            };
        }

        return roles;
    }

    internal static JobFlags? TryMapClassJobId(uint classJobId) {
        return classJobId switch {
            1 => JobFlags.Gladiator,
            2 => JobFlags.Pugilist,
            3 => JobFlags.Marauder,
            4 => JobFlags.Lancer,
            5 => JobFlags.Archer,
            6 => JobFlags.Conjurer,
            7 => JobFlags.Thaumaturge,
            19 => JobFlags.Paladin,
            20 => JobFlags.Monk,
            21 => JobFlags.Warrior,
            22 => JobFlags.Dragoon,
            23 => JobFlags.Bard,
            24 => JobFlags.WhiteMage,
            25 => JobFlags.BlackMage,
            26 => JobFlags.Arcanist,
            27 => JobFlags.Summoner,
            28 => JobFlags.Scholar,
            29 => JobFlags.Rogue,
            30 => JobFlags.Ninja,
            31 => JobFlags.Machinist,
            32 => JobFlags.DarkKnight,
            33 => JobFlags.Astrologian,
            34 => JobFlags.Samurai,
            35 => JobFlags.RedMage,
            36 => JobFlags.BlueMage,
            37 => JobFlags.Gunbreaker,
            38 => JobFlags.Dancer,
            39 => JobFlags.Reaper,
            40 => JobFlags.Sage,
            41 => JobFlags.Viper,
            42 => JobFlags.Pictomancer,
            _ => null,
        };
    }

    internal static JobRoleGroup RoleForClassJobId(uint classJobId) {
        return TryMapClassJobId(classJobId) is { } flag ? RoleFor(flag) : JobRoleGroup.Other;
    }

    internal static ulong GetAcceptingMaskForClassJobId(uint classJobId) {
        return TryMapClassJobId(classJobId) is { } flag ? (ulong)flag : 0UL;
    }

    internal static string AbbreviationForClassJobId(uint classJobId) {
        return TryMapClassJobId(classJobId) is { } flag ? AbbreviationFor(flag) : $"J{classJobId}";
    }

    internal static string FormatRoleSummary(ulong acceptingMask, PfUiLanguage language = PfUiLanguage.English) {
        var roles = SummarizeRoles(acceptingMask);
        if (roles == JobRoleFlags.None) {
            return PfUiText.RoleEmpty(language);
        }

        if (roles == (JobRoleFlags.Tank | JobRoleFlags.Healer | JobRoleFlags.Dps)) {
            return PfUiText.RoleAllRoles(language);
        }

        var parts = new List<string>(3);
        if ((roles & JobRoleFlags.Tank) != 0) parts.Add(PfUiText.RoleTankGroup(language));
        if ((roles & JobRoleFlags.Healer) != 0) parts.Add(PfUiText.RoleHealerGroup(language));
        if ((roles & JobRoleFlags.Dps) != 0) parts.Add(PfUiText.RoleDpsGroup(language));
        return string.Join(" / ", parts);
    }

    internal static string FormatCompactAcceptingJobs(ulong acceptingMask, PfUiLanguage language = PfUiLanguage.English) {
        if (acceptingMask == 0
            || acceptingMask == AllKnownJobFlagsMask
            || (acceptingMask & (AllStandardCombatMask | (ulong)JobFlags.BlueMage)) == (AllStandardCombatMask | (ulong)JobFlags.BlueMage)) {
            return PfUiText.RoleAnyJob(language);
        }

        if ((acceptingMask & AllStandardCombatMask) == AllStandardCombatMask) {
            var hasBlu = (acceptingMask & (ulong)JobFlags.BlueMage) != 0;
            var hasBst = (acceptingMask & (ulong)JobFlags.Beastmaster) != 0;
            var allCombat = PfUiText.RoleAllCombat(language);
            if (hasBlu && !hasBst) {
                return $"{allCombat} (+BLU)";
            }

            if (hasBst && !hasBlu) {
                return $"{allCombat} (+BST)";
            }

            return allCombat;
        }

        var remaining = StripRedundantBaseClasses(acceptingMask);
        var tokens = new List<string>(6);

        if ((remaining & StandardTanksMask) == StandardTanksMask) {
            tokens.Add(PfUiText.RoleAllTanks(language));
            remaining &= ~(StandardTanksMask | TankClassesMask);
        }

        if ((remaining & StandardHealersMask) == StandardHealersMask) {
            tokens.Add(PfUiText.RoleAllHealers(language));
            remaining &= ~(StandardHealersMask | HealerClassesMask);
        }

        if ((remaining & AllStandardDpsMask) == AllStandardDpsMask) {
            var dpsSuffix = new List<string>(2);
            if ((remaining & (ulong)JobFlags.BlueMage) != 0) {
                dpsSuffix.Add("BLU");
                remaining &= ~(ulong)JobFlags.BlueMage;
            }
            if ((remaining & (ulong)JobFlags.Beastmaster) != 0) {
                dpsSuffix.Add("BST");
                remaining &= ~(ulong)JobFlags.Beastmaster;
            }

            var allDps = PfUiText.RoleAllDps(language);
            tokens.Add(dpsSuffix.Count == 0 ? allDps : $"{allDps} (+{string.Join("/", dpsSuffix)})");
            remaining &= ~(AllStandardDpsMask | MeleeClassesMask | PhysRangedClassesMask | CasterClassesMask);
        } else {
            if ((remaining & StandardMeleeMask) == StandardMeleeMask) {
                tokens.Add(PfUiText.RoleMelee(language));
                remaining &= ~(StandardMeleeMask | MeleeClassesMask);
            }

            if ((remaining & StandardPhysRangedMask) == StandardPhysRangedMask) {
                tokens.Add(PfUiText.RolePhysRanged(language));
                remaining &= ~(StandardPhysRangedMask | PhysRangedClassesMask);
            }

            if ((remaining & StandardCastersMask) == StandardCastersMask) {
                tokens.Add(PfUiText.RoleCasters(language));
                remaining &= ~(StandardCastersMask | CasterClassesMask);
            }
        }

        foreach (var flag in OrderedJobFlags) {
            if ((remaining & (ulong)flag) != 0) {
                tokens.Add(AbbreviationFor(flag));
            }
        }

        return tokens.Count == 0 ? PfUiText.RoleNone(language) : string.Join(" / ", tokens);
    }

    private static ulong StripRedundantBaseClasses(ulong mask) {
        var cleaned = mask;
        foreach (var (job, baseClass) in JobBaseClassPairs) {
            if ((cleaned & (ulong)job) != 0) {
                cleaned &= ~(ulong)baseClass;
            }
        }

        return cleaned;
    }

    private static string LabelFor(JobRoleGroup role, PfUiLanguage language) {
        return role switch {
            JobRoleGroup.Tank => PfUiText.RoleTankGroup(language),
            JobRoleGroup.Healer => PfUiText.RoleHealerGroup(language),
            JobRoleGroup.Dps => PfUiText.RoleDpsGroup(language),
            _ => PfUiText.RoleOtherGroup(language),
        };
    }

    internal static string AbbreviationFor(JobFlags job) {
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
            JobFlags.Beastmaster => "BST",
            _ => job.ToString(),
        };
    }

    internal enum JobRoleGroup {
        Tank,
        Healer,
        Dps,
        Other,
    }

    [Flags]
    internal enum JobRoleFlags {
        None = 0,
        Tank = 1 << 0,
        Healer = 1 << 1,
        Dps = 1 << 2,
    }
}
