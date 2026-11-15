using RemotePartyFinderReborn.Tomestone;

namespace RemotePartyFinderReborn.Tests;

internal sealed class StubTomestoneProgressionService : ITomestoneProgressionService
{
    public bool IsConfigured { get; set; } = true;
    public DateTime RateLimitCooldownUntilUtc { get; set; }
    public List<(string Name, string Server, uint ZoneId, uint EncounterId)> Requests { get; } = [];
    private readonly object _requestLock = new();

    public Func<(bool HasCooldown, TimeSpan Remaining)> OnTryGetRateLimitRemaining { get; set; }
        = static () => (false, TimeSpan.Zero);

    public Func<string, string, uint, uint, CancellationToken, Task<TomestoneProgressData>> OnFetchProgressionDataAsync { get; set; }
        = static (_, _, _, _, _) => Task.FromResult(TomestoneProgressData.Empty);

    public Func<string, string, uint, uint, CancellationToken, Task<OperationOutcome<TomestoneProgressData>>>? OnFetchProgressionDataOutcomeAsync { get; set; }
        = null;

    public bool TryGetRateLimitRemaining(out TimeSpan remaining)
    {
        var result = OnTryGetRateLimitRemaining();
        remaining = result.Remaining;
        return result.HasCooldown;
    }

    public void ResetRateLimitCooldown()
    {
        RateLimitCooldownUntilUtc = DateTime.MinValue;
    }

    public async Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken)
    {
        lock (_requestLock)
        {
            Requests.Add((name, server, zoneId, encounterId));
        }

        if (OnFetchProgressionDataOutcomeAsync != null)
        {
            return await OnFetchProgressionDataOutcomeAsync(name, server, zoneId, encounterId, cancellationToken);
        }

        try
        {
            var result = await OnFetchProgressionDataAsync(name, server, zoneId, encounterId, cancellationToken);
            return OperationOutcome<TomestoneProgressData>.Success(result);
        }
        catch (Exception ex)
        {
            return OperationOutcome<TomestoneProgressData>.Failure(true, ex.Message);
        }
    }

    public IReadOnlyDictionary<uint, TomestoneProgressData> TryGetCachedZoneSiblingProgress(
        string name,
        string server,
        uint zoneId,
        IReadOnlyCollection<uint> excludeEncounterIds)
        => new Dictionary<uint, TomestoneProgressData>();
}
