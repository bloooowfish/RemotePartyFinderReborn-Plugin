using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal interface IFFLogsApiClient
{
    bool TryGetRateLimitRemaining(out TimeSpan remaining);
    DateTime RateLimitCooldownUntilUtc { get; }
    void ResetRateLimitCooldown();
    Task<OperationOutcome<bool>> VerifyCredentialsAsync(CancellationToken cancellationToken)
        => Task.FromResult(OperationOutcome<bool>.Success(true));
    Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
        List<FFLogsClient.CandidateCharacterQuery> queries,
        int zoneId,
        int? difficultyId,
        CancellationToken cancellationToken);
}

internal interface IFFLogsTimeProvider
{
    DateTime UtcNow { get; }
}

internal sealed record FFLogsCollectorSeams(
    IIngestHttpSender IngestHttpSender,
    IFFLogsApiClient ApiClient,
    IFFLogsTimeProvider TimeProvider);

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

        public Task<OperationOutcome<bool>> VerifyCredentialsAsync(CancellationToken cancellationToken)
            => client.VerifyCredentialsAsync(cancellationToken);

        public Task<OperationOutcome<Dictionary<string, FFLogsClient.CharacterFetchedData>>> FetchCharacterCandidateDataBatchAsync(
            List<FFLogsClient.CandidateCharacterQuery> queries,
            int zoneId,
            int? difficultyId,
            CancellationToken cancellationToken)
            => client.FetchCharacterCandidateDataBatchOutcomeAsync(
                queries,
                zoneId,
                difficultyId,
                cancellationToken);
    }

    private sealed class SystemFFLogsTimeProvider : IFFLogsTimeProvider
    {
        public DateTime UtcNow
            => DateTime.UtcNow;
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
    private CancellationTokenSource Cts { get; set; } = new();
    private Task _workerTask = Task.CompletedTask;

    internal static FFLogsCollectorSeams CreateSeams(
        IIngestHttpSender ingestHttpSender,
        IFFLogsApiClient apiClient,
        IFFLogsTimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(ingestHttpSender);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new FFLogsCollectorSeams(
            ingestHttpSender,
            apiClient,
            timeProvider);
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
        var leaseAbandoner = new FFLogsLeaseAbandoner(seams);
        collector.Initialize(
            configuration,
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            seams,
            submitBuffer,
            workerPolicy,
            resultSubmitter ?? new FFLogsResultSubmitter(seams, submitBuffer, leaseAbandoner),
            leaseAbandoner,
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
        var ffLogsClient = new FFLogsClient(plugin.Configuration, plugin.ActivationGate.OnFFLogsCredentialStatusReported);
        var workerRegionProvider = new DalamudFFLogsWorkerRegionProvider(
            plugin.Framework,
            plugin.ObjectTable,
            message => Plugin.Log.Warning(message));
        var seams = CreateSeams(
            new HttpClientIngestHttpSender(httpClient),
            new FFLogsApiClientAdapter(ffLogsClient),
            new SystemFFLogsTimeProvider());
        var submitBuffer = new FFLogsSubmitBuffer();
        var leaseAbandoner = new FFLogsLeaseAbandoner(seams);
        Initialize(
            plugin.Configuration,
            message => Plugin.Log.Info(message),
            message => Plugin.Log.Warning(message),
            message => Plugin.Log.Error(message),
            message => Plugin.Log.Debug(message),
            seams,
            submitBuffer,
            new FFLogsWorkerPolicy(plugin.Configuration, message => Plugin.Log.Warning(message), seams.TimeProvider),
            new FFLogsResultSubmitter(seams, submitBuffer, leaseAbandoner),
            leaseAbandoner,
            new FFLogsJobLeaseClient(
                seams,
                workerRegionProvider),
            new FFLogsBatchProcessor(seams),
            [ffLogsClient, httpClient, workerRegionProvider],
            startWorker: true,
            isPluginOperational: () => plugin.IsPluginOperational);
    }

    public void Dispose()
    {
        Cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Ignore cancellation/timeout during shutdown before tearing down HTTP clients.
        }

        foreach (var disposable in OwnedDisposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                WarningLog($"FFLogsCollector: disposable cleanup failed: {ex.Message}");
            }
        }

        Cts.Dispose();
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
        bool startWorker,
        Func<bool> isPluginOperational = null)
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
            DebugLog,
            isPluginOperational);
        if (startWorker)
        {
            StartWorker();
        }
    }

    private void StartWorker()
    {
        _workerTask = Task.Run(() => BackgroundWorker.RunAsync(Cts.Token));
    }

    internal Task<OperationOutcome<bool>> VerifyCredentialsAsync(CancellationToken cancellationToken = default)
        => Seams.ApiClient.VerifyCredentialsAsync(cancellationToken);

    public bool TryGetRateLimitCooldownRemaining(out TimeSpan remaining)
        => Seams.ApiClient.TryGetRateLimitRemaining(out remaining);

    public DateTime RateLimitCooldownUntilUtc
        => Seams.ApiClient.RateLimitCooldownUntilUtc;

    public void ResetRateLimitCooldown()
        => Seams.ApiClient.ResetRateLimitCooldown();
}
