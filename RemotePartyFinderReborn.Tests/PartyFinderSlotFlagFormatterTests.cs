using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class PartyFinderSlotFlagFormatterTests {
    static PartyFinderSlotFlagFormatterTests() {
        DalamudAssemblyResolver.Register();
    }

    [Theory]
    [InlineData("0x000000000420050A", "Tank: GLD/MRD/PLD/WAR/DRK/GNB")]
    [InlineData("0x0000000020422040", "Healer: CNJ/WHM/SCH/AST/SGE")]
    [InlineData("0x00000000D99DDAB4", "DPS: PGL/LNC/ARC/THM/MNK/DRG/BRD/BLM/ACN/SMN/ROG/NIN/MCH/SAM/RDM/DNC/RPR/VPR/PCT")]
    public void FormatAcceptingJobs_groups_common_role_masks(string rawSlotFlag, string expected) {
        Assert.Equal(expected, PartyFinderSlotFlagFormatter.FormatAcceptingJobs(rawSlotFlag));
    }

    [Fact]
    public void FormatAcceptingJobs_returns_none_for_empty_mask() {
        Assert.Equal("None", PartyFinderSlotFlagFormatter.FormatAcceptingJobs("0x0000000000000000"));
    }

    [Fact]
    public void FormatAcceptingJobs_ignores_non_job_bits_without_hiding_known_jobs() {
        Assert.Equal(
            "Tank: PLD",
            PartyFinderSlotFlagFormatter.FormatAcceptingJobs("0x0000000100000101"));
    }

    [Fact]
    public void FormatAcceptingJobs_returns_none_when_mask_has_only_non_job_bits() {
        Assert.Equal("None", PartyFinderSlotFlagFormatter.FormatAcceptingJobs("0x0000000100000001"));
    }

    [Fact]
    public void FormatAcceptingJobs_reports_invalid_input() {
        Assert.Equal("Invalid", PartyFinderSlotFlagFormatter.FormatAcceptingJobs("not-a-flag"));
    }
}
