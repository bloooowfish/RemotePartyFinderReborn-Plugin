using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn.Tomestone;

public static class TomestoneEncounterMapping
{
    private static readonly IReadOnlyDictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams> UltimateMappings =
        new Dictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams>
        {
            [(59, 1073)] = new("stormblood", "ultimates", "the-unending-coil-of-bahamut-ultimate", TargetCanonicalNames: ["the-unending-coil-of-bahamut-ultimate"]),
            [(59, 1074)] = new("stormblood", "ultimates", "the-weapons-refrain-ultimate", TargetCanonicalNames: ["the-weapons-refrain-ultimate"]),
            [(59, 1075)] = new("shadowbringers", "ultimates", "the-epic-of-alexander-ultimate", TargetCanonicalNames: ["the-epic-of-alexander-ultimate"]),
            [(59, 1076)] = new("endwalker", "ultimates", "dragonsongs-reprise-ultimate", TargetCanonicalNames: ["dragonsongs-reprise-ultimate"]),
            [(59, 1077)] = new("endwalker", "ultimates", "the-omega-protocol-ultimate", TargetCanonicalNames: ["the-omega-protocol-ultimate"]),
            [(65, 1079)] = new("dawntrail", "ultimates", "futures-rewritten-ultimate", TargetCanonicalNames: ["futures-rewritten-ultimate"]),
            [(76, 1085)] = new("dawntrail", "ultimates", "dancing-mad-ultimate", TargetCanonicalNames: ["dancing-mad-ultimate"]),
        };

    private static readonly IReadOnlyDictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams> ProgressionMappings =
        UltimateMappings
            .Concat(new Dictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams>
            {
                [(62, 93)] = new("dawntrail", "aac-light-heavyweight-savage", "black-cat", TomestoneProgressKind.BossPercentage, ["black-cat"]),
                [(62, 94)] = new("dawntrail", "aac-light-heavyweight-savage", "honey-b-lovely", TomestoneProgressKind.BossPercentage, ["honey-b-lovely"]),
                [(62, 95)] = new("dawntrail", "aac-light-heavyweight-savage", "brute-bomber", TomestoneProgressKind.BossPercentage, ["brute-bomber"]),
                [(62, 96)] = new("dawntrail", "aac-light-heavyweight-savage", "wicked-thunder", TomestoneProgressKind.BossPercentage, ["wicked-thunder"]),
                [(68, 97)] = new("dawntrail", "aac-cruiserweight-savage", "dancing-green", TomestoneProgressKind.BossPercentage, ["dancing-green"]),
                [(68, 98)] = new("dawntrail", "aac-cruiserweight-savage", "sugar-riot", TomestoneProgressKind.BossPercentage, ["sugar-riot"]),
                [(68, 99)] = new("dawntrail", "aac-cruiserweight-savage", "brute-abombinator", TomestoneProgressKind.BossPercentage, ["brute-abombinator"]),
                [(68, 100)] = new("dawntrail", "aac-cruiserweight-savage", "howling-blade", TomestoneProgressKind.BossPercentage, ["howling-blade"]),
                [(72, 1084)] = new("dawntrail", "trials-extreme", "enuo", TomestoneProgressKind.BossPercentage, ["enuo"]),
                [(73, 101)] = new(
                    "dawntrail",
                    "aac-heavyweight-savage",
                    "vamp-fatale",
                    TomestoneProgressKind.BossPercentage,
                    ["vamp-fatale"]),
                [(73, 102)] = new(
                    "dawntrail",
                    "aac-heavyweight-savage",
                    "red-hot-deep-blue",
                    TomestoneProgressKind.BossPercentage,
                    ["red-hot-deep-blue"]),
                [(73, 103)] = new(
                    "dawntrail",
                    "aac-heavyweight-savage",
                    "the-tyrant",
                    TomestoneProgressKind.BossPercentage,
                    ["the-tyrant"]),
                [(73, 104)] = new(
                    "dawntrail",
                    "aac-heavyweight-savage",
                    "lindwurm",
                    TomestoneProgressKind.BossPercentage,
                    ["lindwurm"]),
                [(73, 105)] = new(
                    "dawntrail",
                    "aac-heavyweight-savage",
                    "lindwurm-ii",
                    TomestoneProgressKind.BossPercentage,
                    ["lindwurm-ii"]),
            })
            .ToDictionary(static mapping => mapping.Key, static mapping => mapping.Value);

    public static bool TryGetUltimateParams(
        uint zoneId,
        uint encounterId,
        out TomestoneEncounterParams encounterParams)
    {
        if (UltimateMappings.TryGetValue((zoneId, encounterId), out encounterParams))
        {
            return true;
        }

        encounterParams = null;
        return false;
    }

    public static bool TryGetProgressionTarget(
        uint zoneId,
        uint encounterId,
        out TomestoneEncounterParams encounterParams)
    {
        if (ProgressionMappings.TryGetValue((zoneId, encounterId), out encounterParams))
        {
            return true;
        }

        encounterParams = null;
        return false;
    }

    public static bool TryGetUltimateParamsByEncounterId(
        uint encounterId,
        out uint zoneId,
        out TomestoneEncounterParams encounterParams)
    {
        foreach (var mapping in UltimateMappings)
        {
            if (mapping.Key.EncounterId == encounterId)
            {
                zoneId = mapping.Key.ZoneId;
                encounterParams = mapping.Value;
                return true;
            }
        }

        zoneId = 0;
        encounterParams = null;
        return false;
    }

    public static bool TryGetProgressionTargetByEncounterId(
        uint encounterId,
        out uint zoneId,
        out TomestoneEncounterParams encounterParams)
    {
        foreach (var mapping in ProgressionMappings)
        {
            if (mapping.Key.EncounterId == encounterId)
            {
                zoneId = mapping.Key.ZoneId;
                encounterParams = mapping.Value;
                return true;
            }
        }

        zoneId = 0;
        encounterParams = null;
        return false;
    }
}
