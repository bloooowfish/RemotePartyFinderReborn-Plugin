using System;
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
                [(62, 94)] = new("dawntrail", "aac-light-heavyweight-savage", "honey-b.-lovely", TomestoneProgressKind.BossPercentage, ["honey-b.-lovely", "honey-b-lovely"]),
                [(62, 95)] = new("dawntrail", "aac-light-heavyweight-savage", "brute-bomber", TomestoneProgressKind.BossPercentage, ["brute-bomber"]),
                [(62, 96)] = new("dawntrail", "aac-light-heavyweight-savage", "wicked-thunder", TomestoneProgressKind.BossPercentage, ["wicked-thunder"]),
                [(68, 97)] = new("dawntrail", "aac-cruiserweight-savage", "dancing-green", TomestoneProgressKind.BossPercentage, ["dancing-green"]),
                [(68, 98)] = new("dawntrail", "aac-cruiserweight-savage", "sugar-riot", TomestoneProgressKind.BossPercentage, ["sugar-riot"]),
                [(68, 99)] = new("dawntrail", "aac-cruiserweight-savage", "brute-abombinator", TomestoneProgressKind.BossPercentage, ["brute-abombinator"]),
                [(68, 100)] = new("dawntrail", "aac-cruiserweight-savage", "howling-blade", TomestoneProgressKind.BossPercentage, ["howling-blade"]),
                [(72, 1084)] = new("dawntrail", "trials-extreme", "enuo", TomestoneProgressKind.BossPercentage, ["enuo", "the-unmaking-extreme"]),
                [(66, 2061)] = new(
                    "dawntrail",
                    "chaotic",
                    "the-cloud-of-darkness-chaotic",
                    TomestoneProgressKind.BossPercentage,
                    ["the-cloud-of-darkness-chaotic", "cloud-of-darkness-chaotic", "the-cloud-of-darkness", "cloud-of-darkness"]),
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

    public static bool TryGetProgressionTarget(
        ParseJob job,
        uint encounterId,
        out TomestoneEncounterParams encounterParams)
    {
        if (job == null || encounterId == 0)
        {
            encounterParams = null;
            return false;
        }

        if (TryBuildServerProgressionTarget(job, encounterId, out encounterParams))
        {
            return true;
        }

        if (TryGetProgressionTarget(job.ZoneId, encounterId, out encounterParams))
        {
            return true;
        }

        if (job.ZoneId == 0 && TryGetProgressionTargetByEncounterId(encounterId, out _, out encounterParams))
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
    /// Resolves the Tomestone progression targets for a <see cref="ParseJob"/> (including <see cref="TomestoneJob"/>),
    /// preferring server-supplied manifest metadata and falling back to the static table when omitted.
    /// </summary>
    public static IReadOnlyList<(uint ZoneId, uint EncounterId, TomestoneEncounterParams EncounterParams)> ResolveJobTargets(ParseJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.EncounterId == 0)
        {
            return Array.Empty<(uint ZoneId, uint EncounterId, TomestoneEncounterParams EncounterParams)>();
        }

        var resolved = new List<(uint ZoneId, uint EncounterId, TomestoneEncounterParams EncounterParams)>(2);
        TryAppendTarget(job, job.EncounterId, resolved);

        if (job.SecondaryEncounterId is { } secondaryEncounterId
            && secondaryEncounterId != 0
            && secondaryEncounterId != job.EncounterId)
        {
            TryAppendTarget(job, secondaryEncounterId, resolved);
        }

        return resolved;
    }

    private static void TryAppendTarget(
        ParseJob job,
        uint encounterId,
        List<(uint ZoneId, uint EncounterId, TomestoneEncounterParams EncounterParams)> resolved)
    {
        if (TryBuildServerProgressionTarget(job, encounterId, out var serverParams))
        {
            resolved.Add((job.ZoneId, encounterId, serverParams));
            return;
        }

        if (TryGetProgressionTarget(job.ZoneId, encounterId, out var encounterParams))
        {
            resolved.Add((job.ZoneId, encounterId, encounterParams));
            return;
        }

        if (TryGetProgressionTargetByEncounterId(encounterId, out var mappedZoneId, out encounterParams))
        {
            resolved.Add((mappedZoneId, encounterId, encounterParams));
        }
    }

    private static bool TryBuildServerProgressionTarget(
        ParseJob job,
        uint encounterId,
        out TomestoneEncounterParams encounterParams)
    {
        encounterParams = null;
        if (string.IsNullOrWhiteSpace(job.TomestoneExpansion)
            || string.IsNullOrWhiteSpace(job.TomestoneCategory)
            || string.IsNullOrWhiteSpace(job.TomestoneZoneSlug))
        {
            return false;
        }

        string encounterSlug;
        IReadOnlyList<string> rawNames;
        if (encounterId == job.EncounterId)
        {
            encounterSlug = job.TomestoneEncounterSlug;
            rawNames = job.TomestoneEncounterNames;
        }
        else if (job.SecondaryEncounterId is { } secondaryId
            && secondaryId != 0
            && encounterId == secondaryId)
        {
            encounterSlug = job.TomestoneSecondaryEncounterSlug;
            rawNames = job.TomestoneSecondaryEncounterNames;
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(encounterSlug))
        {
            return false;
        }

        var trimmedSlug = encounterSlug.Trim();
        var canonicalNames = rawNames?
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (canonicalNames is null || canonicalNames.Length == 0)
        {
            canonicalNames = [trimmedSlug];
        }

        var progressKind = ParseProgressKind(job);
        var clearsCategory = string.IsNullOrWhiteSpace(job.TomestoneClearsCategory)
            ? null
            : job.TomestoneClearsCategory.Trim();

        encounterParams = new TomestoneEncounterParams(
            job.TomestoneExpansion.Trim(),
            job.TomestoneCategory.Trim(),
            trimmedSlug,
            progressKind,
            canonicalNames,
            ZoneSlug: job.TomestoneZoneSlug.Trim(),
            ClearsCategory: clearsCategory);
        return true;
    }

    private static TomestoneProgressKind ParseProgressKind(ParseJob job)
    {
        var normalized = (job.TomestoneProgressKind ?? string.Empty).Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (string.Equals(normalized, "PhaseProgress", StringComparison.OrdinalIgnoreCase))
        {
            return TomestoneProgressKind.PhaseProgress;
        }

        if (string.Equals(normalized, "BossPercentage", StringComparison.OrdinalIgnoreCase))
        {
            return TomestoneProgressKind.BossPercentage;
        }

        if (string.Equals(job.TomestoneClearsCategory?.Trim(), "ultimate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.TomestoneCategory?.Trim(), "ultimates", StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.TomestoneZoneSlug?.Trim(), "ultimates", StringComparison.OrdinalIgnoreCase))
        {
            return TomestoneProgressKind.PhaseProgress;
        }

        return TomestoneProgressKind.BossPercentage;
    }
}
