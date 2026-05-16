using System;
using System.Collections.Generic;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class IngestRouteTests {
    [Fact]
    public void Ingest_route_builds_url_and_signed_request_with_same_path() {
        var uploadUrl = new UploadUrl("https://upload.example/");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
        };

        Assert.True(IngestEndpointResolver.TryBuildEndpoint(uploadUrl, IngestRoutes.Detail, out var endpoint));

        using var request = IngestRequestFactory.CreatePostJsonRequest(
            configuration,
            endpoint,
            "{}"
        );

        Assert.Equal("https://upload.example/contribute/detail", endpoint.Url);
        Assert.Equal(IngestRoutes.Detail, endpoint.Route);
        Assert.Equal(IngestRoutes.Detail.Path, request.RequestUri!.AbsolutePath);
        Assert.True(request.Headers.Contains("X-RPF-Signature"));
    }

    [Fact]
    public void Ingest_get_route_builds_url_and_signed_request_with_same_path() {
        var uploadUrl = new UploadUrl("https://upload.example/");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
        };

        Assert.True(IngestEndpointResolver.TryBuildEndpoint(uploadUrl, IngestRoutes.FflogsJobs, out var endpoint));

        using var request = IngestRequestFactory.CreateGetRequest(
            configuration,
            endpoint
        );

        Assert.Equal("https://upload.example/contribute/fflogs/jobs", endpoint.Url);
        Assert.Equal(IngestRoutes.FflogsJobs, endpoint.Route);
        Assert.Equal(IngestRoutes.FflogsJobs.Path, request.RequestUri!.AbsolutePath);
        Assert.True(request.Headers.Contains("X-RPF-Signature"));
    }

    [Fact]
    public void Ingest_get_route_can_include_extra_headers_without_changing_path() {
        var uploadUrl = new UploadUrl("https://upload.example/");
        var configuration = new Configuration {
            IngestClientId = Guid.NewGuid().ToString("N"),
        };

        Assert.True(IngestEndpointResolver.TryBuildEndpoint(uploadUrl, IngestRoutes.FflogsJobs, out var endpoint));

        using var request = IngestRequestFactory.CreateGetRequest(
            configuration,
            endpoint,
            extraHeaders: new Dictionary<string, string> {
                ["X-RPF-FFLogs-Worker-Region"] = "JP",
            }
        );

        Assert.Equal(IngestRoutes.FflogsJobs.Path, request.RequestUri!.AbsolutePath);
        Assert.Equal(["JP"], request.Headers.GetValues("X-RPF-FFLogs-Worker-Region").ToArray());
        Assert.True(request.Headers.Contains("X-RPF-Signature"));
    }

    [Fact]
    public void Legacy_multiple_url_normalization_still_accepts_contribute_multiple_url() {
        var uploadUrl = new UploadUrl("https://upload.example/contribute/multiple");

        Assert.True(IngestEndpointResolver.TryBuildEndpoint(uploadUrl, IngestRoutes.Players, out var endpoint));

        Assert.Equal("https://upload.example/contribute/players", endpoint.Url);
        Assert.Equal(IngestRoutes.Players, endpoint.Route);
    }

    [Fact]
    public void Ingest_json_deserializes_object_response_and_rejects_non_objects() {
        Assert.True(IngestJson.TryDeserializeObject("""{"status":"ok","values":[1,2]}""", out TestResponse? parsed));
        Assert.NotNull(parsed);
        Assert.Equal("ok", parsed.Status);
        Assert.Equal([1, 2], parsed.Values);

        Assert.False(IngestJson.TryDeserializeObject("""[{"status":"ok"}]""", out TestResponse? arrayParsed));
        Assert.Null(arrayParsed);
        Assert.False(IngestJson.TryDeserializeObject(string.Empty, out TestResponse? emptyParsed));
        Assert.Null(emptyParsed);
    }

    private sealed class TestResponse {
        public string Status { get; init; } = string.Empty;
        public List<int> Values { get; init; } = [];
    }
}
