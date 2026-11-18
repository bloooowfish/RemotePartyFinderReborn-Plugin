using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class Phase3AHardeningTests {
    static Phase3AHardeningTests() {
        DalamudAssemblyResolver.Register();
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public void NativePointerValidator_rejects_null_guard_page_and_kernel_addresses() {
        Assert.False(NativePointerValidator.IsValidUserPointer(nint.Zero));
        Assert.False(NativePointerValidator.IsValidUserPointer((nint)0x1000));
        Assert.False(NativePointerValidator.IsValidUserPointer((nint)0xFFFF));
        Assert.True(NativePointerValidator.IsValidUserPointer((nint)0x10000));

        if (IntPtr.Size == 8) {
            Assert.True(NativePointerValidator.IsValidUserPointer(unchecked((nint)0x00007FFFFFFFFFFFL)));
            Assert.False(NativePointerValidator.IsValidUserPointer(unchecked((nint)0x0000800000000000L)));
            Assert.False(NativePointerValidator.IsValidUserPointer(unchecked((nint)(-1))));
        }
    }

    [Fact]
    public void PluginHttpClientFactory_configures_sockets_handler_timeouts_and_buffer_cap() {
        using var ingestClient = PluginHttpClientFactory.CreateIngestClient();
        using var externalClient = PluginHttpClientFactory.CreateExternalApiClient(allowAutoRedirect: false);

        Assert.Equal(TimeSpan.FromSeconds(10), ingestClient.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(20), externalClient.Timeout);
        Assert.Equal(8 * 1024 * 1024, ingestClient.MaxResponseContentBufferSize);
        Assert.Equal(8 * 1024 * 1024, externalClient.MaxResponseContentBufferSize);

        var handlerField = typeof(HttpMessageInvoker)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(static f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));

        var ingestHandler = Assert.IsType<SocketsHttpHandler>(handlerField.GetValue(ingestClient));
        Assert.Equal(TimeSpan.FromMinutes(5), ingestHandler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(5), ingestHandler.ConnectTimeout);
        Assert.Equal(DecompressionMethods.All, ingestHandler.AutomaticDecompression);
        Assert.True(ingestHandler.AllowAutoRedirect);

        var externalHandler = Assert.IsType<SocketsHttpHandler>(handlerField.GetValue(externalClient));
        Assert.Equal(TimeSpan.FromMinutes(5), externalHandler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(5), externalHandler.ConnectTimeout);
        Assert.Equal(DecompressionMethods.All, externalHandler.AutomaticDecompression);
        Assert.False(externalHandler.AllowAutoRedirect);
    }

    [Fact]
    public async Task PartyDetailUploadExecutor_delivers_applied_listing_to_all_enabled_endpoints() {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var requestedUrls = new List<string>();
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue((request, _) => {
            requestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(CreateBatchOkResponse(9001U, "applied"));
        });
        handler.Enqueue((request, _) => {
            requestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(CreateBatchOkResponse(9001U, "applied"));
        });

        using var httpClient = new HttpClient(handler);
        var config = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            DetailUploadTimeoutMs = 2000,
            UploadUrls = ImmutableList.Create(
                new UploadUrl("https://endpoint-a.example") { IsDefault = true },
                new UploadUrl("https://endpoint-b.example")
            ),
        };
        var executor = new PartyDetailUploadExecutor(
            config,
            httpClient,
            () => now,
            static _ => { },
            static _ => { },
            static _ => { }
        );

        var outcomes = await executor.UploadBatchToAllTargetsAsync([
            CreatePendingDetail(9001U, now),
        ]);

        Assert.Equal(DetailUploadResult.Applied, outcomes[9001U].Result);
        Assert.Equal(2, requestedUrls.Count);
        Assert.Contains(requestedUrls, url => url.StartsWith("https://endpoint-a.example", StringComparison.Ordinal));
        Assert.Contains(requestedUrls, url => url.StartsWith("https://endpoint-b.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartyDetailUploadExecutor_cancellation_stops_without_recording_circuit_failure() {
        var now = new DateTime(2026, 6, 1, 12, 5, 0, DateTimeKind.Utc);
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));

        using var httpClient = new HttpClient(handler);
        var target = new UploadUrl("https://endpoint-cancel.example");
        var config = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            DetailUploadTimeoutMs = 2000,
            UploadUrls = ImmutableList.Create(target),
        };
        var executor = new PartyDetailUploadExecutor(
            config,
            httpClient,
            () => now,
            static _ => { },
            static _ => { },
            static _ => { }
        );

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.UploadBatchToAllTargetsAsync([CreatePendingDetail(9002U, now)], cts.Token));
        Assert.Equal(0, target.FailureCount);
    }

    [Fact]
    public void Gatherer_RetainFailedBatchListings_deduplicates_and_caps_to_latest_200_by_id() {
        var batches = new Dictionary<int, List<UploadableListing>> {
            [1] = [
                CreateStubListing(150, "Newer150"),
                CreateStubListing(250, "New250"),
            ],
        };
        var failed = Enumerable.Range(1, 210)
            .Select(i => CreateStubListing((uint)i, $"Failed{i}"))
            .ToList();

        Gatherer.RetainFailedBatchListings(batches, failed);

        var retained = batches.Values.SelectMany(static list => list).ToList();
        Assert.Equal(200, retained.Count);
        Assert.Equal(12U, retained[0].Id);
        Assert.Equal(250U, retained[^1].Id);
        Assert.DoesNotContain(retained, static l => l.Id == 11U);
        var entry150 = Assert.Single(retained, static l => l.Id == 150U);
        Assert.Equal("Newer150", Encoding.UTF8.GetString(entry150.Name));
    }

    [Fact]
    public void EndpointsPageRenderer_allows_removing_default_only_when_other_enabled_exists_and_promotes_survivor() {
        var defaultOnly = new Configuration {
            UploadUrls = ImmutableList.Create(new UploadUrl("http://127.0.0.1:8000") { IsDefault = true }),
        };
        Assert.False(EndpointsPageRenderer.CanRemoveEndpoint(defaultOnly.UploadUrls, defaultOnly.UploadUrls[0]));
        Assert.False(EndpointsPageRenderer.RemoveEndpoint(defaultOnly, defaultOnly.UploadUrls[0].Id));
        Assert.Single(defaultOnly.UploadUrls);

        var disabledOther = new Configuration {
            UploadUrls = ImmutableList.Create(
                new UploadUrl("http://127.0.0.1:8000") { IsDefault = true },
                new UploadUrl("https://backup.example") { IsEnabled = false }
            ),
        };
        Assert.False(EndpointsPageRenderer.CanRemoveEndpoint(disabledOther.UploadUrls, disabledOther.UploadUrls[0]));
        Assert.True(EndpointsPageRenderer.CanRemoveEndpoint(disabledOther.UploadUrls, disabledOther.UploadUrls[1]));

        var defaultUrl = new UploadUrl("http://127.0.0.1:8000") { IsDefault = true };
        var enabledOther = new UploadUrl("https://primary.example") { IsEnabled = true };
        var multiConfig = new Configuration {
            UploadUrls = ImmutableList.Create(defaultUrl, enabledOther),
        };
        Assert.True(EndpointsPageRenderer.CanRemoveEndpoint(multiConfig.UploadUrls, defaultUrl));
        Assert.True(EndpointsPageRenderer.RemoveEndpoint(multiConfig, defaultUrl.Id));

        var survivor = Assert.Single(multiConfig.UploadUrls);
        Assert.Equal(enabledOther.Id, survivor.Id);
        Assert.True(survivor.IsDefault);
    }

    [Fact]
    public void PartyDetailCaptureState_queues_rapid_arrivals_and_tracks_recent_consumed_acks() {
        var state = new PartyDetailCaptureState();

        var cycle1 = state.BeginRequest(PartyDetailRequestOwner.Scanner, 1001U, 101UL);
        Assert.True(state.TryRecordArrival(cycle1.RequestSerial, new UploadablePartyDetail {
            ListingId = 1001U,
            LeaderContentId = 101UL,
            LeaderName = "First",
        }));

        var cycle2 = state.BeginRequest(PartyDetailRequestOwner.Scanner, 1002U, 102UL);
        Assert.True(state.TryRecordArrival(cycle2.RequestSerial, new UploadablePartyDetail {
            ListingId = 1002U,
            LeaderContentId = 102UL,
            LeaderName = "Second",
        }));

        Assert.True(state.IsScannerAckReady(cycle1.RequestSerial, 1001U, 101UL));
        Assert.True(state.IsScannerAckReady(cycle2.RequestSerial, 1002U, 102UL));

        Assert.True(state.TryGetNextUnconsumedArrival(out var first));
        Assert.Equal(1001U, first.Payload.ListingId);
        Assert.True(state.TryMarkConsumed(first.Generation));

        Assert.True(state.TryGetNextUnconsumedArrival(out var second));
        Assert.Equal(1002U, second.Payload.ListingId);
        Assert.True(state.TryMarkConsumed(second.Generation));

        Assert.False(state.TryGetNextUnconsumedArrival(out _));
        Assert.True(state.IsScannerConsumedAckReady(cycle1.RequestSerial, 1001U, 101UL));
        Assert.True(state.IsScannerConsumedAckReady(cycle2.RequestSerial, 1002U, 102UL));

        // Overflow past capacity 16 retains the latest 16 arrivals.
        var serials = new List<long>();
        for (var i = 0; i < 20; i++) {
            var c = state.BeginRequest(PartyDetailRequestOwner.Manual, (uint)(2000 + i), (ulong)(200 + i));
            serials.Add(c.RequestSerial);
            Assert.True(state.TryRecordArrival(c.RequestSerial, new UploadablePartyDetail {
                ListingId = (uint)(2000 + i),
                LeaderContentId = (ulong)(200 + i),
                LeaderName = $"Player{i}",
            }));
        }

        var drained = new List<uint>();
        while (state.TryGetNextUnconsumedArrival(out var next)) {
            drained.Add(next.Payload.ListingId);
            Assert.True(state.TryMarkConsumed(next.Generation));
        }

        Assert.Equal(16, drained.Count);
        Assert.Equal(2004U, drained[0]);
        Assert.Equal(2019U, drained[^1]);
    }

    [Fact]
    public async Task FFLogs_missing_identity_and_zero_exists_candidates_are_explicitly_abandoned() {
        var now = new DateTime(2026, 6, 1, 13, 0, 0, DateTimeKind.Utc);
        var timeProvider = new ManualFFLogsTimeProvider { UtcNow = now };
        var capturedBodies = new List<string>();
        var ingestSender = new StubFFLogsIngestHttpSender {
            OnSendAsync = async (request, _) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("/contribute/fflogs/leases/abandon", StringComparison.Ordinal)) {
                    capturedBodies.Add(await request.Content!.ReadAsStringAsync());
                    return new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent("""{"released":2,"submitted":2,"rejected":0}"""),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var apiClient = new StubFFLogsApiClient {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) => {
                var output = new Dictionary<string, FFLogsClient.CharacterFetchedData>();
                foreach (var query in queries) {
                    output[query.Key] = new FFLogsClient.CharacterFetchedData {
                        Exists = false,
                        Hidden = false,
                        Parses = [],
                    };
                }
                return Task.FromResult(output);
            },
        };

        var seams = FFLogsCollector.CreateSeams(ingestSender, apiClient, timeProvider);
        var missingJob = new ParseJob {
            ContentId = 1001,
            ZoneId = 73,
            DifficultyId = 101,
            Name = "",
            LeaseToken = "lease-missing",
        };
        var notFoundJob = new ParseJob {
            ContentId = 1002,
            ZoneId = 73,
            DifficultyId = 101,
            Name = "Ghost Player",
            CandidateServers = [
                new ParseJobCandidateServer { Server = "Tonberry", Region = "JP" },
                new ParseJobCandidateServer { Server = "Chocobo", Region = "JP" },
            ],
            LeaseToken = "lease-notfound",
        };

        var processor = new FFLogsBatchProcessor(seams);
        var session = new FFLogsLeaseSession(new UploadUrl("https://ingest.example"), [missingJob, notFoundJob]);

        var batchResult = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Empty(batchResult.ProcessedResults);
        Assert.NotNull(batchResult.AbandonedLeases);
        Assert.Equal(2, batchResult.AbandonedLeases!.Count);
        Assert.Contains(batchResult.AbandonedLeases, a => a.ContentId == 1001 && a.Reason == "fflogs_missing_identity");
        Assert.Contains(batchResult.AbandonedLeases, a => a.ContentId == 1002 && a.Reason == "fflogs_not_found");

        var abandoner = new FFLogsLeaseAbandoner(seams);
        await abandoner.AbandonExplicitLeasesAsync(
            new Configuration { IngestClientId = Guid.NewGuid().ToString("N") },
            session,
            batchResult.AbandonedLeases,
            CancellationToken.None,
            static _ => { },
            static _ => { });

        var payload = Assert.Single(capturedBodies);
        var items = JsonConvert.DeserializeObject<List<AbandonFflogsLease>>(payload)!;
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ContentId == 1001 && i.Reason == "fflogs_missing_identity");
        Assert.Contains(items, i => i.ContentId == 1002 && i.Reason == "fflogs_not_found");
    }

    [Fact]
    public async Task FFLogsJobLeaseClient_handles_malformed_json_gracefully() {
        var now = new DateTime(2026, 6, 1, 13, 10, 0, DateTimeKind.Utc);
        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{invalid-json"),
            }),
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider { UtcNow = now });
        var client = new FFLogsJobLeaseClient(seams, new StaticRegionProvider("JP"));
        var warnings = new List<string>();
        var config = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = ImmutableList.Create(new UploadUrl("https://ingest.example")),
        };

        var attempt = await client.TryAcquireSessionAsync(config, CancellationToken.None, warnings.Add);

        Assert.NotNull(attempt.Session);
        Assert.Empty(attempt.Session.Jobs);
        Assert.True(attempt.HadTransientFailure);
        Assert.Contains(warnings, w => w.Contains("deserialize leased jobs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FFLogsClient_paces_between_candidate_chunks_over_40() {
        var nowUtc = new DateTime(2026, 6, 1, 13, 20, 0, DateTimeKind.Utc);
        var graphqlRequests = 0;
        var handler = new MultiResponseHttpMessageHandler((request, _) => {
            if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.Ordinal)) {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}"),
                });
            }

            Interlocked.Increment(ref graphqlRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"data\":{\"characterData\":{}}}"),
            });
        });

        using var client = new FFLogsClient(
            new Configuration {
                FFLogsClientId = "id",
                FFLogsClientSecret = "secret",
            },
            handler,
            () => nowUtc) {
            CandidateChunkDelay = TimeSpan.FromMilliseconds(20),
        };

        var queries = Enumerable.Range(1, 45)
            .Select(i => new FFLogsClient.CandidateCharacterQuery {
                Key = $"1:{i}",
                Name = $"Player {i}",
                Server = "Tonberry",
                Region = "JP",
            })
            .ToList();

        var outcome = await client.FetchCharacterCandidateDataBatchOutcomeAsync(queries, zoneId: 73, difficultyId: 101, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, graphqlRequests);
        Assert.Equal(45, outcome.Value!.Count);
    }

    [Fact]
    public void UploadCircuitState_enforces_single_half_open_probe_and_positive_threshold() {
        var now = new DateTime(2026, 6, 1, 14, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://circuit.example");
        var configWithZeroThreshold = new Configuration {
            CircuitBreakerFailureThreshold = 0,
            CircuitBreakerBreakDurationMinutes = 2,
        };

        // Clamped threshold to 1: 0 failures must NOT open circuit.
        Assert.False(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, configWithZeroThreshold, now));

        var config = new Configuration {
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationMinutes = 2,
        };
        UploadAttemptRecorder.RecordFailure(uploadUrl, now);
        UploadAttemptRecorder.RecordFailure(uploadUrl, now);

        // Before break expires, both peek and active checks report open.
        Assert.True(UploadAttemptRecorder.PeekIsCircuitOpen(uploadUrl, config, now.AddMinutes(1)));
        Assert.True(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, config, now.AddMinutes(1)));

        var probeTime = now.AddMinutes(2);
        // GetCircuitStatus / Peek must NOT consume the half-open probe slot.
        Assert.False(UploadAttemptRecorder.GetCircuitStatus(uploadUrl, config, probeTime).IsOpen);

        // First active caller gets the single Half-Open probe slot; second concurrent caller is blocked.
        Assert.False(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, config, probeTime));
        Assert.True(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, config, probeTime));
        Assert.True(UploadAttemptRecorder.GetCircuitStatus(uploadUrl, config, probeTime).IsOpen);

        // Failed probe re-opens circuit for a new break window; success resets cleanly.
        UploadAttemptRecorder.RecordFailure(uploadUrl, probeTime);
        Assert.True(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, config, probeTime.AddMinutes(1)));
        Assert.False(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, config, probeTime.AddMinutes(2)));
        UploadAttemptRecorder.RecordSuccess(uploadUrl);
        Assert.False(UploadAttemptRecorder.IsCircuitOpen(uploadUrl, config, probeTime.AddMinutes(2)));
    }

    [Fact]
    public void PlayerLocalDatabase_caches_hits_negatively_caches_absent_falls_back_for_pending_preserves_partial_and_flushes_dirty() {
        var tempDir = Path.Combine(Path.GetTempPath(), "RemotePartyFinderReborn.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "player_cache.db");

        try {
            var resolvedAt = new DateTime(2026, 6, 1, 15, 0, 0, DateTimeKind.Utc);
            using (var seed = new PlayerLocalDatabase(dbPath)) {
                seed.UpsertObservedPlayers([
                    new UploadablePlayer {
                        ContentId = 10UL,
                        Name = "Alpha Player",
                        HomeWorld = 74,
                        CurrentWorld = 74,
                        AccountId = 500UL,
                    },
                ]);
                seed.MarkBatchUploaded([
                    new UploadablePlayer {
                        ContentId = 10UL,
                        Name = "Alpha Player",
                        HomeWorld = 74,
                        CurrentWorld = 74,
                        AccountId = 500UL,
                    },
                ]);

                var identity1 = new CharacterIdentitySnapshot(101UL, "Cached Hit", 74, "Tonberry", resolvedAt);
                var identity2 = new CharacterIdentitySnapshot(102UL, "Overflow Pending", 74, "Tonberry", resolvedAt.AddMinutes(1));
                seed.UpsertResolvedIdentity(identity1);
                seed.UpsertResolvedIdentity(identity2);
                seed.Flush();
            }

            // Seed a partial with world metadata directly in SQLite.
            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate")) {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO player_identity_partials (content_id, name, home_world, world_name, last_updated_utc)
                    VALUES (201, 'Old Name', 74, 'Tonberry', '2026-06-01T14:00:00.0000000Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            using var db = new PlayerLocalDatabase(dbPath);

            // Simulate cache eviction so _identitiesMayBeIncomplete and _partialsMayBeIncomplete are active.
            db.EvictIdentityFromMemoryForTesting(101UL);
            db.EvictIdentityFromMemoryForTesting(102UL);
            db.EvictPartialFromMemoryForTesting(201UL);

            // 1. GetIdentities populates _identities on SQLite hit AND negatively caches absent IDs.
            var fetched = db.GetIdentities([101UL, 99999UL]);
            Assert.Single(fetched);
            Assert.Equal("Cached Hit", fetched[101UL].Name);
            Assert.True(db.IsIdentityCachedInMemoryForTesting(101UL));
            Assert.True(db.IsIdentityNegativeCachedForTesting(99999UL));

            // Inserting a new identity clears negative cache.
            db.UpsertResolvedIdentity(new CharacterIdentitySnapshot(99999UL, "Newly Resolved", 74, "Tonberry", resolvedAt.AddMinutes(2)));
            Assert.False(db.IsIdentityNegativeCachedForTesting(99999UL));

            // 2. TakePendingIdentityUploads falls back to SQLite for evicted pending uploads.
            var pendingIdentities = db.TakePendingIdentityUploads(10);
            Assert.Contains(pendingIdentities, s => s.ContentId == 102UL && s.Name == "Overflow Pending");

            // 3. UpsertPartialIdentityName preserves DB home_world and world_name when evicted.
            db.UpsertPartialIdentityName(201UL, "Renamed Partial", resolvedAt.AddMinutes(3));
            Assert.True(db.TryGetPartialIdentity(201UL, out var partial));
            Assert.Equal("Renamed Partial", partial!.Name);
            Assert.Equal((ushort)74, partial.HomeWorld);
            Assert.Equal("Tonberry", partial.WorldName);

            // 4. MarkAllPlayersDirty flushes synchronously so SQLite dirty=1 is immediately durable.
            Assert.Equal(0, db.PendingCount);
            var requeued = db.MarkAllPlayersDirty();
            Assert.Equal(1, requeued);
            Assert.Equal(1, db.PendingCount);
            using (var verifyConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate")) {
                verifyConn.Open();
                using var cmd = verifyConn.CreateCommand();
                cmd.CommandText = "SELECT dirty FROM players WHERE content_id = 10;";
                Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
            }

            // 5. UpsertObservedPlayers throttles clean prune and leaves unchanged clean rows clean.
            db.MarkBatchUploaded([
                new UploadablePlayer {
                    ContentId = 10UL,
                    Name = "Alpha Player",
                    HomeWorld = 74,
                    CurrentWorld = 74,
                    AccountId = 500UL,
                },
            ]);
            db.Flush();
            db.UpsertObservedPlayers([
                new UploadablePlayer {
                    ContentId = 10UL,
                    Name = "Alpha Player",
                    HomeWorld = 74,
                    CurrentWorld = 74,
                    AccountId = 500UL,
                },
            ]);
            Assert.Equal(0, db.PendingCount);
            Assert.True(db.HasPendingCleanPruneForTesting());
            db.Flush();
            Assert.False(db.HasPendingCleanPruneForTesting());
            // Re-observing immediately within 15 minutes does NOT schedule another clean prune.
            db.UpsertObservedPlayers([
                new UploadablePlayer {
                    ContentId = 10UL,
                    Name = "Alpha Player",
                    HomeWorld = 74,
                    CurrentWorld = 74,
                    AccountId = 500UL,
                },
            ]);
            Assert.False(db.HasPendingCleanPruneForTesting());
        } finally {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ContentIdResolveQueue_and_ExpiringBudgetGate_bound_and_prune_entries() {
        var now = new DateTime(2026, 6, 1, 16, 0, 0, DateTimeKind.Utc);
        var queue = new ContentIdResolveQueue(freshIdentityTtl: TimeSpan.FromHours(1), maxAttempts: 1, maxTrackedRequests: 4);

        for (ulong id = 1; id <= 6; id++) {
            queue.MarkResolved(new CharacterIdentitySnapshot(id, $"P{id}", 74, "Tonberry", now.AddMinutes(id)));
        }

        Assert.True(queue.Requests.Count <= 4);
        Assert.Equal(ResolveState.Resolved, queue.GetState(6UL));

        var gate = new ExpiringBudgetGate<ulong>();
        gate.Arm(101UL, 1, now.AddSeconds(5), nowUtc: now);
        Assert.Equal(1, gate.Count);

        // Arming later after 101UL expired sweeps 101UL automatically.
        gate.Arm(202UL, 1, now.AddSeconds(15), nowUtc: now.AddSeconds(10));
        Assert.Equal(1, gate.Count);
        Assert.False(gate.TryConsume(101UL, now.AddSeconds(10)));
        Assert.True(gate.TryConsume(202UL, now.AddSeconds(10)));
    }

    [Fact]
    public void DpapiSecretProtector_never_returns_plaintext_when_dpapi_unavailable_or_throws() {
        Assert.Equal(string.Empty, DpapiSecretProtector.ProtectCore("my-secret", isWindows: false, static b => b));
        Assert.Equal(string.Empty, DpapiSecretProtector.ProtectCore("my-secret", isWindows: true, static _ => throw new CryptographicException("denied")));
        Assert.Equal(string.Empty, DpapiSecretProtector.ProtectCore("my-secret", isWindows: true, static _ => throw new PlatformNotSupportedException("unsupported")));
    }

    [Fact]
    public async Task TomestoneBatchProcessor_skips_1000ms_delay_when_profile_served_from_in_memory_cache() {
        var now = new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc);
        var httpCalls = 0;
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue((request, _) => {
            httpCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""
                    {
                      "encounters": {
                        "savage": [
                          {
                            "encounters": [
                              {"canonicalName": "vamp-fatale", "progression": {"displayPercent": "22.1%"}},
                              {"canonicalName": "red-hot-deep-blue", "progression": {"displayPercent": "42.5%"}}
                            ]
                          }
                        ]
                      }
                    }
                    """),
            });
        });

        var config = new Configuration {
            EnableTomestoneProgressEnrichment = true,
            TomestoneApiKey = "valid-key",
            TomestoneRequestIntervalMs = 1000,
            TomestoneMaxConcurrency = 1,
        };
        var apiClient = new TomestoneApiClient(config, handler, utcNow: () => now);
        var progressionService = new TomestoneProgressionService(apiClient, config, utcNow: () => now);
        var delays = new List<TimeSpan>();
        var seams = TomestoneCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            progressionService,
            new ManualFFLogsTimeProvider { UtcNow = now },
            config,
            new ApiCredentialStatusStore(),
            requestDelayAsync: (delay, _) => {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var processor = new TomestoneBatchProcessor(seams);
        var session = new TomestoneLeaseSession(
            new UploadUrl("https://ingest.example"),
            [
                new TomestoneJob {
                    ContentId = 1,
                    ZoneId = 73,
                    DifficultyId = 101,
                    EncounterId = 101,
                    SecondaryEncounterId = 102,
                    Name = "Split Cache",
                    Server = "Tonberry",
                    Region = "JP",
                    LeaseToken = "lease-1",
                },
                new TomestoneJob {
                    ContentId = 2,
                    ZoneId = 73,
                    DifficultyId = 101,
                    EncounterId = 101,
                    Name = "Split Cache",
                    Server = "Tonberry",
                    Region = "JP",
                    LeaseToken = "lease-2",
                },
            ]);

        var result = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        Assert.Equal(2, result.ProcessedResults.Count);
        Assert.Equal(1, httpCalls);
        Assert.Empty(delays);
    }

    private static PendingDetailEntry CreatePendingDetail(uint listingId, DateTime nowUtc) {
        var detail = new UploadablePartyDetail {
            ListingId = listingId,
            LeaderContentId = 5000UL + listingId,
            LeaderName = "Test Leader",
            HomeWorld = 74,
            CreatedWorld = 74,
            ObservedAtUnixMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
        };
        return new PendingDetailEntry(
            detail,
            PartyDetailUploadPolicy.ComputeFingerprint(detail),
            PartyDetailRequestOwner.Scanner,
            nowUtc,
            0,
            nowUtc
        );
    }

    private static UploadableListing CreateStubListing(uint id, string name) {
        var listing = (UploadableListing)RuntimeHelpers.GetUninitializedObject(typeof(UploadableListing));
        typeof(UploadableListing)
            .GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(listing, id);
        typeof(UploadableListing)
            .GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(listing, Encoding.UTF8.GetBytes(name));
        return listing;
    }

    private sealed class StaticRegionProvider(string region) : IFFLogsWorkerRegionProvider {
        public bool TryGetWorkerRegion(out string resolvedRegion) {
            resolvedRegion = region;
            return true;
        }
    }

    private sealed class MultiResponseHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return responder(request, cancellationToken);
        }
    }

    private static HttpResponseMessage CreateBatchOkResponse(uint listingId, string status) {
        var normalizedStatus = status == "applied" ? "ok" : status;
        var body = JsonConvert.SerializeObject(new {
            status = "ok",
            requested = 1,
            accepted = 1,
            applied = 1,
            missing = 0,
            failed = 0,
            results = new[] {
                new {
                    listing_id = listingId,
                    status = normalizedStatus,
                    matched_count = 1,
                    modified_count = 1,
                },
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body),
        };
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) {
            _responses.Enqueue(responseFactory);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return _responses.Dequeue()(request, cancellationToken);
        }
    }
}
