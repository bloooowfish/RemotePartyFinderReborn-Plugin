using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

#nullable enable

namespace RemotePartyFinderReborn.Tomestone;

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class TomestoneJob
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Region { get; set; } = "";
    public uint ZoneId { get; set; }
    public int DifficultyId { get; set; }
    public uint EncounterId { get; set; }
    public uint? SecondaryEncounterId { get; set; }
    public string LeaseToken { get; set; } = "";
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class TomestoneResult
{
    public ulong ContentId { get; set; }
    public uint ZoneId { get; set; }
    public int DifficultyId { get; set; }
    public uint EncounterId { get; set; }
    public Dictionary<int, double> Encounters { get; set; } = new();
    public Dictionary<int, int> ClearCounts { get; set; } = new();
    public Dictionary<int, double> BossPercentages { get; set; } = new();
    public Dictionary<int, List<PhaseProgress>> PhaseProgress { get; set; } = new();
    public List<int> TomestoneQueriedEncounters { get; set; } = new();
    public string MatchedServer { get; set; } = "";
    public string SourceKind { get; set; } = "tomestone_api";
    public string LeaseToken { get; set; } = "";
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class AbandonTomestoneLease
{
    public ulong ContentId { get; set; }
    public uint ZoneId { get; set; }
    public int DifficultyId { get; set; }
    public uint EncounterId { get; set; }
    public string LeaseToken { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ProcessorKind { get; set; } = "dalamud_plugin_worker";
    public string SourceKind { get; set; } = "tomestone_api";
    public string FailureCode { get; set; } = "";
    public string FailureFingerprint { get; set; } = "";
    public string Message { get; set; } = "";
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class ContributeTomestoneResultsResponse
{
    public string Status { get; set; } = "";
    public int Submitted { get; set; }
    public int Accepted { get; set; }
    public int Updated { get; set; }
    public int Rejected { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public class ContributeTomestoneLeaseAbandonResponse
{
    public string Status { get; set; } = "";
    public int Submitted { get; set; }
    public int Released { get; set; }
    public int Rejected { get; set; }
}
