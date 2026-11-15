using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class TomestoneEncounterMappingTests
{
    [Theory]
    [InlineData(59u, 1073u, "stormblood", "ultimates", "the-unending-coil-of-bahamut-ultimate")]
    [InlineData(59u, 1074u, "stormblood", "ultimates", "the-weapons-refrain-ultimate")]
    [InlineData(59u, 1075u, "shadowbringers", "ultimates", "the-epic-of-alexander-ultimate")]
    [InlineData(59u, 1076u, "endwalker", "ultimates", "dragonsongs-reprise-ultimate")]
    [InlineData(59u, 1077u, "endwalker", "ultimates", "the-omega-protocol-ultimate")]
    [InlineData(65u, 1079u, "dawntrail", "ultimates", "futures-rewritten-ultimate")]
    [InlineData(76u, 1085u, "dawntrail", "ultimates", "dancing-mad-ultimate")]
    public void TryGetUltimateParams_returns_expected_params_for_known_ultimate(
        uint zoneId,
        uint encounterId,
        string expansion,
        string category,
        string encounter)
    {
        var found = TomestoneEncounterMapping.TryGetUltimateParams(
            zoneId,
            encounterId,
            out var encounterParams);

        Assert.True(found);
        Assert.Equal(expansion, encounterParams.Expansion);
        Assert.Equal(category, encounterParams.Category);
        Assert.Equal(encounter, encounterParams.Encounter);
    }

    [Fact]
    public void TryGetUltimateParams_returns_false_for_non_ultimate()
    {
        var found = TomestoneEncounterMapping.TryGetUltimateParams(54, 1068, out var encounterParams);

        Assert.False(found);
        Assert.Null(encounterParams);
    }

    [Theory]
    [InlineData(73u, 101u, "vamp-fatale")]
    [InlineData(73u, 102u, "red-hot-deep-blue")]
    [InlineData(73u, 103u, "the-tyrant")]
    [InlineData(73u, 104u, "lindwurm")]
    [InlineData(73u, 105u, "lindwurm-ii")]
    public void TryGetProgressionTarget_returns_boss_percent_target_for_current_savage(
        uint zoneId,
        uint encounterId,
        string encounter)
    {
        var found = TomestoneEncounterMapping.TryGetProgressionTarget(
            zoneId,
            encounterId,
            out var encounterParams);

        Assert.True(found);
        Assert.Equal("dawntrail", encounterParams.Expansion);
        Assert.Equal("aac-heavyweight-savage", encounterParams.Category);
        Assert.Equal(encounter, encounterParams.Encounter);
        Assert.Equal(TomestoneProgressKind.BossPercentage, encounterParams.ProgressKind);
        Assert.NotEmpty(encounterParams.TargetCanonicalNames);
    }

    [Fact]
    public void TryGetProgressionTarget_returns_target_canonical_names_for_multi_boss_encounter()
    {
        var found = TomestoneEncounterMapping.TryGetProgressionTarget(73, 105, out var encounterParams);

        Assert.True(found);
        Assert.Equal(["lindwurm-ii"], encounterParams.TargetCanonicalNames);
    }

    [Theory]
    [InlineData(62u, 93u, "aac-light-heavyweight-savage", "black-cat", new[] { "black-cat" })]
    [InlineData(62u, 94u, "aac-light-heavyweight-savage", "honey-b.-lovely", new[] { "honey-b.-lovely", "honey-b-lovely" })]
    [InlineData(62u, 95u, "aac-light-heavyweight-savage", "brute-bomber", new[] { "brute-bomber" })]
    [InlineData(62u, 96u, "aac-light-heavyweight-savage", "wicked-thunder", new[] { "wicked-thunder" })]
    [InlineData(68u, 97u, "aac-cruiserweight-savage", "dancing-green", new[] { "dancing-green" })]
    [InlineData(68u, 98u, "aac-cruiserweight-savage", "sugar-riot", new[] { "sugar-riot" })]
    [InlineData(68u, 99u, "aac-cruiserweight-savage", "brute-abombinator", new[] { "brute-abombinator" })]
    [InlineData(68u, 100u, "aac-cruiserweight-savage", "howling-blade", new[] { "howling-blade" })]
    [InlineData(72u, 1084u, "trials-extreme", "enuo", new[] { "enuo", "the-unmaking-extreme" })]
    [InlineData(66u, 2061u, "chaotic", "the-cloud-of-darkness-chaotic", new[] { "the-cloud-of-darkness-chaotic", "cloud-of-darkness-chaotic", "the-cloud-of-darkness", "cloud-of-darkness" })]
    public void TryGetProgressionTarget_supports_other_server_savage_extreme_and_chaotic_targets(
        uint zoneId,
        uint encounterId,
        string category,
        string encounter,
        string[] expectedCanonicals)
    {
        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var encounterParams));
        Assert.Equal("dawntrail", encounterParams.Expansion);
        Assert.Equal(category, encounterParams.Category);
        Assert.Equal(encounter, encounterParams.Encounter);
        Assert.Equal(TomestoneProgressKind.BossPercentage, encounterParams.ProgressKind);
        Assert.Equal(expectedCanonicals, encounterParams.TargetCanonicalNames);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTargetByEncounterId(encounterId, out var mappedZoneId, out var byId));
        Assert.Equal(zoneId, mappedZoneId);
        Assert.Equal(encounterParams, byId);
        Assert.False(TomestoneEncounterMapping.TryGetUltimateParams(zoneId, encounterId, out _));
    }

    [Fact]
    public void TryGetProgressionTarget_does_not_guess_unsupported_targets()
    {
        Assert.False(TomestoneEncounterMapping.TryGetProgressionTarget(68, 101, out var wrongZone));
        Assert.Null(wrongZone);
        Assert.False(TomestoneEncounterMapping.TryGetProgressionTarget(999, 9999, out var unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void ResolveJobTargets_handles_exact_match_encounter_fallback_zero_and_unmapped()
    {
        var exactTargets = TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 73,
            EncounterId = 101,
        });
        var exact = Assert.Single(exactTargets);
        Assert.Equal(73u, exact.ZoneId);
        Assert.Equal(101u, exact.EncounterId);
        Assert.Equal("aac-heavyweight-savage", exact.EncounterParams.Category);
        Assert.Equal("vamp-fatale", exact.EncounterParams.Encounter);

        var zoneFallbackTargets = TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 0,
            EncounterId = 1079,
        });
        var fallback = Assert.Single(zoneFallbackTargets);
        Assert.Equal(65u, fallback.ZoneId);
        Assert.Equal(1079u, fallback.EncounterId);

        Assert.Empty(TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 73,
            EncounterId = 0,
        }));
        Assert.Empty(TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 999,
            EncounterId = 9999,
        }));
    }

    [Fact]
    public void ResolveJobTargets_resolves_split_boss_secondary_encounter_in_order_and_deduplicates()
    {
        var splitBossTargets = TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 73,
            EncounterId = 104,
            SecondaryEncounterId = 105,
        });
        Assert.Equal(2, splitBossTargets.Count);
        Assert.Equal(104u, splitBossTargets[0].EncounterId);
        Assert.Equal("lindwurm", splitBossTargets[0].EncounterParams.Encounter);
        Assert.Equal(105u, splitBossTargets[1].EncounterId);
        Assert.Equal("lindwurm-ii", splitBossTargets[1].EncounterParams.Encounter);

        var deduplicated = TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 73,
            EncounterId = 104,
            SecondaryEncounterId = 104,
        });
        Assert.Equal(104u, Assert.Single(deduplicated).EncounterId);

        var primaryWithUnmappedSecondary = TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 73,
            EncounterId = 104,
            SecondaryEncounterId = 9999,
        });
        Assert.Equal(104u, Assert.Single(primaryWithUnmappedSecondary).EncounterId);

        var fallbackBoth = TomestoneEncounterMapping.ResolveJobTargets(new TomestoneJob
        {
            ZoneId = 0,
            EncounterId = 104,
            SecondaryEncounterId = 105,
        });
        Assert.Equal(2, fallbackBoth.Count);
        Assert.All(fallbackBoth, t => Assert.Equal(73u, t.ZoneId));
    }
}
