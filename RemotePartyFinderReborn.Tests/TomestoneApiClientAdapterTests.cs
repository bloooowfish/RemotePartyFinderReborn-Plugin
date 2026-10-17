using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneApiClientAdapterTests
{
    static TomestoneApiClientAdapterTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public async Task Disabled_or_blank_key_returns_unconfigured()
    {
        var disabledHandler = new RecordingTomestoneHttpMessageHandler();
        using var disabledClient = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = false,
                TomestoneApiKey = "secret-token",
            },
            disabledHandler);
        var disabledAdapter = CreateAdapter(
            disabledClient,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = false,
                TomestoneApiKey = "secret-token",
            });

        var disabledResult = await disabledAdapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.False(disabledAdapter.IsConfigured);
        Assert.True(disabledResult.Succeeded);
        Assert.Empty(disabledResult.Value!.Phases);
        Assert.Null(disabledResult.Value!.BossPercentage);
        Assert.Empty(disabledHandler.Requests);

        var blankKeyHandler = new RecordingTomestoneHttpMessageHandler();
        using var blankKeyClient = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = " ",
            },
            blankKeyHandler);
        var blankKeyAdapter = CreateAdapter(
            blankKeyClient,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = " ",
            });

        var blankKeyResult = await blankKeyAdapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.False(blankKeyAdapter.IsConfigured);
        Assert.True(blankKeyResult.Succeeded);
        Assert.Empty(blankKeyResult.Value!.Phases);
        Assert.Null(blankKeyResult.Value!.BossPercentage);
        Assert.Empty(blankKeyHandler.Requests);
    }

    [Fact]
    public async Task FetchProgressionDataAsync_builds_encoded_activity_endpoint()
    {
        var handler = new RecordingTomestoneHttpMessageHandler();
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        await adapter.FetchProgressionDataAsync(
            "Name With/Slash",
            "Aether & Co",
            65,
            1079,
            CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            profileRequest => Assert.Equal(
                "/api/character/profile/Aether%20%26%20Co/Name%20With%2FSlash",
                profileRequest.RequestUri?.AbsolutePath),
            activityRequest =>
            {
                Assert.Equal("/api/character/activity/Aether%20%26%20Co/Name%20With%2FSlash", activityRequest.RequestUri?.AbsolutePath);
                Assert.Equal("?expansion=dawntrail&zone=ultimates&encounter=futures-rewritten-ultimate", activityRequest.RequestUri?.Query);
            });
    }

    [Fact]
    public async Task FetchProgressionDataAsync_builds_dancing_mad_activity_endpoint()
    {
        var handler = new RecordingTomestoneHttpMessageHandler();
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            76,
            1085,
            CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            profileRequest => Assert.Equal("/api/character/profile/Tonberry/Alpha", profileRequest.RequestUri?.AbsolutePath),
            activityRequest => Assert.Equal(
                "?expansion=dawntrail&zone=ultimates&encounter=dancing-mad-ultimate",
                activityRequest.RequestUri?.Query));
    }

    [Fact]
    public async Task FetchProgressionDataAsync_builds_current_savage_activity_endpoint()
    {
        var handler = new RecordingTomestoneHttpMessageHandler();
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            73,
            101,
            CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            profileRequest => Assert.Equal("/api/character/profile/Tonberry/Alpha", profileRequest.RequestUri?.AbsolutePath),
            activityRequest => Assert.Equal(
                "?expansion=dawntrail&zone=aac-heavyweight-savage&encounter=vamp-fatale",
                activityRequest.RequestUri?.Query));
    }

    [Fact]
    public async Task FetchProgressionDataAsync_logs_mapped_tomestone_target_when_not_found()
    {
        var debugLogs = new List<string>();
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found"),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            debugLogs.Add);

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            73,
            105,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var log = Assert.Single(debugLogs, entry => entry.Contains("Tomestone profile not found"));
        Assert.Contains("fflogs zone=73, encounter=105", log);
        Assert.Contains("tomestone expansion=dawntrail, zone=aac-heavyweight-savage, encounter=lindwurm-ii", log);
        Assert.Contains("kind=BossPercentage", log);
    }

    [Fact]
    public async Task Reads_flat_profile_progress_without_fallback_and_uses_display_phase()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/profile/Tonberry/Alpha", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"encounters":{"ultimate":[{"zoneName":"Futures Rewritten (Ultimate)","progression":{"mechanic":{"name":"Wyrmsbreath","number":7},"displayPercent":"41.13% P4"}}]}}
                        """),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}"),
                    }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(handler.Requests);
        var phase = Assert.Single(result.Value!.Phases);
        Assert.Equal("P4 41.13%", phase.DisplayText);
    }

    [Fact]
    public async Task Uses_official_client_and_parser_for_phase_progress()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"data":{"encounters":[
                  {"canonicalName":"futures-rewritten-ultimate","progression":{"mechanic":{"number":7},"displayPercent":"12% P1"}},
                  {"canonicalName":"futures-rewritten-ultimate","progression":{"mechanic":{"number":8},"displayPercent":"24% P2"}},
                  {"canonicalName":"futures-rewritten-ultimate","progression":{"mechanic":{"number":9},"displayPercent":"55.5% P3"}},
                  {"canonicalName":"futures-rewritten-ultimate","progression":{"mechanic":{"number":10},"displayPercent":"100% P4"}}
                ]}}
                """),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Collection(
            result.Value!.Phases,
            phase =>
            {
                Assert.Equal("p1", phase.PhaseKey);
                Assert.Equal(12, phase.BossPercentage);
            },
            phase =>
            {
                Assert.Equal("p2", phase.PhaseKey);
                Assert.Equal(24, phase.BossPercentage);
            },
            phase =>
            {
                Assert.Equal("p3", phase.PhaseKey);
                Assert.Equal(55.5, phase.BossPercentage);
            },
            phase =>
            {
                Assert.Equal("p4", phase.PhaseKey);
                Assert.Equal(100, phase.BossPercentage);
            });
    }

    [Fact]
    public async Task Missing_profile_progress_falls_back_to_scoped_activity_percent()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal)
                    ? "{}"
                    : """
                      {"data":{"paginator":{"data":[
                        {"activity":{"bestPercent":"41.13% P4","killsCount":0}},
                        {"activity":{"bestPercent":"12.34% P3","killsCount":0}}
                      ]}}}
                      """),
            }),
        };
        using var client = new TomestoneApiClient(EnabledConfiguration(), handler);
        var adapter = CreateAdapter(client, EnabledConfiguration());

        var result = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Collection(handler.Requests,
            profile => Assert.Contains("/profile/", profile.RequestUri!.AbsolutePath),
            activity => Assert.Contains("/activity/", activity.RequestUri!.AbsolutePath));
        Assert.Contains(result.Value!.Phases, phase => phase.DisplayText == "P4 41.13%");
        Assert.False(result.Value.Cleared);
    }

    [Fact]
    public async Task Uses_official_client_and_parser_for_savage_boss_percent()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"data":{"encounters":[
                  {"canonicalName":"vamp-fatale","progression":{"rawPercent":22.1}},
                  {"canonicalName":"vamp-fatale","progression":{"rawPercent":9.8}}
                ]}}
                """),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            73,
            101,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Phases);
        Assert.Equal(9.8, result.Value!.BossPercentage!.Value, 3);
    }

    [Theory]
    [InlineData(62u, 93u, "savage", "Black Cat", "aac-light-heavyweight-savage", "black-cat")]
    [InlineData(68u, 100u, "savage", "Howling Blade", "aac-cruiserweight-savage", "howling-blade")]
    [InlineData(72u, 1084u, "trials", "Enuo", "trials-extreme", "enuo")]
    public async Task Reads_nonultimate_profile_boss_percentage_without_activity_request(
        uint zoneId,
        uint encounterId,
        string profileCategory,
        string name,
        string category,
        string encounter)
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""
                {"encounters":{"{{{profileCategory}}}":[{"name":"{{{name}}}","canonicalName":"{{{encounter}}}","progression":{"displayPercent":"31.42%"}}]}}
                """),
            }),
        };
        using var client = new TomestoneApiClient(EnabledConfiguration(), handler);
        var adapter = CreateAdapter(client, EnabledConfiguration());

        var result = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", zoneId, encounterId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.Cleared);
        Assert.Empty(result.Value.Phases);
        Assert.Equal(31.42, result.Value.BossPercentage!.Value, 3);
        Assert.Contains("/profile/", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var target));
        Assert.Equal(category, target.Category);
    }

    [Theory]
    [InlineData(62u, 93u, "aac-light-heavyweight-savage", "black-cat")]
    [InlineData(68u, 100u, "aac-cruiserweight-savage", "howling-blade")]
    [InlineData(72u, 1084u, "trials-extreme", "enuo")]
    public async Task Missing_nonultimate_profile_falls_back_to_scoped_activity_boss_percentage(
        uint zoneId,
        uint encounterId,
        string category,
        string encounter)
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal)
                    ? "{}"
                    : """
                      {"data":{"paginator":{"data":[
                        {"activity":{"bestPercent":"41.13%","killsCount":0}},
                        {"activity":{"bestPercent":"12.34%","killsCount":0}}
                      ]}}}
                      """),
            }),
        };
        using var client = new TomestoneApiClient(EnabledConfiguration(), handler);
        var adapter = CreateAdapter(client, EnabledConfiguration());

        var result = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", zoneId, encounterId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.Cleared);
        Assert.Empty(result.Value.Phases);
        Assert.Equal(12.34, result.Value.BossPercentage!.Value, 3);
        Assert.Collection(handler.Requests,
            profile => Assert.Contains("/profile/", profile.RequestUri!.AbsolutePath),
            activity =>
            {
                Assert.Equal("/api/character/activity/Tonberry/Alpha", activity.RequestUri!.AbsolutePath);
                Assert.Equal($"?expansion=dawntrail&zone={category}&encounter={encounter}", activity.RequestUri.Query);
            });
    }

    [Fact]
    public async Task Uses_target_aware_parser_for_completed_current_savage_progress()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReadFixture("progression-target-m2s-completed-with-wipe-activity.json")),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            73,
            102,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.TransientFailure);
        Assert.True(result.Value!.Cleared);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Value!.Phases);
        Assert.Null(result.Value!.BossPercentage);
    }

    [Fact]
    public async Task Uses_target_aware_parser_for_current_savage_raw_percent()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReadFixture("progression-target-m2s-raw-percent-with-unrelated-activity.json")),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            73,
            102,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.TransientFailure);
        Assert.Empty(result.Value!.Phases);
        Assert.Equal(29.69, result.Value!.BossPercentage!.Value, 3);
    }

    [Fact]
    public async Task Tomestone_adapter_reports_transient_failure_when_raw_request_fails()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("network failed")),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.TransientFailure);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Tomestone_adapter_reports_non_transient_failure_when_payload_is_malformed()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"graph":[{"phaseName":"P1","bestPull":"12%"}]"""),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.TransientFailure);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task Tomestone_adapter_classifies_http_failures_by_retryability(
        HttpStatusCode statusCode,
        bool expectedTransient)
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("failed"),
            }),
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedTransient, result.TransientFailure);
        Assert.Null(result.Value);
    }

    [Theory]
    [MemberData(nameof(NonFatalResponses))]
    public async Task FetchProgressionDataAsync_returns_empty_for_non_fatal_responses(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = sendAsync,
        };
        using var client = new TomestoneApiClient(
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            },
            handler);
        var adapter = CreateAdapter(
            client,
            new Configuration
            {
                EnableTomestoneProgressEnrichment = true,
                TomestoneApiKey = "secret-token",
            });

        var result = await adapter.FetchProgressionDataAsync(
            "Alpha",
            "Tonberry",
            65,
            1079,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Phases);
        Assert.Null(result.Value!.BossPercentage);
    }

    public static IEnumerable<object[]> NonFatalResponses()
    {
        yield return
        [
            static (HttpRequestMessage _, CancellationToken _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found"),
            }),
        ];
        yield return
        [
            static (HttpRequestMessage _, CancellationToken _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
        ];
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.Forbidden, "{\"error\":\"Character activity is hidden.\"}")]
    public async Task Reuses_unknown_or_hidden_profile_without_fallback(HttpStatusCode status, string body)
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }),
        };
        using var client = new TomestoneApiClient(EnabledConfiguration(), handler);
        var adapter = CreateAdapter(client, EnabledConfiguration());

        var ultimate = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);
        var savage = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 73, 101, CancellationToken.None);

        Assert.True(ultimate.Succeeded);
        Assert.True(savage.Succeeded);
        Assert.Empty(ultimate.Value!.Phases);
        Assert.Null(savage.Value!.BossPercentage);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reuses_successful_profile_for_different_encounters_on_same_character()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(ProfileWithTwoEncounters()),
        };
        using var client = new TomestoneApiClient(EnabledConfiguration(), handler);
        var adapter = CreateAdapter(client, EnabledConfiguration());

        var ultimate = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);
        var savage = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 73, 101, CancellationToken.None);

        Assert.True(ultimate.Succeeded);
        Assert.Equal("P4 41.13%", Assert.Single(ultimate.Value!.Phases).DisplayText);
        Assert.Equal(22.1, savage.Value!.BossPercentage!.Value, 3);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Overlapping_lookups_share_profile_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return ProfileWithTwoEncounters();
            },
        };
        using var client = new TomestoneApiClient(EnabledConfiguration(), handler);
        var adapter = CreateAdapter(client, EnabledConfiguration());

        var ultimateTask = adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var savageTask = adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 73, 101, CancellationToken.None);
        release.TrySetResult();
        var results = await Task.WhenAll(ultimateTask, savageTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(handler.Requests);
        Assert.Equal("P4 41.13%", Assert.Single(results[0].Value!.Phases).DisplayText);
        Assert.Equal(22.1, results[1].Value!.BossPercentage!.Value, 3);
    }

    [Fact]
    public async Task Changed_credentials_do_not_reuse_previous_profile()
    {
        var configuration = EnabledConfiguration();
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(ProfileWithTwoEncounters()),
        };
        using var client = new TomestoneApiClient(configuration, handler);
        var adapter = CreateAdapter(client, configuration);

        await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);
        configuration.TomestoneApiKey = "changed-token";
        var result = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("changed-token", handler.Requests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Failed_profile_is_not_cached()
    {
        var attempts = 0;
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = (_, _) => Task.FromResult(++attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failed") }
                : ProfileWithTwoEncounters()),
        };
        using var client = new TomestoneApiClient(EnabledConfiguration(), handler);
        var adapter = CreateAdapter(client, EnabledConfiguration());

        var failed = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);
        var retried = await adapter.FetchProgressionDataAsync("Alpha", "Tonberry", 65, 1079, CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.True(retried.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("P4 41.13%", Assert.Single(retried.Value!.Phases).DisplayText);
    }

    private static Configuration EnabledConfiguration() => new()
    {
        EnableTomestoneProgressEnrichment = true,
        TomestoneApiKey = "secret-token",
    };

    private static HttpResponseMessage ProfileWithTwoEncounters() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {"encounters":{
          "ultimate":[{"zoneName":"Futures Rewritten (Ultimate)","progression":{"mechanic":{"number":7},"displayPercent":"41.13% P4"}}],
          "savage":[{"encounters":[{"name":"Vamp Fatale","progression":{"displayPercent":"22.1%"}}]}]
        }}
        """),
    };

    private static FFLogsCollector.TomestoneApiClientAdapter CreateAdapter(
        TomestoneApiClient client,
        Configuration configuration,
        Action<string>? debugLog = null,
        Action<string>? warningLog = null)
    {
        return new FFLogsCollector.TomestoneApiClientAdapter(
            client,
            configuration,
            debugLog ?? (_ => { }),
            warningLog ?? (_ => { }));
    }

    private static string ReadFixture(
        string fileName,
        [CallerFilePath] string sourceFile = "")
    {
        var testDirectory = Path.GetDirectoryName(sourceFile)
            ?? throw new InvalidOperationException("Could not determine test source directory.");
        return File.ReadAllText(Path.Combine(testDirectory, "Fixtures", "tomestone", fileName));
    }

    private sealed class RecordingTomestoneHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSendAsync { get; set; }
            = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"graph":[]}}"""),
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
}
