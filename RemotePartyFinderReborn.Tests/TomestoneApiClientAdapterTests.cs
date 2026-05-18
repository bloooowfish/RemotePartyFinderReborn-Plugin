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
    public async Task FetchProgressionDataAsync_builds_encoded_progression_graph_endpoint()
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

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/character/progression-graph/Aether%20%26%20Co/Name%20With%2FSlash", request.RequestUri?.AbsolutePath);
        Assert.Equal("?expansion=dawntrail&zone=ultimates&encounter=futures-rewritten-ultimate", request.RequestUri?.Query);
    }

    [Fact]
    public async Task FetchProgressionDataAsync_builds_current_savage_progression_graph_endpoint()
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

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/character/progression-graph/Tonberry/Alpha", request.RequestUri?.AbsolutePath);
        Assert.Equal("?expansion=dawntrail&zone=raids&encounter=aac-heavyweight-m1-savage", request.RequestUri?.Query);
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
        var log = Assert.Single(debugLogs, entry => entry.Contains("Tomestone progress not found"));
        Assert.Contains("fflogs zone=73, encounter=105", log);
        Assert.Contains("tomestone expansion=dawntrail, zone=raids, encounter=aac-heavyweight-m4-savage", log);
        Assert.Contains("kind=BossPercentage", log);
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
                  {"canonicalName":"futures-rewritten-ultimate","mechanic":{"number":1},"progression":{"rawPercent":12}},
                  {"canonicalName":"futures-rewritten-ultimate","mechanic":{"number":2},"progression":{"rawPercent":24}},
                  {"canonicalName":"futures-rewritten-ultimate","mechanic":{"number":3},"progression":{"rawPercent":55.5}},
                  {"canonicalName":"futures-rewritten-ultimate","mechanic":{"number":4},"progression":{"rawPercent":100}}
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
    public async Task Uses_legacy_phase_progress_fallback_when_target_nodes_are_absent()
    {
        var handler = new RecordingTomestoneHttpMessageHandler
        {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReadFixture("progression-graph-ultimate-with-phases.json")),
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
            Requests.Add(request);
            return OnSendAsync(request, cancellationToken);
        }
    }
}
