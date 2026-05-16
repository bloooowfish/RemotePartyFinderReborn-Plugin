using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RemotePartyFinderReborn.Tomestone;

namespace RemotePartyFinderReborn.Tests;

internal sealed class ManualFFLogsTimeProvider : IFFLogsTimeProvider {
    public DateTime UtcNow { get; set; }
}

internal sealed class RecordingFFLogsWarningSink {
    public List<string> Messages { get; } = [];

    public void Warning(string message) {
        Messages.Add(message);
    }
}

internal sealed class StubFFLogsIngestHttpSender : IIngestHttpSender {
    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSendAsync { get; set; }
        = static (_, cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Requests.Add(request);
        return OnSendAsync(request, cancellationToken);
    }
}

internal sealed class StubFFLogsApiClient : IFFLogsApiClient {
    public Func<(bool HasCooldown, TimeSpan Remaining)> OnTryGetRateLimitRemaining { get; set; }
        = static () => (false, TimeSpan.Zero);

    public Func<List<FFLogsClient.CandidateCharacterQuery>, int, int?, int, CancellationToken, Task<Dictionary<string, FFLogsClient.CharacterFetchedData>>> OnFetchCharacterCandidateDataBatchAsync { get; set; }
        = static (_, _, _, _, _) => Task.FromResult(new Dictionary<string, FFLogsClient.CharacterFetchedData>());

    public Func<List<FFLogsClient.CandidateCharacterQuery>, int, int?, int, CancellationToken, Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>>>? OnFetchCharacterCandidateDataBatchOutcomeAsync { get; set; }
        = null;

    public Func<List<string>, int, int?, CancellationToken, Task<Dictionary<string, double>>> OnFetchBestBossPercentByReportAsync { get; set; }
        = static (_, _, _, _) => Task.FromResult(new Dictionary<string, double>());

    public Func<List<string>, int, int?, CancellationToken, Task<OperationOutcome<Dictionary<string, double>>>>? OnFetchBestBossPercentByReportOutcomeAsync { get; set; }
        = null;

    public DateTime RateLimitCooldownUntilUtc { get; set; }

    public bool TryGetRateLimitRemaining(out TimeSpan remaining) {
        var result = OnTryGetRateLimitRemaining();
        remaining = result.Remaining;
        return result.HasCooldown;
    }

    public void ResetRateLimitCooldown() {
        RateLimitCooldownUntilUtc = DateTime.MinValue;
    }

    public async Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
        List<FFLogsClient.CandidateCharacterQuery> queries,
        int zoneId,
        int? difficultyId,
        int recentReportsLimit,
        CancellationToken cancellationToken) {
        if (OnFetchCharacterCandidateDataBatchOutcomeAsync != null) {
            return await OnFetchCharacterCandidateDataBatchOutcomeAsync(
                queries,
                zoneId,
                difficultyId,
                recentReportsLimit,
                cancellationToken);
        }

        try {
            var result = await OnFetchCharacterCandidateDataBatchAsync(
                queries,
                zoneId,
                difficultyId,
                recentReportsLimit,
                cancellationToken);
            return OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>.Success(result);
        }
        catch (Exception ex) {
            return OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>.Failure(true, ex.Message);
        }
    }

    public async Task<OperationOutcome<Dictionary<string, double>>> FetchBestBossPercentByReportAsync(
        List<string> reportCodes,
        int encounterId,
        int? difficultyId,
        CancellationToken cancellationToken) {
        if (OnFetchBestBossPercentByReportOutcomeAsync != null) {
            return await OnFetchBestBossPercentByReportOutcomeAsync(
                reportCodes,
                encounterId,
                difficultyId,
                cancellationToken);
        }

        try {
            var result = await OnFetchBestBossPercentByReportAsync(
                reportCodes,
                encounterId,
                difficultyId,
                cancellationToken);
            return OperationOutcome<Dictionary<string, double>>.Success(result);
        }
        catch (Exception ex) {
            return OperationOutcome<Dictionary<string, double>>.Failure(true, ex.Message);
        }
    }
}

internal sealed class StubTomestoneApiClient : ITomestoneApiClient {
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

    public bool TryGetRateLimitRemaining(out TimeSpan remaining) {
        var result = OnTryGetRateLimitRemaining();
        remaining = result.Remaining;
        return result.HasCooldown;
    }

    public void ResetRateLimitCooldown() {
        RateLimitCooldownUntilUtc = DateTime.MinValue;
    }

    public async Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken) {
        lock (_requestLock) {
            Requests.Add((name, server, zoneId, encounterId));
        }

        if (OnFetchProgressionDataOutcomeAsync != null) {
            return await OnFetchProgressionDataOutcomeAsync(name, server, zoneId, encounterId, cancellationToken);
        }

        try {
            var result = await OnFetchProgressionDataAsync(name, server, zoneId, encounterId, cancellationToken);
            return OperationOutcome<TomestoneProgressData>.Success(result);
        }
        catch (Exception ex) {
            return OperationOutcome<TomestoneProgressData>.Failure(true, ex.Message);
        }
    }
}

internal static class FFLogsTestAssemblyResolver {
    private static int _registered;

    public static void Register() {
        if (Interlocked.Exchange(ref _registered, 1) != 0) {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += static (_, args) => {
            var assemblyName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(assemblyName)) {
                return null;
            }

            var dalamudHome = Environment.GetEnvironmentVariable("DALAMUD_HOME");
            if (string.IsNullOrWhiteSpace(dalamudHome)) {
                dalamudHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XIVLauncher",
                    "addon",
                    "Hooks",
                    "dev"
                );
            }

            var candidatePath = Path.Combine(dalamudHome, assemblyName + ".dll");
            return File.Exists(candidatePath) ? Assembly.LoadFrom(candidatePath) : null;
        };
    }
}
