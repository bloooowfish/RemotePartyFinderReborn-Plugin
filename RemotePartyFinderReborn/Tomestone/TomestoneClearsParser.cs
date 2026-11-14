using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RemotePartyFinderReborn.Tomestone;

/// <summary>
/// Parses Tomestone character clears rankings (<c>encounters.rankings</c>) and converts
/// Tier 1/Tier 2 payloads into fallback <see cref="ParseResult"/> records matching Server expectations.
/// </summary>
public static class TomestoneClearsParser
{
    public static TomestoneClearsRankingsOutcome ExtractClearsRankings(
        JToken content,
        IReadOnlyCollection<int> requestedEncounters)
    {
        if (content is null)
        {
            return TomestoneClearsRankingsOutcome.Empty;
        }

        var rankingsToken = content["encounters"]?["rankings"];
        if (rankingsToken is null)
        {
            return TomestoneClearsRankingsOutcome.Empty;
        }

        var rankingEntries = FlattenRankingObjects(rankingsToken);
        var hasRankingsSection = rankingsToken is JArray
            || (rankingsToken is JObject obj && obj["rankings"] is JArray);

        if (!hasRankingsSection)
        {
            return TomestoneClearsRankingsOutcome.Empty;
        }

        var requestedSet = requestedEncounters is { Count: > 0 }
            ? new HashSet<int>(requestedEncounters)
            : new HashSet<int>();

        var matchingRankings = rankingEntries
            .Where(ranking => EnumerateEncounterObjects(ranking)
                .Any(enc => TryReadEncounterId(enc, out var id) && requestedSet.Contains(id)))
            .ToList();

        var candidateRankings = matchingRankings.Count > 0 ? matchingRankings : rankingEntries;

        var encounters = new Dictionary<int, double>();
        var clearCounts = new Dictionary<int, int>();
        var seenEncounters = new List<int>();
        var requestedEntryCount = 0;

        foreach (var ranking in candidateRankings)
        {
            foreach (var encounterObj in EnumerateEncounterObjects(ranking))
            {
                if (!TryReadEncounterId(encounterObj, out var encounterId))
                {
                    continue;
                }

                if (seenEncounters.Count < 20)
                {
                    seenEncounters.Add(encounterId);
                }

                if (!requestedSet.Contains(encounterId))
                {
                    continue;
                }

                requestedEntryCount++;

                var totalClears = TryReadTotalClears(encounterObj);
                int? positiveClears = totalClears is > 0 ? totalClears.Value : null;
                var percent = TryReadRankPercent(encounterObj);

                if (positiveClears.HasValue && !clearCounts.ContainsKey(encounterId))
                {
                    clearCounts[encounterId] = positiveClears.Value;
                }

                if (percent.HasValue && (percent.Value > 0.0 || positiveClears.HasValue))
                {
                    if (!encounters.TryGetValue(encounterId, out var existing) || percent.Value > existing)
                    {
                        encounters[encounterId] = percent.Value;
                    }
                }
            }
        }

        return new TomestoneClearsRankingsOutcome(
            encounters,
            clearCounts,
            requestedEntryCount,
            seenEncounters,
            true);
    }

    public static ParseResult ParseClearsOrProgressIntoResult(
        JToken content,
        ParseJob job,
        IReadOnlyList<(uint EncounterId, TomestoneEncounterParams EncounterParams)> targets,
        string fallbackSourceKind = "tomestone_web",
        string matchedServer = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (content is null)
        {
            return null;
        }

        var requested = BuildRequestedEncounterIds(job);
        var clears = ExtractClearsRankings(content, requested);
        var effectiveServer = !string.IsNullOrWhiteSpace(matchedServer)
            ? matchedServer.Trim()
            : job.Server?.Trim() ?? string.Empty;
        var isEstimated = job.CandidateServers is { Count: > 0 };

        if (clears.Encounters.Count > 0 || clears.ClearCounts.Count > 0)
        {
            var result = new ParseResult
            {
                ContentId = job.ContentId,
                ZoneId = job.ZoneId,
                DifficultyId = job.DifficultyId,
                Encounters = new Dictionary<int, double>(clears.Encounters),
                ClearCounts = new Dictionary<int, int>(clears.ClearCounts),
                IsHidden = false,
                IsEstimated = isEstimated,
                MatchedServer = effectiveServer,
                SourceKind = fallbackSourceKind,
                LeaseToken = job.LeaseToken ?? string.Empty,
            };

            if (targets is { Count: > 0 })
            {
                foreach (var (encounterId, encounterParams) in targets)
                {
                    var key = (int)encounterId;
                    var alreadyCleared =
                        (result.Encounters.TryGetValue(key, out var pct) && pct > 0.0)
                        || (result.ClearCounts.TryGetValue(key, out var count) && count > 0);
                    if (alreadyCleared)
                    {
                        continue;
                    }

                    var progress = TomestoneProgressParser.ParseProgress(content, encounterParams);
                    ApplyProgress(result, key, encounterParams.ProgressKind, progress);
                }
            }

            return result;
        }

        if (targets is not { Count: > 0 })
        {
            return null;
        }

        var progressOnlyResult = new ParseResult
        {
            ContentId = job.ContentId,
            ZoneId = job.ZoneId,
            DifficultyId = job.DifficultyId,
            IsHidden = false,
            IsEstimated = isEstimated,
            MatchedServer = effectiveServer,
            SourceKind = "tomestone_api",
            LeaseToken = job.LeaseToken ?? string.Empty,
        };

        var foundUnclearedProgress = false;
        foreach (var (encounterId, encounterParams) in targets)
        {
            var progress = TomestoneProgressParser.ParseProgress(content, encounterParams);
            if (progress.IsAuthoritative && !progress.Cleared)
            {
                foundUnclearedProgress = true;
                ApplyProgress(progressOnlyResult, (int)encounterId, encounterParams.ProgressKind, progress);
            }
        }

        return foundUnclearedProgress ? progressOnlyResult : null;
    }

    internal static void ApplyProgress(
        ParseResult parseResult,
        int encounterId,
        TomestoneProgressKind progressKind,
        TomestoneProgressData progress)
    {
        if (parseResult == null || progress == null || !progress.IsAuthoritative)
        {
            return;
        }

        if (!parseResult.TomestoneQueriedEncounters.Contains(encounterId))
        {
            parseResult.TomestoneQueriedEncounters.Add(encounterId);
        }

        parseResult.PhaseProgress.Remove(encounterId);
        parseResult.BossPercentages.Remove(encounterId);
        if (progress.Cleared)
        {
            return;
        }

        switch (progressKind)
        {
            case TomestoneProgressKind.PhaseProgress:
                if (progress.Phases.Count > 0)
                {
                    parseResult.PhaseProgress[encounterId] = progress.Phases.ToList();
                }

                break;
            case TomestoneProgressKind.BossPercentage:
                if (TryGetBossPercentage(progress, out var bossPercentage))
                {
                    parseResult.BossPercentages[encounterId] = bossPercentage;
                }

                break;
        }
    }

    private static bool TryGetBossPercentage(TomestoneProgressData progress, out double bossPercentage)
    {
        if (progress.BossPercentage.HasValue)
        {
            bossPercentage = progress.BossPercentage.Value;
            return true;
        }

        var phaseMin = progress.Phases
            .Where(static phase => phase.BossPercentage.HasValue)
            .Select(static phase => phase.BossPercentage!.Value)
            .DefaultIfEmpty(double.NaN)
            .Min();
        if (!double.IsNaN(phaseMin))
        {
            bossPercentage = phaseMin;
            return true;
        }

        bossPercentage = 0;
        return false;
    }

    private static List<int> BuildRequestedEncounterIds(ParseJob job)
    {
        var requested = new List<int>();
        if (job.EncounterId != 0)
        {
            requested.Add((int)job.EncounterId);
        }

        if (job.SecondaryEncounterId.HasValue && job.SecondaryEncounterId.Value != 0)
        {
            var sec = (int)job.SecondaryEncounterId.Value;
            if (!requested.Contains(sec))
            {
                requested.Add(sec);
            }
        }

        return requested;
    }

    private static List<JObject> FlattenRankingObjects(JToken rankingsToken)
    {
        var buckets = rankingsToken as JArray
            ?? (rankingsToken as JObject)?["rankings"] as JArray;
        if (buckets is null)
        {
            return [];
        }

        var flattened = new List<JObject>();
        foreach (var bucket in buckets.OfType<JObject>())
        {
            if (bucket["rankings"] is JArray nestedGroup)
            {
                foreach (var inner in nestedGroup.OfType<JObject>())
                {
                    flattened.Add(inner);
                }
            }
            else if (bucket["encounters"] is JArray)
            {
                flattened.Add(bucket);
            }
        }

        return flattened;
    }

    private static IEnumerable<JObject> EnumerateEncounterObjects(JObject ranking)
    {
        return ranking["encounters"] is JArray array
            ? array.OfType<JObject>()
            : [];
    }

    private static bool TryReadEncounterId(JObject encounter, out int encounterId)
    {
        encounterId = 0;
        var token = encounter["encounterId"] ?? encounter["encounter_id"];
        if (token == null || token.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            return false;
        }

        encounterId = token.Value<int>();
        return true;
    }

    private static int? TryReadTotalClears(JObject encounter)
    {
        var token = encounter["totalClears"] ?? encounter["total_clears"];
        if (token == null || token.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            return null;
        }

        return token.Value<int>();
    }

    private static double? TryReadRankPercent(JObject encounter)
    {
        var rankPercent = encounter["rankPercent"] ?? encounter["rank_percent"];
        var percentToken = rankPercent?["percent"];
        if (percentToken == null || percentToken.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            return null;
        }

        var value = percentToken.Value<double>();
        return !double.IsNaN(value) && !double.IsInfinity(value) ? value : null;
    }
}
