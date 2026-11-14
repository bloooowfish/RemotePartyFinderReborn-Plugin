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
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (ch < ' ' || ch == '\x7f')
                    {
                        sb.Append($"\\u{(int)ch:x4}");
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }

        return sb.ToString();
    }
}
