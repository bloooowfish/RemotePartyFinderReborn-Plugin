using System;
using System.Collections.Generic;

namespace RemotePartyFinderReborn;

internal sealed record FFLogsBatchProcessResult(
    IReadOnlyList<ParseResult> ProcessedResults,
    bool HadTransientFailure,
    bool HitRateLimitCooldown,
    TimeSpan CooldownRemaining,
    bool ShouldAbandonRemainingLeases);
