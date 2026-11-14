using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneThreeTierTests
{
    static TomestoneThreeTierTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public void ClearsParser_extracts_grouped_and_flat_rankings_and_ignores_achievement_rank_percent()
    {
        var groupedPayload = JObject.Parse("""
        {
          "achievement": { "rankPercent": { "percent": 99.9 } },
          "encounters": {
            "rankings": {
              "rankings": [
                {
                  "rankings": [
                    {
                      "encounters": [
                        {
                          "encounterId": 2061,
                          "rankPercent": { "percent": 84.5 },
                          "totalClears": 4
                        },
                        {
                          "encounterId": 1001,
                          "rankPercent": { "percent": 97.0 },
                          "totalClears": 9
                        }
                      ]
                    },
                    {
                      "encounters": [
                        {
                          "encounterId": 2061,
                          "rankPercent": { "percent": 88.25 },
                          "totalClears": 6
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          }
        }
        """);

        var groupedRankings = TomestoneClearsParser.ExtractClearsRankings(groupedPayload, [2061]);

        Assert.True(groupedRankings.Encounters.TryGetValue(2061, out var chaoticPercent));
        Assert.Equal(88.25, chaoticPercent, 2);
        Assert.Equal(4, groupedRankings.ClearCounts[2061]);
        Assert.False(groupedRankings.Encounters.ContainsKey(1001));

        var flatPayload = JObject.Parse("""
        {
          "achievement": { "rankPercent": { "percent": 99.4 } },
          "encounters": {
            "rankings": [
              {
                "encounters": [
                  { "encounterId": 94, "rankPercent": { "percent": 72.1 }, "totalClears": 3 },
                  { "encounterId": 95, "rankPercent": { "percent": 91.8 }, "totalClears": 2 }
                ]
              }
            ]
          }
        }
        """);

        var flatRankings = TomestoneClearsParser.ExtractClearsRankings(flatPayload, [94, 95]);

        Assert.Equal(72.1, flatRankings.Encounters[94], 1);
        Assert.Equal(3, flatRankings.ClearCounts[94]);
        Assert.Equal(91.8, flatRankings.Encounters[95], 1);
        Assert.Equal(2, flatRankings.ClearCounts[95]);

        var achievementOnlyPayload = JObject.Parse("""
        {
          "achievement": { "rankPercent": { "percent": 98.7 } },
          "encounters": {
            "rankings": []
          }
        }
        """);

        var emptyRankings = TomestoneClearsParser.ExtractClearsRankings(achievementOnlyPayload, [94, 2061]);
        Assert.Empty(emptyRankings.Encounters);
        Assert.Empty(emptyRankings.ClearCounts);
    }

    [Fact]
    public void ClearsParser_handles_chaotic_cloud_of_darkness_and_live_dot_slug_and_uncleared_progress()
    {
        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(66, 2061, out var chaoticParams));
        Assert.Equal("the-cloud-of-darkness-chaotic", chaoticParams.Encounter);
        Assert.Equal("chaotic", chaoticParams.EffectiveClearsCategory);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(62, 94, out var honeyParams));
        Assert.Contains("honey-b.-lovely", honeyParams.TargetCanonicalNames!);
        Assert.Contains("honey-b-lovely", honeyParams.TargetCanonicalNames!);

        var chaoticJob = new ParseJob
        {
            ContentId = 7001,
            Name = "Chaotic Player",
            Server = "Tonberry",
            Region = "JP",
            ZoneId = 66,
            DifficultyId = 100,
            EncounterId = 2061,
            LeaseToken = "lease-chaotic",
        };
        var chaoticContent = JObject.Parse("""
        {
          "encounters": {
            "rankings": [
              {
                "encounters": [
                  { "encounterId": 2061, "rankPercent": { "percent": 79.4 }, "totalClears": 5 }
                ]
              }
            ]
          }
        }
        """);

        var chaoticResult = TomestoneClearsParser.ParseClearsOrProgressIntoResult(
            chaoticContent,
            chaoticJob,
            [(2061U, chaoticParams)],
            matchedServer: "Tonberry");

        Assert.NotNull(chaoticResult);
        Assert.Equal(79.4, chaoticResult!.Encounters[2061], 1);
        Assert.Equal(5, chaoticResult.ClearCounts[2061]);

        var honeyJob = new ParseJob
        {
            ContentId = 7002,
            Name = "Honey Player",
            Server = "Tonberry",
            Region = "JP",
            ZoneId = 62,
            DifficultyId = 101,
            EncounterId = 94,
            LeaseToken = "lease-honey",
        };
        var honeyActivityContent = JObject.Parse("""
        {
          "encounters": {
            "savage": [
              {
                "canonicalName": "honey-b.-lovely",
                "progression": {
                  "displayPercent": "32.5%"
                }
              }
            ]
          }
        }
        """);

        var honeyResult = TomestoneClearsParser.ParseClearsOrProgressIntoResult(
            honeyActivityContent,
            honeyJob,
            [(94U, honeyParams)],
            matchedServer: "Tonberry");

        Assert.NotNull(honeyResult);
        Assert.Equal(32.5, honeyResult!.BossPercentages[94], 1);
    }

    [Fact]
    public void ProgressParser_extracts_and_parses_progression_graph_url_and_nodes()
    {
        var activityContent = JObject.Parse("""
        {
          "activity": {
            "progressionGraphUrl": "/character/12345/alpha-omega/activity/futures-rewritten-ultimate/graph",
            "encounters": [
              { "canonicalName": "futures-rewritten-ultimate", "progression": { "displayPercent": "12% P1" } }
            ]
          }
        }
        """);

        var graphUrl = TomestoneProgressParser.ExtractProgressionGraphUrl(activityContent);
        Assert.Equal("/character/12345/alpha-omega/activity/futures-rewritten-ultimate/graph", graphUrl);

        var graphPayload = JObject.Parse("""
        {
          "data": {
            "graph": [
              { "phase": { "name": "P1", "number": 1 }, "bestPull": "12%" },
              { "phase": { "name": "P3", "number": 3 }, "bestPull": "44.2%" }
            ]
          }
        }
        """);

        var parsed = TomestoneProgressParser.ParseGraphProgress(graphPayload);
        Assert.Equal(2, parsed.Phases.Count);
        Assert.Equal("p3", parsed.Phases[1].PhaseKey);
        Assert.Equal(44.2, parsed.Phases[1].BossPercentage);
    }

    [Fact]
    public async Task Adapter_preserves_root_activity_in_profile_cache_and_serves_savage_siblings_without_extra_http()
    {
        var targets = TomestoneEncounterMapping.GetProgressionTargetsForZone(73);
        Assert.True(targets.Count >= 2);
        var firstTarget = targets.Single(static t => t.EncounterId == 101);
        var secondTarget = targets.Single(static t => t.EncounterId == 102);

        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri.Contains("/profile/", StringComparison.Ordinal))
                {
                    // Profile contains bloat ("gear"/"cosmetics") trimmed by Adapter, "encounters" for firstTarget,
                    // AND root "activity" ONLY for secondTarget (proves Adapter preserves root "activity" on trim!)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "gear": { "head": "bloated" },
                          "cosmetics": [1, 2, 3],
                          "encounters": {
                            "savage": [
                              {
                                "name": "Vamp Fatale",
                                "canonicalName": "vamp-fatale",
                                "progression": { "displayPercent": "45.0%" }
                              }
                            ]
                          },
                          "activity": {
                            "activities": {
                              "activities": {
                                "paginator": {
                                  "data": [
                                    {
                                      "activity": {
                                        "bestPercent": "18.5%",
                                        "killsCount": 0,
                                        "encounter": { "canonical_name": "red-hot-deep-blue" }
                                      }
                                    }
                                  ]
                                }
                              }
                            }
                          }
                        }
                        """),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            TomestoneApiKey = "secret-token",
        };
        using var client = new TomestoneApiClient(config, handler);
        var adapter = new FFLogsCollector.TomestoneApiClientAdapter(client, config, _ => { }, _ => { });

        var firstOutcome = await adapter.FetchProgressionDataAsync(
            "Savage Raider",
            "Tonberry",
            73,
            firstTarget.EncounterId,
            CancellationToken.None);

        Assert.True(firstOutcome.Succeeded);
        Assert.Equal(45.0, firstOutcome.Value!.BossPercentage);
        Assert.Single(handler.Requests);

        var secondOutcome = await adapter.FetchProgressionDataAsync(
            "Savage Raider",
            "Tonberry",
            73,
            secondTarget.EncounterId,
            CancellationToken.None);

        Assert.True(secondOutcome.Succeeded);
        Assert.Equal(18.5, secondOutcome.Value!.BossPercentage);
        Assert.Single(handler.Requests);

        var cachedSiblings = adapter.TryGetCachedZoneSiblingProgress(
            "Savage Raider",
            "Tonberry",
            73,
            [firstTarget.EncounterId]);
        Assert.Contains(secondTarget.EncounterId, cachedSiblings.Keys);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Adapter_follows_same_origin_progressionGraphUrl_and_rejects_cross_origin()
    {
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri.Contains("/profile/", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "progressionGraphUrl": "/character/777/alpha/activity/fru/graph",
                          "encounters": {
                            "ultimate": [
                              {
                                "canonicalName": "futures-rewritten-ultimate",
                                "progression": { "displayPercent": "10%" }
                              }
                            ]
                          }
                        }
                        """),
                    });
                }

                if (uri.Equals("https://tomestone.gg/character/777/alpha/activity/fru/graph", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "data": {
                            "graph": [
                              { "phase": { "name": "P2", "number": 2 }, "bestPull": "42%" }
                            ]
                          }
                        }
                        """),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            TomestoneApiKey = "secret-token",
        };
        using var client = new TomestoneApiClient(config, handler);
        var adapter = new FFLogsCollector.TomestoneApiClientAdapter(client, config, _ => { }, _ => { });

        var sameOriginOutcome = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.True(sameOriginOutcome.Succeeded);
        var phase = Assert.Single(sameOriginOutcome.Value!.Phases);
        Assert.Equal("p2", phase.PhaseKey);
        Assert.Equal(42, phase.BossPercentage);
        Assert.Contains(handler.Requests, static r => r.RequestUri!.AbsoluteUri == "https://tomestone.gg/character/777/alpha/activity/fru/graph");

        var warnings = new List<string>();
        var evilHandler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/profile/", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "progressionGraphUrl": "https://evil.example/steal-bearer",
                          "encounters": {
                            "ultimate": [
                              {
                                "canonicalName": "futures-rewritten-ultimate",
                                "progression": { "displayPercent": "10%" }
                              }
                            ]
                          }
                        }
                        """),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            },
        };
        using var evilClient = new TomestoneApiClient(config, evilHandler);
        var evilAdapter = new FFLogsCollector.TomestoneApiClientAdapter(evilClient, config, _ => { }, warnings.Add);

        var crossOriginOutcome = await evilAdapter.FetchProgressionDataAsync(
            "Beta",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.True(crossOriginOutcome.Succeeded);
        Assert.DoesNotContain(evilHandler.Requests, static r => r.RequestUri!.Host.Contains("evil.example", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, static w => w.Contains("cross-origin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WebFallback_follows_same_origin_progressionGraphUrl_and_rejects_cross_origin()
    {
        var requestedUris = new List<string>();
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                requestedUris.Add(uri);

                if (uri.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "characters": [
                            { "href": "/character/111/wrong", "item": { "name": "Graph Runner", "serverName": "Aegis" } },
                            { "href": "/character/222/graph-runner", "item": { "name": "Graph Runner", "serverName": "Tonberry" } }
                          ]
                        }
                        """),
                    });
                }

                if (uri.EndsWith("/character/222/graph-runner", StringComparison.Ordinal))
                {
                    var page = WebUtility.HtmlEncode("""
                    {
                      "component": "Character/Overview",
                      "version": "v1",
                      "props": {
                        "characterPageContent": {
                          "progressionGraphUrl": "/character/222/graph-runner/activity/fru/graph",
                          "encounters": {
                            "ultimate": [
                              {
                                "canonicalName": "futures-rewritten-ultimate",
                                "progression": { "displayPercent": "10%" }
                              }
                            ]
                          }
                        }
                      }
                    }
                    """);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"<html><body><div id=\"app\" data-page=\"{page}\"></div></body></html>"),
                    });
                }

                if (uri.EndsWith("/character/222/graph-runner/activity/fru/graph", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "data": {
                            "graph": [
                              { "phase": { "name": "P1", "number": 1 }, "bestPull": "10%" },
                              { "phase": { "name": "P4", "number": 4 }, "bestPull": "88.0%" }
                            ]
                          }
                        }
                        """),
                    });
                }

                if (uri.Contains("/clears", StringComparison.Ordinal) || uri.Contains("/activity", StringComparison.Ordinal))
                {
                    var page = WebUtility.HtmlEncode("""{"component":"Character/Activity","version":"v1","props":{"characterPageContent":{}}}""");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"<html><body><div id=\"app\" data-page=\"{page}\"></div></body></html>"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            TomestoneRequestIntervalMs = 0,
        };
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(65, 1079, out var fruTarget));
        var outcome = await webFallback.FetchProgressionAsync(
            "Graph Runner",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Contains(requestedUris, static uri => uri.EndsWith("/character/222/graph-runner/activity/fru/graph", StringComparison.Ordinal));
        Assert.Contains(outcome.Value!.Phases, static p => p.PhaseKey == "p4" && Math.Abs(p.BossPercentage!.Value - 88.0) < 0.01);

        // Cross-origin graph URL MUST NOT be requested!
        var crossOriginHandler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"characters":[{"href":"/character/333/cross","item":{"name":"Cross Origin","serverName":"Tonberry"}}]}"""),
                    });
                }

                if (uri.EndsWith("/character/333/cross", StringComparison.Ordinal))
                {
                    var page = WebUtility.HtmlEncode("""
                    {
                      "component": "Character/Overview",
                      "version": "v1",
                      "props": {
                        "characterPageContent": {
                          "progressionGraphUrl": "https://evil.example/steal-session",
                          "encounters": {
                            "ultimate": [
                              {
                                "canonicalName": "futures-rewritten-ultimate",
                                "progression": { "displayPercent": "15%" }
                              }
                            ]
                          }
                        }
                      }
                    }
                    """);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"<html><body><div id=\"app\" data-page=\"{page}\"></div></body></html>"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            },
        };
        using var crossOriginFallback = new TomestoneWebFallbackClient(
            config,
            crossOriginHandler,
            delayAsync: static (_, _) => Task.CompletedTask);

        var crossOriginOutcome = await crossOriginFallback.FetchProgressionAsync(
            "Cross Origin",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.True(crossOriginOutcome.Succeeded);
        Assert.DoesNotContain(crossOriginHandler.Requests, static r => r.RequestUri!.Host.Contains("evil.example", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WebFallback_handles_inertia_409_single_retry_and_fetches_clears_and_progression()
    {
        var inertiaAttempts = 0;
        var sentVersions = new List<string>();
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"characters":[{"href":"/character/555/inertia-raider","item":{"name":"Inertia Raider","serverName":"Tonberry"}}]}"""),
                    });
                }

                if (uri.EndsWith("/character/555/inertia-raider", StringComparison.Ordinal))
                {
                    var page = WebUtility.HtmlEncode("""
                    {
                      "component": "Character/Overview",
                      "version": "v1",
                      "props": {
                        "characterPageContent": {
                          "encounters": {
                            "ultimate": [
                              {
                                "canonicalName": "futures-rewritten-ultimate",
                                "progression": { "mechanic": { "number": 7 }, "displayPercent": "15% P1" }
                              }
                            ]
                          }
                        }
                      }
                    }
                    """);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"<html><body><div id=\"app\" data-page=\"{page}\"></div></body></html>"),
                    });
                }

                if (uri.Contains("/clears", StringComparison.Ordinal))
                {
                    inertiaAttempts++;
                    if (request.Headers.TryGetValues("X-Inertia-Version", out var versionVals))
                    {
                        sentVersions.Add(string.Join(",", versionVals));
                    }

                    if (inertiaAttempts == 1)
                    {
                        var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                        conflict.Headers.TryAddWithoutValidation("X-Inertia-Version", "v2");
                        return Task.FromResult(conflict);
                    }

                    var pageJson = """
                    {
                      "component": "Character/Clears",
                      "version": "v2",
                      "props": {
                        "characterPageContent": {
                          "encounters": {
                            "rankings": [
                              {
                                "encounters": [
                                  { "encounterId": 2061, "rankPercent": { "percent": 93.4 }, "totalClears": 8 }
                                ]
                              }
                            ]
                          }
                        }
                      }
                    }
                    """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(pageJson, Encoding.UTF8, "application/json"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            TomestoneRequestIntervalMs = 0,
        };
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);

        // Warm up cached Inertia version "v1" from the profile page bootstrap HTML
        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(65, 1079, out var fruTarget));
        var warmup = await webFallback.FetchProgressionAsync("Inertia Raider", "Tonberry", 65, fruTarget, CancellationToken.None);
        Assert.True(warmup.Succeeded);

        var job = new ParseJob
        {
            ContentId = 9001,
            Name = "Inertia Raider",
            Server = "Tonberry",
            Region = "JP",
            ZoneId = 66,
            DifficultyId = 100,
            EncounterId = 2061,
            LeaseToken = "lease-9001",
        };

        var result = await webFallback.FetchFallbackParseResultAsync(job, "Tonberry", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(2, inertiaAttempts);
        Assert.Equal(["v1", "v2"], sentVersions);
        Assert.Equal(93.4, result.Value!.Encounters[2061], 1);
        Assert.Equal(8, result.Value.ClearCounts[2061]);
    }

    [Theory]
    [InlineData(403, "", "<html><body>Forbidden</body></html>")]
    [InlineData(429, "", "Too Many Requests")]
    [InlineData(200, "challenge", "<html><body>checking</body></html>")]
    [InlineData(200, "", "<html><title>Just a moment...</title></html>")]
    [InlineData(200, "", "<html><div class=\"cf-turnstile\"></div></html>")]
    [InlineData(200, "", "<html><div class=\"g-recaptcha\"></div></html>")]
    [InlineData(200, "", "<html><div class=\"h-captcha\"></div></html>")]
    public async Task WebFallback_aborts_immediately_and_enters_cooldown_on_antibot_or_rate_limit_without_invoking_cdp(
        int statusCode,
        string cfMitigatedHeader,
        string body)
    {
        var nowUtc = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc);
        var cdpInvocations = 0;
        var stubCdp = new StubCdpHydrator
        {
            OnHydrateAsync = (_, _, _) =>
            {
                cdpInvocations++;
                return Task.FromResult("{}");
            },
        };
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/html"),
                };
                if (!string.IsNullOrEmpty(cfMitigatedHeader))
                {
                    response.Headers.TryAddWithoutValidation("cf-mitigated", cfMitigatedHeader);
                }

                return Task.FromResult(response);
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            TomestoneObscuraCdpUrl = "ws://127.0.0.1:9222/devtools/browser",
            TomestoneRequestIntervalMs = 0,
        };
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            cdpHydrator: stubCdp,
            utcNow: () => nowUtc,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(65, 1079, out var fruTarget));
        var firstOutcome = await webFallback.FetchProgressionAsync(
            "Challenge Victim",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.False(firstOutcome.Succeeded);
        Assert.Equal(0, cdpInvocations);
        Assert.Single(handler.Requests);
        Assert.True(webFallback.TryGetCooldownRemaining(out var remaining));
        Assert.True(remaining > TimeSpan.Zero);

        var secondOutcome = await webFallback.FetchProgressionAsync(
            "Challenge Victim",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.False(secondOutcome.Succeeded);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(403, "cf-mitigated", "<html><body>Turnstile</body></html>")]
    [InlineData(200, "", "<html><title>Just a moment...</title><div class=\"cf-turnstile\"></div></html>")]
    [InlineData(200, "", "<html><script src=\"https://www.google.com/recaptcha/api.js\"></script><div class=\"g-recaptcha\"></div></html>")]
    [InlineData(200, "", "<html><div class=\"hcaptcha\"></div></html>")]
    public async Task Official_api_client_aborts_and_enters_cooldown_on_challenge_indicators(
        int statusCode,
        string headerName,
        string body)
    {
        var nowUtc = new DateTime(2026, 5, 12, 1, 0, 0, DateTimeKind.Utc);
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/html"),
                };
                if (!string.IsNullOrEmpty(headerName))
                {
                    response.Headers.TryAddWithoutValidation(headerName, "challenge");
                }

                return Task.FromResult(response);
            },
        };

        using var client = new TomestoneApiClient(
            new Configuration { TomestoneApiKey = "secret-token" },
            handler,
            () => nowUtc,
            static _ => { },
            static _ => { },
            static _ => { });

        var firstResponse = await client.GetRawAsync("/characters/Tonberry/Alpha", CancellationToken.None);

        Assert.Null(firstResponse);
        Assert.True(client.TryGetRateLimitRemaining(out _));
        Assert.Single(handler.Requests);

        var secondResponse = await client.GetRawAsync("/characters/Tonberry/Alpha", CancellationToken.None);

        Assert.Null(secondResponse);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ObscuraCdpClient_hydrates_unhydrated_spa_shell_via_tier3_and_always_closes_target()
    {
        var hydratedPageJson = """
        {
          "component": "Character/Overview",
          "version": "v1",
          "props": {
            "characterPageContent": {
              "encounters": {
                "ultimate": [
                  {
                    "canonicalName": "futures-rewritten-ultimate",
                    "progression": { "mechanic": { "number": 10 }, "displayPercent": "66.7% P4" }
                  }
                ]
              }
            }
          }
        }
        """;
        var probeResult = JsonConvert.SerializeObject(new
        {
            challenge = string.Empty,
            dataPage = hydratedPageJson,
        });

        await using var cdpServer = await LoopbackCdpWebSocketServer.StartAsync(probeResult);
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"characters":[{"href":"/character/888/spa-shell","item":{"name":"Spa Shell","serverName":"Tonberry"}}]}"""),
                    });
                }

                // Unhydrated SPA shell without data-page triggers Tier 3 CDP hydration
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><div id=\"app\"></div><script src=\"/assets/app.js\"></script></body></html>"),
                });
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            TomestoneObscuraCdpUrl = cdpServer.EndpointUrl,
            TomestoneRequestIntervalMs = 0,
        };
        var cdpClient = new ObscuraCdpClient(
            navigationTimeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(10));
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            cdpHydrator: cdpClient,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(65, 1079, out var fruTarget));
        var outcome = await webFallback.FetchProgressionAsync(
            "Spa Shell",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var phase = Assert.Single(outcome.Value!.Phases);
        Assert.Equal("p4", phase.PhaseKey);
        Assert.Equal(66.7, phase.BossPercentage);
        Assert.Contains("Target.createTarget", cdpServer.Methods);
        Assert.Contains("Page.navigate", cdpServer.Methods);
        Assert.Contains("Runtime.evaluate", cdpServer.Methods);
        Assert.Contains("Target.closeTarget", cdpServer.Methods);
    }

    [Fact]
    public async Task ObscuraCdpClient_aborts_and_enters_cooldown_when_dom_shows_challenge_and_still_closes_target()
    {
        var probeChallenge = JsonConvert.SerializeObject(new
        {
            challenge = "cf-turnstile",
            dataPage = string.Empty,
        });
        await using var cdpServer = await LoopbackCdpWebSocketServer.StartAsync(probeChallenge);
        var nowUtc = new DateTime(2026, 5, 12, 2, 0, 0, DateTimeKind.Utc);
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"characters":[{"href":"/character/999/blocked-spa","item":{"name":"Blocked Spa","serverName":"Tonberry"}}]}"""),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><div id=\"app\"></div></body></html>"),
                });
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            TomestoneObscuraCdpUrl = cdpServer.EndpointUrl,
            TomestoneRequestIntervalMs = 0,
        };
        var cdpClient = new ObscuraCdpClient(
            navigationTimeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(10));
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            cdpHydrator: cdpClient,
            utcNow: () => nowUtc,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(65, 1079, out var fruTarget));
        var outcome = await webFallback.FetchProgressionAsync(
            "Blocked Spa",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(webFallback.TryGetCooldownRemaining(out var remaining));
        Assert.True(remaining > TimeSpan.Zero);
        Assert.Contains("Target.closeTarget", cdpServer.Methods);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:9222/devtools/browser", true)]
    [InlineData("wss://localhost:9222/devtools/browser", true)]
    [InlineData("http://127.0.0.1:9222/devtools/browser", false)]
    [InlineData("file:///c:/windows", false)]
    [InlineData("", false)]
    public void ObscuraCdpClient_TryValidateEndpointUri_validates_websocket_schemes(string endpointUrl, bool expected)
    {
        Assert.Equal(expected, ObscuraCdpClient.TryValidateEndpointUri(endpointUrl, out _));
    }

    [Fact]
    public async Task BatchProcessor_uses_web_fallback_when_fflogs_has_no_parses_and_tomestone_api_key_is_blank()
    {
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"characters":[{"href":"/character/444/fallback-hero","item":{"name":"Fallback Hero","serverName":"Tonberry"}}]}"""),
                    });
                }

                if (uri.EndsWith("/character/444/fallback-hero", StringComparison.Ordinal))
                {
                    var page = WebUtility.HtmlEncode("""{"component":"Character/Overview","version":"v1","props":{"characterPageContent":{}}}""");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"<html><body><div id=\"app\" data-page=\"{page}\"></div></body></html>"),
                    });
                }

                if (uri.Contains("/clears", StringComparison.Ordinal))
                {
                    var page = WebUtility.HtmlEncode("""
                    {
                      "component": "Character/Clears",
                      "version": "v1",
                      "props": {
                        "characterPageContent": {
                          "encounters": {
                            "rankings": [
                              {
                                "encounters": [
                                  { "encounterId": 2061, "rankPercent": { "percent": 81.2 }, "totalClears": 3 }
                                ]
                              }
                            ]
                          }
                        }
                      }
                    }
                    """);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"<html><body><div id=\"app\" data-page=\"{page}\"></div></body></html>"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            TomestoneApiKey = string.Empty,
            TomestoneRequestIntervalMs = 0,
        };
        using var apiClient = new TomestoneApiClient(config, new RecordingHttpMessageHandler());
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            delayAsync: static (_, _) => Task.CompletedTask);
        var adapter = new FFLogsCollector.TomestoneApiClientAdapter(
            apiClient,
            config,
            _ => { },
            _ => { },
            webFallbackClient: webFallback);

        var fflogsApiClient = new StubFFLogsApiClient
        {
            OnFetchCharacterCandidateDataBatchAsync = static (queries, _, _, _) =>
                Task.FromResult(queries.ToDictionary(
                    static q => q.Key,
                    static _ => new FFLogsClient.CharacterFetchedData
                    {
                        Exists = true,
                        Hidden = false,
                        Parses = [],
                    })),
        };

        var seams = FFLogsCollector.CreateSeams(
            new StubFFLogsIngestHttpSender(),
            fflogsApiClient,
            new ManualFFLogsTimeProvider(),
            adapter,
            config,
            static (_, _) => Task.CompletedTask);
        var processor = new FFLogsBatchProcessor(seams);
        var session = new FFLogsLeaseSession(
            new UploadUrl("https://upload.example/"),
            [
                new ParseJob
                {
                    ContentId = 8001,
                    Name = "Fallback Hero",
                    Server = "Tonberry",
                    Region = "JP",
                    ZoneId = 66,
                    DifficultyId = 100,
                    EncounterId = 2061,
                    LeaseToken = "lease-8001",
                },
            ]);

        var batchResult = await processor.ProcessLeaseSessionAsync(session, CancellationToken.None);

        var parse = Assert.Single(batchResult.ProcessedResults);
        Assert.Equal(81.2, parse.Encounters[2061], 1);
        Assert.Equal(3, parse.ClearCounts[2061]);
        Assert.Contains(handler.Requests, static r => r.RequestUri!.AbsoluteUri.Contains("/clears", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreferTomestoneObscura_routes_json_and_pages_directly_through_cdp_without_direct_http()
    {
        var hydrateCalls = 0;
        var jsonCalls = 0;
        var profileJson = """
        {
          "component": "Character/Overview",
          "version": "cdp-v1",
          "props": {
            "characterPageContent": {
              "encounters": {
                "ultimate": [
                  {
                    "canonicalName": "futures-rewritten-ultimate",
                    "progression": { "mechanic": { "number": 10 }, "displayPercent": "44.5% P4" }
                  }
                ]
              }
            }
          }
        }
        """;
        var stubCdp = new StubCdpHydrator
        {
            OnHydrateAsync = (_, _, _) =>
            {
                hydrateCalls++;
                return Task.FromResult(profileJson);
            },
            OnFetchJsonAsync = (_, targetUrl, _) =>
            {
                jsonCalls++;
                Assert.Contains("/search/autocomplete", targetUrl, StringComparison.Ordinal);
                return Task.FromResult("""{"characters":[{"href":"/character/777/cdp-first","item":{"name":"Cdp First","serverName":"Tonberry"}}]}""");
            },
        };
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (_, _) => throw new InvalidOperationException("Direct HTTP should not be called when PreferTomestoneObscura is enabled."),
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            PreferTomestoneObscura = true,
            TomestoneObscuraCdpUrl = "ws://127.0.0.1:9222/devtools/browser",
            TomestoneRequestIntervalMs = 0,
        };
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            cdpHydrator: stubCdp,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(65, 1079, out var fruTarget));
        var outcome = await webFallback.FetchProgressionAsync(
            "Cdp First",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var phase = Assert.Single(outcome.Value!.Phases);
        Assert.Equal("p4", phase.PhaseKey);
        Assert.Equal(44.5, phase.BossPercentage);
        Assert.Equal(1, jsonCalls);
        Assert.Equal(1, hydrateCalls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Auto_mode_escalates_non_json_http_and_non_inertia_html_to_obscura_and_extracts_pre_json()
    {
        var graphProbeResult = JsonConvert.SerializeObject(new
        {
            challenge = string.Empty,
            dataPage = string.Empty,
            preText = """[{"phase":{"name":"P2","order":2},"bestPull":"18.25%"}]""",
            rawBodyText = string.Empty,
        });
        await using var cdpServer = await LoopbackCdpWebSocketServer.StartAsync(graphProbeResult);

        var cdpClient = new ObscuraCdpClient(
            navigationTimeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(10));
        var fetchedGraph = await cdpClient.FetchJsonUrlAsync(
            cdpServer.EndpointUrl,
            "https://tomestone.gg/character/progress-graph/1/2",
            CancellationToken.None);
        Assert.Contains("18.25%", fetchedGraph, StringComparison.Ordinal);

        Assert.Equal(
            "ws://obscura:19222/devtools/browser/abc",
            ObscuraCdpClient.RewriteWebSocketDebuggerUrl(
                new Uri("ws://obscura:19222/"),
                "ws://0.0.0.0:9222/devtools/browser/abc")?.ToString());

        var jsonCalls = 0;
        var hydrateCalls = 0;
        var handler = new RecordingHttpMessageHandler
        {
            OnSendAsync = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><body>loading...</body></html>"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<main>fragment without version or #app</main>"),
                });
            },
        };
        var stubCdp = new StubCdpHydrator
        {
            OnFetchJsonAsync = (_, targetUrl, _) =>
            {
                jsonCalls++;
                if (targetUrl.Contains("/search/autocomplete", StringComparison.Ordinal))
                {
                    return Task.FromResult("""{"characters":[{"href":"/character/555/escalated","item":{"name":"Escalated Hero","serverName":"Tonberry"}}]}""");
                }
                return Task.FromResult(fetchedGraph);
            },
            OnHydrateAsync = (_, _, _) =>
            {
                hydrateCalls++;
                return Task.FromResult("""{"component":"Character/Overview","version":"v2","props":{"characterPageContent":{"activity":{"progressionGraphUrl":"/character/progress-graph/555/9"}}}}""");
            },
        };

        var config = new Configuration
        {
            EnableTomestoneProgressEnrichment = true,
            EnableTomestoneWebFallback = true,
            TomestoneObscuraCdpUrl = "ws://127.0.0.1:9222/devtools/browser",
            TomestoneRequestIntervalMs = 0,
        };
        using var webFallback = new TomestoneWebFallbackClient(
            config,
            handler,
            cdpHydrator: stubCdp,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(65, 1079, out var fruTarget));
        var outcome = await webFallback.FetchProgressionAsync(
            "Escalated Hero",
            "Tonberry",
            65,
            fruTarget,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var phase = Assert.Single(outcome.Value!.Phases);
        Assert.Equal("p2", phase.PhaseKey);
        Assert.Equal(18.25, phase.BossPercentage);
        Assert.Equal(2, jsonCalls);
        Assert.Equal(1, hydrateCalls);
    }

    [Fact]
    public async Task CdpClient_sends_bearer_token_on_discovery_and_websocket_upgrade()
    {
        const string token = "test-csharp-cdp-token-0123456789abcdef";
        var graphProbeResult = JsonConvert.SerializeObject(new
        {
            challenge = string.Empty,
            dataPage = string.Empty,
            preText = """[{"phase":{"name":"P1","order":1},"bestPull":"14.0%"}]""",
            rawBodyText = string.Empty,
        });
        await using var cdpServer = await LoopbackCdpWebSocketServer.StartAsync(
            graphProbeResult,
            expectedBearerToken: token);

        var baseEndpointUrl = new UriBuilder(cdpServer.EndpointUrl) { Path = "/" }.Uri.ToString();
        var authedClient = new ObscuraCdpClient(
            navigationTimeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(10),
            bearerTokenProvider: () => token);
        var payload = await authedClient.FetchJsonUrlAsync(
            baseEndpointUrl,
            "https://tomestone.gg/character/progress-graph/1/2",
            CancellationToken.None);
        Assert.Contains("14.0%", payload, StringComparison.Ordinal);

        var unauthedClient = new ObscuraCdpClient(
            navigationTimeout: TimeSpan.FromSeconds(3),
            pollInterval: TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => unauthedClient.FetchJsonUrlAsync(
            cdpServer.EndpointUrl,
            "https://tomestone.gg/character/progress-graph/1/2",
            CancellationToken.None));
    }

    private sealed class StubCdpHydrator : ITomestoneCdpHydrator
    {
        public Func<string, string, CancellationToken, Task<string>> OnHydrateAsync { get; set; }
            = static (_, _, _) => Task.FromResult("{}");

        public Func<string, string, CancellationToken, Task<string>> OnFetchJsonAsync { get; set; }
            = static (_, _, _) => Task.FromResult("{}");

        public Task<string> HydrateUrlAsync(string endpointUrl, string targetUrl, CancellationToken cancellationToken)
            => OnHydrateAsync(endpointUrl, targetUrl, cancellationToken);

        public Task<string> FetchJsonUrlAsync(string endpointUrl, string targetUrl, CancellationToken cancellationToken)
            => OnFetchJsonAsync(endpointUrl, targetUrl, cancellationToken);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSendAsync { get; set; }
            = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            return OnSendAsync(request, cancellationToken);
        }
    }

    private sealed class LoopbackCdpWebSocketServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _evalProbeJson;
        private readonly string? _expectedBearerToken;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public string EndpointUrl { get; }
        public ConcurrentQueue<string> Methods { get; } = new();

        private LoopbackCdpWebSocketServer(
            TcpListener listener,
            int port,
            string evalProbeJson,
            string? expectedBearerToken)
        {
            _listener = listener;
            _evalProbeJson = evalProbeJson;
            _expectedBearerToken = expectedBearerToken;
            EndpointUrl = $"ws://127.0.0.1:{port}/devtools/browser";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static Task<LoopbackCdpWebSocketServer> StartAsync(
            string evalProbeJson,
            string? expectedBearerToken = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult(new LoopbackCdpWebSocketServer(
                listener,
                port,
                evalProbeJson,
                expectedBearerToken));
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient)
        {
            using (tcpClient)
            await using (var stream = tcpClient.GetStream())
            {
                var buffer = new byte[8192];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), _cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    total += read;
                    var requestText = Encoding.ASCII.GetString(buffer, 0, total);
                    var headerEnd = requestText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        if (!string.IsNullOrEmpty(_expectedBearerToken))
                        {
                            var expectedHeader = $"Authorization: Bearer {_expectedBearerToken}";
                            var hasBearer = requestText
                                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                                .Any(line => string.Equals(line.Trim(), expectedHeader, StringComparison.Ordinal));
                            if (!hasBearer)
                            {
                                var unauthorized = Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(unauthorized, _cts.Token).ConfigureAwait(false);
                                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                                return;
                            }
                        }

                        var requestLine = requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                        if (requestLine.StartsWith("GET /json/version ", StringComparison.Ordinal))
                        {
                            var jsonBody = JsonConvert.SerializeObject(new
                            {
                                webSocketDebuggerUrl = $"{EndpointUrl}/discovered",
                            });
                            var httpResp =
                                "HTTP/1.1 200 OK\r\n" +
                                "Content-Type: application/json\r\n" +
                                $"Content-Length: {Encoding.UTF8.GetByteCount(jsonBody)}\r\n" +
                                "Connection: close\r\n\r\n" +
                                jsonBody;
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(httpResp), _cts.Token).ConfigureAwait(false);
                            await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                            return;
                        }

                        var key = ExtractWebSocketKey(requestText);
                        var accept = Convert.ToBase64String(
                            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        var handshake =
                            "HTTP/1.1 101 Switching Protocols\r\n" +
                            "Connection: Upgrade\r\n" +
                            "Upgrade: websocket\r\n" +
                            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), _cts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                        break;
                    }
                }

                using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
                var rxBuffer = new byte[16384];
                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    using var messageStream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(rxBuffer, _cts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _cts.Token).ConfigureAwait(false);
                            return;
                        }

                        messageStream.Write(rxBuffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var msgJson = JObject.Parse(Encoding.UTF8.GetString(messageStream.ToArray()));
                    var id = msgJson["id"]?.Value<int>() ?? 0;
                    var method = msgJson["method"]?.Value<string>() ?? string.Empty;
                    Methods.Enqueue(method);

                    JObject responsePayload = method switch
                    {
                        "Target.createTarget" => new JObject
                        {
                            ["id"] = id,
                            ["result"] = new JObject { ["targetId"] = "target-1" },
                        },
                        "Target.attachToTarget" => new JObject
                        {
                            ["id"] = id,
                            ["result"] = new JObject { ["sessionId"] = "session-1" },
                        },
                        "Runtime.evaluate" => new JObject
                        {
                            ["id"] = id,
                            ["result"] = new JObject
                            {
                                ["result"] = new JObject
                                {
                                    ["type"] = "string",
                                    ["value"] = _evalProbeJson,
                                },
                            },
                        },
                        _ => new JObject
                        {
                            ["id"] = id,
                            ["result"] = new JObject(),
                        },
                    };

                    var responseBytes = Encoding.UTF8.GetBytes(responsePayload.ToString(Formatting.None));
                    await ws.SendAsync(responseBytes, WebSocketMessageType.Text, endOfMessage: true, _cts.Token).ConfigureAwait(false);
                }
            }
        }

        private static string ExtractWebSocketKey(string requestText)
        {
            foreach (var line in requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                const string prefix = "Sec-WebSocket-Key:";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }

            throw new InvalidOperationException("Missing Sec-WebSocket-Key header.");
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Ignore listener shutdown race.
            }

            _cts.Dispose();
        }
    }
}
