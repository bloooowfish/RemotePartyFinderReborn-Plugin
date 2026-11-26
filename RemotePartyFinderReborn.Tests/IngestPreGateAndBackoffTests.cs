using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class IngestPreGateAndBackoffTests {
    static IngestPreGateAndBackoffTests() {
        DalamudAssemblyResolver.Register();
    }

    [Fact]
    public async Task PlayerCollector_skips_batch_take_and_http_when_all_endpoints_have_open_circuits() {
        using var tempDb = new TempPlayerCacheDatabase();
        using var database = new PlayerLocalDatabase(tempDb.DatabasePath);
        database.UpsertObservedPlayers([
            new UploadablePlayer {
                ContentId = 1001UL,
                Name = "Test Player",
                HomeWorld = 74,
                CurrentWorld = 74,
                AccountId = 500UL,
            },
        ]);

        var nowUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 5,
            UploadUrls = [uploadUrl],
        };
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);

        var warnings = new List<string>();
        var errors = new List<string>();
        var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException("No HTTP call should occur when circuits are open."));
        using var httpClient = new HttpClient(handler);
        using var collector = new PlayerCollector(
            configuration,
            database,
            httpClient,
            utcNow: () => nowUtc.AddMinutes(1),
            isOperational: () => true,
            warningLog: warnings.Add,
            errorLog: errors.Add
        );

        await collector.UploadPendingBatchForTestingAsync();

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(1, database.PendingCount);
        Assert.Empty(warnings);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task PlayerCollector_404_triggers_15_minute_cooldown_and_suppresses_repeated_warnings() {
        using var tempDb = new TempPlayerCacheDatabase();
        using var database = new PlayerLocalDatabase(tempDb.DatabasePath);
        database.UpsertObservedPlayers([
            new UploadablePlayer {
                ContentId = 2002UL,
                Name = "Missing Route Player",
                HomeWorld = 74,
                CurrentWorld = 74,
                AccountId = 600UL,
            },
        ]);

        var nowUtc = new DateTime(2026, 6, 1, 13, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerBreakDurationMinutes = 1,
            UploadUrls = [uploadUrl],
        };

        var warnings = new List<string>();
        var errors = new List<string>();
        var nextStatus = HttpStatusCode.NotFound;
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(nextStatus) {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var collector = new PlayerCollector(
            configuration,
            database,
            httpClient,
            utcNow: () => nowUtc,
            isOperational: () => true,
            warningLog: warnings.Add,
            errorLog: errors.Add
        );

        await collector.UploadPendingBatchForTestingAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("/contribute/players", handler.RequestPaths[0]);
        Assert.True(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(14)));
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(15)));
        Assert.Single(warnings);
        Assert.Contains("404", warnings[0]);
        Assert.Empty(errors);
        Assert.Equal(1, database.PendingCount);

        // Subsequent ticks during the 15-minute cooldown must not hit HTTP or log warnings.
        nowUtc = nowUtc.AddMinutes(1);
        await collector.UploadPendingBatchForTestingAsync();
        nowUtc = nowUtc.AddMinutes(9);
        await collector.UploadPendingBatchForTestingAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Single(warnings);
        Assert.Empty(errors);

        // Once the 15-minute cooldown expires, upload resumes and clears circuit state on success.
        nowUtc = nowUtc.AddMinutes(5);
        nextStatus = HttpStatusCode.OK;
        await collector.UploadPendingBatchForTestingAsync();

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, database.PendingCount);
        Assert.Equal(0, uploadUrl.FailureCount);
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc));
    }

    [Fact]
    public async Task PlayerCollector_handles_null_plugin_log_on_failure_and_rate_limit_paths() {
        Assert.Null(typeof(Plugin).GetProperty("Log", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null));

        using var tempDb = new TempPlayerCacheDatabase();
        using var database = new PlayerLocalDatabase(tempDb.DatabasePath);
        database.UpsertObservedPlayers([
            new UploadablePlayer {
                ContentId = 3003UL,
                Name = "Null Log Player",
                HomeWorld = 74,
                CurrentWorld = 74,
                AccountId = 700UL,
            },
        ]);

        var nowUtc = new DateTime(2026, 6, 1, 14, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationMinutes = 1,
            UploadUrls = [uploadUrl],
        };

        var handler = new RecordingHttpHandler(_ => {
            var response = new HttpResponseMessage((HttpStatusCode)429) {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("Retry-After", "30");
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var collector = new PlayerCollector(
            configuration,
            database,
            httpClient,
            utcNow: () => nowUtc,
            isOperational: () => true
        );

        await collector.UploadPendingBatchForTestingAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, uploadUrl.FailureCount);
    }

    [Fact]
    public async Task Gatherer_skips_worker_and_http_when_all_endpoints_have_open_circuits_then_recovers() {
        var nowUtc = new DateTime(2026, 6, 1, 15, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerBreakDurationMinutes = 2,
            UploadUrls = [uploadUrl],
        };
        UploadAttemptRecorder.RecordFailure(uploadUrl, nowUtc);

        var warnings = new List<string>();
        var errors = new List<string>();
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var gatherer = new Gatherer(
            configuration,
            httpClient,
            utcNow: () => nowUtc,
            isOperational: () => true,
            warningLog: warnings.Add,
            errorLog: errors.Add
        );

        gatherer.EnqueueListingForTesting(CreateHighEndListing(9001U));

        nowUtc = nowUtc.AddMinutes(1);
        await gatherer.TriggerImmediateUploadAndWaitForTestingAsync();

        Assert.Equal(0, handler.RequestCount);
        Assert.True(gatherer.HasPendingUploadWork);
        Assert.Empty(warnings);
        Assert.Empty(errors);

        nowUtc = nowUtc.AddMinutes(1);
        await gatherer.TriggerImmediateUploadAndWaitForTestingAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("/contribute/multiple", handler.RequestPaths[0]);
        Assert.False(gatherer.HasPendingUploadWork);
        Assert.True(gatherer.WasListingUploadedAfter(9001U, nowUtc));
        Assert.Equal(0, uploadUrl.FailureCount);
    }

    [Fact]
    public async Task Gatherer_404_triggers_15_minute_cooldown_and_pre_gates_subsequent_ticks() {
        Assert.Null(typeof(Plugin).GetProperty("Log", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null));

        var nowUtc = new DateTime(2026, 6, 1, 16, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://upload.example");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerBreakDurationMinutes = 1,
            UploadUrls = [uploadUrl],
        };

        var warnings = new List<string>();
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var gatherer = new Gatherer(
            configuration,
            httpClient,
            utcNow: () => nowUtc,
            isOperational: () => true,
            warningLog: warnings.Add
        );

        gatherer.EnqueueListingForTesting(CreateHighEndListing(9002U));
        await gatherer.TriggerImmediateUploadAndWaitForTestingAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.True(gatherer.HasPendingUploadWork);
        Assert.True(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(14)));
        Assert.False(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, configuration, nowUtc.AddMinutes(15)));
        Assert.Single(warnings);
        Assert.Contains("404", warnings[0]);

        nowUtc = nowUtc.AddMinutes(5);
        await gatherer.TriggerImmediateUploadAndWaitForTestingAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Single(warnings);
    }

    private static UploadableListing CreateHighEndListing(uint id) {
        var listing = (UploadableListing)RuntimeHelpers.GetUninitializedObject(typeof(UploadableListing));
        SetBackingField(listing, nameof(UploadableListing.Id), id);
        SetBackingField(listing, nameof(UploadableListing.Name), Array.Empty<byte>());
        SetBackingField(listing, nameof(UploadableListing.Description), Array.Empty<byte>());
        SetBackingField(listing, nameof(UploadableListing.Slots), new List<UploadableSlot>());
        SetBackingField(listing, nameof(UploadableListing.JobsPresent), new List<byte>());
        SetBackingField(listing, nameof(UploadableListing.ClientHighEndDuty), true);
        return listing;
    }

    private static void SetBackingField<T>(UploadableListing target, string propertyName, T value) {
        var field = typeof(UploadableListing).GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        internal int RequestCount { get; private set; }
        internal List<string> RequestPaths { get; } = [];

        internal RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestCount++;
            RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class TempPlayerCacheDatabase : IDisposable {
        private readonly string _dir;

        internal string DatabasePath { get; }

        internal TempPlayerCacheDatabase() {
            _dir = Path.Combine(Path.GetTempPath(), "RemotePartyFinderReborn.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            DatabasePath = Path.Combine(_dir, "player_cache.db");
        }

        public void Dispose() {
            try {
                if (Directory.Exists(_dir)) {
                    Directory.Delete(_dir, recursive: true);
                }
            } catch {
            }
        }
    }
}
