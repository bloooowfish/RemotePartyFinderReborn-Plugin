using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn.Tomestone;

internal sealed class TomestoneSubmitBuffer
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TomestoneResult> _pendingSubmitResults = new(StringComparer.Ordinal);

    internal static string GetTomestoneResultKey(TomestoneResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{result.ContentId}:{result.ZoneId}:{result.DifficultyId}:{result.EncounterId}";
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

    public void QueueSubmitResults(IEnumerable<TomestoneResult> freshResults)
    {
        ArgumentNullException.ThrowIfNull(freshResults);

        lock (_sync)
        {
            foreach (var result in freshResults)
            {
                _pendingSubmitResults[GetTomestoneResultKey(result)] = result;
            }
        }
    }

    public List<TomestoneResult> BuildSubmitBatch(List<TomestoneResult> freshResults)
    {
        ArgumentNullException.ThrowIfNull(freshResults);

        lock (_sync)
        {
            foreach (var result in freshResults)
            {
                _pendingSubmitResults[GetTomestoneResultKey(result)] = result;
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

    public void RequeueSubmitBatch(IEnumerable<TomestoneResult> failedBatch)
    {
        ArgumentNullException.ThrowIfNull(failedBatch);

        lock (_sync)
        {
            foreach (var result in failedBatch)
            {
                _pendingSubmitResults[GetTomestoneResultKey(result)] = result;
            }
        }
    }

    public List<TomestoneResult> DrainPendingResults()
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
