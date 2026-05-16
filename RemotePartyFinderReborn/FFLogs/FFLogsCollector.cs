using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;

namespace RemotePartyFinderReborn;

internal interface IFFLogsApiClient
{
    bool TryGetRateLimitRemaining(out TimeSpan remaining);
    DateTime RateLimitCooldownUntilUtc { get; }
    void ResetRateLimitCooldown();
    Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
        List<FFLogsClient.CandidateCharacterQuery> queries,
        int zoneId,
        int? difficultyId,
        int recentReportsLimit,
        CancellationToken cancellationToken);
    Task<OperationOutcome<Dictionary<string, double>>> FetchBestBossPercentByReportAsync(
        List<string> reportCodes,
        int encounterId,
        int? difficultyId,
        CancellationToken cancellationToken);
}

internal interface IFFLogsTimeProvider
{
    DateTime UtcNow { get; }
}

internal interface ITomestoneApiClient
{
    bool IsConfigured { get; }
    bool TryGetRateLimitRemaining(out TimeSpan remaining);
    DateTime RateLimitCooldownUntilUtc { get; }
    void ResetRateLimitCooldown();
    Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
        string name,
        string server,
        uint zoneId,
        uint encounterId,
        CancellationToken cancellationToken);
}

internal sealed record FFLogsCollectorSeams(
    IIngestHttpSender IngestHttpSender,
    IFFLogsApiClient ApiClient,
    IFFLogsTimeProvider TimeProvider,
    ITomestoneApiClient TomestoneApiClient,
    Configuration Configuration,
    Func<TimeSpan, CancellationToken, Task> TomestoneRequestDelayAsync);

public class FFLogsCollector : IDisposable
{
    private sealed class HttpClientIngestHttpSender(HttpClient httpClient) : IIngestHttpSender
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => httpClient.SendAsync(request, cancellationToken);
    }

    private sealed class FFLogsApiClientAdapter(FFLogsClient client) : IFFLogsApiClient
    {
        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
            => client.TryGetRateLimitRemaining(out remaining);

        public DateTime RateLimitCooldownUntilUtc
            => client.RateLimitCooldownUntilUtc;

        public void ResetRateLimitCooldown()
            => client.ResetRateLimitCooldown();

        public Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
            List<FFLogsClient.CandidateCharacterQuery> queries,
            int zoneId,
            int? difficultyId,
            int recentReportsLimit,
            CancellationToken cancellationToken)
            => client.FetchCharacterCandidateDataBatchOutcomeAsync(
                queries,
                zoneId,
                difficultyId,
                recentReportsLimit,
                cancellationToken);

        public Task<OperationOutcome<Dictionary<string, double>>> FetchBestBossPercentByReportAsync(
            List<string> reportCodes,
            int encounterId,
            int? difficultyId,
            CancellationToken cancellationToken)
            => client.FetchBestBossPercentByReportOutcomeAsync(
                reportCodes,
                encounterId,
                difficultyId,
                cancellationToken);
    }

    private sealed class SystemFFLogsTimeProvider : IFFLogsTimeProvider
    {
        public DateTime UtcNow
            => DateTime.UtcNow;
    }

    private sealed class NoOpTomestoneApiClient : ITomestoneApiClient
    {
        public bool IsConfigured => false;

        public DateTime RateLimitCooldownUntilUtc => DateTime.MinValue;

        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        public void ResetRateLimitCooldown()
        {
        }

        public Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
            string name,
            string server,
            uint zoneId,
            uint encounterId,
            CancellationToken cancellationToken)
            => Task.FromResult(OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty));
    }

    internal sealed class TomestoneApiClientAdapter(
        TomestoneApiClient client,
        Configuration configuration,
        Action<string> debugLog,
        Action<string> warningLog) : ITomestoneApiClient
    {
        public bool IsConfigured
            => configuration.EnableTomestoneProgressEnrichment
               && !string.IsNullOrWhiteSpace(configuration.TomestoneApiKey);

        public DateTime RateLimitCooldownUntilUtc
            => client.RateLimitCooldownUntilUtc;

        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
            => client.TryGetRateLimitRemaining(out remaining);

        public void ResetRateLimitCooldown()
            => client.ResetRateLimitCooldown();

        public async Task<OperationOutcome<TomestoneProgressData>> FetchProgressionDataAsync(
            string name,
            string server,
            uint zoneId,
            uint encounterId,
            CancellationToken cancellationToken)
        {
            if (!IsConfigured)
            {
                debugLog("Tomestone progress enrichment is disabled or unconfigured.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            }

            if (!TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var encounterParams))
            {
                debugLog($"Tomestone progress skipped for unmapped encounter zone={zoneId}, encounter={encounterId}.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            }

            var path = BuildProgressionGraphPath(name, server, encounterParams);
            var targetDescription = FormatTomestoneProgressTarget(zoneId, encounterId, encounterParams);
            var rawOutcome = await client.GetRawOutcomeAsync(path, cancellationToken);
            if (!rawOutcome.Succeeded)
            {
                debugLog($"Tomestone progress returned no response for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Failure(
                    rawOutcome.TransientFailure,
                    rawOutcome.ErrorMessage ?? "Tomestone progress returned no response.");
            }

            var response = rawOutcome.Value!;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                debugLog($"Tomestone progress not found for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                debugLog($"Tomestone progress had no content for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Success(TomestoneProgressData.Empty);
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                warningLog($"Tomestone progress failed with {(int)response.StatusCode} for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Failure(
                    IsTransientStatusCode(response.StatusCode),
                    $"Tomestone progress failed with {(int)response.StatusCode}.");
            }

            try
            {
                JToken.Parse(response.Body);
                var progress = TomestoneProgressParser.ParseProgress(response.Body);
                debugLog(
                    $"Tomestone progress parsed {progress.Phases.Count} phase(s), boss={progress.BossPercentage?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none"} for {targetDescription}.");
                return OperationOutcome<TomestoneProgressData>.Success(progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warningLog($"Tomestone progress parse failed for {targetDescription}: {ex.GetType().Name}: {ex.Message}");
                return OperationOutcome<TomestoneProgressData>.Failure(
                    transientFailure: false,
                    $"Tomestone progress parse failed: {ex.Message}");
            }
        }

        private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return statusCode == HttpStatusCode.RequestTimeout
                || code == 429
                || code >= 500;
        }

        private static string FormatTomestoneProgressTarget(
            uint zoneId,
            uint encounterId,
            TomestoneEncounterParams encounterParams)
        {
            return
                $"fflogs zone={zoneId}, encounter={encounterId}; "
                + $"tomestone expansion={encounterParams.Expansion}, "
                + $"zone={encounterParams.Category}, "
                + $"encounter={encounterParams.Encounter}, "
                + $"kind={encounterParams.ProgressKind}";
        }

        private static string BuildProgressionGraphPath(
            string name,
            string server,
            TomestoneEncounterParams encounterParams)
        {
            static string Encode(string value)
                => Uri.EscapeDataString((value ?? string.Empty).Trim());

            return
                $"character/progression-graph/{Encode(server)}/{Encode(name)}"
                + $"?expansion={Encode(encounterParams.Expansion)}"
                + $"&zone={Encode(encounterParams.Category)}"
                + $"&encounter={Encode(encounterParams.Encounter)}";
        }
    }

    private Configuration Configuration { get; set; }
    private Action<string> InfoLog { get; set; } = static _ => { };
    private Action<string> WarningLog { get; set; } = static _ => { };
    private Action<string> ErrorLog { get; set; } = static _ => { };
    private Action<string> DebugLog { get; set; } = static _ => { };
    private FFLogsCollectorSeams Seams { get; set; }
    private FFLogsSubmitBuffer SubmitBuffer { get; set; }
    private FFLogsWorkerPolicy WorkerPolicy { get; set; }
    private FFLogsResultSubmitter ResultSubmitter { get; set; }
    private FFLogsLeaseAbandoner LeaseAbandoner { get; set; }
    private FFLogsJobLeaseClient JobLeaseClient { get; set; }
    private FFLogsBatchProcessor BatchProcessor { get; set; }
    private FFLogsBackgroundWorker BackgroundWorker { get; set; }
    private IReadOnlyList<IDisposable> OwnedDisposables { get; set; } = [];
    private System.Threading.CancellationTokenSource Cts { get; set; } = new();

    internal static FFLogsCollectorSeams CreateSeams(
        IIngestHttpSender ingestHttpSender,
        IFFLogsApiClient apiClient,
        IFFLogsTimeProvider timeProvider,
        ITomestoneApiClient tomestoneApiClient = null,
        Configuration configuration = null,
        Func<TimeSpan, CancellationToken, Task> tomestoneRequestDelayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(ingestHttpSender);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new FFLogsCollectorSeams(
            ingestHttpSender,
            apiClient,
            timeProvider,
            tomestoneApiClient ?? new NoOpTomestoneApiClient(),
            configuration ?? new Configuration(),
            tomestoneRequestDelayAsync ?? Task.Delay);
    }

    internal static FFLogsCollector CreateForTesting(
        Configuration configuration,
        FFLogsCollectorSeams seams,
        FFLogsSubmitBuffer submitBuffer,
        FFLogsWorkerPolicy workerPolicy,
        FFLogsJobLeaseClient jobLeaseClient = null,
        FFLogsResultSubmitter resultSubmitter = null,
        FFLogsBatchProcessor batchProcessor = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(submitBuffer);
        ArgumentNullException.ThrowIfNull(workerPolicy);

        var collector = new FFLogsCollector();
        collector.Initialize(
            configuration,
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            seams,
            submitBuffer,
            workerPolicy,
            resultSubmitter ?? new FFLogsResultSubmitter(seams, submitBuffer),
            new FFLogsLeaseAbandoner(seams),
            jobLeaseClient ?? new FFLogsJobLeaseClient(seams),
            batchProcessor ?? new FFLogsBatchProcessor(seams),
            [],
            startWorker: false);
        return collector;
    }

    internal Task RunWorkerLoopForTestingAsync()
        => BackgroundWorker.RunAsync(Cts.Token);

    private FFLogsCollector()
    {
    }

    public FFLogsCollector(Plugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var httpClient = new HttpClient();
        var ffLogsClient = new FFLogsClient(plugin.Configuration);
        var workerRegionProvider = new DalamudFFLogsWorkerRegionProvider(
            plugin.Framework,
            plugin.ObjectTable,
            message => Plugin.Log.Warning(message));
        var seams = CreateSeams(
            new HttpClientIngestHttpSender(httpClient),
            new FFLogsApiClientAdapter(ffLogsClient),
            new SystemFFLogsTimeProvider(),
            new TomestoneApiClientAdapter(
                plugin.TomestoneApiClient,
                plugin.Configuration,
                message => Plugin.Log.Debug(message),
                message => Plugin.Log.Warning(message)),
            plugin.Configuration);
        var submitBuffer = new FFLogsSubmitBuffer();
        Initialize(
            plugin.Configuration,
            message => Plugin.Log.Info(message),
            message => Plugin.Log.Warning(message),
            message => Plugin.Log.Error(message),
            message => Plugin.Log.Debug(message),
            seams,
            submitBuffer,
            new FFLogsWorkerPolicy(plugin.Configuration, message => Plugin.Log.Warning(message), seams.TimeProvider),
            new FFLogsResultSubmitter(seams, submitBuffer),
            new FFLogsLeaseAbandoner(seams),
            new FFLogsJobLeaseClient(
                seams,
                workerRegionProvider),
            new FFLogsBatchProcessor(seams),
            [ffLogsClient, httpClient, workerRegionProvider],
            startWorker: true);
    }

    public void Dispose()
    {
        Cts.Cancel();
        foreach (var disposable in OwnedDisposables)
        {
            disposable.Dispose();
        }
    }

    private void Initialize(
        Configuration configuration,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<string> debugLog,
        FFLogsCollectorSeams seams,
        FFLogsSubmitBuffer submitBuffer,
        FFLogsWorkerPolicy workerPolicy,
        FFLogsResultSubmitter resultSubmitter,
        FFLogsLeaseAbandoner leaseAbandoner,
        FFLogsJobLeaseClient jobLeaseClient,
        FFLogsBatchProcessor batchProcessor,
        IReadOnlyList<IDisposable> ownedDisposables,
        bool startWorker)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        InfoLog = infoLog ?? throw new ArgumentNullException(nameof(infoLog));
        WarningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        ErrorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
        DebugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        Seams = seams ?? throw new ArgumentNullException(nameof(seams));
        SubmitBuffer = submitBuffer ?? throw new ArgumentNullException(nameof(submitBuffer));
        WorkerPolicy = workerPolicy ?? throw new ArgumentNullException(nameof(workerPolicy));
        ResultSubmitter = resultSubmitter ?? throw new ArgumentNullException(nameof(resultSubmitter));
        LeaseAbandoner = leaseAbandoner ?? throw new ArgumentNullException(nameof(leaseAbandoner));
        JobLeaseClient = jobLeaseClient ?? throw new ArgumentNullException(nameof(jobLeaseClient));
        BatchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
        OwnedDisposables = ownedDisposables ?? throw new ArgumentNullException(nameof(ownedDisposables));
        BackgroundWorker = new FFLogsBackgroundWorker(
            Configuration,
            Seams.ApiClient,
            WorkerPolicy,
            JobLeaseClient,
            BatchProcessor,
            ResultSubmitter,
            LeaseAbandoner,
            InfoLog,
            WarningLog,
            ErrorLog,
            DebugLog);
        if (startWorker)
        {
            StartWorker();
        }
    }

    private void StartWorker()
    {
        Task.Run(() => BackgroundWorker.RunAsync(Cts.Token), Cts.Token);
    }

    public bool TryGetRateLimitCooldownRemaining(out TimeSpan remaining)
        => Seams.ApiClient.TryGetRateLimitRemaining(out remaining);

    public DateTime RateLimitCooldownUntilUtc
        => Seams.ApiClient.RateLimitCooldownUntilUtc;

    public void ResetRateLimitCooldown()
        => Seams.ApiClient.ResetRateLimitCooldown();
}
