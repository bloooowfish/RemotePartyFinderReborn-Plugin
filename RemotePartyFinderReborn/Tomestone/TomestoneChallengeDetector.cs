using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;

namespace RemotePartyFinderReborn.Tomestone;

/// <summary>
/// Shared detector for HTTP rate-limits, anti-bot challenges (403/429/Cloudflare/CAPTCHA),
/// and Retry-After cooldown durations across Tier 1, Tier 2, and Tier 3 Tomestone clients.
/// </summary>
internal static class TomestoneChallengeDetector
{
    internal static readonly TimeSpan DefaultChallengeCooldown = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan MaxRetryAfterDuration = TimeSpan.FromHours(1);

    private static readonly (string Marker, TomestoneChallengeKind Kind, string Label)[] TextMarkers =
    [
        ("cf-turnstile", TomestoneChallengeKind.Turnstile, "cf-turnstile"),
        ("data-cf-turnstile", TomestoneChallengeKind.Turnstile, "cf-turnstile"),
        ("challenges.cloudflare.com", TomestoneChallengeKind.Turnstile, "challenges.cloudflare.com"),
        ("g-recaptcha", TomestoneChallengeKind.Recaptcha, "g-recaptcha"),
        ("recaptcha", TomestoneChallengeKind.Recaptcha, "recaptcha"),
        ("hcaptcha", TomestoneChallengeKind.HCaptcha, "hcaptcha"),
        ("h-captcha", TomestoneChallengeKind.HCaptcha, "h-captcha"),
        ("cf-mitigated", TomestoneChallengeKind.CfMitigatedHeader, "cf-mitigated"),
        ("just a moment...", TomestoneChallengeKind.CloudflareJustAMoment, "Just a moment..."),
    ];

    internal static TomestoneChallengeInfo DetectTextChallenge(string text, TimeSpan? retryAfter = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var (marker, kind, label) in TextMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new TomestoneChallengeInfo(kind, label, retryAfter);
            }
        }

        return null;
    }

    internal static TomestoneChallengeInfo DetectWebHttpChallenge(
        HttpStatusCode statusCode,
        HttpResponseHeaders headers,
        string body,
        DateTime utcNow)
    {
        var hasRetryAfter = TryParseRetryAfter(headers, utcNow, out var parsedRetryAfter);
        TimeSpan? retryAfter = hasRetryAfter ? parsedRetryAfter : null;

        if (HasNonEmptyHeader(headers, "cf-mitigated"))
        {
            return new TomestoneChallengeInfo(
                TomestoneChallengeKind.CfMitigatedHeader,
                "header:cf-mitigated",
                retryAfter);
        }

        if (DetectTextChallenge(body, retryAfter) is { } textChallenge)
        {
            return textChallenge;
        }

        return statusCode switch
        {
            HttpStatusCode.TooManyRequests => new TomestoneChallengeInfo(
                TomestoneChallengeKind.Http429,
                "HTTP 429",
                retryAfter),
            HttpStatusCode.Forbidden => new TomestoneChallengeInfo(
                TomestoneChallengeKind.Http403,
                "HTTP 403",
                retryAfter),
            _ => null,
        };
    }

    internal static TomestoneChallengeInfo DetectApiNonSuccessChallenge(
        HttpStatusCode statusCode,
        HttpResponseHeaders headers,
        string body,
        DateTime utcNow,
        bool isHiddenActivity)
    {
        if (isHiddenActivity)
        {
            return null;
        }

        var hasRetryAfter = TryParseRetryAfter(headers, utcNow, out var parsedRetryAfter);
        TimeSpan? retryAfter = hasRetryAfter ? parsedRetryAfter : null;

        if (HasNonEmptyHeader(headers, "cf-mitigated"))
        {
            return new TomestoneChallengeInfo(
                TomestoneChallengeKind.CfMitigatedHeader,
                "header:cf-mitigated",
                retryAfter);
        }

        if (DetectTextChallenge(body, retryAfter) is { } textChallenge)
        {
            return textChallenge;
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return new TomestoneChallengeInfo(
                TomestoneChallengeKind.Http429,
                "HTTP 429",
                retryAfter);
        }

        return null;
    }

    internal static bool TryParseRetryAfter(
        HttpResponseHeaders headers,
        DateTime utcNow,
        out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (headers == null)
        {
            return false;
        }

        if (headers.RetryAfter?.Delta is { } delta)
        {
            retryAfter = ClampCooldown(delta);
            return true;
        }

        if (headers.RetryAfter?.Date is { } date)
        {
            retryAfter = ClampCooldown(date.UtcDateTime - utcNow);
            return true;
        }

        if (headers.TryGetValues("Retry-After", out var rawValues))
        {
            foreach (var rawValue in rawValues)
            {
                var trimmed = rawValue?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && !double.IsNaN(seconds)
                    && !double.IsInfinity(seconds))
                {
                    retryAfter = ClampCooldown(TimeSpan.FromSeconds(Math.Max(1.0, seconds)));
                    return true;
                }

                if (DateTimeOffset.TryParse(
                    trimmed,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedDate))
                {
                    retryAfter = ClampCooldown(parsedDate.UtcDateTime - utcNow);
                    return true;
                }
            }
        }

        return false;
    }

    internal static TimeSpan EffectiveCooldownDuration(TomestoneChallengeInfo challenge, TimeSpan defaultDuration)
    {
        if (challenge?.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            return ClampCooldown(retryAfter);
        }

        return defaultDuration > TimeSpan.Zero ? defaultDuration : DefaultChallengeCooldown;
    }

    private static TimeSpan ClampCooldown(TimeSpan value)
    {
        if (value < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return value > MaxRetryAfterDuration ? MaxRetryAfterDuration : value;
    }

    private static bool HasNonEmptyHeader(HttpResponseHeaders headers, string headerName)
    {
        return headers != null
            && headers.TryGetValues(headerName, out var values)
            && values.Any(static value => !string.IsNullOrWhiteSpace(value));
    }
}
