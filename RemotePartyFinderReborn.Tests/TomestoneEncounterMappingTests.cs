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
    [InlineData(62u, 93u, "aac-light-heavyweight-savage", "black-cat")]
    [InlineData(62u, 94u, "aac-light-heavyweight-savage", "honey-b-lovely")]
    [InlineData(62u, 95u, "aac-light-heavyweight-savage", "brute-bomber")]
    [InlineData(62u, 96u, "aac-light-heavyweight-savage", "wicked-thunder")]
    [InlineData(68u, 97u, "aac-cruiserweight-savage", "dancing-green")]
    [InlineData(68u, 98u, "aac-cruiserweight-savage", "sugar-riot")]
    [InlineData(68u, 99u, "aac-cruiserweight-savage", "brute-abombinator")]
    [InlineData(68u, 100u, "aac-cruiserweight-savage", "howling-blade")]
    [InlineData(72u, 1084u, "trials-extreme", "enuo")]
    public void TryGetProgressionTarget_supports_other_server_savage_and_extreme_targets(
        uint zoneId,
        uint encounterId,
        string category,
        string encounter)
    {
        Assert.True(TomestoneEncounterMapping.TryGetProgressionTarget(zoneId, encounterId, out var encounterParams));
        Assert.Equal("dawntrail", encounterParams.Expansion);
        Assert.Equal(category, encounterParams.Category);
        Assert.Equal(encounter, encounterParams.Encounter);
        Assert.Equal(TomestoneProgressKind.BossPercentage, encounterParams.ProgressKind);
        Assert.Equal([encounter], encounterParams.TargetCanonicalNames);

        Assert.True(TomestoneEncounterMapping.TryGetProgressionTargetByEncounterId(encounterId, out var mappedZoneId, out var byId));
        Assert.Equal(zoneId, mappedZoneId);
        Assert.Equal(encounterParams, byId);
        Assert.False(TomestoneEncounterMapping.TryGetUltimateParams(zoneId, encounterId, out _));
    }

    [Fact]
    public void TryGetProgressionTarget_does_not_guess_unsupported_targets()
    {
        Assert.False(TomestoneEncounterMapping.TryGetProgressionTarget(66, 2061, out var chaotic));
        Assert.Null(chaotic);
        Assert.False(TomestoneEncounterMapping.TryGetProgressionTarget(68, 101, out var wrongZone));
        Assert.Null(wrongZone);
    }
}
