#if DEBUG
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

#nullable enable

namespace RemotePartyFinderReborn;

internal enum DebugPerformancePath {
    PartySnapshotBuild,
    PartySnapshotSkippedNoRequest,
    PartySnapshotSkippedAwaitPopulation,
    PartySnapshotEmpty,
    PartySnapshotAccepted,
    PartySnapshotRejected,
    PlayerObjectScan,
    PlayerObservationDbWrite,
    PlayerUpload,
    CharaCardPump,
    CharaCardIdentityDbProbe,
    IdentityUploadDbProbe,
    DebugScannerAcceptedTick,
    DebugScannerStateAction,
    DebugScannerCompletedRun,
    PendingDetailQueueSelect,
    PendingDetailQueueSelected,
}

/// <summary>Debug-build-only, privacy-safe rolling performance counters.</summary>
internal static class DebugPerformanceDiagnostics {
    private static readonly long SummaryIntervalTicks = Stopwatch.Frequency * 60L;
    private static readonly DebugPerformanceAggregator Aggregator = new();
    private static long _nextSummaryAt = Stopwatch.GetTimestamp() + SummaryIntervalTicks;

    internal static long Begin() => Stopwatch.GetTimestamp();

    internal static long AllocatedBytes() => GC.GetAllocatedBytesForCurrentThread();

    internal static void Record(DebugPerformancePath path, long startedAt, long allocatedBefore, long workUnits = 0) {
        Aggregator.Record(path, Stopwatch.GetTimestamp() - startedAt,
            Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore), workUnits, includeAllocation: true);
    }

    internal static void RecordDuration(DebugPerformancePath path, long startedAt, long workUnits = 0) =>
        Aggregator.Record(path, Stopwatch.GetTimestamp() - startedAt, 0, workUnits, includeAllocation: false);

    internal static void Count(DebugPerformancePath path) => Aggregator.Count(path);

    internal static void Count(DebugPerformancePath path, long workUnits) => Aggregator.Count(path, workUnits);

    internal static void TryLogSummary(Action<string>? log) {
        if (log is null) {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref _nextSummaryAt);
        if (now < next || Interlocked.CompareExchange(ref _nextSummaryAt, now + SummaryIntervalTicks, next) != next) {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(next - SummaryIntervalTicks, now);
        log(Aggregator.DrainSummary(elapsed));
    }
}

/// <summary>Thread-safe accumulator whose drain operation defines an atomic summary window.</summary>
internal sealed class DebugPerformanceAggregator {
    private static readonly string[] Names = [
        "party_snapshot", "party_skip_no_request", "party_skip_await_population", "party_empty",
        "party_accepted", "party_rejected", "player_scan", "player_db_write", "player_upload",
        "chara_pump", "chara_db_probe", "identity_db_probe", "scanner_tick", "scanner_action", "scanner_run",
        "detail_queue_select", "detail_queue_selected",
    ];
    private static readonly double[] DurationBucketUpperBoundsMs = [
        0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 250, 500, 1000, double.PositiveInfinity,
    ];
    private readonly object _gate = new();
    private readonly Counters[] _counters = CreateCounters();

    internal void Record(DebugPerformancePath path, long durationTicks, long allocatedBytes, long workUnits = 0, bool includeAllocation = true) {
        lock (_gate) {
            var counters = _counters[(int)path];
            counters.Count++;
            counters.DurationTicks += Math.Max(0, durationTicks);
            counters.MaxDurationTicks = Math.Max(counters.MaxDurationTicks, durationTicks);
            counters.AllocatedBytes += Math.Max(0, allocatedBytes);
            counters.MaxAllocatedBytes = Math.Max(counters.MaxAllocatedBytes, allocatedBytes);
            counters.WorkUnits += Math.Max(0, workUnits);
            counters.HasAllocationMetrics |= includeAllocation;
            var durationMs = Math.Max(0, durationTicks) * 1000d / Stopwatch.Frequency;
            counters.DurationHistogram[GetBucket(durationMs)]++;
        }
    }

    internal void Count(DebugPerformancePath path) {
        Count(path, 0);
    }

    internal void Count(DebugPerformancePath path, long workUnits) {
        lock (_gate) {
            var counters = _counters[(int)path];
            counters.Count++;
            counters.WorkUnits += Math.Max(0, workUnits);
        }
    }

    internal string DrainSummary(TimeSpan elapsed) {
        lock (_gate) {
            var builder = new StringBuilder("DebugPerf: window=")
                .Append(elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append('s');

            for (var index = 0; index < _counters.Length; index++) {
                var counters = _counters[index];
                builder.Append(' ').Append(Names[index]).Append("{n=").Append(counters.Count);
                if (counters.WorkUnits > 0) {
                    builder.Append(",units=").Append(counters.WorkUnits);
                }
                if (counters.Count > 0 && counters.DurationTicks != 0) {
                    builder.Append(",avg_ms=").Append((counters.DurationTicks * 1000d / Stopwatch.Frequency / counters.Count).ToString("F3", CultureInfo.InvariantCulture))
                        .Append(",p95_ms=").Append(FormatP95(counters))
                        .Append(",max_ms=").Append((counters.MaxDurationTicks * 1000d / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture));
                    if (counters.HasAllocationMetrics) {
                        builder.Append(",avg_alloc_b=").Append(counters.AllocatedBytes / counters.Count)
                            .Append(",max_alloc_b=").Append(counters.MaxAllocatedBytes);
                    }
                }

                builder.Append('}');
                counters.Reset();
            }

            return builder.ToString();
        }
    }

    private static Counters[] CreateCounters() {
        var result = new Counters[Names.Length];
        for (var index = 0; index < result.Length; index++) {
            result[index] = new Counters(DurationBucketUpperBoundsMs.Length);
        }

        return result;
    }

    private static int GetBucket(double durationMs) {
        for (var index = 0; index < DurationBucketUpperBoundsMs.Length; index++) {
            if (durationMs <= DurationBucketUpperBoundsMs[index]) {
                return index;
            }
        }

        return DurationBucketUpperBoundsMs.Length - 1;
    }

    private static double GetP95Milliseconds(Counters counters) {
        var target = (long)Math.Ceiling(counters.Count * 0.95d);
        long cumulative = 0;
        for (var index = 0; index < counters.DurationHistogram.Length; index++) {
            cumulative += counters.DurationHistogram[index];
            if (cumulative >= target) {
                return DurationBucketUpperBoundsMs[index];
            }
        }

        return DurationBucketUpperBoundsMs[^1];
    }

    private static string FormatP95(Counters counters) {
        var value = GetP95Milliseconds(counters);
        return double.IsPositiveInfinity(value) ? "1000+" : value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private sealed class Counters(int bucketCount) {
        internal long Count;
        internal long DurationTicks;
        internal long MaxDurationTicks;
        internal long AllocatedBytes;
        internal long MaxAllocatedBytes;
        internal long WorkUnits;
        internal bool HasAllocationMetrics;
        internal long[] DurationHistogram { get; } = new long[bucketCount];

        internal void Reset() {
            Count = 0;
            DurationTicks = 0;
            MaxDurationTicks = 0;
            AllocatedBytes = 0;
            MaxAllocatedBytes = 0;
            WorkUnits = 0;
            HasAllocationMetrics = false;
            Array.Clear(DurationHistogram);
        }
    }
}
#endif
