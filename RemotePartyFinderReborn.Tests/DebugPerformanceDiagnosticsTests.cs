#if DEBUG
using System.Diagnostics;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class DebugPerformanceDiagnosticsTests {
    [Fact]
    public void Drain_summary_reports_aggregates_and_resets_the_window() {
        var aggregator = new DebugPerformanceAggregator();
        aggregator.Record(DebugPerformancePath.PartySnapshotBuild, Stopwatch.Frequency / 1000, 128);
        aggregator.Record(DebugPerformancePath.PartySnapshotBuild, Stopwatch.Frequency / 100, 512);
        aggregator.Count(DebugPerformancePath.PartySnapshotSkippedNoRequest);

        var summary = aggregator.DrainSummary(TimeSpan.FromSeconds(60));
        var nextSummary = aggregator.DrainSummary(TimeSpan.FromSeconds(60));

        Assert.Contains("window=60.0s", summary);
        Assert.Contains("party_snapshot{n=2,avg_ms=5.500,p95_ms=10.000,max_ms=10.000,avg_alloc_b=320,max_alloc_b=512}", summary);
        Assert.Contains("party_skip_no_request{n=1}", summary);
        Assert.Contains("party_snapshot{n=0}", nextSummary);
        Assert.DoesNotContain("party_skip_no_request{n=1}", nextSummary);
    }

    [Fact]
    public void Duration_only_path_reports_work_units_without_cross_thread_allocation_metrics() {
        var aggregator = new DebugPerformanceAggregator();
        aggregator.Record(DebugPerformancePath.PlayerUpload, Stopwatch.Frequency * 2, 0, 7, includeAllocation: false);

        var summary = aggregator.DrainSummary(TimeSpan.FromSeconds(60));

        Assert.Contains("player_upload{n=1,units=7,avg_ms=2000.000,p95_ms=1000+,max_ms=2000.000}", summary);
        Assert.DoesNotContain("avg_alloc_b", summary);
    }
}
#endif
