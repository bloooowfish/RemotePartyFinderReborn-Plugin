using System;
using System.Collections.Generic;
using System.Net;

namespace RemotePartyFinderReborn.Tomestone;

public sealed class TomestoneRawResponse
{
    public HttpStatusCode StatusCode { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool ActivityHidden { get; init; }
}

public enum TomestoneProgressKind
{
    PhaseProgress,
    BossPercentage,
}

public sealed record TomestoneProgressData(
    IReadOnlyList<PhaseProgress> Phases,
    double? BossPercentage,
    bool Cleared = false,
    bool IsAuthoritative = true)
{
    public static TomestoneProgressData Empty { get; } = new([], null);
    public static TomestoneProgressData Skipped { get; } = new([], null, IsAuthoritative: false);
    public static TomestoneProgressData ClearedProgress { get; } = new([], null, true);
}

public sealed record TomestoneEncounterParams(
    string Expansion,
    string Category,
    string Encounter,
    TomestoneProgressKind ProgressKind = TomestoneProgressKind.PhaseProgress,
    IReadOnlyList<string> TargetCanonicalNames = null,
    string ClearsCategory = null,
    string ActivityCategory = null)
{
    /// <summary>
    /// Category query parameter used by Tomestone web <c>/clears?encounterCategory=...</c>.
    /// </summary>
    public string EffectiveClearsCategory => !string.IsNullOrWhiteSpace(ClearsCategory)
        ? ClearsCategory
        : Category switch
        {
            "ultimates" => "ultimate",
            "trials-extreme" => "extreme",
            "chaotic" => "chaotic",
            _ => "savage",
        };

    /// <summary>
    /// Category query parameter used by Tomestone web <c>/activity?category=...</c>.
    /// </summary>
    public string EffectiveActivityCategory => !string.IsNullOrWhiteSpace(ActivityCategory)
        ? ActivityCategory
        : Category switch
        {
            "ultimates" => "ultimates",
            "trials-extreme" => "trials",
            "chaotic" => "chaotic",
            _ => "raids",
        };
}

public sealed record TomestoneClearsRankingsOutcome(
    IReadOnlyDictionary<int, double> Encounters,
    IReadOnlyDictionary<int, int> ClearCounts,
    int RequestedEntryCount,
    IReadOnlyList<int> SeenEncounters,
    bool HasRankingsSection)
{
    public static TomestoneClearsRankingsOutcome Empty { get; } = new(
        new Dictionary<int, double>(),
        new Dictionary<int, int>(),
        0,
        [],
        false);
}

public enum TomestoneChallengeKind
{
    Http403,
    Http429,
    CfMitigatedHeader,
    CloudflareJustAMoment,
    Turnstile,
    Recaptcha,
    HCaptcha,
}

public sealed record TomestoneChallengeInfo(
    TomestoneChallengeKind Kind,
    string Label,
    TimeSpan? RetryAfter = null);

public sealed class TomestoneChallengeException : Exception
{
    public TomestoneChallengeException(TomestoneChallengeInfo challenge, string message)
        : base(message)
    {
        Challenge = challenge;
    }

    public TomestoneChallengeInfo Challenge { get; }
}
