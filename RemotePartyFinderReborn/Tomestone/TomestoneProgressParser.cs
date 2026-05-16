using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RemotePartyFinderReborn.Tomestone;

public static class TomestoneProgressParser
{
    private static readonly string[] PercentFields =
    [
        "bestPull",
        "bestPulls",
        "best_pull",
        "best_pulls",
        "bossPercentage",
        "boss_percentage",
        "percent",
        "percentage",
    ];

    public static IReadOnlyList<PhaseProgress> ParsePhaseProgress(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JToken root;
        try
        {
            root = JToken.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        return ParsePhaseProgress(root);
    }

    public static TomestoneProgressData ParseProgress(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return TomestoneProgressData.Empty;
        }

        JToken root;
        try
        {
            root = JToken.Parse(json);
        }
        catch (JsonException)
        {
            return TomestoneProgressData.Empty;
        }

        return new TomestoneProgressData(
            ParsePhaseProgress(root),
            ParseBestBossPercentage(root));
    }

    private static IReadOnlyList<PhaseProgress> ParsePhaseProgress(JToken root)
    {
        var graph = FindGraphArray(root);
        if (graph is not null)
        {
            var graphProgress = ParseGraphProgress(graph);
            if (graphProgress.Count > 0)
            {
                return graphProgress;
            }
        }

        return ParseActivityProgress(root);
    }

    private static double? ParseBestBossPercentage(JToken root)
    {
        var values = new List<double>();

        if (FindGraphArray(root) is { } graph)
        {
            CollectGraphBossPercentages(graph, values);
        }

        CollectActivityBossPercentages(root, values);

        return values.Count == 0
            ? null
            : values.Min();
    }

    private static void CollectGraphBossPercentages(JArray graph, List<double> values)
    {
        foreach (var item in graph)
        {
            if (item is not JObject graphItem || !TryReadBossPercentage(graphItem, out var bossPercentage))
            {
                continue;
            }

            values.Add(Math.Clamp(bossPercentage, 0, 100));
        }
    }

    private static void CollectActivityBossPercentages(JToken root, List<double> values)
    {
        var rows = FindActivityRows(root);
        if (rows is null)
        {
            return;
        }

        foreach (var row in rows)
        {
            if (row is not JObject rowObject || IsKillRow(rowObject))
            {
                continue;
            }

            foreach (var source in EnumerateActivityProgressSources(rowObject))
            {
                if (TryReadBestPercentValue(source, out var bossPercentage))
                {
                    values.Add(Math.Clamp(bossPercentage, 0, 100));
                }
            }
        }
    }

    private static IReadOnlyList<PhaseProgress> ParseGraphProgress(JArray graph)
    {
        var phasesByKey = new Dictionary<string, ParsedPhase>(StringComparer.Ordinal);
        var graphIndex = 0;
        foreach (var item in graph)
        {
            if (item is not JObject graphItem)
            {
                graphIndex++;
                continue;
            }

            if (!TryReadPhaseLabel(graphItem, out var phaseName, out var explicitOrder)
                || !TryReadBossPercentage(graphItem, out var bossPercentage))
            {
                graphIndex++;
                continue;
            }

            bossPercentage = Math.Clamp(bossPercentage, 0, 100);
            var sortOrder = explicitOrder ?? graphIndex + 1;
            UpsertParsedPhase(phasesByKey, phaseName, sortOrder, graphIndex, bossPercentage);

            graphIndex++;
        }

        return BuildPhaseProgress(phasesByKey);
    }

    private static IReadOnlyList<PhaseProgress> ParseActivityProgress(JToken root)
    {
        var rows = FindActivityRows(root);
        if (rows is null)
        {
            return [];
        }

        var phasesByKey = new Dictionary<string, ParsedPhase>(StringComparer.Ordinal);
        var rowIndex = 0;
        foreach (var row in rows)
        {
            if (row is not JObject rowObject)
            {
                rowIndex++;
                continue;
            }

            if (IsKillRow(rowObject))
            {
                rowIndex++;
                continue;
            }

            foreach (var source in EnumerateActivityProgressSources(rowObject))
            {
                if (!TryReadBestPercentLabel(
                    source,
                    out var phaseName,
                    out var explicitOrder,
                    out var bossPercentage))
                {
                    continue;
                }

                bossPercentage = Math.Clamp(bossPercentage, 0, 100);
                var sortOrder = explicitOrder ?? rowIndex + 1;
                UpsertParsedPhase(phasesByKey, phaseName, sortOrder, rowIndex, bossPercentage);
            }

            rowIndex++;
        }

        return BuildBestPhaseProgress(phasesByKey);
    }

    private static IReadOnlyList<PhaseProgress> BuildPhaseProgress(
        Dictionary<string, ParsedPhase> phasesByKey)
    {
        return phasesByKey.Values
            .OrderBy(static phase => phase.Order)
            .ThenBy(static phase => phase.GraphIndex)
            .Select(static phase => new PhaseProgress
            {
                PhaseKey = phase.PhaseKey,
                PhaseName = phase.PhaseName,
                Order = phase.Order,
                BossPercentage = phase.BossPercentage,
                DisplayText = $"{phase.PhaseName} {FormatPercent(phase.BossPercentage)}%",
                Source = "tomestone_api",
            })
            .ToList();
    }

    private static IReadOnlyList<PhaseProgress> BuildBestPhaseProgress(
        Dictionary<string, ParsedPhase> phasesByKey)
    {
        return BuildPhaseProgress(phasesByKey)
            .OrderByDescending(static phase => phase.Order)
            .ThenBy(static phase => phase.BossPercentage ?? double.PositiveInfinity)
            .ThenBy(static phase => phase.PhaseKey, StringComparer.Ordinal)
            .Take(1)
            .ToList();
    }

    private static void UpsertParsedPhase(
        Dictionary<string, ParsedPhase> phasesByKey,
        string phaseName,
        int sortOrder,
        int graphIndex,
        double bossPercentage)
    {
        phaseName = phaseName.Trim();
        if (IsInstanceProgressLabel(phaseName))
        {
            return;
        }

        var phaseKey = NormalizePhaseKey(phaseName);
        if (phaseKey.Length == 0)
        {
            return;
        }

        var parsedPhase = new ParsedPhase(
            phaseKey,
            phaseName,
            sortOrder,
            graphIndex,
            bossPercentage);

        if (!phasesByKey.TryGetValue(phaseKey, out var existing))
        {
            phasesByKey[phaseKey] = parsedPhase;
        }
        else if (parsedPhase.BossPercentage < existing.BossPercentage)
        {
            phasesByKey[phaseKey] = existing with { BossPercentage = parsedPhase.BossPercentage };
        }
    }

    private static JArray FindGraphArray(JToken root)
    {
        if (root is JArray rootArray)
        {
            return rootArray;
        }

        if (root is not JObject rootObject)
        {
            return null;
        }

        if (rootObject["data"] is JObject dataObject
            && dataObject["graph"] is JArray dataGraph)
        {
            return dataGraph;
        }

        return rootObject["graph"] as JArray;
    }

    private static JArray FindActivityRows(JToken root)
    {
        if (root is not JObject rootObject)
        {
            return null;
        }

        string[] paths =
        [
            "activity.activities.activities.paginator.data",
            "data.activity.activities.activities.paginator.data",
            "activities.activities.paginator.data",
            "data.activities.activities.paginator.data",
            "paginator.data",
        ];

        foreach (var path in paths)
        {
            if (rootObject.SelectToken(path) is JArray rows)
            {
                return rows;
            }
        }

        return null;
    }

    private static IEnumerable<JObject> EnumerateActivityProgressSources(JObject rowObject)
    {
        if (rowObject["activity"] is JObject activityObject)
        {
            yield return activityObject;
        }

        yield return rowObject;

        if (rowObject["perspectives"] is not JObject perspectivesObject)
        {
            yield break;
        }

        foreach (var property in perspectivesObject.Properties())
        {
            if (property.Value is JObject perspectiveObject)
            {
                yield return perspectiveObject;
            }
        }
    }

    private static bool IsKillRow(JObject rowObject)
    {
        if (ReadIntProperty(rowObject, "killsCount") is > 0)
        {
            return true;
        }

        return rowObject["activity"] is JObject activityObject
               && ReadIntProperty(activityObject, "killsCount") is > 0;
    }

    private static bool TryReadPhaseLabel(
        JObject graphItem,
        out string phaseName,
        out int? explicitOrder)
    {
        phaseName = null;
        explicitOrder = null;

        if (graphItem["phase"] is JObject phaseObject)
        {
            phaseName = ReadStringProperty(phaseObject, "name");
            explicitOrder = ReadIntProperty(phaseObject, "order")
                ?? ReadIntProperty(phaseObject, "number");
            if (!string.IsNullOrWhiteSpace(phaseName))
            {
                return true;
            }
        }

        phaseName = ReadStringProperty(graphItem, "phaseName")
            ?? ReadStringProperty(graphItem, "phase_name");
        explicitOrder = ReadIntProperty(graphItem, "order")
            ?? ReadIntProperty(graphItem, "number");
        if (!string.IsNullOrWhiteSpace(phaseName))
        {
            return true;
        }

        if (graphItem["mechanic"] is JObject mechanicObject)
        {
            phaseName = ReadStringProperty(mechanicObject, "name");
            explicitOrder = ReadIntProperty(mechanicObject, "order")
                ?? ReadIntProperty(mechanicObject, "number");
            return !string.IsNullOrWhiteSpace(phaseName);
        }

        phaseName = ReadStringProperty(graphItem, "name");
        if (!string.IsNullOrWhiteSpace(phaseName) && explicitOrder.HasValue)
        {
            return true;
        }

        return false;
    }

    private static bool TryReadBossPercentage(JObject graphItem, out double bossPercentage)
    {
        if (ReadStringProperty(graphItem, "bestPullsLabel") is { Length: > 0 } label
            && TryReadPropertyValue(graphItem, label, out var labeledValue)
            && TryParsePercent(labeledValue, out bossPercentage))
        {
            return true;
        }

        foreach (var field in PercentFields)
        {
            if (TryReadPropertyValue(graphItem, field, out var value)
                && TryParsePercent(value, out bossPercentage))
            {
                return true;
            }
        }

        bossPercentage = 0;
        return false;
    }

    private static bool TryReadBestPercentLabel(
        JObject source,
        out string phaseName,
        out int? explicitOrder,
        out double bossPercentage)
    {
        phaseName = null;
        explicitOrder = null;
        bossPercentage = 0;

        var label = ReadStringProperty(source, "bestPercent")
            ?? ReadStringProperty(source, "best_percent");
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var percentIndex = label.IndexOf('%');
        if (percentIndex <= 0 || percentIndex >= label.Length - 1)
        {
            return false;
        }

        var percentText = label[..percentIndex].Trim();
        if (!double.TryParse(
                percentText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out bossPercentage)
            || !IsFinite(bossPercentage))
        {
            return false;
        }

        phaseName = label[(percentIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(phaseName))
        {
            return false;
        }

        explicitOrder = TryReadPhaseOrder(phaseName);
        if (!explicitOrder.HasValue)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadBestPercentValue(JObject source, out double bossPercentage)
    {
        bossPercentage = 0;

        var label = ReadStringProperty(source, "bestPercent")
            ?? ReadStringProperty(source, "best_percent");
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var percentIndex = label.IndexOf('%');
        if (percentIndex <= 0)
        {
            return false;
        }

        var percentText = label[..percentIndex].Trim();
        return double.TryParse(
                   percentText,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out bossPercentage)
               && IsFinite(bossPercentage);
    }

    private static int? TryReadPhaseOrder(string phaseName)
    {
        var trimmed = phaseName.Trim();
        if (trimmed.Length < 2 || trimmed[0] is not ('P' or 'p'))
        {
            return null;
        }

        return int.TryParse(
            trimmed[1..],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var order)
            ? order
            : null;
    }

    private static bool IsInstanceProgressLabel(string phaseName)
    {
        var trimmed = phaseName.Trim();
        if (trimmed.Length < 2 || trimmed[0] is not ('I' or 'i'))
        {
            return false;
        }

        return trimmed[1..].All(static character => character is >= '0' and <= '9');
    }

    private static bool TryReadPropertyValue(
        JObject source,
        string propertyName,
        out JToken value)
    {
        var property = source.Properties()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        value = property?.Value;
        return value is not null;
    }

    private static string ReadStringProperty(JObject source, string propertyName)
    {
        return TryReadPropertyValue(source, propertyName, out var value)
            ? value.Type == JTokenType.String ? value.Value<string>() : value.ToString()
            : null;
    }

    private static int? ReadIntProperty(JObject source, string propertyName)
    {
        if (!TryReadPropertyValue(source, propertyName, out var value))
        {
            return null;
        }

        return value.Type switch
        {
            JTokenType.Integer => value.Value<int>(),
            JTokenType.Float => (int)Math.Round(value.Value<double>()),
            JTokenType.String when int.TryParse(value.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool TryParsePercent(JToken value, out double percent)
    {
        switch (value.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                percent = value.Value<double>();
                return IsFinite(percent);
            case JTokenType.String:
                return TryParsePercentString(value.Value<string>(), out percent);
            default:
                percent = 0;
                return false;
        }
    }

    private static bool TryParsePercentString(string value, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.EndsWith("%", StringComparison.Ordinal))
        {
            return false;
        }

        trimmed = trimmed[..^1].Trim();

        return double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out percent)
            && IsFinite(percent);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string NormalizePhaseKey(string phaseName)
    {
        var builder = new StringBuilder();
        var previousWasSeparator = false;
        foreach (var character in phaseName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        if (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static string FormatPercent(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private sealed record ParsedPhase(
        string PhaseKey,
        string PhaseName,
        int Order,
        int GraphIndex,
        double BossPercentage);
}
