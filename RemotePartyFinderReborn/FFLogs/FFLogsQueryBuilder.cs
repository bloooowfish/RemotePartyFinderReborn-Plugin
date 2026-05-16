using System.Collections.Generic;
using System.Text;

namespace RemotePartyFinderReborn;

internal static class FFLogsQueryBuilder
{
    internal static string BuildCharacterCandidateDataBatchQuery(
        IReadOnlyList<FFLogsClient.CandidateCharacterQuery> candidates,
        int zoneId,
        int? difficultyId,
        int recentReportsLimit)
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

            if (recentReportsLimit > 0)
            {
                sb.Append($" recentReports(limit: {recentReportsLimit}) {{ data {{ code }} }} ");
            }

            sb.Append(" }");
        }

        sb.Append(" } }");
        return sb.ToString();
    }

    internal static string BuildBestBossPercentByReportQuery(
        IReadOnlyList<string> reportCodes,
        int encounterId,
        int? difficultyId)
    {
        var sb = new StringBuilder();
        sb.Append("query { reportData {");

        for (var i = 0; i < reportCodes.Count; i++)
        {
            var code = EscapeGraphQlString(reportCodes[i]);
            sb.Append($" r{i}: report(code: \"{code}\") {{");

            sb.Append($" fights(encounterID: {encounterId}, killType: Encounters");
            if (difficultyId.HasValue)
            {
                sb.Append($", difficulty: {difficultyId.Value}");
            }

            sb.Append(") { kill bossPercentage } ");
            sb.Append(" }");
        }

        sb.Append(" } }");
        return sb.ToString();
    }

    internal static string EscapeGraphQlString(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
