using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RemotePartyFinderReborn;

public class FFLogsClient : IDisposable
{
    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _infoLog;
    private readonly Action<string> _warningLog;
    private readonly Action<string> _errorLog;
    private readonly Action<Exception, string> _exceptionLog;
    private string _accessToken = string.Empty;
    private DateTime _tokenExpiration;
    private readonly RateLimitCooldownState _rateLimitCooldown = new();

    private DateTime _lastGraphQlErrorLog = DateTime.MinValue;
    private string _lastGraphQlErrorMessage = string.Empty;
    private static readonly TimeSpan RateLimitCooldownDuration = TimeSpan.FromHours(1);

    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string GraphQlUrl = "https://www.fflogs.com/api/v2/client";

    public FFLogsClient(Configuration configuration)
        : this(
            configuration,
            new HttpClient(),
            static () => DateTime.UtcNow,
            message => Plugin.Log.Info(message),
            message => Plugin.Log.Warning(message),
            message => Plugin.Log.Error(message),
            (exception, message) => Plugin.Log.Error(exception, message))
    {
    }

    internal FFLogsClient(
        Configuration configuration,
        HttpMessageHandler messageHandler,
        Func<DateTime> utcNow = null,
        Action<string> infoLog = null,
        Action<string> warningLog = null,
        Action<string> errorLog = null,
        Action<Exception, string> exceptionLog = null)
        : this(
            configuration,
            new HttpClient(messageHandler),
            utcNow ?? (static () => DateTime.UtcNow),
            infoLog ?? (static _ => { }),
            warningLog ?? (static _ => { }),
            errorLog ?? (static _ => { }),
            exceptionLog ?? (static (_, _) => { }))
    {
    }

    private FFLogsClient(
        Configuration configuration,
        HttpClient httpClient,
        Func<DateTime> utcNow,
        Action<string> infoLog,
        Action<string> warningLog,
        Action<string> errorLog,
        Action<Exception, string> exceptionLog)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _infoLog = infoLog ?? throw new ArgumentNullException(nameof(infoLog));
        _warningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
        _errorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
        _exceptionLog = exceptionLog ?? throw new ArgumentNullException(nameof(exceptionLog));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public DateTime RateLimitCooldownUntilUtc
        => _rateLimitCooldown.CooldownUntilUtc;

    public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        => _rateLimitCooldown.TryGetRemaining(_utcNow(), out remaining);

    public void ResetRateLimitCooldown()
    {
        _rateLimitCooldown.Reset();
        _infoLog("FFLogs rate-limit cooldown was reset manually.");
    }

    private void ActivateRateLimitCooldown(string source)
    {
        _rateLimitCooldown.Activate(_utcNow(), RateLimitCooldownDuration);

        _warningLog(
            $"FFLogs API rate limited at {source}. Pausing FFLogs requests until {RateLimitCooldownUntilUtc:O} (1 hour lockout).");
    }

    private void LogCooldownSkipIfNeeded(TimeSpan remaining)
    {
        if (!_rateLimitCooldown.ShouldLogSkip(_utcNow(), TimeSpan.FromMinutes(1)))
        {
            return;
        }

        var minutesRemaining = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        _warningLog($"FFLogs cooldown active. Skipping FFLogs query for about {minutesRemaining} minute(s).");
    }

    private async Task<OperationOutcome<bool>> EnsureTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && _utcNow() < _tokenExpiration)
        {
            return OperationOutcome<bool>.Success(true);
        }

        if (string.IsNullOrEmpty(_configuration.FFLogsClientId) || string.IsNullOrEmpty(_configuration.FFLogsClientSecret))
        {
            return OperationOutcome<bool>.Failure(false, "FFLogs credentials are not configured.");
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var form = new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "client_id", _configuration.FFLogsClientId },
                { "client_secret", _configuration.FFLogsClientSecret }
            };
            request.Content = new FormUrlEncodedContent(form);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var transientFailure = IsTransientStatusCode(response.StatusCode);
                if ((int)response.StatusCode == 429)
                {
                    ActivateRateLimitCooldown("oauth token request");
                    transientFailure = true;
                }

                _errorLog($"FFLogs OAuth request failed: {response.StatusCode}");
                return OperationOutcome<bool>.Failure(
                    transientFailure,
                    $"FFLogs OAuth request failed: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            _accessToken = json["access_token"]?.ToString();
            var expiresIn = json["expires_in"]?.ToObject<int>() ?? 0;
            _tokenExpiration = _utcNow().AddSeconds(expiresIn - 60); // Buffer 60s

            return !string.IsNullOrEmpty(_accessToken)
                ? OperationOutcome<bool>.Success(true)
                : OperationOutcome<bool>.Failure(false, "FFLogs OAuth response did not include an access token.");
        }
        catch (Exception ex)
        {
            _exceptionLog(ex, "Failed to authenticate with FFLogs.");
            return OperationOutcome<bool>.Failure(true, ex.Message);
        }
    }

    public Task<JObject> QueryAsync(string query)
        => QueryAsync(query, CancellationToken.None);

    public async Task<JObject> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var outcome = await QueryOutcomeAsync(query, cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? outcome.Value : null;
    }

    private async Task<OperationOutcome<JObject>> QueryOutcomeAsync(string query, CancellationToken cancellationToken)
    {
        if (TryGetRateLimitRemaining(out var cooldownRemaining))
        {
            LogCooldownSkipIfNeeded(cooldownRemaining);
            return OperationOutcome<JObject>.Failure(true, "FFLogs rate-limit cooldown is active.");
        }

        var tokenOutcome = await EnsureTokenAsync();
        if (!tokenOutcome.Succeeded)
        {
            return OperationOutcome<JObject>.Failure(
                tokenOutcome.TransientFailure,
                tokenOutcome.ErrorMessage);
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
             
            var payload = new { query };
            request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                var transientFailure = IsTransientStatusCode(response.StatusCode);
                if ((int)response.StatusCode == 429)
                {
                    ActivateRateLimitCooldown("GraphQL query");
                    transientFailure = true;
                }

                _errorLog($"FFLogs API Query failed: {response.StatusCode} - {errorBody}");
                return OperationOutcome<JObject>.Failure(
                    transientFailure,
                    $"FFLogs API query failed: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            if (json["errors"] is JArray errors && errors.Count > 0)
            {
                var msg = errors[0]?["message"]?.ToString();
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    var now = _utcNow();
                    if (!string.Equals(msg, _lastGraphQlErrorMessage, StringComparison.Ordinal)
                        || (now - _lastGraphQlErrorLog) > TimeSpan.FromSeconds(60))
                    {
                        _lastGraphQlErrorMessage = msg;
                        _lastGraphQlErrorLog = now;
                        _errorLog($"FFLogs GraphQL error: {msg}");
                    }
                }

                if (json["data"] is not JObject data || !data.Properties().Any())
                {
                    return OperationOutcome<JObject>.Failure(IsTransientGraphQlError(msg), msg);
                }
            }

            return OperationOutcome<JObject>.Success(json);
        }
        catch (Exception ex)
        {
            _exceptionLog(ex, "Failed to query FFLogs API.");
            return OperationOutcome<JObject>.Failure(true, ex.Message);
        }
    }

    public async Task<Dictionary<ulong, List<(int EncounterId, double Percentile)>>> FetchCharacterParsesBatchAsync(
        List<(ulong ContentId, string Name, string Server, string Region)> characters,
        int zoneId,
        int? difficultyId = null)
    {
        if (characters.Count == 0) return new();

        var sb = new StringBuilder();
        sb.Append("query { characterData {");

        for (int i = 0; i < characters.Count; i++)
        {
            var charInfo = characters[i];
            // Alias: c{index}
            sb.Append($" c{i}: character(name: \"{charInfo.Name}\", serverSlug: \"{charInfo.Server}\", serverRegion: \"{charInfo.Region}\") {{");
            
            // Build zoneRankings arguments
            var args = new List<string> { $"zoneID: {zoneId}" };
            if (difficultyId.HasValue) args.Add($"difficulty: {difficultyId.Value}");
            args.Add("metric: rdps");
            args.Add("timeframe: Historical");

            sb.Append($" zoneRankings({string.Join(", ", args)})");
            sb.Append(" }");
        }

        sb.Append(" }}");

        var result = await QueryAsync(sb.ToString());
        var output = new Dictionary<ulong, List<(int, double)>>();

        if (result?["data"]?["characterData"] is not JObject data)
        {
            return output;
        }

        for (int i = 0; i < characters.Count; i++)
        {
            var charInfo = characters[i];
            var alias = $"c{i}";
            var characterData = data[alias];
             
            var encounterParses = new List<(int, double)>();

            if (characterData is JObject && characterData["zoneRankings"]?["rankings"] is JArray rankings)
            {
                foreach (var rank in rankings)
                {
                    if (rank is not JObject) continue;

                    var encounterId = rank["encounter"]?["id"]?.ToObject<int>();
                    var percentile = rank["rankPercent"]?.ToObject<double?>();

                    if (encounterId.HasValue && percentile.HasValue)
                    {
                        encounterParses.Add((encounterId.Value, percentile.Value));
                    }
                }
            }
            
            output[charInfo.ContentId] = encounterParses;
        }

        return output;
    }

    public sealed class CharacterFetchedData
    {
        public bool Hidden { get; init; }
        public List<CharacterEncounterParse> Parses { get; init; } = new();
    }

    public sealed class CharacterEncounterParse
    {
        public int EncounterId { get; init; }
        public double Percentile { get; init; }
        public int? ClearCount { get; init; }
    }

    public sealed class CandidateCharacterQuery
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Server { get; set; } = "";
        public string Region { get; set; } = "";
    }

    public async Task<Dictionary<ulong, CharacterFetchedData>> FetchCharacterDataBatchAsync(
        List<(ulong ContentId, string Name, string Server, string Region)> characters,
        int zoneId,
        int? difficultyId,
        CancellationToken cancellationToken)
    {
        if (characters.Count == 0) return new();

        var sb = new StringBuilder();
        sb.Append("query { characterData {");

        for (var i = 0; i < characters.Count; i++)
        {
            var charInfo = characters[i];
            var name = FFLogsQueryBuilder.EscapeGraphQlString(charInfo.Name);
            var server = FFLogsQueryBuilder.EscapeGraphQlString(charInfo.Server);
            var region = FFLogsQueryBuilder.EscapeGraphQlString(charInfo.Region);

            sb.Append($" c{i}: character(name: \"{name}\", serverSlug: \"{server}\", serverRegion: \"{region}\") {{");
            sb.Append(" hidden ");

            // zoneRankings (parse)
            var args = new List<string> { $"zoneID: {zoneId}" };
            if (difficultyId.HasValue) args.Add($"difficulty: {difficultyId.Value}");
            args.Add("metric: rdps");
            args.Add("timeframe: Historical");

            sb.Append($" zoneRankings({string.Join(", ", args)}) ");

            sb.Append(" }");
        }

        sb.Append(" } }");

        var result = await QueryAsync(sb.ToString(), cancellationToken);
        var output = new Dictionary<ulong, CharacterFetchedData>();

        if (result?[
                "data"]?[
                "characterData"] is not JObject data)
        {
            return output;
        }

        for (var i = 0; i < characters.Count; i++)
        {
            var charInfo = characters[i];
            var alias = $"c{i}";
            if (data[alias] is not JObject character)
            {
                // Character not found on that server/region.
                // Return empty data so the server can negative-cache this lookup.
                output[charInfo.ContentId] = new CharacterFetchedData { Hidden = false };
                continue;
            }

            var hidden = character["hidden"]?.ToObject<bool?>() ?? false;
            var parses = new List<CharacterEncounterParse>();

            if (character["zoneRankings"]?["rankings"] is JArray rankings)
            {
                foreach (var rank in rankings)
                {
                    if (rank is not JObject) continue;
                    var encounterId = rank["encounter"]?["id"]?.ToObject<int?>();
                    var percentile = rank["rankPercent"]?.ToObject<double?>();
                    if (encounterId.HasValue && percentile.HasValue)
                    {
                        parses.Add(new CharacterEncounterParse
                        {
                            EncounterId = encounterId.Value,
                            Percentile = percentile.Value,
                            ClearCount = ReadClearCount(rank),
                        });
                    }
                }
            }

            output[charInfo.ContentId] = new CharacterFetchedData
            {
                Hidden = hidden,
                Parses = parses,
            };
        }

        return output;
    }

    public async Task<Dictionary<string, CharacterFetchedData>> FetchCharacterCandidateDataBatchAsync(
        List<CandidateCharacterQuery> candidates,
        int zoneId,
        int? difficultyId,
        CancellationToken cancellationToken)
    {
        var outcome = await FetchCharacterCandidateDataBatchOutcomeAsync(
            candidates,
            zoneId,
            difficultyId,
            cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? outcome.Value ?? [] : [];
    }

    internal async Task<OperationOutcome<Dictionary<string, CharacterFetchedData>>> FetchCharacterCandidateDataBatchOutcomeAsync(
        List<CandidateCharacterQuery> candidates,
        int zoneId,
        int? difficultyId,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, CharacterFetchedData>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0)
        {
            return OperationOutcome<Dictionary<string, CharacterFetchedData>>.Success(output);
        }

        const int chunkSize = 40;

        for (var offset = 0; offset < candidates.Count; offset += chunkSize)
        {
            var chunk = candidates.Skip(offset).Take(chunkSize).ToList();

            var query = FFLogsQueryBuilder.BuildCharacterCandidateDataBatchQuery(
                chunk,
                zoneId,
                difficultyId);
            var result = await QueryOutcomeAsync(query, cancellationToken);
            if (!result.Succeeded)
            {
                return OperationOutcome<Dictionary<string, CharacterFetchedData>>.Failure(
                    result.TransientFailure,
                    result.ErrorMessage);
            }

            if (result.Value?["data"]?["characterData"] is not JObject data)
            {
                continue;
            }

            for (var i = 0; i < chunk.Count; i++)
            {
                var alias = $"c{i}";
                if (data[alias] is not JObject character)
                {
                    // Character not found on that server/region.
                    output[chunk[i].Key] = new CharacterFetchedData { Hidden = false };
                    continue;
                }

                var hidden = character["hidden"]?.ToObject<bool?>() ?? false;
                var parses = new List<CharacterEncounterParse>();

                if (character["zoneRankings"]?["rankings"] is JArray rankings)
                {
                    foreach (var rank in rankings)
                    {
                        if (rank is not JObject) continue;
                        var encounterId = rank["encounter"]?["id"]?.ToObject<int?>();
                        var percentile = rank["rankPercent"]?.ToObject<double?>();
                        if (encounterId.HasValue && percentile.HasValue)
                        {
                            parses.Add(new CharacterEncounterParse
                            {
                                EncounterId = encounterId.Value,
                                Percentile = percentile.Value,
                                ClearCount = ReadClearCount(rank),
                            });
                        }
                    }
                }

                output[chunk[i].Key] = new CharacterFetchedData
                {
                    Hidden = hidden,
                    Parses = parses,
                };
            }
        }

        return OperationOutcome<Dictionary<string, CharacterFetchedData>>.Success(output);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout
            || code == 429
            || code >= 500;
    }

    private static bool IsTransientGraphQlError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        return !message.Contains("Cannot query field", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("Unknown argument", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("Syntax Error", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("Validation error", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ReadClearCount(JToken ranking)
    {
        var totalKills = ranking["totalKills"]?.ToObject<int?>();
        if (totalKills.HasValue)
        {
            return totalKills.Value;
        }

        var kills = ranking["kills"]?.ToObject<int?>();
        return kills;
    }
}
