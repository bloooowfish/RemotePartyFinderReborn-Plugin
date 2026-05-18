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
    [InlineData(73u, 101u, "aac-heavyweight-m1-savage")]
    [InlineData(73u, 102u, "aac-heavyweight-m2-savage")]
    [InlineData(73u, 103u, "aac-heavyweight-m3-savage")]
    [InlineData(73u, 105u, "aac-heavyweight-m4-savage")]
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
        Assert.Equal("raids", encounterParams.Category);
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

    [Fact]
    public void TryGetProgressionTarget_does_not_treat_old_savage_as_current_savage()
    {
        var found = TomestoneEncounterMapping.TryGetProgressionTarget(68, 97, out var encounterParams);

        Assert.False(found);
        Assert.Null(encounterParams);
    }
}
