using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class DetailCapabilityStore
{
    private sealed record CachedCapability(string Token, DateTimeOffset ExpiresAtUtc);

    private readonly object _lock = new();
    private readonly Dictionary<uint, CachedCapability> _capabilities = new();
    private bool _requiresCapabilities;

    internal void ApplyDetailCapabilities(
        IEnumerable<ListingDetailCapability> detailCapabilities,
        DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredCapabilities(observedNow);

            if (detailCapabilities == null)
            {
                return;
            }

            foreach (var detailCapability in detailCapabilities)
            {
                if (detailCapability == null || detailCapability.ListingId == 0)
                {
                    continue;
                }

                var cached = BuildCachedCapability(detailCapability.Token, detailCapability.ExpiresAt, observedNow);
                if (cached == null)
                {
                    continue;
                }

                _capabilities[detailCapability.ListingId] = cached;
                _requiresCapabilities = true;
            }
        }
    }

    internal bool ShouldDeferDetailRequest(uint listingId, DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredCapabilities(observedNow);
            return _requiresCapabilities && !_capabilities.ContainsKey(listingId);
        }
    }

    internal bool TryGetDetailCapability(
        uint listingId,
        out string token,
        DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredCapabilities(observedNow);
            if (_capabilities.TryGetValue(listingId, out var cached))
            {
                token = cached.Token;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    internal void InvalidateDetailCapability(uint listingId)
    {
        lock (_lock)
        {
            _capabilities.Remove(listingId);
        }
    }

    internal void MarkDetailCapabilitiesRequired()
    {
        lock (_lock)
        {
            _requiresCapabilities = true;
        }
    }

    private static CachedCapability BuildCachedCapability(
        string token,
        long expiresAtUnixSeconds,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token) || expiresAtUnixSeconds <= 0)
        {
            return null;
        }

        DateTimeOffset expiresAtUtc;
        try
        {
            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (expiresAtUtc <= now)
        {
            return null;
        }

        return new CachedCapability(token, expiresAtUtc);
    }

    private void PruneExpiredCapabilities(DateTimeOffset now)
    {
        foreach (var (listingId, cached) in _capabilities.ToArray())
        {
            if (cached.ExpiresAtUtc <= now)
            {
                _capabilities.Remove(listingId);
            }
        }
    }
}
