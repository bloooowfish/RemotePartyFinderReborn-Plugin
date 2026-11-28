#nullable enable

using System;

namespace RemotePartyFinderReborn;

/// <summary>
/// Builds canonical FFLogs and Tomestone character profile URLs from PF member/leader identities.
/// </summary>
internal static class PfExternalLinks {
    internal static bool TryBuildFFLogsCharacterUrl(
        string? name,
        string? homeWorldName,
        ushort homeWorldId,
        string? dataCenterName,
        out string url
    ) {
        url = string.Empty;
        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedWorld = (homeWorldName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || string.IsNullOrWhiteSpace(trimmedWorld)) {
            return false;
        }

        string? region = null;
        if (homeWorldId != 0 && FFLogsWorkerRegionResolver.TryMapWorldId(homeWorldId, out var worldRegion)) {
            region = worldRegion;
        } else if (FFLogsWorkerRegionResolver.TryMapDataCenterName(dataCenterName, out var dcRegion)) {
            region = dcRegion;
        }

        if (string.IsNullOrWhiteSpace(region)) {
            return false;
        }

        url = $"https://www.fflogs.com/character/{region.ToLowerInvariant()}/{Uri.EscapeDataString(trimmedWorld.ToLowerInvariant())}/{Uri.EscapeDataString(trimmedName)}";
        return true;
    }

    internal static bool TryBuildTomestoneCharacterUrl(
        string? name,
        string? homeWorldName,
        out string url
    ) {
        url = string.Empty;
        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedWorld = (homeWorldName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || string.IsNullOrWhiteSpace(trimmedWorld)) {
            return false;
        }

        url = $"https://tomestone.gg/character-name/{Uri.EscapeDataString(trimmedWorld)}/{Uri.EscapeDataString(trimmedName)}";
        return true;
    }
}
