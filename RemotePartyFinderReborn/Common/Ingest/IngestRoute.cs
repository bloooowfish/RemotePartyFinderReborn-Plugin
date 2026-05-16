namespace RemotePartyFinderReborn;

internal readonly record struct IngestRoute(string Path)
{
    public override string ToString() => Path;
}

internal readonly record struct IngestEndpoint(string Url, IngestRoute Route)
{
    public override string ToString() => Url;
}

internal static class IngestRoutes
{
    internal static readonly IngestRoute ListingsMultiple = new("/contribute/multiple");
    internal static readonly IngestRoute Players = new("/contribute/players");
    internal static readonly IngestRoute CharacterIdentity = new("/contribute/character-identity");
    internal static readonly IngestRoute Detail = new("/contribute/detail");
    internal static readonly IngestRoute DetailBatch = new("/contribute/detail/batch");
    internal static readonly IngestRoute FflogsJobs = new("/contribute/fflogs/jobs");
    internal static readonly IngestRoute FflogsResults = new("/contribute/fflogs/results");
    internal static readonly IngestRoute FflogsLeasesAbandon = new("/contribute/fflogs/leases/abandon");
}
