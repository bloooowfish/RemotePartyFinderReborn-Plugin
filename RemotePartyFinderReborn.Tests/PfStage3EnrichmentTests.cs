using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PfStage3EnrichmentTests {
    static PfStage3EnrichmentTests() {
        DalamudAssemblyResolver.Register();
    }

    [Fact]
    public void FilterState_defaults_to_live_data_without_samples() {
        var filter = new PfFilterState();
        Assert.False(filter.IncludeSampleListings);
        Assert.False(filter.UseSampleEnrichment);
    }

    [Fact]
    public void IconCatalog_and_FilterEngine_expose_full_region_logical_dcs_via_fallback() {
        var catalog = new PfIconCatalog();

        Assert.Equal(["Elemental", "Gaia", "Mana", "Meteor"], catalog.GetRegionDataCenters(52).ToArray());
        Assert.Equal(["Aether", "Crystal", "Dynamis", "Primal"], catalog.GetRegionDataCenters(73).ToArray());
        Assert.Equal(["Chaos", "Light", "Shadow"], catalog.GetRegionDataCenters(33).ToArray());
        Assert.Equal(["Materia"], catalog.GetRegionDataCenters(21).ToArray());
        Assert.Empty(catalog.GetRegionDataCenters(0));

        var now = new DateTime(2026, 11, 28, 16, 0, 0, DateTimeKind.Utc);
        var entries = new[] {
            new PfListingEntry {
                Id = 1,
                DataCenterName = "Mana",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            },
        };

        var regionDcs = catalog.GetRegionDataCenters(52);
        var availableDcs = PfFilterEngine.GetAvailableDataCenters(entries, includeSampleListings: false, regionDataCenters: regionDcs);
        Assert.Equal(["Elemental", "Gaia", "Mana", "Meteor"], availableDcs.ToArray());
    }

    [Fact]
    public void NativePfBridge_headless_inspect_respects_debounce_guards_timeout_and_manual_open_safety() {
        var now = new DateTime(2026, 11, 28, 16, 10, 0, DateTimeKind.Utc);
        var executed = new List<NativePfAction>();
        var driver = new StubHeadlessInspectDriver();
        using var bridge = new NativePfBridge(
            action => executed.Add(action),
            mirrorStore: null,
            utcNow: () => now,
            headlessDriver: driver);

        // 1. Debounce (150ms): second call replaces first and resets deadline
        bridge.QueueInspectListingHeadless(101UL, 5001UL);
        bridge.QueueInspectListingHeadless(102UL, 5002UL);
        Assert.Equal(102UL, bridge.PendingHeadlessListingIdForTesting);

        now = now.AddMilliseconds(100);
        bridge.ProcessPendingHeadlessInspectForTesting();
        Assert.Equal(0, bridge.QueuedCount);

        now = now.AddMilliseconds(60);
        bridge.ProcessPendingHeadlessInspectForTesting();
        Assert.Equal(1, bridge.QueuedCount);

        // 2. LookingForGroupDetail open guard -> skip
        driver.IsDetailAddonOpen = true;
        bridge.DrainQueue();
        Assert.Null(bridge.ActiveUiHeadlessAttemptIdForTesting);
        Assert.Empty(driver.ArmedAttempts);

        // 3. DebugPfScanner running guard -> skip
        driver.IsDetailAddonOpen = false;
        driver.IsScannerRunning = true;
        bridge.QueueInspectListingHeadless(103UL, 5003UL);
        now = now.AddMilliseconds(160);
        bridge.ProcessPendingHeadlessInspectForTesting();
        bridge.DrainQueue();
        Assert.Null(bridge.ActiveUiHeadlessAttemptIdForTesting);
        Assert.Empty(driver.ArmedAttempts);

        // 4. Not ready guard -> skip
        driver.IsScannerRunning = false;
        driver.IsScannerCaptureReady = false;
        bridge.QueueInspectListingHeadless(104UL, 5004UL);
        now = now.AddMilliseconds(160);
        bridge.ProcessPendingHeadlessInspectForTesting();
        bridge.DrainQueue();
        Assert.Null(bridge.ActiveUiHeadlessAttemptIdForTesting);
        Assert.Empty(driver.ArmedAttempts);

        // 5. Ready -> arms, begins, opens, ends, and clears on NotifyDetailCaptured
        driver.IsScannerCaptureReady = true;
        bridge.QueueInspectListingHeadless(105UL, 5005UL);
        now = now.AddMilliseconds(160);
        bridge.ProcessPendingHeadlessInspectForTesting();
        bridge.DrainQueue();

        var attemptId = bridge.ActiveUiHeadlessAttemptIdForTesting;
        Assert.NotNull(attemptId);
        Assert.Equal([(attemptId!.Value, 105UL, 5005UL, true)], driver.ArmedAttempts);
        Assert.Equal([attemptId.Value], driver.BegunAttempts);
        Assert.Equal([105UL], driver.OpenedListings);
        Assert.Equal([attemptId.Value], driver.EndedAttempts);
        Assert.Empty(driver.ClearedAttempts);

        bridge.NotifyDetailCaptured(105UL);
        Assert.Null(bridge.ActiveUiHeadlessAttemptIdForTesting);
        Assert.Equal([attemptId.Value], driver.ClearedAttempts);

        // 6. Manual OpenListing cancels pending debounce AND clears active headless attempt before opening
        bridge.QueueInspectListingHeadless(106UL, 5006UL);
        now = now.AddMilliseconds(160);
        bridge.ProcessPendingHeadlessInspectForTesting();
        bridge.DrainQueue();
        var activeAttemptId = bridge.ActiveUiHeadlessAttemptIdForTesting;
        Assert.NotNull(activeAttemptId);

        bridge.QueueInspectListingHeadless(107UL, 5007UL); // pending debounce
        bridge.QueueOpenListing(106UL);
        Assert.Equal(0UL, bridge.PendingHeadlessListingIdForTesting);

        bridge.DrainQueue();
        Assert.Null(bridge.ActiveUiHeadlessAttemptIdForTesting);
        Assert.Contains(activeAttemptId!.Value, driver.ClearedAttempts);
        Assert.Contains(executed, a => a.Kind == NativePfActionKind.OpenListing && a.ListingId == 106UL);

        // 7. 1500ms timeout clears active headless attempt
        bridge.QueueInspectListingHeadless(108UL, 5008UL);
        now = now.AddMilliseconds(160);
        bridge.ProcessPendingHeadlessInspectForTesting();
        bridge.DrainQueue();
        var timedOutAttemptId = bridge.ActiveUiHeadlessAttemptIdForTesting;
        Assert.NotNull(timedOutAttemptId);

        now = now.AddMilliseconds(1501);
        bridge.ProcessPendingHeadlessInspectForTesting();
        Assert.Null(bridge.ActiveUiHeadlessAttemptIdForTesting);
        Assert.Contains(timedOutAttemptId!.Value, driver.ClearedAttempts);
    }

    [Fact]
    public void PartyDetailCaptureRuntime_DetailCaptured_updates_mirror_store_without_consuming_arrival_queue() {
        var now = new DateTime(2026, 11, 28, 16, 20, 0, DateTimeKind.Utc);
        var state = new PartyDetailCaptureState(() => now);
        using var runtime = PartyDetailCaptureRuntime.CreateForTesting(state, ScannerHeadlessBackendKind.PopulateCorrelation);
        using var store = new PfLocalMirrorStore(() => now);

        store.UpsertLiveListing(new PfListingEntry {
            Id = 8001UL,
            LeaderContentId = 90001UL,
            LeaderName = "Leader",
            HomeWorldId = 52,
            HomeWorldName = "Chocobo",
            RawDuty = 1004,
            DutyName = "AAC Cruiserweight M4 (Savage)",
            IsHighEnd = true,
            SlotsAvailable = 8,
            SlotsFilled = 2,
            JobsPresent = [19, 24, 0, 0, 0, 0, 0, 0],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });

        var capturedCount = 0;
        runtime.DetailCaptured += _ => throw new InvalidOperationException("listener failure should be isolated");
        runtime.DetailCaptured += detail => {
            capturedCount++;
            store.UpsertLiveDetail(detail);
        };

        // 1. Agent snapshot path (manual/OpenListing)
        runtime.TestBeginManualRequestCycle(8001UL, 90001UL);
        Assert.True(runtime.TestPopulateListingDataSnapshot(new UploadablePartyDetail {
            ListingId = 8001UL,
            LeaderContentId = 90001UL,
            LeaderName = "Leader",
            HomeWorld = 52,
            CreatedWorld = 52,
            MemberContentIds = [90001UL, 90002UL, 0, 0, 0, 0, 0, 0],
            MemberJobs = [19, 24, 0, 0, 0, 0, 0, 0],
            SlotFlags = ["0x1", "0x2", "0x0", "0x0", "0x0", "0x0", "0x0", "0x0"],
        }));

        Assert.Equal(1, capturedCount);
        Assert.True(state.TryGetNextUnconsumedArrival(out var pendingManual));
        Assert.Equal(8001UL, pendingManual.Payload.ListingId);
        Assert.True(state.TryMarkConsumed(pendingManual.Generation));

        var liveListing = store.TryGetListing(8001UL, includeSamples: false, useSampleEnrichment: false)!;
        var rows = store.BuildMemberRows(liveListing, useSampleEnrichment: false, playerClassJobId: null);
        Assert.Equal(90002UL, rows[1].ContentId);

        // 2. Scanner headless path
        var attemptId = Guid.NewGuid();
        runtime.ArmScannerRequest(attemptId, 8002UL, 90003UL, allowHeadless: true);
        runtime.BeginScannerOpenAttempt(attemptId);
        runtime.TestInterceptOpenListing(8002UL, 90003UL);
        runtime.EndScannerOpenAttempt(attemptId);
        runtime.TestObservePopulationEvent();
        Assert.True(runtime.TryBeginScannerHeadlessScope(attemptId));
        Assert.True(runtime.TryRecordScannerHeadlessArrival(attemptId, new UploadablePartyDetail {
            ListingId = 8002UL,
            LeaderContentId = 90003UL,
            LeaderName = "Scanner Leader",
            HomeWorld = 52,
            CreatedWorld = 52,
            MemberContentIds = [90003UL, 90004UL],
            MemberJobs = [21, 28],
            SlotFlags = ["0x1", "0x2"],
        }));

        Assert.Equal(2, capturedCount);
        Assert.True(state.TryGetNextUnconsumedArrival(out var pendingScanner));
        Assert.Equal(8002UL, pendingScanner.Payload.ListingId);
    }

    [Fact]
    public void ProtectedCapabilityStore_handles_PluginEnrichment_and_snake_case_deserialization() {
        var now = new DateTimeOffset(2026, 11, 28, 16, 30, 0, DateTimeOffset.UtcNow.Offset);
        var json = $$"""
            {
                "status": "ok",
                "protected_endpoints": {
                    "plugin_enrichment": {
                        "token": "enrich-cap-token",
                        "expires_at": {{now.AddMinutes(10).ToUnixTimeSeconds()}}
                    }
                }
            }
            """;

        Assert.True(IngestJson.TryDeserializeObject(json, out ContributeMultipleResponse? response));
        Assert.NotNull(response);
        Assert.NotNull(response!.ProtectedEndpoints);
        Assert.Equal("enrich-cap-token", response.ProtectedEndpoints.PluginEnrichment.Token);

        var uploadUrl = new UploadUrl("https://secure.example/");
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.PluginEnrichment, now));

        uploadUrl.RequireProtectedCapabilities(ProtectedEndpointCapabilityKind.PluginEnrichment);
        Assert.True(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.PluginEnrichment, now));
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.FflogsJobs, now));

        uploadUrl.ApplyProtectedCapabilities(response.ProtectedEndpoints, now);
        Assert.False(uploadUrl.ShouldDeferProtectedRequest(ProtectedEndpointCapabilityKind.PluginEnrichment, now));
        Assert.True(uploadUrl.TryGetProtectedCapability(ProtectedEndpointCapabilityKind.PluginEnrichment, out var token, now));
        Assert.Equal("enrich-cap-token", token);
    }

    [Fact]
    public async Task PfEnrichmentClient_listings_enrichment_dedupes_batches_and_overlays_store() {
        var now = new DateTime(2026, 11, 28, 16, 40, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://enrichment.example/");
        var config = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };

        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = async (request, _) => {
                Assert.Equal("/api/plugin/listings-enrichment", request.RequestUri!.AbsolutePath);
                var reqBody = await request.Content!.ReadAsStringAsync();
                var reqDto = JsonConvert.DeserializeObject<PfEnrichmentClient.ListingsEnrichmentRequestDto>(reqBody)!;
                Assert.Equal(50, reqDto.Items.Count);
                Assert.Equal(1UL, reqDto.Items[0].ListingId);

                var respDto = new PfEnrichmentClient.ListingsEnrichmentResponseDto {
                    Results = [
                        new PfEnrichmentClient.ListingsEnrichmentResultItemDto {
                            ListingId = 1UL,
                            ZoneId = 125,
                            DifficultyId = 101,
                            LeaderParse = new PfEnrichmentClient.ApiParseDisplayDto {
                                PrimaryPercentile = 97,
                                PrimaryColorClass = "parse-orange",
                                SecondaryPercentile = 88,
                                SecondaryColorClass = "parse-purple",
                                HasSecondary = true,
                            },
                            LeaderProgress = new PfEnrichmentClient.ApiProgressDisplayDto {
                                FinalClearCount = 12,
                            },
                            CachedMemberCount = 5,
                        },
                    ],
                };

                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(JsonConvert.SerializeObject(respDto), Encoding.UTF8, "application/json"),
                };
            },
        };

        using var client = new PfEnrichmentClient(
            config,
            httpSender: sender,
            utcNow: () => now,
            delayAsync: static (_, _) => Task.CompletedTask);
        using var store = new PfLocalMirrorStore(new PfIconCatalog(), null, () => now, client);

        var listings = new List<PfListingEntry>();
        // 55 listings + duplicate of listing 1 -> deduped to 55, batched to 50
        for (ulong i = 1; i <= 55; i++) {
            var entry = new PfListingEntry {
                Id = i,
                LeaderContentId = 10_000UL + i,
                LeaderName = $"Leader{i}",
                HomeWorldId = 52,
                HomeWorldName = "Chocobo",
                RawDuty = 1004,
                DutyName = "AAC Cruiserweight M4 (Savage)",
                IsHighEnd = true,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            };
            listings.Add(entry);
            store.UpsertLiveListing(entry);
        }
        listings.Add(listings[0]); // duplicate

        client.NotifyVisibleListings(listings);
        await client.WaitForListingsEnrichmentForTestingAsync();

        Assert.Single(sender.Requests);
        Assert.True(client.TryGetLeaderSummary(1UL, 1004, 10_001UL, out var cachedSummary));
        Assert.Equal((byte)97, cachedSummary.Parse.PrimaryPercentile);
        Assert.Equal((byte)88, cachedSummary.Parse.SecondaryPercentile);
        Assert.Equal(5, cachedSummary.CachedMemberCount);

        var snapshotEntry = store.TryGetListing(1UL, includeSamples: false, useSampleEnrichment: false)!;
        Assert.NotNull(snapshotEntry.LeaderParse);
        Assert.Equal((byte)97, snapshotEntry.LeaderParse!.PrimaryPercentile);
        Assert.Equal(5, snapshotEntry.CachedMemberCount);
        Assert.Equal((ushort)12, snapshotEntry.LeaderProgress!.ClearCount);

        // TTL check: after 61s, cache expires
        now = now.AddSeconds(61);
        Assert.False(client.TryGetLeaderSummary(1UL, 1004, 10_001UL, out _));
    }

    [Fact]
    public async Task PfEnrichmentClient_party_enrichment_merges_cache_executes_self_lease_and_requeries_once() {
        var now = new DateTime(2026, 11, 28, 16, 50, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://enrichment.example/");
        var config = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };

        var callCount = 0;
        var fflogsSelfLeaseCalls = 0;
        var tomestoneSelfLeaseCalls = 0;

        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = async (request, _) => {
                var body = await request.Content!.ReadAsStringAsync();
                var reqDto = JsonConvert.DeserializeObject<PfEnrichmentClient.PartyEnrichmentRequestDto>(body)!;
                callCount++;

                if (callCount == 1) {
                    Assert.True(reqDto.AllowSelfLeaseFflogs);
                    Assert.True(reqDto.AllowSelfLeaseTomestone);
                    Assert.Equal("JP", reqDto.WorkerRegion);
                    var resp = new PfEnrichmentClient.PartyEnrichmentResponseDto {
                        ListingId = 701UL,
                        ZoneId = 125,
                        DifficultyId = 101,
                        Members = [
                            new PfEnrichmentClient.PartyEnrichmentMemberDto {
                                ContentId = 2001UL,
                                Name = "Resolved One",
                                HomeWorld = 52,
                                Parse = new PfEnrichmentClient.ApiParseDisplayDto(),
                                Progress = new PfEnrichmentClient.ApiProgressDisplayDto(),
                                State = "loading",
                            },
                        ],
                        SelfLeaseFflogsJobs = [new ParseJob { ContentId = 2001UL, Name = "Resolved One", Server = "Chocobo", Region = "JP", ZoneId = 125, DifficultyId = 101, LeaseToken = "ff-lease" }],
                        SelfLeaseTomestoneJobs = [new TomestoneJob { ContentId = 2001UL, Name = "Resolved One", Server = "Chocobo", Region = "JP", ZoneId = 125, DifficultyId = 101, EncounterId = 99, LeaseToken = "ts-lease" }],
                        RetryAfterMs = 2000,
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(JsonConvert.SerializeObject(resp), Encoding.UTF8, "application/json"),
                    };
                }

                // Second call immediately after self-lease must disable self-lease
                Assert.False(reqDto.AllowSelfLeaseFflogs);
                Assert.False(reqDto.AllowSelfLeaseTomestone);
                var updated = new PfEnrichmentClient.PartyEnrichmentResponseDto {
                    ListingId = 701UL,
                    ZoneId = 125,
                    DifficultyId = 101,
                    Members = [
                        new PfEnrichmentClient.PartyEnrichmentMemberDto {
                            ContentId = 2001UL,
                            Name = "Resolved One",
                            HomeWorld = 52,
                            Parse = new PfEnrichmentClient.ApiParseDisplayDto { PrimaryPercentile = 92, PrimaryColorClass = "parse-orange" },
                            Progress = new PfEnrichmentClient.ApiProgressDisplayDto { FinalClearCount = 7 },
                            State = "cached",
                        },
                    ],
                    RetryAfterMs = 2000,
                };
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(JsonConvert.SerializeObject(updated), Encoding.UTF8, "application/json"),
                };
            },
        };

        var regionProvider = new CachedFFLogsWorkerRegionProvider();
        regionProvider.UpdateCurrentWorld(52);

        using var client = new PfEnrichmentClient(
            config,
            httpSender: sender,
            workerRegionProvider: regionProvider,
            isPluginOperational: () => true,
            isFFLogsSelfLeaseAvailable: () => true,
            executeFFLogsSelfLeaseAsync: (_, jobs, _) => {
                fflogsSelfLeaseCalls += jobs.Count;
                return Task.FromResult(true);
            },
            isTomestoneSelfLeaseAvailable: () => true,
            executeTomestoneSelfLeaseAsync: (_, jobs, _) => {
                tomestoneSelfLeaseCalls += jobs.Count;
                return Task.FromResult(true);
            },
            utcNow: () => now,
            delayAsync: static (_, _) => Task.CompletedTask);

        using var store = new PfLocalMirrorStore(new PfIconCatalog(), null, () => now, client);
        var listing = new PfListingEntry {
            Id = 701UL,
            LeaderContentId = 2001UL,
            LeaderName = "Resolved One",
            HomeWorldId = 52,
            HomeWorldName = "Chocobo",
            RawDuty = 1004,
            DutyName = "AAC Cruiserweight M4 (Savage)",
            IsHighEnd = true,
            SlotsAvailable = 8,
            SlotsFilled = 1,
            JobsPresent = [19, 0, 0, 0, 0, 0, 0, 0],
            MemberContentIds = [2001UL],
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        store.UpsertLiveListing(listing);

        client.RequestPartyEnrichment(listing, [2001UL], [new PfKnownMemberHint(2001UL, "Resolved One", 52)]);
        await client.WaitForPartyEnrichmentForTestingAsync();

        Assert.Equal(2, callCount);
        Assert.Equal(1, fflogsSelfLeaseCalls);
        Assert.Equal(1, tomestoneSelfLeaseCalls);

        var rows = store.BuildMemberRows(listing, useSampleEnrichment: false, playerClassJobId: null);
        Assert.Equal(PfMemberEnrichmentState.Cached, rows[0].EnrichmentState);
        Assert.Equal((byte)92, rows[0].Parse.PrimaryPercentile);
        Assert.Equal((ushort)7, rows[0].Progress.ClearCount);
        Assert.True(rows[0].Progress.Cleared);
    }

    [Fact]
    public async Task PfEnrichmentClient_performs_bounded_loading_backoff_and_stops_on_close_or_dispose() {
        var now = new DateTime(2026, 11, 28, 17, 0, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://enrichment.example/");
        var config = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
        };

        var observedDelays = new List<TimeSpan>();
        var requestCount = 0;

        var sender = new StubFFLogsIngestHttpSender {
            OnSendAsync = (_, _) => {
                requestCount++;
                var resp = new PfEnrichmentClient.PartyEnrichmentResponseDto {
                    ListingId = 801UL,
                    ZoneId = 125,
                    DifficultyId = 101,
                    Members = [
                        new PfEnrichmentClient.PartyEnrichmentMemberDto {
                            ContentId = 3001UL,
                            Name = "Loading Player",
                            HomeWorld = 52,
                            Parse = new PfEnrichmentClient.ApiParseDisplayDto(),
                            Progress = new PfEnrichmentClient.ApiProgressDisplayDto(),
                            State = "loading",
                        },
                    ],
                    RetryAfterMs = 2000,
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(JsonConvert.SerializeObject(resp), Encoding.UTF8, "application/json"),
                });
            },
        };

        using var client = new PfEnrichmentClient(
            config,
            httpSender: sender,
            utcNow: () => now,
            delayAsync: (delay, ct) => {
                observedDelays.Add(delay);
                return Task.Delay(1, ct);
            });

        var listing = new PfListingEntry {
            Id = 801UL,
            LeaderContentId = 3001UL,
            LeaderName = "Loading Player",
            HomeWorldId = 52,
            RawDuty = 1004,
            IsHighEnd = true,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        client.RequestPartyEnrichment(listing, [3001UL], [new PfKnownMemberHint(3001UL, "Loading Player", 52)]);
        await client.WaitForPartyEnrichmentForTestingAsync();

        // 1 initial + 3 bounded backoff retries = 4 requests
        Assert.Equal(4, requestCount);
        Assert.Equal([TimeSpan.FromMilliseconds(2000), TimeSpan.FromMilliseconds(3000), TimeSpan.FromMilliseconds(5000)], observedDelays);

        // Verify SetActiveListing(0) cancels polling immediately
        var secondDelays = 0;
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelClient = new PfEnrichmentClient(
            config,
            httpSender: sender,
            utcNow: () => now,
            delayAsync: async (_, ct) => {
                secondDelays++;
                cancelGate.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            });

        cancelClient.RequestPartyEnrichment(listing, [3001UL], [new PfKnownMemberHint(3001UL, "Loading Player", 52)], forceRefresh: true);
        await cancelGate.Task;
        cancelClient.SetActiveListing(0);
        await cancelClient.WaitForPartyEnrichmentForTestingAsync();
        Assert.Equal(1, secondDelays);
    }

    [Fact]
    public async Task FFLogsCollector_and_TomestoneCollector_execute_self_leased_jobs_and_submit_results() {
        var now = new DateTime(2026, 11, 28, 17, 10, 0, DateTimeKind.Utc);
        var uploadUrl = new UploadUrl("https://selflease.example/");
        var config = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
            UploadUrls = [uploadUrl],
            FFLogsClientId = "ff-id",
            FFLogsClientSecret = "ff-secret",
            EnableFFLogsWorker = true,
            TomestoneApiKey = "ts-key",
            EnableTomestoneProgressEnrichment = true,
            TomestoneRequestIntervalMs = 0,
        };

        // FFLogs self-lease
        var ffSender = new StubFFLogsIngestHttpSender {
            OnSendAsync = static (req, _) => {
                Assert.Equal("/contribute/fflogs/results", req.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("""{"status":"ok","submitted":1,"accepted":1,"updated":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                });
            },
        };
        var ffApiClient = new StubFFLogsApiClient {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) => Task.FromResult(
                queries.ToDictionary(
                    static q => q.Key,
                    static _ => new FFLogsClient.CharacterFetchedData {
                        Hidden = false,
                        Parses = [
                            new FFLogsClient.CharacterEncounterParse {
                                EncounterId = 99,
                                Percentile = 95.5,
                                ClearCount = 4,
                            },
                        ],
                    })),
        };
        var ffSeams = FFLogsCollector.CreateSeams(ffSender, ffApiClient, new ManualFFLogsTimeProvider { UtcNow = now });
        using var ffCollector = FFLogsCollector.CreateForTesting(
            config,
            ffSeams,
            new FFLogsSubmitBuffer(),
            new FFLogsWorkerPolicy(config, static _ => { }, ffSeams.TimeProvider));

        Assert.True(ffCollector.IsSelfLeaseAvailable);
        var ffSucceeded = await ffCollector.ExecuteSelfLeasedJobsAsync(
            uploadUrl,
            [
                new ParseJob {
                    ContentId = 5001UL,
                    Name = "Alice",
                    Server = "Chocobo",
                    Region = "JP",
                    ZoneId = 125,
                    DifficultyId = 101,
                    EncounterId = 99,
                    ZoneEncounters = [99],
                    LeaseToken = "ff-self-1",
                },
            ],
            CancellationToken.None);

        Assert.True(ffSucceeded);
        Assert.Single(ffSender.Requests);

        // Tomestone self-lease
        var tsSender = new StubFFLogsIngestHttpSender {
            OnSendAsync = static (req, _) => {
                Assert.Equal("/contribute/tomestone/results", req.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("""{"status":"ok","submitted":1,"accepted":1,"updated":1,"rejected":0}""", Encoding.UTF8, "application/json"),
                });
            },
        };
        var tsService = new StubTomestoneProgressionService {
            OnFetchProgressionDataAsync = static (_, _, _, _, _) => Task.FromResult(
                new TomestoneProgressData(
                    [new PhaseProgress { PhaseKey = "p2", PhaseName = "P2", Order = 2, BossPercentage = 42.5, DisplayText = "P2 42.5%" }],
                    42.5,
                    Cleared: false)),
        };
        var tsSeams = TomestoneCollector.CreateSeams(
            tsSender,
            tsService,
            new ManualFFLogsTimeProvider { UtcNow = now },
            config,
            new ApiCredentialStatusStore(),
            static (_, _) => Task.CompletedTask);
        using var tsCollector = TomestoneCollector.CreateForTesting(
            config,
            tsSeams,
            new TomestoneSubmitBuffer(),
            new FFLogsWorkerPolicy(config, static _ => { }, tsSeams.TimeProvider));

        Assert.True(tsCollector.IsSelfLeaseAvailable);
        var tsSucceeded = await tsCollector.ExecuteSelfLeasedJobsAsync(
            uploadUrl,
            [
                new TomestoneJob {
                    ContentId = 5001UL,
                    Name = "Alice",
                    Server = "Chocobo",
                    Region = "JP",
                    ZoneId = 62,
                    DifficultyId = 0,
                    EncounterId = 1077,
                    TomestoneExpansion = "dawntrail",
                    TomestoneCategory = "raids",
                    TomestoneEncounterSlug = "aac-light-heavyweight",
                    TomestoneEncounterNames = ["Black Cat"],
                    TomestoneProgressKind = "boss_percentage",
                    LeaseToken = "ts-self-1",
                },
            ],
            CancellationToken.None);

        Assert.True(tsSucceeded);
        Assert.Single(tsSender.Requests);
    }

    private sealed class StubHeadlessInspectDriver : INativePfHeadlessInspectDriver {
        public bool IsDetailAddonOpen { get; set; }
        public bool IsScannerRunning { get; set; }
        public bool IsScannerCaptureReady { get; set; } = true;
        public bool OpenListingResult { get; set; } = true;

        public List<(Guid AttemptId, ulong ListingId, ulong LeaderContentId, bool AllowHeadless)> ArmedAttempts { get; } = [];
        public List<Guid> BegunAttempts { get; } = [];
        public List<ulong> OpenedListings { get; } = [];
        public List<Guid> EndedAttempts { get; } = [];
        public List<Guid> ClearedAttempts { get; } = [];

        public void ArmScannerRequest(Guid attemptId, ulong listingId, ulong leaderContentId, bool allowHeadless)
            => ArmedAttempts.Add((attemptId, listingId, leaderContentId, allowHeadless));

        public void BeginScannerOpenAttempt(Guid attemptId)
            => BegunAttempts.Add(attemptId);

        public bool OpenListing(ulong listingId) {
            OpenedListings.Add(listingId);
            return OpenListingResult;
        }

        public void EndScannerOpenAttempt(Guid attemptId)
            => EndedAttempts.Add(attemptId);

        public void ClearScannerRequest(Guid attemptId)
            => ClearedAttempts.Add(attemptId);
    }
}
