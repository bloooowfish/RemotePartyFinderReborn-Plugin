#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace RemotePartyFinderReborn;

internal readonly record struct PfCategoryTabDef(
    string Id,
    string Label,
    DutyCategory? Category,
    bool IsOther,
    uint IconId);

internal readonly record struct PfTextureUvRect(Vector2 Uv0, Vector2 Uv1) {
    internal static PfTextureUvRect FromPixels(float texW, float texH, float x, float y, float w, float h)
        => new(new Vector2(x / texW, y / texH), new Vector2((x + w) / texW, (y + h) / texH));
}

/// <summary>
/// Loads game-original category, job, and Party Finder status textures through Dalamud's texture provider
/// alongside cached Lumina lookups for duties and worlds.
/// </summary>
internal sealed class PfIconCatalog {
    internal const string LfgTexturePath = "ui/uld/LFG_hr1.tex";
    internal const string PartyFinderTexturePath = "ui/uld/PartyFinder_hr1.tex";

    private const float LfgTexW = 264f;
    private const float LfgTexH = 632f;
    private const float PfTexW = 312f;
    private const float PfTexH = 356f;

    internal static readonly PfTextureUvRect UvRoleTankFrameless = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 4f, 216f, 50f, 54f);
    internal static readonly PfTextureUvRect UvRoleHealerFrameless = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 58f, 216f, 52f, 54f);
    internal static readonly PfTextureUvRect UvRoleDpsFrameless = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 114f, 216f, 52f, 54f);
    internal static readonly PfTextureUvRect UvRoleAnyFrameless = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 168f, 216f, 52f, 54f);
    internal static readonly PfTextureUvRect UvRoleCrafterFrameless = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 4f, 578f, 52f, 54f);
    internal static readonly PfTextureUvRect UvRoleGathererFrameless = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 58f, 578f, 52f, 54f);

    internal static readonly PfTextureUvRect UvStatusWarning = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 220f, 16f, 32f, 38f);
    internal static readonly PfTextureUvRect UvStatusGoldCheck = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 218f, 58f, 36f, 36f);
    internal static readonly PfTextureUvRect UvStatusWhiteCheck = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 224f, 100f, 32f, 32f);
    internal static readonly PfTextureUvRect UvStatusCannotJoin = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 4f, 272f, 50f, 52f);
    internal static readonly PfTextureUvRect UvStatusCrossedSwords = PfTextureUvRect.FromPixels(LfgTexW, LfgTexH, 180f, 452f, 48f, 48f);

    internal static readonly PfTextureUvRect UvPrivateLock = PfTextureUvRect.FromPixels(PfTexW, PfTexH, 144f, 298f, 40f, 44f);
    internal static readonly PfTextureUvRect UvFilterSearch = PfTextureUvRect.FromPixels(PfTexW, PfTexH, 79f, 234f, 59f, 60f);

    internal static readonly IReadOnlyList<PfCategoryTabDef> CategoryTabs = [
        new("all", "All", null, false, 61845),
        new("roulette", "Duty Roulette", DutyCategory.Roulette, false, 61807),
        new("dungeons", "Dungeons", DutyCategory.Dungeons, false, 61801),
        new("guildhests", "Guildhests", DutyCategory.GuildQuests, false, 61803),
        new("trials", "Trials", DutyCategory.Trials, false, 61804),
        new("raids", "Raids", DutyCategory.Raids, false, 61802),
        new("highend", "High-end Duty", DutyCategory.HighEndDuty, false, 61832),
        new("pvp", "PvP", DutyCategory.PvP, false, 61806),
        new("goldsaucer", "Gold Saucer", DutyCategory.GoldSaucer, false, 61820),
        new("fates", "FATEs", DutyCategory.FATEs, false, 61809),
        new("treasure", "Treasure Hunt", DutyCategory.TreasureHunts, false, 61808),
        new("hunt", "The Hunt", DutyCategory.TheHunt, false, 61819),
        new("gathering", "Gathering Forays", DutyCategory.GatheringForays, false, 61815),
        new("deep", "Deep Dungeons", DutyCategory.DeepDungeons, false, 61824),
        new("field", "Field Operations", DutyCategory.FieldOperations, false, 61837),
        new("vc", "V&C Dungeon", DutyCategory.VCDungeonFinder, false, 61846),
        new("other", "Other", DutyCategory.None, true, 61845),
    ];

    private readonly ITextureProvider? _textureProvider;
    private readonly IDataManager? _dataManager;
    private readonly ConcurrentDictionary<uint, string> _dutyNameCache = new();
    private readonly ConcurrentDictionary<uint, ushort> _dutySortKeyCache = new();
    private readonly ConcurrentDictionary<ushort, string> _worldNameCache = new();
    private readonly ConcurrentDictionary<ushort, string> _dataCenterNameCache = new();
    private readonly ConcurrentDictionary<uint, IReadOnlyList<string>> _regionDataCentersCache = new();
    private readonly ConcurrentDictionary<uint, string> _jobAbbrCache = new();
    private readonly ConcurrentDictionary<uint, string> _addonTextCache = new();

    internal PfUiLanguage UiLanguage { get; }

    internal PfIconCatalog() : this(PfUiLanguage.English) {
    }

    internal PfIconCatalog(PfUiLanguage uiLanguage) {
        UiLanguage = uiLanguage;
    }

    internal PfIconCatalog(ITextureProvider? textureProvider, IDataManager? dataManager) {
        _textureProvider = textureProvider;
        _dataManager = dataManager;
        UiLanguage = MapClientLanguage(dataManager?.Language);
    }

    internal static PfUiLanguage MapClientLanguage(ClientLanguage? language) => language switch {
        ClientLanguage.Japanese => PfUiLanguage.Japanese,
        ClientLanguage.German => PfUiLanguage.German,
        ClientLanguage.French => PfUiLanguage.French,
        _ => PfUiLanguage.English,
    };

    internal static uint GetCategoryIconId(DutyCategory category) {
        if ((category & DutyCategory.HighEndDuty) == DutyCategory.HighEndDuty) return 61832;
        if ((category & DutyCategory.Raids) == DutyCategory.Raids) return 61802;
        if ((category & DutyCategory.Trials) == DutyCategory.Trials) return 61804;
        if ((category & DutyCategory.Dungeons) == DutyCategory.Dungeons) return 61801;
        if ((category & DutyCategory.Roulette) == DutyCategory.Roulette) return 61807;
        if ((category & DutyCategory.GuildQuests) == DutyCategory.GuildQuests) return 61803;
        if ((category & DutyCategory.PvP) == DutyCategory.PvP) return 61806;
        if ((category & DutyCategory.GoldSaucer) == DutyCategory.GoldSaucer) return 61820;
        if ((category & DutyCategory.FATEs) == DutyCategory.FATEs) return 61809;
        if ((category & DutyCategory.TreasureHunts) == DutyCategory.TreasureHunts) return 61808;
        if ((category & DutyCategory.TheHunt) == DutyCategory.TheHunt) return 61819;
        if ((category & DutyCategory.GatheringForays) == DutyCategory.GatheringForays) return 61815;
        if ((category & DutyCategory.DeepDungeons) == DutyCategory.DeepDungeons) return 61824;
        if ((category & DutyCategory.FieldOperations) == DutyCategory.FieldOperations) return 61837;
        if ((category & DutyCategory.VCDungeonFinder) == DutyCategory.VCDungeonFinder) return 61846;
        return 61845;
    }

    internal static int GetNativeCategorySortIndex(DutyCategory category) {
        if ((category & DutyCategory.Roulette) == DutyCategory.Roulette) return 1;
        if ((category & DutyCategory.Dungeons) == DutyCategory.Dungeons) return 2;
        if ((category & DutyCategory.GuildQuests) == DutyCategory.GuildQuests) return 3;
        if ((category & DutyCategory.Trials) == DutyCategory.Trials) return 4;
        if ((category & DutyCategory.Raids) == DutyCategory.Raids) return 5;
        if ((category & DutyCategory.HighEndDuty) == DutyCategory.HighEndDuty) return 6;
        if ((category & DutyCategory.PvP) == DutyCategory.PvP) return 7;
        if ((category & DutyCategory.GoldSaucer) == DutyCategory.GoldSaucer) return 8;
        if ((category & DutyCategory.FATEs) == DutyCategory.FATEs) return 9;
        if ((category & DutyCategory.TreasureHunts) == DutyCategory.TreasureHunts) return 10;
        if ((category & DutyCategory.TheHunt) == DutyCategory.TheHunt) return 11;
        if ((category & DutyCategory.GatheringForays) == DutyCategory.GatheringForays) return 12;
        if ((category & DutyCategory.DeepDungeons) == DutyCategory.DeepDungeons) return 13;
        if ((category & DutyCategory.FieldOperations) == DutyCategory.FieldOperations) return 14;
        if ((category & DutyCategory.VCDungeonFinder) == DutyCategory.VCDungeonFinder) return 15;
        return 16;
    }

    internal static uint GetJobIconId(byte jobId) {
        return jobId is >= 1 and <= 42 ? (uint)(62100 + jobId) : 0u;
    }

    internal static PfTextureUvRect GetFramelessRoleUv(PartyFinderSlotFlagFormatter.JobRoleFlags roles) {
        var isTank = (roles & PartyFinderSlotFlagFormatter.JobRoleFlags.Tank) != 0;
        var isHealer = (roles & PartyFinderSlotFlagFormatter.JobRoleFlags.Healer) != 0;
        var isDps = (roles & PartyFinderSlotFlagFormatter.JobRoleFlags.Dps) != 0;

        return (isTank, isHealer, isDps) switch {
            (true, false, false) => UvRoleTankFrameless,
            (false, true, false) => UvRoleHealerFrameless,
            (false, false, true) => UvRoleDpsFrameless,
            _ => UvRoleAnyFrameless,
        };
    }

    internal string ResolveDutyName(uint dutyId, DutyType dutyType, string? fallback = null) {
        if (!string.IsNullOrWhiteSpace(fallback)) {
            return fallback;
        }

        if (dutyId == 0) {
            return ResolveAddonText(2279, PfUiText.NoneSpecified(UiLanguage));
        }

        return _dutyNameCache.GetOrAdd(dutyId, id => {
            if (_dataManager == null) {
                return $"Duty #{id}";
            }

            try {
                if (dutyType == DutyType.Normal) {
                    var sheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
                    if (sheet != null && sheet.TryGetRow(id, out var row)) {
                        var name = row.Name.ExtractText();
                        if (!string.IsNullOrWhiteSpace(name)) {
                            return name;
                        }
                    }
                }
            } catch {
                // Fall back cleanly if Excel sheets are unavailable.
            }

            return $"Duty #{id}";
        });
    }

    internal ushort ResolveDutySortKey(uint dutyId, DutyType dutyType) {
        if (dutyId == 0 || dutyType != DutyType.Normal || _dataManager == null) {
            return 0;
        }

        return _dutySortKeyCache.GetOrAdd(dutyId, id => {
            try {
                var sheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
                if (sheet != null && sheet.TryGetRow(id, out var row)) {
                    return row.SortKey;
                }
            } catch {
                // Fall back cleanly if Excel sheets are unavailable.
            }

            return 0;
        });
    }

    internal string ResolveWorldName(ushort worldId, string? fallback = null) {
        if (!string.IsNullOrWhiteSpace(fallback)) {
            return fallback;
        }

        if (worldId == 0) {
            return string.Empty;
        }

        return _worldNameCache.GetOrAdd(worldId, id => {
            if (_dataManager == null) {
                return $"World#{id}";
            }

            try {
                var sheet = _dataManager.GetExcelSheet<World>();
                if (sheet != null && sheet.TryGetRow(id, out var row)) {
                    var name = row.Name.ExtractText();
                    if (!string.IsNullOrWhiteSpace(name)) {
                        return name;
                    }
                }
            } catch {
                // Fall back cleanly if Excel sheets are unavailable.
            }

            return $"World#{id}";
        });
    }

    internal string ResolveDataCenterName(ushort worldId, string? fallback = null) {
        if (!string.IsNullOrWhiteSpace(fallback)) {
            return fallback;
        }

        if (worldId == 0) {
            return string.Empty;
        }

        return _dataCenterNameCache.GetOrAdd(worldId, id => {
            if (_dataManager == null) {
                return string.Empty;
            }

            try {
                var sheet = _dataManager.GetExcelSheet<World>();
                if (sheet != null && sheet.TryGetRow(id, out var row)) {
                    var dcName = row.DataCenter.ValueNullable?.Name.ExtractText();
                    if (!string.IsNullOrWhiteSpace(dcName)) {
                        return dcName;
                    }
                }
            } catch {
                // Fall back cleanly if Excel sheets are unavailable.
            }

            return string.Empty;
        });
    }

    private static readonly IReadOnlyList<string> JpRegionDataCenters = ["Elemental", "Gaia", "Mana", "Meteor"];
    private static readonly IReadOnlyList<string> NaRegionDataCenters = ["Aether", "Crystal", "Dynamis", "Primal"];
    private static readonly IReadOnlyList<string> EuRegionDataCenters = ["Chaos", "Light", "Shadow"];
    private static readonly IReadOnlyList<string> OcRegionDataCenters = ["Materia"];

    internal IReadOnlyList<string> GetRegionDataCenters(ushort currentWorldId) {
        if (currentWorldId == 0) {
            return Array.Empty<string>();
        }

        uint? resolvedRegionId = null;
        if (_dataManager != null) {
            try {
                var sheet = _dataManager.GetExcelSheet<World>();
                if (sheet != null && sheet.TryGetRow(currentWorldId, out var currentRow)) {
                    resolvedRegionId = currentRow.DataCenter.ValueNullable?.Region.RowId;
                    if (resolvedRegionId is > 0) {
                        var regionId = resolvedRegionId.Value;
                        var cached = _regionDataCentersCache.GetOrAdd(regionId, rid => {
                            var dcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var row in sheet) {
                                if (!row.IsPublic) {
                                    continue;
                                }

                                if (row.DataCenter.ValueNullable is not { } dc || dc.Region.RowId != rid) {
                                    continue;
                                }

                                var dcName = dc.Name.ExtractText();
                                if (!string.IsNullOrWhiteSpace(dcName)) {
                                    dcs.Add(dcName.Trim());
                                }
                            }

                            return dcs.Count > 0
                                ? dcs.OrderBy(static dc => dc, StringComparer.OrdinalIgnoreCase).ToArray()
                                : Array.Empty<string>();
                        });

                        if (cached.Count > 0) {
                            return cached;
                        }
                    }
                }
            } catch {
                // Fall back cleanly to static region DC lists if Excel sheets are unavailable.
            }
        }

        if (FFLogsWorkerRegionResolver.TryMapWorldId(currentWorldId, out var worldRegion)) {
            return GetStaticRegionDataCenters(worldRegion);
        }

        if (resolvedRegionId is > 0 && FFLogsWorkerRegionResolver.TryMapDataCenterRegionId(resolvedRegionId.Value, out var regionFromId)) {
            return GetStaticRegionDataCenters(regionFromId);
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> GetStaticRegionDataCenters(string region) => region switch {
        "JP" => JpRegionDataCenters,
        "NA" => NaRegionDataCenters,
        "EU" => EuRegionDataCenters,
        "OC" => OcRegionDataCenters,
        _ => Array.Empty<string>(),
    };

    internal string ResolveJobAbbreviation(byte jobId) {
        if (jobId == 0) {
            return "--";
        }

        return _jobAbbrCache.GetOrAdd(jobId, id => {
            if (_dataManager != null) {
                try {
                    var sheet = _dataManager.GetExcelSheet<ClassJob>();
                    if (sheet != null && sheet.TryGetRow(id, out var row)) {
                        var abbr = row.Abbreviation.ExtractText();
                        if (!string.IsNullOrWhiteSpace(abbr)) {
                            return abbr;
                        }
                    }
                } catch {
                    // Use static fallback below.
                }
            }

            return PartyFinderSlotFlagFormatter.AbbreviationForClassJobId(id);
        });
    }

    internal string ResolveAddonText(uint addonRowId, string fallback = "") {
        if (addonRowId == 0) {
            return fallback;
        }

        return _addonTextCache.GetOrAdd(addonRowId, id => {
            if (_dataManager != null) {
                try {
                    var sheet = _dataManager.GetExcelSheet<Addon>();
                    if (sheet != null && sheet.TryGetRow(id, out var row)) {
                        var text = row.Text.ExtractText().Trim();
                        if (!string.IsNullOrEmpty(text)) {
                            return text;
                        }
                    }
                } catch {
                    // Fall back cleanly if Addon sheet is unavailable.
                }
            }

            return fallback;
        });
    }

    internal bool TryDrawGameIcon(uint iconId, Vector2 size) {
        if (_textureProvider == null || iconId == 0) {
            return false;
        }

        var shared = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId, hiRes: true));
        if (shared.TryGetWrap(out var wrap, out _)) {
            ImGui.Image(wrap.Handle, size);
            return true;
        }

        return false;
    }

    internal bool TryDrawGameIconAt(ImDrawListPtr drawList, uint iconId, Vector2 min, Vector2 max) {
        if (_textureProvider == null || iconId == 0) {
            return false;
        }

        var shared = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId, hiRes: true));
        if (shared.TryGetWrap(out var wrap, out _)) {
            drawList.AddImage(wrap.Handle, min, max);
            return true;
        }

        return false;
    }

    internal bool TryDrawAtlasSprite(string texPath, PfTextureUvRect uv, Vector2 size, Vector4? tint = null) {
        if (_textureProvider == null) {
            return false;
        }

        var shared = _textureProvider.GetFromGame(texPath);
        if (shared.TryGetWrap(out var wrap, out _)) {
            if (tint.HasValue) {
                ImGui.Image(wrap.Handle, size, uv.Uv0, uv.Uv1, tint.Value);
            } else {
                ImGui.Image(wrap.Handle, size, uv.Uv0, uv.Uv1);
            }
            return true;
        }

        return false;
    }

    internal bool TryDrawAtlasSpriteAt(ImDrawListPtr drawList, string texPath, PfTextureUvRect uv, Vector2 min, Vector2 max) {
        if (_textureProvider == null) {
            return false;
        }

        var shared = _textureProvider.GetFromGame(texPath);
        if (shared.TryGetWrap(out var wrap, out _)) {
            drawList.AddImage(wrap.Handle, min, max, uv.Uv0, uv.Uv1);
            return true;
        }

        return false;
    }
}
