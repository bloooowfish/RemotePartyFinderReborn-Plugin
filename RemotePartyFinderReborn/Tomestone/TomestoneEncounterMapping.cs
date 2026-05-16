using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn.Tomestone;

public static class TomestoneEncounterMapping
{
    private static readonly IReadOnlyDictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams> UltimateMappings =
        new Dictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams>
        {
            [(59, 1073)] = new("stormblood", "ultimates", "the-unending-coil-of-bahamut-ultimate"),
            [(59, 1074)] = new("stormblood", "ultimates", "the-weapons-refrain-ultimate"),
            [(59, 1075)] = new("shadowbringers", "ultimates", "the-epic-of-alexander-ultimate"),
            [(59, 1076)] = new("endwalker", "ultimates", "dragonsongs-reprise-ultimate"),
            [(59, 1077)] = new("endwalker", "ultimates", "the-omega-protocol-ultimate"),
            [(65, 1079)] = new("dawntrail", "ultimates", "futures-rewritten-ultimate"),
        };

    private static readonly IReadOnlyDictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams> ProgressionMappings =
        UltimateMappings
            .Concat(new Dictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams>
            {
                [(73, 101)] = new(
                    "dawntrail",
                    "raids",
                    "aac-heavyweight-m1-savage",
                    TomestoneProgressKind.BossPercentage),
                [(73, 102)] = new(
                    "dawntrail",
                    "raids",
                    "aac-heavyweight-m2-savage",
                    TomestoneProgressKind.BossPercentage),
                [(73, 103)] = new(
                    "dawntrail",
                    "raids",
                    "aac-heavyweight-m3-savage",
                    TomestoneProgressKind.BossPercentage),
                [(73, 105)] = new(
                    "dawntrail",
                    "raids",
                    "aac-heavyweight-m4-savage",
                    TomestoneProgressKind.BossPercentage),
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
