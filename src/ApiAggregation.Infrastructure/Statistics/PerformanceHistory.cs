using System.Collections.Immutable;

namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Retains the current UTC minute and the five preceding minutes on the statistics worker.
/// </summary>
internal sealed class PerformanceHistory
{
    private const int RecentMinutes = 5;
    private readonly MinuteBucket[] _buckets = new MinuteBucket[RecentMinutes + 1];
    private Int128 _totalTicks;

    /// <summary>
    /// Adds a sample by completion time while preserving newer data in a reused slot.
    /// </summary>
    internal void Record(DateTimeOffset completedAt, long durationTicks)
    {
        _totalTicks += durationTicks;
        var minute = GetMinute(completedAt);
        ref var bucket = ref _buckets[(int)(minute % _buckets.Length)];

        if (bucket.Minute > minute)
            return;

        if (bucket.Minute != minute)
            bucket = new MinuteBucket(minute, 0, 0);

        bucket = new MinuteBucket(minute, bucket.Count + 1, bucket.DurationTicks + durationTicks);
    }

    /// <summary>
    /// Copies the retained buckets and captures the matching lifetime totals.
    /// </summary>
    internal Snapshot Capture(string source, long totalCount)
        => new(source, totalCount, _totalTicks, ImmutableArray.CreateRange(_buckets));

    private static long GetMinute(DateTimeOffset time) => time.UtcTicks / TimeSpan.TicksPerMinute;

    /// <summary>
    /// Holds the published history for one provider and calculates windows as of a given time.
    /// </summary>
    internal readonly record struct Snapshot(string Source, long TotalCount, Int128 DurationTicks, ImmutableArray<MinuteBucket> Buckets)
    {
        /// <summary>
        /// Splits the sample into the last five complete UTC minutes and the preceding baseline.
        /// </summary>
        internal ProviderPerformanceWindow GetWindow(DateTimeOffset now)
        {
            var currentMinute = GetMinute(now);
            var recentStart = currentMinute - RecentMinutes;
            var baselineCount = TotalCount;
            var baselineTicks = DurationTicks;
            long recentCount = 0;
            Int128 recentTicks = 0;

            foreach (var bucket in Buckets)
            {
                if (bucket.Minute < recentStart)
                    continue;

                baselineCount -= bucket.Count;
                baselineTicks -= bucket.DurationTicks;

                if (bucket.Minute >= currentMinute)
                    continue;

                recentCount += bucket.Count;
                recentTicks += bucket.DurationTicks;
            }

            return new ProviderPerformanceWindow(Source, recentCount, recentTicks, baselineCount, baselineTicks);
        }
    }

    /// <summary>
    /// Holds the sample count and total duration for one UTC minute.
    /// </summary>
    internal readonly record struct MinuteBucket(long Minute, long Count, Int128 DurationTicks);
}
