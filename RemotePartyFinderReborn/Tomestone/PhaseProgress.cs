using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

#nullable enable

namespace RemotePartyFinderReborn;

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class PhaseProgress
{
    public string PhaseKey { get; set; } = "";
    public string PhaseName { get; set; } = "";
    public int Order { get; set; }
    public double? BossPercentage { get; set; }
    public string DisplayText { get; set; } = "";
    public string Source { get; set; } = "tomestone_api";
}
