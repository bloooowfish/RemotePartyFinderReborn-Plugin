using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn.Tomestone;

public static class TomestoneEncounterMapping
{
    private static readonly IReadOnlyDictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams> UltimateMappings =
        new Dictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams>
        {
            [(59, 1073)] = new("stormblood", "ultimates", "the-unending-coil-of-bahamut-ultimate", TargetCanonicalNames: ["the-unending-coil-of-bahamut-ultimate"], ClearsCategory: "ultimate", ActivityCategory: "ultimates"),
            [(59, 1074)] = new("stormblood", "ultimates", "the-weapons-refrain-ultimate", TargetCanonicalNames: ["the-weapons-refrain-ultimate"], ClearsCategory: "ultimate", ActivityCategory: "ultimates"),
            [(59, 1075)] = new("shadowbringers", "ultimates", "the-epic-of-alexander-ultimate", TargetCanonicalNames: ["the-epic-of-alexander-ultimate"], ClearsCategory: "ultimate", ActivityCategory: "ultimates"),
            [(59, 1076)] = new("endwalker", "ultimates", "dragonsongs-reprise-ultimate", TargetCanonicalNames: ["dragonsongs-reprise-ultimate"], ClearsCategory: "ultimate", ActivityCategory: "ultimates"),
            [(59, 1077)] = new("endwalker", "ultimates", "the-omega-protocol-ultimate", TargetCanonicalNames: ["the-omega-protocol-ultimate"], ClearsCategory: "ultimate", ActivityCategory: "ultimates"),
            [(65, 1079)] = new("dawntrail", "ultimates", "futures-rewritten-ultimate", TargetCanonicalNames: ["futures-rewritten-ultimate"], ClearsCategory: "ultimate", ActivityCategory: "ultimates"),
            [(76, 1085)] = new("dawntrail", "ultimates", "dancing-mad-ultimate", TargetCanonicalNames: ["dancing-mad-ultimate"], ClearsCategory: "ultimate", ActivityCategory: "ultimates"),
        };

    private static readonly IReadOnlyDictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams> ProgressionMappings =
        UltimateMappings
            .Concat(new Dictionary<(uint ZoneId, uint EncounterId), TomestoneEncounterParams>
            {
                [(62, 93)] = new("dawntrail", "aac-light-heavyweight-savage", "black-cat", TomestoneProgressKind.BossPercentage, ["black-cat"]),
                [(62, 94)] = new("dawntrail", "aac-light-heavyweight-savage", "honey-b.-lovely", TomestoneProgressKind.BossPercentage, ["honey-b.-lovely", "honey-b-lovely"]),
                [(62, 95)] = new("dawntrail", "aac-light-heavyweight-savage", "brute-bomber", TomestoneProgressKind.BossPercentage, ["brute-bomber"]),
                [(62, 96)] = new("dawntrail", "aac-light-heavyweight-savage", "wicked-thunder", TomestoneProgressKind.BossPercentage, ["wicked-thunder"]),
                [(68, 97)] = new("dawntrail", "aac-cruiserweight-savage", "dancing-green", TomestoneProgressKind.BossPercentage, ["dancing-green"]),
                [(68, 98)] = new("dawntrail", "aac-cruiserweight-savage", "sugar-riot", TomestoneProgressKind.BossPercentage, ["sugar-riot"]),
                [(68, 99)] = new("dawntrail", "aac-cruiserweight-savage", "brute-abombinator", TomestoneProgressKind.BossPercentage, ["brute-abombinator"]),
                [(68, 100)] = new("dawntrail", "aac-cruiserweight-savage", "howling-blade", TomestoneProgressKind.BossPercentage, ["howling-blade"]),
                [(72, 1084)] = new("dawntrail", "trials-extreme", "enuo", TomestoneProgressKind.BossPercentage, ["enuo", "the-unmaking-extreme"], ClearsCategory: "extreme", ActivityCategory: "trials"),
                [(66, 2061)] = new(
                    "dawntrail",
                    "chaotic",
                    "the-cloud-of-darkness-chaotic",
                    TomestoneProgressKind.BossPercentage,
                    ["the-cloud-of-darkness-chaotic", "cloud-of-darkness-chaotic", "the-cloud-of-darkness", "cloud-of-darkness"],
                    ClearsCategory: "chaotic",
                    ActivityCategory: "chaotic"),
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

    /// <summary>
    /// Returns all mapped progression targets sharing <paramref name="zoneId"/>.
    /// </summary>
    public static IReadOnlyList<(uint EncounterId, TomestoneEncounterParams EncounterParams)> GetProgressionTargetsForZone(uint zoneId)
    {
        return ProgressionMappings
            .Where(pair => pair.Key.ZoneId == zoneId)
            .OrderBy(static pair => pair.Key.EncounterId)
            .Select(static pair => (pair.Key.EncounterId, pair.Value))
            .ToArray();
    }

    /// <summary>
    /// Resolves Tomestone progression targets for a FFLogs <see cref="ParseJob"/> (primary and secondary encounter IDs).
    /// </summary>
    public static IReadOnlyList<(uint ZoneId, uint EncounterId, TomestoneEncounterParams EncounterParams)> ResolveJobTargets(ParseJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var resolved = new List<(uint ZoneId, uint EncounterId, TomestoneEncounterParams EncounterParams)>();
        var seen = new HashSet<(uint ZoneId, uint EncounterId)>();

        void TryAdd(uint encounterId)
        {
            if (encounterId == 0)
            {
                return;
            }

            if (TryGetProgressionTarget(job.ZoneId, encounterId, out var encounterParams))
            {
                if (seen.Add((job.ZoneId, encounterId)))
                {
                    resolved.Add((job.ZoneId, encounterId, encounterParams));
                }
            }
            else if (TryGetProgressionTargetByEncounterId(encounterId, out var mappedZoneId, out encounterParams))
            {
                if (seen.Add((mappedZoneId, encounterId)))
                {
                    resolved.Add((mappedZoneId, encounterId, encounterParams));
                }
            }
        }

        TryAdd(job.EncounterId);
        if (job.SecondaryEncounterId.HasValue)
        {
            TryAdd(job.SecondaryEncounterId.Value);
        }

        return resolved;
    }

    /// <summary>
    /// Infers the Tomestone <c>encounterCategory</c> query parameter for a <see cref="ParseJob"/>.
    /// </summary>
    public static string InferClearsCategory(ParseJob job)
    {
        if (job is null)
        {
            return "savage";
        }

        var targets = ResolveJobTargets(job);
        if (targets.Count > 0)
        {
            return targets[0].EncounterParams.EffectiveClearsCategory;
        }

        foreach (var mapping in ProgressionMappings)
        {
            if (mapping.Key.ZoneId == job.ZoneId)
            {
                return mapping.Value.EffectiveClearsCategory;
            }
        }

        return job.DifficultyId switch
        {
            100 => "extreme",
            101 => "savage",
            _ => "savage",
        };
    }
}
