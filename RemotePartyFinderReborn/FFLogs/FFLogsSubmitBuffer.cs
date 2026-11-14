using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsSubmitBuffer
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ParseResult> _pendingSubmitResults = new(StringComparer.Ordinal);

    internal static string GetParseResultKey(ParseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{result.ContentId}:{result.ZoneId}:{result.DifficultyId}";
    }

    public bool HasPendingResults
    {
        get
        {
            lock (_sync)
            {
                return _pendingSubmitResults.Count > 0;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pendingSubmitResults.Count;
            }
        }
    }

    public void QueueSubmitResults(IEnumerable<ParseResult> freshResults)
    {
        ArgumentNullException.ThrowIfNull(freshResults);

        lock (_sync)
        {
            foreach (var result in freshResults)
            {
                _pendingSubmitResults[GetParseResultKey(result)] = result;
            }
        }
    }

    public List<ParseResult> BuildSubmitBatch(List<ParseResult> freshResults)
    {
        ArgumentNullException.ThrowIfNull(freshResults);

        lock (_sync)
        {
            foreach (var result in freshResults)
            {
                _pendingSubmitResults[GetParseResultKey(result)] = result;
            }

            if (_pendingSubmitResults.Count == 0)
            {
                return [];
            }

            var batch = _pendingSubmitResults.Values.ToList();
            _pendingSubmitResults.Clear();
            return batch;
        }
    }

    public void RequeueSubmitBatch(IEnumerable<ParseResult> failedBatch)
    {
        ArgumentNullException.ThrowIfNull(failedBatch);

        lock (_sync)
        {
            foreach (var result in failedBatch)
            {
                _pendingSubmitResults[GetParseResultKey(result)] = result;
            }
        }
    }

    public List<ParseResult> DrainPendingResults()
    {
        lock (_sync)
        {
            if (_pendingSubmitResults.Count == 0)
            {
                return [];
            }

            var drained = _pendingSubmitResults.Values.ToList();
            _pendingSubmitResults.Clear();
            return drained;
        }
    }
}
