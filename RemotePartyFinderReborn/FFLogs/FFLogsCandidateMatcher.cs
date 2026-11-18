using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsCandidateMatcher
{
    private readonly IReadOnlyList<ParseJob> _jobs;
    private readonly uint _zoneId;
    private readonly int _difficultyId;
    private readonly Dictionary<ulong, List<ParseJobCandidateServer>> _candidatesByContentId = new();
    private readonly List<ParseJob> _missingIdentityJobs = new();

    public FFLogsCandidateMatcher(IReadOnlyList<ParseJob> jobs, uint zoneId, int difficultyId)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        _jobs = jobs
            .GroupBy(j => j.ContentId)
            .Select(g => g.First())
            .ToList();
        _zoneId = zoneId;
        _difficultyId = difficultyId;

        foreach (var job in _jobs)
        {
            var candidates = GetCandidates(job);
            if (string.IsNullOrWhiteSpace(job.Name) || candidates.Count == 0)
            {
                _missingIdentityJobs.Add(job);
                continue;
            }

            _candidatesByContentId[job.ContentId] = candidates;
        }
    }

    public IReadOnlyList<ParseJob> Jobs => _jobs;
    public IReadOnlyList<ParseJob> MissingIdentityJobs => _missingIdentityJobs;

    public List<FFLogsClient.CandidateCharacterQuery> BuildCandidateQueries()
    {
        var candidateQueries = new List<FFLogsClient.CandidateCharacterQuery>();

        foreach (var job in _jobs)
        {
            if (!_candidatesByContentId.TryGetValue(job.ContentId, out var candidates))
            {
                continue;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                candidateQueries.Add(new FFLogsClient.CandidateCharacterQuery
                {
                    Key = $"{job.ContentId}:{i}",
                    Name = job.Name,
                    Server = candidate.Server,
                    Region = candidate.Region,
                });
            }
        }

        return candidateQueries;
    }

    public FFLogsCandidateMatchResult MatchFetchedCandidates(
        IReadOnlyDictionary<string, FFLogsClient.CharacterFetchedData> fetchedByKey)
    {
        ArgumentNullException.ThrowIfNull(fetchedByKey);

        var resultsByContentId = new Dictionary<ulong, ParseResult>();
        var notFoundCandidateJobs = new List<ParseJob>();
        foreach (var job in _jobs)
        {
            if (!_candidatesByContentId.TryGetValue(job.ContentId, out var candidates) || candidates.Count == 0)
            {
                continue;
            }

            var isCandidateSearch = job.CandidateServers != null && job.CandidateServers.Count > 0;
            var bestIdx = -1;
            var bestScore = long.MinValue;
            FFLogsClient.CharacterFetchedData bestData = null;

            for (var i = 0; i < candidates.Count; i++)
            {
                var key = $"{job.ContentId}:{i}";
                if (!fetchedByKey.TryGetValue(key, out var data))
                {
                    continue;
                }

                if (isCandidateSearch && !data.Exists)
                {
                    continue;
                }

                var score = ScoreCandidate(data, job.EncounterId, job.SecondaryEncounterId);
                if (bestIdx < 0 || score > bestScore)
                {
                    bestIdx = i;
                    bestScore = score;
                    bestData = data;
                }
            }

            if (bestIdx < 0 || bestData == null)
            {
                if (isCandidateSearch)
                {
                    notFoundCandidateJobs.Add(job);
                }

                continue;
            }

            var matched = candidates[bestIdx];
            var parseResult = new ParseResult
            {
                ContentId = job.ContentId,
                ZoneId = _zoneId,
                DifficultyId = _difficultyId,
                IsHidden = bestData.Hidden,
                IsEstimated = job.CandidateServers != null && job.CandidateServers.Count > 0,
                MatchedServer = matched.Server,
                LeaseToken = job.LeaseToken,
            };

            if (!parseResult.IsHidden)
            {
                parseResult.Encounters = bestData.Parses
                    .GroupBy(e => e.EncounterId)
                    .ToDictionary(g => g.Key, g => g.Max(x => x.Percentile));

                parseResult.ClearCounts = bestData.Parses
                    .Where(e => e.ClearCount.HasValue && e.ClearCount.Value > 0)
                    .GroupBy(e => e.EncounterId)
                    .ToDictionary(g => g.Key, g => g.Max(x => x.ClearCount!.Value));

                parseResult.FflogsQueriedEncounters = BuildQueriedEncounters(job, bestData);
            }

            resultsByContentId[job.ContentId] = parseResult;
        }

        return new FFLogsCandidateMatchResult(resultsByContentId, notFoundCandidateJobs, _missingIdentityJobs);
    }

    private static List<int> BuildQueriedEncounters(
        ParseJob job,
        FFLogsClient.CharacterFetchedData bestData)
    {
        var queried = new SortedSet<int>();
        if (job.ZoneEncounters is { Count: > 0 })
        {
            foreach (var encounterId in job.ZoneEncounters)
            {
                if (encounterId > 0 && encounterId <= int.MaxValue)
                {
                    queried.Add((int)encounterId);
                }
            }
        }

        if (queried.Count == 0)
        {
            if (job.EncounterId > 0 && job.EncounterId <= int.MaxValue)
            {
                queried.Add((int)job.EncounterId);
            }

            if (job.SecondaryEncounterId is { } secondaryEncounterId
                && secondaryEncounterId > 0
                && secondaryEncounterId <= int.MaxValue)
            {
                queried.Add((int)secondaryEncounterId);
            }
        }

        if (bestData.Parses is { Count: > 0 })
        {
            foreach (var parse in bestData.Parses)
            {
                if (parse.EncounterId > 0)
                {
                    queried.Add(parse.EncounterId);
                }
            }
        }

        return queried.ToList();
    }

    private static long ScoreCandidate(
        FFLogsClient.CharacterFetchedData data,
        uint encounterId,
        uint? secondaryEncounterId)
    {
        long score = 0;

        // Slightly prefer visible rankings, but keep parse volume as a separate signal.
        if (!data.Hidden)
        {
            score += 1;
        }

        if (data.Parses.Count > 0)
        {
            score += 1000 + data.Parses.Count;
        }

        void ScoreEncounter(uint? encounter)
        {
            if (!encounter.HasValue || encounter.Value == 0)
            {
                return;
            }

            var hit = data.Parses.FirstOrDefault(p => p.EncounterId == (int)encounter.Value);
            if (hit == null)
            {
                return;
            }

            score += 100_000 + (long)Math.Round(hit.Percentile * 100.0);
        }

        ScoreEncounter(encounterId);
        ScoreEncounter(secondaryEncounterId);

        return score;
    }

    private static List<ParseJobCandidateServer> GetCandidates(ParseJob job)
    {
        if (job.CandidateServers != null && job.CandidateServers.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<ParseJobCandidateServer>();
            foreach (var candidate in job.CandidateServers)
            {
                var server = (candidate?.Server ?? string.Empty).Trim();
                var region = (candidate?.Region ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(region))
                {
                    continue;
                }

                var key = server + "|" + region;
                if (!seen.Add(key))
                {
                    continue;
                }

                list.Add(new ParseJobCandidateServer { Server = server, Region = region });
            }

            return list;
        }

        if (!string.IsNullOrWhiteSpace(job.Server) && !string.IsNullOrWhiteSpace(job.Region))
        {
            return
            [
                new ParseJobCandidateServer
                {
                    Server = job.Server.Trim(),
                    Region = job.Region.Trim(),
                },
            ];
        }

        return [];
    }
}

internal sealed class FFLogsCandidateMatchResult
{
    public FFLogsCandidateMatchResult(
        Dictionary<ulong, ParseResult> resultsByContentId,
        IReadOnlyList<ParseJob> notFoundCandidateJobs = null,
        IReadOnlyList<ParseJob> missingIdentityJobs = null)
    {
        ResultsByContentId = resultsByContentId ?? throw new ArgumentNullException(nameof(resultsByContentId));
        NotFoundCandidateJobs = notFoundCandidateJobs ?? Array.Empty<ParseJob>();
        MissingIdentityJobs = missingIdentityJobs ?? Array.Empty<ParseJob>();
    }

    public Dictionary<ulong, ParseResult> ResultsByContentId { get; }
    public IReadOnlyList<ParseJob> NotFoundCandidateJobs { get; }
    public IReadOnlyList<ParseJob> MissingIdentityJobs { get; }
}
