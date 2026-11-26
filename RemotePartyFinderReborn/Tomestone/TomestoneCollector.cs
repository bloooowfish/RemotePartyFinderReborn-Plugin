using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn.Tomestone;

internal sealed record TomestoneCollectorSeams(
    IIngestHttpSender IngestHttpSender,
    ITomestoneProgressionService ProgressionService,
    IFFLogsTimeProvider TimeProvider,
    Configuration Configuration,
    ApiCredentialStatusStore ApiCredentialStatus,
    Func<TimeSpan, CancellationToken, Task> RequestDelayAsync);

internal sealed class TomestoneCollector : IDisposable
{
    private sealed class HttpClientIngestHttpSender(HttpClient httpClient) : IIngestHttpSender
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => httpClient.SendAsync(request, cancellationToken);
    }

    private sealed class SystemTimeProvider : IFFLogsTimeProvider
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    private Action<string> WarningLog { get; set; } = static _ => { };
    private TomestoneBackgroundWorker BackgroundWorker { get; set; }
    private IReadOnlyList<IDisposable> OwnedDisposables { get; set; } = [];
    private CancellationTokenSource Cts { get; set; } = new();
    private Task _workerTask = Task.CompletedTask;
    private int _shutdownSignaled;
    private int _disposed;

    internal static TomestoneCollectorSeams CreateSeams(
        IIngestHttpSender ingestHttpSender,
        ITomestoneProgressionService progressionService,
        IFFLogsTimeProvider timeProvider,
        Configuration configuration = null,
        ApiCredentialStatusStore apiCredentialStatus = null,
        Func<TimeSpan, CancellationToken, Task> requestDelayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(ingestHttpSender);
        ArgumentNullException.ThrowIfNull(progressionService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new TomestoneCollectorSeams(
            ingestHttpSender,
            progressionService,
            timeProvider,
            configuration ?? new Configuration(),
            apiCredentialStatus ?? new ApiCredentialStatusStore(),
            requestDelayAsync ?? Task.Delay);
    }

    internal static TomestoneCollector CreateForTesting(
        Configuration configuration,
        TomestoneCollectorSeams seams,
        TomestoneSubmitBuffer submitBuffer,
        FFLogsWorkerPolicy workerPolicy,
        TomestoneJobLeaseClient jobLeaseClient = null,
        TomestoneResultSubmitter resultSubmitter = null,
        TomestoneBatchProcessor batchProcessor = null,
        Func<bool> isPluginOperational = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(submitBuffer);
        ArgumentNullException.ThrowIfNull(workerPolicy);

        var collector = new TomestoneCollector();
        var leaseAbandoner = new TomestoneLeaseAbandoner(seams);
        collector.Initialize(
            configuration,
            seams,
            submitBuffer,
            workerPolicy,
            resultSubmitter ?? new TomestoneResultSubmitter(seams, submitBuffer, leaseAbandoner),
            leaseAbandoner,
            jobLeaseClient ?? new TomestoneJobLeaseClient(seams),
            batchProcessor ?? new TomestoneBatchProcessor(seams),
            [],
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            startWorker: false,
            isPluginOperational: isPluginOperational);
        return collector;
    }

    internal Task RunWorkerLoopForTestingAsync()
        => BackgroundWorker.RunAsync(Cts.Token);

    private TomestoneCollector()
    {
    }

    public TomestoneCollector(Plugin plugin)
        : this(plugin, null)
    {
    }

    internal TomestoneCollector(Plugin plugin, IFFLogsWorkerRegionProvider workerRegionProvider)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var httpClient = PluginHttpClientFactory.CreateIngestClient();
        var progressionService = new TomestoneProgressionService(
            plugin.TomestoneApiClient,
            plugin.Configuration,
            message => Plugin.Log.Debug(message),
            message => Plugin.Log.Warning(message));
        var ownsWorkerRegionProvider = workerRegionProvider is null;
        workerRegionProvider ??= new DalamudFFLogsWorkerRegionProvider(
            plugin.Framework,
            plugin.ObjectTable,
            plugin.DataManager,
            message => Plugin.Log.Warning(message));
        var seams = CreateSeams(
            new HttpClientIngestHttpSender(httpClient),
            progressionService,
            new SystemTimeProvider(),
            plugin.Configuration,
            plugin.ApiCredentialStatus);
        var submitBuffer = new TomestoneSubmitBuffer();
        var leaseAbandoner = new TomestoneLeaseAbandoner(seams);
        var ownedDisposables = new List<IDisposable> { httpClient };
        if (ownsWorkerRegionProvider && workerRegionProvider is IDisposable disposableProvider)
        {
            ownedDisposables.Add(disposableProvider);
        }

        Initialize(
            plugin.Configuration,
            seams,
            submitBuffer,
            new FFLogsWorkerPolicy(plugin.Configuration, message => Plugin.Log.Warning(message), seams.TimeProvider),
            new TomestoneResultSubmitter(seams, submitBuffer, leaseAbandoner),
            leaseAbandoner,
            new TomestoneJobLeaseClient(seams, workerRegionProvider),
            new TomestoneBatchProcessor(seams, message => Plugin.Log.Warning(message)),
            ownedDisposables,
            message => Plugin.Log.Info(message),
            message => Plugin.Log.Warning(message),
            message => Plugin.Log.Error(message),
            message => Plugin.Log.Debug(message),
            startWorker: true,
            isPluginOperational: () => plugin.IsPluginOperational);
    }

    private void Initialize(
        Configuration configuration,
        TomestoneCollectorSeams seams,
        TomestoneSubmitBuffer submitBuffer,
        FFLogsWorkerPolicy workerPolicy,
        TomestoneResultSubmitter resultSubmitter,
        TomestoneLeaseAbandoner leaseAbandoner,
        TomestoneJobLeaseClient jobLeaseClient,
        TomestoneBatchProcessor batchProcessor,
        IReadOnlyList<IDisposable> ownedDisposables,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<string> debugLog,
        bool startWorker,
        Func<bool> isPluginOperational)
    {
        WarningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        OwnedDisposables = ownedDisposables ?? throw new ArgumentNullException(nameof(ownedDisposables));
        BackgroundWorker = new TomestoneBackgroundWorker(
            configuration,
            seams.ProgressionService,
            seams.ApiCredentialStatus,
            seams.TimeProvider,
            workerPolicy,
            jobLeaseClient,
            batchProcessor,
            resultSubmitter,
            leaseAbandoner,
            infoLog,
            WarningLog,
            errorLog,
            debugLog,
            isPluginOperational);

        if (startWorker)
        {
            _workerTask = Task.Run(() => BackgroundWorker.RunAsync(Cts.Token));
        }
    }

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownSignaled, 1) != 0)
        {
            return;
        }

        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void CollectShutdownTasks(List<Task> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (!_workerTask.IsCompleted)
        {
            tasks.Add(_workerTask);
        }
    }

    public void Dispose()
    {
        Dispose(TimeSpan.FromSeconds(2));
    }

    internal void Dispose(TimeSpan waitTimeout)
    {
        BeginShutdown();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (waitTimeout > TimeSpan.Zero && !_workerTask.IsCompleted)
        {
            try
            {
                _workerTask.Wait(waitTimeout);
            }
            catch (Exception)
            {
                // Ignore cancellation/timeout during shutdown before disposing owned HTTP clients.
            }
        }

        foreach (var disposable in OwnedDisposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                WarningLog($"TomestoneCollector: disposable cleanup failed: {ex.Message}");
            }
        }

        Cts.Dispose();
    }
}
