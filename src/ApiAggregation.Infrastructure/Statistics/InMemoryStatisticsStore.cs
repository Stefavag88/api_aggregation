using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Application.Features.Statistics;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace ApiAggregation.Infrastructure.Statistics;

public sealed class InMemoryStatisticsStore(
    IEnumerable<ProviderDescriptor> providers,
    TimeProvider timeProvider) : IStatisticsReader, IRequestStatisticsRecorder
{
    private readonly DateTimeOffset startedAt = timeProvider.GetUtcNow();
    private readonly FrozenDictionary<string, ProviderAccumulator> accumulators = providers.ToFrozenDictionary(
        provider => provider.Name,
        provider => new ProviderAccumulator(provider.Name),
        StringComparer.Ordinal);

    public void Record(AttemptObservation observation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(observation.Duration, TimeSpan.Zero);

        if (!accumulators.TryGetValue(observation.Source, out var accumulator))
            throw new ArgumentException("Unknown statistics provider.", nameof(observation));

        accumulator.Record(observation);
    }

    public StatisticsSnapshot GetSnapshot()
    {
        ImmutableArray<ProviderStatistics> providers = [.. accumulators.Values
            .OrderBy(accumulator => accumulator.Source, StringComparer.Ordinal)
            .Select(accumulator => accumulator.Snapshot())];

        return new StatisticsSnapshot(startedAt, timeProvider.GetUtcNow(), providers);
    }

    private static double? Mean(double sumMilliseconds, long count)
        => count == 0 ? null : sumMilliseconds / count;

    private sealed class ProviderAccumulator(string source)
    {
        private readonly Lock gate = new();
        private readonly BucketAccumulator fast = new();
        private readonly BucketAccumulator average = new();
        private readonly BucketAccumulator slow = new();
        private long successes;
        private long failures;
        private long cancellations;

        public string Source { get; } = source;

        public void Record(AttemptObservation observation)
        {
            var milliseconds = observation.Duration.TotalMilliseconds;

            lock (gate)
            {
                switch (observation.Outcome)
                {
                    case AttemptOutcome.Success:
                        successes++;
                        break;
                    case AttemptOutcome.Failure:
                        failures++;
                        break;
                    case AttemptOutcome.Canceled:
                        cancellations++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(observation), "Unknown attempt outcome.");
                }

                var bucket = milliseconds switch
                {
                    < 100 => fast,
                    <= 200 => average,
                    _ => slow
                };
                bucket.Add(milliseconds);
            }
        }

        public ProviderStatistics Snapshot()
        {
            lock (gate)
            {
                var total = successes + failures + cancellations;
                var sum = fast.SumMilliseconds + average.SumMilliseconds + slow.SumMilliseconds;

                return new ProviderStatistics(
                    Source,
                    total,
                    successes,
                    failures,
                    cancellations,
                    Mean(sum, total),
                    fast.Snapshot(),
                    average.Snapshot(),
                    slow.Snapshot());
            }
        }
    }

    private sealed class BucketAccumulator
    {
        public long Count { get; private set; }
        public double SumMilliseconds { get; private set; }

        public void Add(double milliseconds)
        {
            Count++;
            SumMilliseconds += milliseconds;
        }

        public BucketStatistics Snapshot() => new(Count, Mean(SumMilliseconds, Count));
    }
}
