using System.Collections.Generic;
using System.Net;

namespace RemotePartyFinderReborn.Tomestone;

public sealed class TomestoneRawResponse
{
    public HttpStatusCode StatusCode { get; init; }
    public string Body { get; init; } = string.Empty;
}

public enum TomestoneProgressKind
{
    PhaseProgress,
    BossPercentage,
}

public sealed record TomestoneProgressData(
    IReadOnlyList<PhaseProgress> Phases,
    double? BossPercentage)
{
    public static TomestoneProgressData Empty { get; } = new([], null);
}

public sealed record TomestoneEncounterParams(
    string Expansion,
    string Category,
    string Encounter,
    TomestoneProgressKind ProgressKind = TomestoneProgressKind.PhaseProgress);
