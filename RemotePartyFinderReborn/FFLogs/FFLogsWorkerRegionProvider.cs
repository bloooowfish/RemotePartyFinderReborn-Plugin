using System;
using System.Diagnostics.CodeAnalysis;
using Dalamud.Plugin.Services;

#nullable enable

namespace RemotePartyFinderReborn;

internal interface IFFLogsWorkerRegionProvider
{
    bool TryGetWorkerRegion(out string region);
}

internal interface IFFLogsWorkerRegionSnapshotState
{
    bool HasObservedSnapshot { get; }
}

internal sealed class CachedFFLogsWorkerRegionProvider(Action<string>? warningLog = null) :
    IFFLogsWorkerRegionProvider,
    IFFLogsWorkerRegionSnapshotState
{
    private readonly object _gate = new();
    private string? _cachedRegion;
    private string _unavailableReason = "local player is not available.";
    private bool _hasObservedSnapshot;
    private bool _loggedUnavailable;

    public bool HasObservedSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _hasObservedSnapshot;
            }
        }
    }

    public bool TryGetWorkerRegion(out string region)
    {
        string reason;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_cachedRegion))
            {
                region = _cachedRegion;
                return true;
            }

            region = string.Empty;
            if (!_hasObservedSnapshot)
            {
                return false;
            }

            reason = _unavailableReason;
            if (_loggedUnavailable)
            {
                return false;
            }

            _loggedUnavailable = true;
        }

        warningLog?.Invoke($"FFLogs worker region unavailable: {reason}");
        return false;
    }

    public void UpdateCurrentWorld(ushort? worldId)
    {
        if (worldId is null or 0)
        {
            SetUnavailable("local player is not available.");
            return;
        }

        if (FFLogsWorkerRegionResolver.TryMapWorldId(worldId.Value, out var resolvedRegion))
        {
            lock (_gate)
            {
                _cachedRegion = resolvedRegion;
                _unavailableReason = string.Empty;
                _hasObservedSnapshot = true;
                _loggedUnavailable = false;
            }
            return;
        }

        SetUnavailable($"current world id {worldId.Value} is not mapped.");
    }

    private void SetUnavailable(string reason)
    {
        lock (_gate)
        {
            _cachedRegion = null;
            _unavailableReason = reason;
            _hasObservedSnapshot = true;
        }
    }
}

internal sealed class DalamudFFLogsWorkerRegionProvider :
    IFFLogsWorkerRegionProvider,
    IFFLogsWorkerRegionSnapshotState,
    IDisposable
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly CachedFFLogsWorkerRegionProvider _cache;

    public DalamudFFLogsWorkerRegionProvider(
        IFramework framework,
        IObjectTable objectTable,
        Action<string>? warningLog = null)
    {
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));
        _objectTable = objectTable ?? throw new ArgumentNullException(nameof(objectTable));
        _cache = new CachedFFLogsWorkerRegionProvider(warningLog);
        _framework.Update += OnFrameworkUpdate;
    }

    public bool TryGetWorkerRegion(out string region)
        => _cache.TryGetWorkerRegion(out region);

    public bool HasObservedSnapshot
        => _cache.HasObservedSnapshot;

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        RefreshCurrentWorld();
    }

    private void RefreshCurrentWorld()
    {
        var localPlayer = _objectTable.LocalPlayer;
        _cache.UpdateCurrentWorld(localPlayer == null ? null : (ushort)localPlayer.CurrentWorld.RowId);
    }
}

internal static class FFLogsWorkerRegionResolver
{
    public static bool TryMapDataCenterName(string? dataCenterName, [NotNullWhen(true)] out string? region)
    {
        region = dataCenterName?.Trim() switch
        {
            "Elemental" or "Gaia" or "Mana" or "Meteor" => "JP",
            "Aether" or "Primal" or "Crystal" or "Dynamis" => "NA",
            "Chaos" or "Light" => "EU",
            "Materia" => "OC",
            _ => null,
        };

        return region != null;
    }

    public static bool TryMapWorldId(ushort worldId, [NotNullWhen(true)] out string? region)
    {
        region = worldId switch
        {
            // JP: Elemental, Gaia, Mana, Meteor
            23 or 24 or 28 or 29 or 30 or 31 or 32 or 43 or 44 or 45 or 46 or 47 or 48 or 49
                or 50 or 51 or 52 or 58 or 59 or 60 or 61 or 68 or 69 or 70 or 72 or 76 or 82
                or 90 or 92 or 94 or 96 or 98 => "JP",

            // NA: Aether, Primal, Crystal, Dynamis
            34 or 35 or 37 or 40 or 41 or 53 or 54 or 55 or 57 or 62 or 63 or 64 or 65 or 73
                or 74 or 75 or 77 or 78 or 79 or 81 or 91 or 93 or 95 or 99 or 404 or 405 or 406
                or 407 or 408 or 409 or 410 or 411 => "NA",

            // EU: Chaos, Light
            33 or 36 or 39 or 42 or 56 or 66 or 67 or 71 or 80 or 83 or 85 or 97 or 400 or 401
                or 402 or 403 => "EU",

            // OC: Materia
            21 or 22 or 86 or 87 or 88 => "OC",

            _ => null,
        };

        return region != null;
    }
}
