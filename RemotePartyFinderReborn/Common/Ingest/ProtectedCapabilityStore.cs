using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class ProtectedCapabilityStore
{
    private sealed record CachedCapability(string Token, DateTimeOffset ExpiresAtUtc);

    private readonly object _lock = new();
    private readonly Dictionary<ProtectedEndpointCapabilityKind, CachedCapability> _capabilities = new();
    private bool _requiresFflogsCapabilities;
    private bool _requiresTomestoneCapabilities;

    internal void ApplyProtectedCapabilities(
        ProtectedEndpointCapabilities protectedEndpoints,
        DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredCapabilities(observedNow);

            _requiresFflogsCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.FflogsJobs,
                protectedEndpoints?.FflogsJobs,
                observedNow);
            _requiresFflogsCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.FflogsResults,
                protectedEndpoints?.FflogsResults,
                observedNow);
            _requiresFflogsCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.FflogsLeasesAbandon,
                protectedEndpoints?.FflogsLeasesAbandon,
                observedNow);

            _requiresTomestoneCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.TomestoneJobs,
                protectedEndpoints?.TomestoneJobs,
                observedNow);
            _requiresTomestoneCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.TomestoneResults,
                protectedEndpoints?.TomestoneResults,
                observedNow);
            _requiresTomestoneCapabilities |= UpsertCapability(
                ProtectedEndpointCapabilityKind.TomestoneLeasesAbandon,
                protectedEndpoints?.TomestoneLeasesAbandon,
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
            return RequiresCapabilitiesFor(kind) && !_capabilities.ContainsKey(kind);
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

    internal void RequireProtectedCapabilities(ProtectedEndpointCapabilityKind kind)
    {
        lock (_lock)
        {
            switch (kind)
            {
                case ProtectedEndpointCapabilityKind.FflogsJobs:
                case ProtectedEndpointCapabilityKind.FflogsResults:
                case ProtectedEndpointCapabilityKind.FflogsLeasesAbandon:
                    _requiresFflogsCapabilities = true;
                    break;
                case ProtectedEndpointCapabilityKind.TomestoneJobs:
                case ProtectedEndpointCapabilityKind.TomestoneResults:
                case ProtectedEndpointCapabilityKind.TomestoneLeasesAbandon:
                    _requiresTomestoneCapabilities = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }

    private bool RequiresCapabilitiesFor(ProtectedEndpointCapabilityKind kind)
    {
        return kind switch
        {
            ProtectedEndpointCapabilityKind.FflogsJobs or
            ProtectedEndpointCapabilityKind.FflogsResults or
            ProtectedEndpointCapabilityKind.FflogsLeasesAbandon => _requiresFflogsCapabilities,
            ProtectedEndpointCapabilityKind.TomestoneJobs or
            ProtectedEndpointCapabilityKind.TomestoneResults or
            ProtectedEndpointCapabilityKind.TomestoneLeasesAbandon => _requiresTomestoneCapabilities,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
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
