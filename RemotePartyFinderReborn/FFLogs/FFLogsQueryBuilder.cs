using System.Collections.Generic;
using System.Text;

namespace RemotePartyFinderReborn;

internal static class FFLogsQueryBuilder
{
    internal static string BuildCharacterCandidateDataBatchQuery(
        IReadOnlyList<FFLogsClient.CandidateCharacterQuery> candidates,
        int zoneId,
        int? difficultyId)
    {
        var sb = new StringBuilder();
        sb.Append("query { characterData {");

        for (var i = 0; i < candidates.Count; i++)
        {
            var q = candidates[i];
            var name = EscapeGraphQlString(q.Name);
            var server = EscapeGraphQlString(q.Server);
            var region = EscapeGraphQlString(q.Region);

            sb.Append($" c{i}: character(name: \"{name}\", serverSlug: \"{server}\", serverRegion: \"{region}\") {{");
            sb.Append(" hidden ");

            var args = new List<string> { $"zoneID: {zoneId}" };
            if (difficultyId.HasValue) args.Add($"difficulty: {difficultyId.Value}");
            args.Add("metric: rdps");
            args.Add("timeframe: Historical");
            sb.Append($" zoneRankings({string.Join(", ", args)}) ");

            sb.Append(" }");
        }

        sb.Append(" } }");
        return sb.ToString();
    }

    internal static string EscapeGraphQlString(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
