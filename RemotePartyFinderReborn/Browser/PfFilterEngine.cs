#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Gui.PartyFinder.Types;

namespace RemotePartyFinderReborn;

internal readonly record struct ParsedSearchQuery(
    IReadOnlyList<string> IncludeTerms,
    IReadOnlyList<string> ExcludeTerms) {
    internal bool IsEmpty => IncludeTerms.Count == 0 && ExcludeTerms.Count == 0;
}

/// <summary>
/// Pure filtering, category counting, and sorting logic for the in-game PF browser.
/// </summary>
internal static class PfFilterEngine {
    private static readonly char[] SearchSeparators = [' ', '\t', '\r', '\n'];

    internal static IReadOnlyList<PfListingEntry> FilterAndSort(
        IEnumerable<PfListingEntry> listings,
        PfFilterState filter,
        uint? playerClassJobId,
        int? currentPageGeneration = null,
        IReadOnlyList<ulong>? nativeOrderedListingIds = null
    ) {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(filter);

        var parsedSearch = ParseSearchQuery(filter.SearchText);
        var dcFilter = (filter.SelectedDataCenter ?? string.Empty).Trim();
        var filtered = new List<PfListingEntry>();

        foreach (var entry in listings) {
            if (!filter.IncludeSampleListings && entry.IsSample) {
                continue;
            }

            if (!entry.IsSample
                && currentPageGeneration is > 0
                && entry.PageGeneration != currentPageGeneration.Value) {
                continue;
            }

            if (!entry.MatchesAreaTab(filter.AreaTab)) {
                continue;
            }

            if (!string.IsNullOrEmpty(dcFilter)
                && !string.Equals(entry.DataCenterName, dcFilter, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (!filter.MatchesCategory(entry.Category)) {
                continue;
            }

            if (filter.HighEndOnly && !entry.IsHighEnd) {
                continue;
            }

            if (filter.MyJobOnly && !PfSlotBarRenderer.HasAnyEligibleOpenSlot(entry, playerClassJobId)) {
                continue;
            }

            if (filter.BlockedDisplayMode == PfBlockedDisplayMode.Hide && entry.IsLeaderBlocked) {
                continue;
            }

            if (!parsedSearch.IsEmpty && !MatchesSearch(entry, parsedSearch)) {
                continue;
            }

            filtered.Add(entry);
        }

        Dictionary<ulong, int>? nativeOrderMap = null;
        if (filter.SortMode == PfSortMode.NativeDefault && nativeOrderedListingIds is { Count: > 0 }) {
            nativeOrderMap = new Dictionary<ulong, int>(nativeOrderedListingIds.Count);
            for (var i = 0; i < nativeOrderedListingIds.Count; i++) {
                nativeOrderMap.TryAdd(nativeOrderedListingIds[i], i);
            }
        }

        Comparison<PfListingEntry> comparison = filter.SortMode switch {
            PfSortMode.TimeRemaining => static (a, b) => {
                var c = b.SecondsRemaining.CompareTo(a.SecondsRemaining);
                return c != 0 ? c : b.Id.CompareTo(a.Id);
            },
            PfSortMode.SlotsFilled => static (a, b) => {
                var c = b.SlotsFilled.CompareTo(a.SlotsFilled);
                if (c != 0) return c;
                var t = b.SecondsRemaining.CompareTo(a.SecondsRemaining);
                return t != 0 ? t : b.Id.CompareTo(a.Id);
            },
            PfSortMode.ItemLevel => static (a, b) => {
                var c = b.MinItemLevel.CompareTo(a.MinItemLevel);
                if (c != 0) return c;
                var t = b.SecondsRemaining.CompareTo(a.SecondsRemaining);
                return t != 0 ? t : b.Id.CompareTo(a.Id);
            },
            _ => (a, b) => CompareNativeOrder(a, b, playerClassJobId, nativeOrderMap),
        };

        filtered.Sort((a, b) => {
            var blockedCmp = a.IsLeaderBlocked.CompareTo(b.IsLeaderBlocked);
            return blockedCmp != 0 ? blockedCmp : comparison(a, b);
        });
        return filtered;
    }

    internal static uint ComputeEffectiveNativeDutySortKey(DutyCategory category, ushort dutySortKey, ushort rawDuty) {
        var catIdx = PfIconCatalog.GetNativeCategorySortIndex(category);
        var baseKey = dutySortKey != 0 ? dutySortKey : rawDuty;
        if (baseKey == 0) {
            return uint.MaxValue;
        }

        return catIdx is 2 or 4 or 5 or 6 or 15
            ? (uint)(0xFFFF - baseKey)
            : baseKey;
    }

    private static int CompareNativeOrder(
        PfListingEntry a,
        PfListingEntry b,
        uint? playerClassJobId,
        Dictionary<ulong, int>? nativeOrderMap
    ) {
        if (nativeOrderMap != null) {
            var hasA = nativeOrderMap.TryGetValue(a.Id, out var idxA);
            var hasB = nativeOrderMap.TryGetValue(b.Id, out var idxB);
            if (hasA != hasB) {
                return hasA ? -1 : 1;
            }

            if (hasA) {
                var nativeCmp = idxA.CompareTo(idxB);
                if (nativeCmp != 0) {
                    return nativeCmp;
                }
            }
        }

        var unopenedCmp = a.IsDutyUnopened.CompareTo(b.IsDutyUnopened);
        if (unopenedCmp != 0) {
            return unopenedCmp;
        }

        var catCmp = PfIconCatalog.GetNativeCategorySortIndex(a.Category)
            .CompareTo(PfIconCatalog.GetNativeCategorySortIndex(b.Category));
        if (catCmp != 0) {
            return catCmp;
        }

        var dutyCmp = ComputeEffectiveNativeDutySortKey(a.Category, a.DutySortKey, a.RawDuty)
            .CompareTo(ComputeEffectiveNativeDutySortKey(b.Category, b.DutySortKey, b.RawDuty));
        if (dutyCmp != 0) {
            return dutyCmp;
        }

        var timeCmp = b.SecondsRemaining.CompareTo(a.SecondsRemaining);
        return timeCmp != 0 ? timeCmp : b.Id.CompareTo(a.Id);
    }

    internal static Dictionary<string, int> ComputeCategoryCounts(
        IEnumerable<PfListingEntry> listings,
        PfSearchAreaTab areaTab,
        bool includeSampleListings,
        string? dataCenterFilter = null,
        int? currentPageGeneration = null,
        bool hideBlocked = false
    ) {
        ArgumentNullException.ThrowIfNull(listings);

        var dcFilter = (dataCenterFilter ?? string.Empty).Trim();
        var counts = new Dictionary<string, int>(PfIconCatalog.CategoryTabs.Count, StringComparer.Ordinal);
        foreach (var tab in PfIconCatalog.CategoryTabs) {
            counts[tab.Id] = 0;
        }

        foreach (var entry in listings) {
            if (!includeSampleListings && entry.IsSample) {
                continue;
            }

            if (hideBlocked && entry.IsLeaderBlocked) {
                continue;
            }

            if (!entry.IsSample
                && currentPageGeneration is > 0
                && entry.PageGeneration != currentPageGeneration.Value) {
                continue;
            }

            if (!entry.MatchesAreaTab(areaTab)) {
                continue;
            }

            if (!string.IsNullOrEmpty(dcFilter)
                && !string.Equals(entry.DataCenterName, dcFilter, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            counts["all"]++;
            foreach (var tab in PfIconCatalog.CategoryTabs) {
                if (tab.Category is null && !tab.IsOther) {
                    continue;
                }

                if (tab.IsOther) {
                    if (entry.Category == DutyCategory.None) {
                        counts[tab.Id]++;
                    }
                    continue;
                }

                if (tab.Category is { } cat && (entry.Category & cat) == cat) {
                    counts[tab.Id]++;
                }
            }
        }

        return counts;
    }

    internal static Dictionary<PfSearchAreaTab, int> ComputeAreaCounts(
        IEnumerable<PfListingEntry> listings,
        bool includeSampleListings,
        int? currentPageGeneration = null,
        bool hideBlocked = false
    ) {
        var counts = new Dictionary<PfSearchAreaTab, int> {
            [PfSearchAreaTab.All] = 0,
            [PfSearchAreaTab.World] = 0,
            [PfSearchAreaTab.Private] = 0,
        };

        foreach (var entry in listings) {
            if (!includeSampleListings && entry.IsSample) {
                continue;
            }

            if (hideBlocked && entry.IsLeaderBlocked) {
                continue;
            }

            if (!entry.IsSample
                && currentPageGeneration is > 0
                && entry.PageGeneration != currentPageGeneration.Value) {
                continue;
            }

            if (entry.IsPrivate) {
                counts[PfSearchAreaTab.Private]++;
                continue;
            }

            counts[PfSearchAreaTab.All]++;
            if (entry.IsWorldOnly) {
                counts[PfSearchAreaTab.World]++;
            }
        }

        return counts;
    }

    internal static IReadOnlyList<string> GetAvailableDataCenters(
        IEnumerable<PfListingEntry> listings,
        bool includeSampleListings,
        int? currentPageGeneration = null,
        IReadOnlyList<string>? regionDataCenters = null
    ) {
        ArgumentNullException.ThrowIfNull(listings);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (regionDataCenters != null) {
            foreach (var dc in regionDataCenters) {
                if (!string.IsNullOrWhiteSpace(dc)) {
                    set.Add(dc.Trim());
                }
            }
        }

        foreach (var entry in listings) {
            if (!includeSampleListings && entry.IsSample) {
                continue;
            }

            if (!entry.IsSample
                && currentPageGeneration is > 0
                && entry.PageGeneration != currentPageGeneration.Value) {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.DataCenterName)) {
                set.Add(entry.DataCenterName);
            }
        }

        return set.OrderBy(static dc => dc, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string NormalizeForSearch(string? text) {
        if (string.IsNullOrEmpty(text)) {
            return string.Empty;
        }

        var nfkc = text.Normalize(NormalizationForm.FormKC);
        return string.Create(nfkc.Length, nfkc, static (span, src) => {
            for (var i = 0; i < src.Length; i++) {
                var ch = src[i];
                if (ch is >= '\u30A1' and <= '\u30F6') {
                    ch = (char)(ch - 0x60);
                } else if (ch == '\u30FD') {
                    ch = '\u309D';
                } else if (ch == '\u30FE') {
                    ch = '\u309E';
                } else {
                    ch = char.ToLowerInvariant(ch);
                }

                span[i] = ch;
            }
        });
    }

    internal static ParsedSearchQuery ParseSearchQuery(string? rawSearch) {
        var normalized = NormalizeForSearch(rawSearch);
        if (string.IsNullOrWhiteSpace(normalized)) {
            return new ParsedSearchQuery(Array.Empty<string>(), Array.Empty<string>());
        }

        var tokens = normalized.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);
        var includeTerms = new List<string>(tokens.Length);
        List<string>? excludeTerms = null;

        foreach (var token in tokens) {
            if (token[0] == '-') {
                var trimmed = token.TrimStart('-');
                if (!string.IsNullOrEmpty(trimmed) && token.Length > 1) {
                    excludeTerms ??= [];
                    excludeTerms.Add(token[1..]);
                }
                continue;
            }

            includeTerms.Add(token);
        }

        return new ParsedSearchQuery(
            includeTerms,
            excludeTerms is null ? Array.Empty<string>() : excludeTerms);
    }

    private static bool MatchesSearch(PfListingEntry entry, ParsedSearchQuery query) {
        var haystack = NormalizeForSearch(
            $"{entry.DutyName}\n{entry.Comment}\n{entry.LeaderName}\n{entry.HomeWorldName}\n{entry.DataCenterName}");

        for (var i = 0; i < query.IncludeTerms.Count; i++) {
            if (!haystack.Contains(query.IncludeTerms[i], StringComparison.Ordinal)) {
                return false;
            }
        }

        for (var i = 0; i < query.ExcludeTerms.Count; i++) {
            if (haystack.Contains(query.ExcludeTerms[i], StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }
}
