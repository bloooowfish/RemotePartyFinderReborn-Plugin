namespace RemotePartyFinderReborn;

internal static class IngestResponseParser
{
    public static void CaptureMultipleResponse(UploadUrl uploadUrl, string responseBody)
    {
        if (!IngestJson.TryDeserializeObject(responseBody, out ContributeMultipleResponse parsed))
        {
            return;
        }

        uploadUrl.ApplyIngestCapabilities(parsed.ProtectedEndpoints, parsed.DetailCapabilities);
    }

    public static void CapturePlayersResponse(UploadUrl uploadUrl, string responseBody)
    {
        if (!IngestJson.TryDeserializeObject(responseBody, out ContributePlayersResponse parsed))
        {
            return;
        }

        uploadUrl.ApplyIngestCapabilities(parsed.ProtectedEndpoints, null);
    }

}
