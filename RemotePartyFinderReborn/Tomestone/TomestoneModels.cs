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
    double? BossPercentage,
    bool Cleared = false)
{
    public static TomestoneProgressData Empty { get; } = new([], null);
    public static TomestoneProgressData ClearedProgress { get; } = new([], null, true);
}

public sealed record TomestoneEncounterParams(
    string Expansion,
    string Category,
    string Encounter,
    TomestoneProgressKind ProgressKind = TomestoneProgressKind.PhaseProgress,
    IReadOnlyList<string> TargetCanonicalNames = null);
