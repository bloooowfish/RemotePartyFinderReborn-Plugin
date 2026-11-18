using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class WorkstreamThreeRegressionTests
{
    static WorkstreamThreeRegressionTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Job_lease_client_sends_processor_kind_and_job_mode_headers()
    {
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            }),
        };
        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var leaseClient = new FFLogsJobLeaseClient(seams, new FixedRegionProvider("JP"));
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [new UploadUrl("https://upload.example/")],
        };

        await leaseClient.TryAcquireSessionAsync(configuration, CancellationToken.None);

        var request = Assert.Single(sender.Requests);
        Assert.Equal(["dalamud_plugin_worker"], request.Headers.GetValues(FFLogsJobLeaseClient.ProcessorKindHeader).ToArray());
        Assert.Equal(["candidate-capable"], request.Headers.GetValues(FFLogsJobLeaseClient.JobModeHeader).ToArray());
        Assert.Equal(["JP"], request.Headers.GetValues(FFLogsJobLeaseClient.WorkerRegionHeader).ToArray());
    }

    [Fact]
    public async Task Result_submitter_splits_413_batch_in_half_and_abandons_single_oversized_result()
    {
        var submitPayloads = new List<List<ParseResult>>();
        var abandonPayloads = new List<List<AbandonFflogsLease>>();
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = async (request, ct) =>
            {
                var uri = request.RequestUri!.AbsolutePath;
                var body = await request.Content!.ReadAsStringAsync(ct);

                if (uri.EndsWith(IngestRoutes.FflogsLeasesAbandon.Path, StringComparison.Ordinal))
                {
                    abandonPayloads.Add(JsonConvert.DeserializeObject<List<AbandonFflogsLease>>(body)!);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"submitted\":1,\"released\":1,\"rejected\":0}"),
                    };
                }

                var chunk = JsonConvert.DeserializeObject<List<ParseResult>>(body)!;
                submitPayloads.Add(chunk);
                if (chunk.Count > 2 || chunk.Any(static item => item.ContentId == 9999))
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
                    {
                        Content = new StringContent("payload too large"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"status\":\"ok\",\"updated\":{chunk.Count},\"accepted\":{chunk.Count},\"submitted\":{chunk.Count},\"rejected\":0}}"),
                };
            },
        };

        var seams = FFLogsCollector.CreateSeams(sender, new StubFFLogsApiClient(), new ManualFFLogsTimeProvider());
        var submitBuffer = new FFLogsSubmitBuffer();
        var submitter = new FFLogsResultSubmitter(seams, submitBuffer);
        var uploadUrl = new UploadUrl("https://upload.example/");
        var session = new FFLogsLeaseSession(uploadUrl, []);
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };

        var batchResults = new[]
        {
            CreateParseResult(1001, "lease-1"),
            CreateParseResult(1002, "lease-2"),
            CreateParseResult(1003, "lease-3"),
            CreateParseResult(1004, "lease-4"),
        };

        var attempt = await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            batchResults,
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        Assert.False(attempt.HadTransientFailure);
        Assert.False(attempt.ShouldUseBaseDelayBeforeNextPoll);
        Assert.Equal([4, 2, 2], submitPayloads.Select(static p => p.Count).ToArray());

        // Single oversized item -> drops and abandons lease with payload_too_large
        submitPayloads.Clear();
        var oversizedAttempt = await submitter.TrySubmitResultsAsync(
            configuration,
            session,
            [CreateParseResult(9999, "lease-oversized")],
            CancellationToken.None,
            static _ => { },
            static _ => { },
            static _ => { });

        Assert.False(oversizedAttempt.HadTransientFailure);
        var abandonedBatch = Assert.Single(abandonPayloads);
        var abandoned = Assert.Single(abandonedBatch);
        Assert.Equal(9999UL, abandoned.ContentId);
        Assert.Equal("lease-oversized", abandoned.LeaseToken);
        Assert.Equal("payload_too_large", abandoned.Reason);
        Assert.Equal(FFLogsJobLeaseClient.ProcessorKindValue, abandoned.ProcessorKind);
    }

    [Fact]
    public async Task FFLogsClient_marks_null_character_as_nonexistent_and_CandidateMatcher_does_not_pick_null_first_server()
    {
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}"),
                    });
                }

                // c0 ("Aegis") returns null node, c1 ("Tonberry") returns visible empty node
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": {
                        "characterData": {
                          "c0": null,
                          "c1": {
                            "hidden": false,
                            "zoneRankings": { "rankings": [] }
                          }
                        }
                      }
                    }
                    """),
                });
            },
        };

        using var client = new FFLogsClient(
            new Configuration { FFLogsClientId = "id", FFLogsClientSecret = "secret" },
            handler);

        var job = new ParseJob
        {
            ContentId = 3001,
            Name = "Null Node Player",
            Server = "Aegis",
            Region = "JP",
            CandidateServers =
            [
                new ParseJobCandidateServer { Server = "Aegis", Region = "JP" },
                new ParseJobCandidateServer { Server = "Tonberry", Region = "JP" },
            ],
            ZoneId = 77,
            DifficultyId = 5,
            EncounterId = 88,
            LeaseToken = "lease-3001",
        };

        var matcher = new FFLogsCandidateMatcher([job], zoneId: 77, difficultyId: 5);
        var queries = matcher.BuildCandidateQueries();
        var fetched = await client.FetchCharacterCandidateDataBatchAsync(queries, 77, 5, CancellationToken.None);

        Assert.False(fetched[queries[0].Key].Exists);
        Assert.True(fetched[queries[1].Key].Exists);

        var matched = matcher.MatchFetchedCandidates(fetched);
        var parse = Assert.Single(matched.ResultsByContentId.Values);
        Assert.Equal("Tonberry", parse.MatchedServer);
    }

    [Fact]
    public async Task FFLogsClient_retries_oauth_refresh_once_on_401_and_stops_on_second_401()
    {
        var tokenIndex = 0;
        var graphQlCount = 0;
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.Ordinal))
                {
                    tokenIndex++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"access_token\":\"tok-{tokenIndex}\",\"expires_in\":3600}}"),
                    });
                }

                graphQlCount++;
                if (graphQlCount == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"ok":true}}"""),
                });
            },
        };

        using var client = new FFLogsClient(
            new Configuration { FFLogsClientId = "id", FFLogsClientSecret = "secret" },
            handler);

        var response = await client.QueryAsync("query { ok }", CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response!["data"]?["ok"]?.Value<bool>());
        Assert.Equal(2, tokenIndex);
        Assert.Equal(2, graphQlCount);

        // Persistent 401 should stop after single refresh retry
        tokenIndex = 0;
        graphQlCount = 0;
        var always401Handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.Ordinal))
                {
                    tokenIndex++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"access_token\":\"tok-{tokenIndex}\",\"expires_in\":3600}}"),
                    });
                }

                graphQlCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            },
        };
        using var always401Client = new FFLogsClient(
            new Configuration { FFLogsClientId = "id", FFLogsClientSecret = "secret" },
            always401Handler);

        var failed = await always401Client.QueryAsync("query { ok }", CancellationToken.None);

        Assert.Null(failed);
        Assert.Equal(2, tokenIndex);
        Assert.Equal(2, graphQlCount);
    }

    [Fact]
    public void QueryBuilder_EscapeGraphQlString_escapes_control_characters_and_quotes()
    {
        var escaped = FFLogsQueryBuilder.EscapeGraphQlString("A\"B\\C\n\r\t\b\f\u0001");

        Assert.Equal("A\\\"B\\\\C\\n\\r\\t\\b\\f\\u0001", escaped);
    }

    [Fact]
    public void Scanner_deferred_backoff_prevents_spin_and_stale_manual_states_expire()
    {
        var nowUtc = new DateTime(2026, 5, 12, 5, 0, 0, DateTimeKind.Utc);
        var firstDefer = PartyDetailUploadPolicy.ComputeDeferredNextAttemptAtUtc(nowUtc, deferCount: 1, static (min, _) => min);
        var secondDefer = PartyDetailUploadPolicy.ComputeDeferredNextAttemptAtUtc(nowUtc, deferCount: 2, static (min, _) => min);

        Assert.True(firstDefer - nowUtc >= TimeSpan.FromSeconds(2));
        Assert.True(secondDefer > firstDefer);

        var captureState = new PartyDetailCaptureState(() => nowUtc);
        var manualCycle = captureState.BeginRequest(PartyDetailRequestOwner.Manual, 10UL, 20UL);
        nowUtc = nowUtc.Add(PartyDetailCaptureState.StaleManualRequestTtl);
        var scannerCycle = captureState.BeginRequest(PartyDetailRequestOwner.Scanner, 30UL, 40UL);

        Assert.False(captureState.TryCancelRequest(manualCycle.RequestSerial));
        Assert.True(captureState.HasActiveRequest);
        Assert.Equal(scannerCycle.RequestSerial, captureState.CurrentRequestSerial);

        var queue = new PendingDetailQueue();
        var t0 = new DateTime(2026, 5, 12, 6, 0, 0, DateTimeKind.Utc);
        queue.Enqueue(new UploadablePartyDetail { ListingId = 1, LeaderContentId = 11, HomeWorld = 23, CreatedWorld = 77 }, 111UL, t0, PartyDetailRequestOwner.Manual);
        queue.Enqueue(new UploadablePartyDetail { ListingId = 2, LeaderContentId = 22, HomeWorld = 23, CreatedWorld = 77 }, 222UL, t0, PartyDetailRequestOwner.Scanner);

        var pruned = queue.PruneExpiredManualEntries(t0.AddSeconds(31), TimeSpan.FromSeconds(30));

        Assert.Equal(1, pruned);
        Assert.False(queue.TryGet(1U, out _));
        Assert.True(queue.TryGet(2U, out _));
    }

    [Fact]
    public async Task Configuration_upload_urls_have_stable_guids_and_thread_safe_snapshots()
    {
        var first = new UploadUrl("https://same.example/");
        var second = new UploadUrl("https://same.example/");

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);

        var configuration = new Configuration
        {
            UploadUrls = [first],
        };

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                var nextUrl = $"https://target-{i++ % 5}.example/";
                configuration.UpdateUploadUrls(list => list.Clear().Add(new UploadUrl(nextUrl)));
            }
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = configuration.GetUploadUrlsSnapshot();
                foreach (var item in snapshot)
                {
                    Assert.NotNull(item.Url);
                }
            }
        });

        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void DpapiSecretProtector_roundtrips_and_flags_legacy_plaintext_during_config_load()
    {
        var protectedValue = DpapiSecretProtector.Protect("super-secret-value");
        Assert.NotEqual("super-secret-value", protectedValue);
        Assert.StartsWith("dpapi:", protectedValue, StringComparison.Ordinal);

        var unprotected = DpapiSecretProtector.Unprotect(protectedValue, out var protectedNeedsMigration);
        Assert.False(protectedNeedsMigration);
        Assert.Equal("super-secret-value", unprotected);

        var legacyPlaintext = DpapiSecretProtector.Unprotect("legacy-key", out var legacyNeedsMigration);
        Assert.True(legacyNeedsMigration);
        Assert.Equal("legacy-key", legacyPlaintext);

        var json = """
        {
          "TomestoneApiKey": "plaintext-tomestone-key",
          "FFLogsClientSecret": "plaintext-fflogs-secret"
        }
        """;

        DpapiSecretProtector.BeginDeserialization();
        var loaded = JsonConvert.DeserializeObject<Configuration>(json)!;
        var hasMigration = DpapiSecretProtector.EndDeserialization();

        Assert.True(hasMigration);
        Assert.Equal("plaintext-tomestone-key", loaded.TomestoneApiKey);
        Assert.Equal("plaintext-fflogs-secret", loaded.FFLogsClientSecret);
    }

    [Fact]
    public void PlayerLocalDatabase_uses_wal_serves_in_memory_reads_flushes_coalesced_writes_and_prunes_stale_clean_players()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RemotePartyFinderReborn.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "player_cache.db");

        try
        {
            // Pre-seed old clean row (> 14 days old) and dirty row before opening PlayerLocalDatabase
            using (var seedConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate"))
            {
                seedConn.Open();
                using var cmd = seedConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE players (
                        content_id INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        home_world INTEGER NOT NULL,
                        current_world INTEGER NOT NULL,
                        account_id INTEGER NOT NULL,
                        payload_hash TEXT NOT NULL,
                        dirty INTEGER NOT NULL DEFAULT 1,
                        first_seen_utc TEXT NOT NULL,
                        last_seen_utc TEXT NOT NULL,
                        last_uploaded_utc TEXT
                    );
                    INSERT INTO players VALUES (10, 'Stale Clean', 23, 23, 100, 'h1', 0, '2020-01-01T00:00:00.0000000Z', '2020-01-02T00:00:00.0000000Z', '2020-01-02T00:00:00.0000000Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var db = new PlayerLocalDatabase(dbPath))
            {
                db.UpsertResolvedIdentity(new CharacterIdentitySnapshot(
                    ContentId: 777UL,
                    Name: "Fast Cache",
                    HomeWorld: 23,
                    WorldName: "Asura",
                    LastResolvedAtUtc: new DateTime(2026, 5, 12, 8, 0, 0, DateTimeKind.Utc)));

                // Readable immediately from in-memory cache before disk flush
                Assert.True(db.TryGetIdentity(777UL, out var cachedIdentity));
                Assert.Equal("Fast Cache", cachedIdentity.Name);

                db.UpsertObservedPlayers(
                [
                    new UploadablePlayer
                    {
                        ContentId = 20UL,
                        Name = "Fresh Player",
                        HomeWorld = 23,
                        CurrentWorld = 23,
                        AccountId = 200UL,
                    },
                ]);

                Assert.Single(db.TakePendingBatch(10));
                db.Flush();
            }

            // Re-open raw SQLite to verify WAL mode, persisted identity, persisted player, and stale clean pruning
            using var verifyConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
            verifyConn.Open();

            using (var modeCmd = verifyConn.CreateCommand())
            {
                modeCmd.CommandText = "PRAGMA journal_mode;";
                var mode = modeCmd.ExecuteScalar()?.ToString();
                Assert.Equal("wal", mode, ignoreCase: true);
            }

            using (var staleCmd = verifyConn.CreateCommand())
            {
                staleCmd.CommandText = "SELECT COUNT(*) FROM players WHERE content_id = 10;";
                Assert.Equal(0L, (long)staleCmd.ExecuteScalar()!);
            }

            using (var freshCmd = verifyConn.CreateCommand())
            {
                freshCmd.CommandText = "SELECT COUNT(*) FROM players WHERE content_id = 20;";
                Assert.Equal(1L, (long)freshCmd.ExecuteScalar()!);
            }

            using (var reopenedDb = new PlayerLocalDatabase(dbPath))
            {
                Assert.True(reopenedDb.TryGetIdentity(777UL, out var persisted));
                Assert.Equal("Fast Cache", persisted.Name);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Protected_executor_rethrows_operation_canceled_without_recording_endpoint_failure()
    {
        var sender = new StubFFLogsIngestHttpSender
        {
            OnSendAsync = static (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
        };
        var uploadUrl = new UploadUrl("https://secure.example/");
        var configuration = new Configuration
        {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };
        var executor = new ProtectedIngestEndpointExecutor(sender, static () => new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            configuration,
            uploadUrl,
            IngestRoutes.FflogsJobs,
            ProtectedEndpointCapabilityKind.FflogsJobs,
            HttpMethod.Get,
            jsonBody: null,
            recordUploadAttempt: true,
            cts.Token));

        Assert.Equal(0, uploadUrl.FailureCount);
    }

    private static ParseResult CreateParseResult(ulong contentId, string leaseToken) => new()
    {
        ContentId = contentId,
        ZoneId = 77,
        DifficultyId = 5,
        MatchedServer = "Tonberry",
        LeaseToken = leaseToken,
        Encounters = new Dictionary<int, double> { [88] = 85.0 },
        ClearCounts = new Dictionary<int, int> { [88] = 3 },
    };

    private sealed class FixedRegionProvider(string region) : IFFLogsWorkerRegionProvider
    {
        public bool TryGetWorkerRegion(out string value)
        {
            value = region;
            return true;
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSendAsync { get; set; }
            = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => OnSendAsync(request, cancellationToken);
    }
}
