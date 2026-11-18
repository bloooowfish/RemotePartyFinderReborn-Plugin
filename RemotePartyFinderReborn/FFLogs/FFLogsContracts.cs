using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

#nullable enable

namespace RemotePartyFinderReborn;

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class ParseJob
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Region { get; set; } = "";
    public List<ParseJobCandidateServer> CandidateServers { get; set; } = new();
    public uint ZoneId { get; set; }
    public int DifficultyId { get; set; }
    public uint EncounterId { get; set; }
    public uint? SecondaryEncounterId { get; set; }
    public List<uint> ZoneEncounters { get; set; } = new();
    public string? TomestoneExpansion { get; set; }
    public string? TomestoneCategory { get; set; }
    public string? TomestoneClearsCategory { get; set; }
    public string? TomestoneZoneSlug { get; set; }
    public string? TomestoneEncounterSlug { get; set; }
    public List<string> TomestoneEncounterNames { get; set; } = new();
    public string? TomestoneSecondaryEncounterSlug { get; set; }
    public List<string> TomestoneSecondaryEncounterNames { get; set; } = new();
    public string? TomestoneProgressKind { get; set; }
    public string LeaseToken { get; set; } = "";
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class ParseJobCandidateServer
{
    public string Server { get; set; } = "";
    public string Region { get; set; } = "";
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class ParseResult
{
    public ulong ContentId { get; set; }
    public uint ZoneId { get; set; }
    public int DifficultyId { get; set; }
    public Dictionary<int, double> Encounters { get; set; } = new();
    public Dictionary<int, int> ClearCounts { get; set; } = new();
    public List<int> FflogsQueriedEncounters { get; set; } = new();
    public bool IsHidden { get; set; }
    public bool IsEstimated { get; set; }
    public string MatchedServer { get; set; } = "";
    public string LeaseToken { get; set; } = "";
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class AbandonFflogsLease
{
    public ulong ContentId { get; set; }
    public uint ZoneId { get; set; }
    public int DifficultyId { get; set; }
    public string LeaseToken { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ProcessorKind { get; set; } = "";
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class ContributeFflogsResultsResponse
{
    public string Status { get; set; } = "";
    public int Submitted { get; set; }
    public int Accepted { get; set; }
    public int Updated { get; set; }
    public int Rejected { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class ContributeFflogsLeaseAbandonResponse
{
    public string Status { get; set; } = "";
    public int Submitted { get; set; }
    public int Released { get; set; }
    public int Rejected { get; set; }
}
