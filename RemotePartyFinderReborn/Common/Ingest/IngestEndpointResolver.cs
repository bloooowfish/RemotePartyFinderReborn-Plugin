using System;
using System.Net;

namespace RemotePartyFinderReborn;

internal static class IngestEndpointResolver
{
    private static readonly TimeSpan WarningCooldown = TimeSpan.FromMinutes(5);

    public static bool TryBuildEndpoint(UploadUrl uploadUrl, IngestRoute route, out IngestEndpoint endpoint)
    {
        endpoint = default;
        if (!TryResolveBaseUrl(uploadUrl.Url, out var baseUrl, out var error))
        {
            LogBlockedUrl(uploadUrl, error);
            return false;
        }

        endpoint = new IngestEndpoint(baseUrl + route.Path, route);
        return true;
    }

    public static bool IsValidUploadUrl(string configuredUrl, out string error)
        => TryResolveBaseUrl(configuredUrl, out _, out error);

    public static bool IsValidUploadUrl(UploadUrl uploadUrl, Action<string> warningLog)
    {
        ArgumentNullException.ThrowIfNull(uploadUrl);
        ArgumentNullException.ThrowIfNull(warningLog);

        if (TryResolveBaseUrl(uploadUrl.Url, out _, out var error))
        {
            return true;
        }

        LogBlockedUrl(uploadUrl, error, warningLog);
        return false;
    }

    private static bool TryResolveBaseUrl(string configuredUrl, out string baseUrl, out string error)
    {
        baseUrl = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            error = "Upload URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Invalid URL format.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "Only http:// or https:// URLs are supported.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri))
        {
            error = "Remote HTTP upload URLs are blocked. Use HTTPS or localhost HTTP only.";
            return false;
        }

        baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (baseUrl.EndsWith("/contribute/multiple", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/contribute/multiple".Length];
        }
        else if (baseUrl.EndsWith("/contribute", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/contribute".Length];
        }

        return true;
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (IPAddress.TryParse(uri.Host, out var ipAddress))
        {
            return IPAddress.IsLoopback(ipAddress);
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogBlockedUrl(UploadUrl uploadUrl, string error, Action<string> warningLog = null)
    {
        var now = DateTime.UtcNow;
        if (!uploadUrl.ShouldLogSecurityWarning(now, WarningCooldown))
        {
            return;
        }

        var message = $"RemotePartyFinderReborn: skipped upload target '{uploadUrl.Url}': {error}";
        if (warningLog is null)
        {
            Plugin.Log.Warning(message);
            return;
        }

        warningLog(message);
    }
}
