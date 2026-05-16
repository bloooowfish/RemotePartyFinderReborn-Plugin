using Newtonsoft.Json;

#nullable enable

namespace RemotePartyFinderReborn;

internal static class IngestJson
{
    internal static bool TryDeserializeObject<T>(string responseBody, out T? parsed)
        where T : class
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        var trimmed = responseBody.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            parsed = JsonConvert.DeserializeObject<T>(responseBody);
            return parsed != null;
        }
        catch
        {
            return false;
        }
    }
}
