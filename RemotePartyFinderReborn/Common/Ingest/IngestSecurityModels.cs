using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RemotePartyFinderReborn;

internal enum ProtectedEndpointCapabilityKind
{
    FflogsJobs,
    FflogsResults,
    FflogsLeasesAbandon,
    TomestoneJobs,
    TomestoneResults,
    TomestoneLeasesAbandon,
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class ProtectedEndpointCapabilityGrant
{
    public string Token { get; set; } = string.Empty;
    public long ExpiresAt { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class ProtectedEndpointCapabilities
{
    public ProtectedEndpointCapabilityGrant FflogsJobs { get; set; } = new();
    public ProtectedEndpointCapabilityGrant FflogsResults { get; set; } = new();
    public ProtectedEndpointCapabilityGrant FflogsLeasesAbandon { get; set; } = new();
    public ProtectedEndpointCapabilityGrant TomestoneJobs { get; set; } = new();
    public ProtectedEndpointCapabilityGrant TomestoneResults { get; set; } = new();
    public ProtectedEndpointCapabilityGrant TomestoneLeasesAbandon { get; set; } = new();
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class ListingDetailCapability
{
    public ulong ListingId { get; set; }
    public string Token { get; set; } = string.Empty;
    public long ExpiresAt { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class ContributeMultipleResponse
{
    public string Status { get; set; } = string.Empty;
    public int Requested { get; set; }
    public int Accepted { get; set; }
    public int Updated { get; set; }
    public int Failed { get; set; }
    public List<ListingDetailCapability> DetailCapabilities { get; set; } = [];
    public ProtectedEndpointCapabilities ProtectedEndpoints { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class ContributePlayersResponse
{
    public string Status { get; set; } = string.Empty;
    public int Requested { get; set; }
    public int Accepted { get; set; }
    public int Updated { get; set; }
    public int Invalid { get; set; }
    public int Failed { get; set; }
    public ProtectedEndpointCapabilities ProtectedEndpoints { get; set; }
}
