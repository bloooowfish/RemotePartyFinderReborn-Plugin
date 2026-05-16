using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class ProtectedCapabilityStore
{
    private sealed record CachedCapability(string Token, DateTimeOffset ExpiresAtUtc);

    private readonly object _lock = new();
    private readonly Dictionary<ProtectedEndpointCapabilityKind, CachedCapability> _capabilities = new();
    private bool _requiresCapabilities;

    internal void ApplyProtectedCapabilities(
        ProtectedEndpointCapabilities protectedEndpoints,
        DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredCapabilities(observedNow);

            _requiresCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.FflogsJobs,
                protectedEndpoints?.FflogsJobs,
                observedNow);
            _requiresCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.FflogsResults,
                protectedEndpoints?.FflogsResults,
                observedNow);
            _requiresCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.FflogsLeasesAbandon,
                protectedEndpoints?.FflogsLeasesAbandon,
                observedNow);
        }
    }

    internal bool ShouldDeferProtectedRequest(
        ProtectedEndpointCapabilityKind kind,
        DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredCapabilities(observedNow);
            return _requiresCapabilities && !_capabilities.ContainsKey(kind);
        }
    }

    internal bool TryGetProtectedCapability(
        ProtectedEndpointCapabilityKind kind,
        out string token,
        DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredCapabilities(observedNow);
            if (_capabilities.TryGetValue(kind, out var cached))
            {
                token = cached.Token;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    internal void InvalidateProtectedCapability(ProtectedEndpointCapabilityKind kind)
    {
        lock (_lock)
        {
            _capabilities.Remove(kind);
        }
    }

    internal void RequireProtectedCapabilities()
    {
        lock (_lock)
        {
            _requiresCapabilities = true;
        }
    }

    private bool UpsertCapability(
        ProtectedEndpointCapabilityKind kind,
        ProtectedEndpointCapabilityGrant grant,
        DateTimeOffset now)
    {
        var cached = BuildCachedCapability(grant?.Token, grant?.ExpiresAt ?? 0, now);
        if (cached == null)
        {
            return false;
        }

        _capabilities[kind] = cached;
        return true;
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
        foreach (var (kind, cached) in _capabilities.ToArray())
        {
            if (cached.ExpiresAtUtc <= now)
            {
                _capabilities.Remove(kind);
            }
        }
    }
}
