using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class IngestRequestFactoryTests {
    private const string EmptyBodySha256Hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void CreateGetRequest_signs_strict_v2_eight_line_payload_with_empty_body_hash() {
        var configuration = new Configuration {
            IngestClientId = "0123456789abcdef0123456789abcdef",
            IngestSharedSecret = "test-shared-secret",
        };
        var endpoint = new IngestEndpoint(
            "https://UPLOAD.EXAMPLE:443/contribute/fflogs/jobs?limit=5",
            IngestRoutes.FflogsJobs
        );

        using var request = IngestRequestFactory.CreateGetRequest(
            configuration,
            endpoint,
            capabilityToken: "cap-token-1",
            extraHeaders: new Dictionary<string, string> {
                ["X-RPF-FFLogs-Worker-Region"] = "EU",
            },
            timestampSeconds: 1_700_000_000,
            nonceOverride: "nonce-get-1"
        );

        Assert.Equal("v2", Assert.Single(request.Headers.GetValues("X-RPF-Signature-Version")));
        Assert.Equal("0123456789abcdef0123456789abcdef", Assert.Single(request.Headers.GetValues("X-RPF-Client-Id")));
        Assert.Equal("1700000000", Assert.Single(request.Headers.GetValues("X-RPF-Timestamp")));
        Assert.Equal("nonce-get-1", Assert.Single(request.Headers.GetValues("X-RPF-Nonce")));
        Assert.Equal("upload.example", Assert.Single(request.Headers.GetValues("X-RPF-Target-Host")));
        Assert.Equal("cap-token-1", Assert.Single(request.Headers.GetValues("X-RPF-Capability")));
        Assert.Equal("EU", Assert.Single(request.Headers.GetValues("X-RPF-FFLogs-Worker-Region")));

        var expectedCanonical =
            "v2\n" +
            "GET\n" +
            "upload.example\n" +
            "/contribute/fflogs/jobs?limit=5\n" +
            "1700000000\n" +
            "nonce-get-1\n" +
            "0123456789abcdef0123456789abcdef\n" +
            EmptyBodySha256Hex;
        var expectedSignature = ComputeExpectedSignature("test-shared-secret", expectedCanonical);
        Assert.Equal(expectedSignature, Assert.Single(request.Headers.GetValues("X-RPF-Signature")));
    }

    [Fact]
    public async Task CreatePostJsonRequest_signs_exact_utf8_wire_bytes_and_preserves_non_default_port() {
        var configuration = new Configuration {
            IngestClientId = "abcdef0123456789abcdef0123456789",
            IngestSharedSecret = "secret-v2",
        };
        var endpoint = new IngestEndpoint(
            "http://127.0.0.1:8000/contribute/detail",
            IngestRoutes.Detail
        );
        const string jsonBody = """{"listing_id":42,"leader_name":"테스트プレイヤー"}""";
        var expectedBytes = Encoding.UTF8.GetBytes(jsonBody);
        var expectedHashHex = Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant();

        using var request = IngestRequestFactory.CreatePostJsonRequest(
            configuration,
            endpoint,
            jsonBody,
            capabilityToken: null,
            extraHeaders: null,
            timestampSeconds: 1_750_000_123,
            nonceOverride: "nonce-post-utf8"
        );

        Assert.NotNull(request.Content);
        Assert.IsType<ByteArrayContent>(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", request.Content.Headers.ContentType?.CharSet);

        var wireBytes = await request.Content.ReadAsByteArrayAsync();
        Assert.Equal(expectedBytes, wireBytes);

        Assert.Equal("v2", Assert.Single(request.Headers.GetValues("X-RPF-Signature-Version")));
        Assert.Equal("127.0.0.1:8000", Assert.Single(request.Headers.GetValues("X-RPF-Target-Host")));
        Assert.False(request.Headers.Contains("X-RPF-Capability"));

        var expectedCanonical =
            "v2\n" +
            "POST\n" +
            "127.0.0.1:8000\n" +
            "/contribute/detail\n" +
            "1750000123\n" +
            "nonce-post-utf8\n" +
            "abcdef0123456789abcdef0123456789\n" +
            expectedHashHex;
        var expectedSignature = ComputeExpectedSignature("secret-v2", expectedCanonical);
        Assert.Equal(expectedSignature, Assert.Single(request.Headers.GetValues("X-RPF-Signature")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreatePostJsonRequest_with_empty_or_null_body_uses_empty_sha256_hash(string? body) {
        var configuration = new Configuration {
            IngestClientId = "11111111222222223333333344444444",
            IngestSharedSecret = "empty-body-secret",
        };
        var endpoint = new IngestEndpoint(
            "http://LOCALHOST:80/contribute/players",
            IngestRoutes.Players
        );

        using var request = IngestRequestFactory.CreatePostJsonRequest(
            configuration,
            endpoint,
            body,
            capabilityToken: null,
            extraHeaders: null,
            timestampSeconds: 1_700_000_999,
            nonceOverride: "nonce-empty"
        );

        var wireBytes = await request.Content!.ReadAsByteArrayAsync();
        Assert.Empty(wireBytes);
        Assert.Equal("localhost", Assert.Single(request.Headers.GetValues("X-RPF-Target-Host")));

        var expectedCanonical =
            "v2\n" +
            "POST\n" +
            "localhost\n" +
            "/contribute/players\n" +
            "1700000999\n" +
            "nonce-empty\n" +
            "11111111222222223333333344444444\n" +
            EmptyBodySha256Hex;
        Assert.Equal(
            ComputeExpectedSignature("empty-body-secret", expectedCanonical),
            Assert.Single(request.Headers.GetValues("X-RPF-Signature"))
        );
    }

    private static string ComputeExpectedSignature(string secret, string canonicalPayload) {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalPayload)));
    }
}
