using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Application.Features.Statistics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Collects outbound attempt observations on a single worker and publishes immutable statistics snapshots.
/// </summary>
public sealed class InMemoryStatisticsStore : BackgroundService, IStatisticsReader, IRequestStatisticsRecorder
{
    private const int QueueCapacity = 4096;

    private readonly Channel<AttemptObservation> _observations = Channel.CreateBounded<AttemptObservation>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _snapshotInterval;
    private readonly DateTimeOffset _startedAt;
    private readonly FrozenDictionary<string, ProviderAccumulator> _accumulators;
    private StatisticsSnapshot _snapshot;

    /// <summary>
    /// Initializes the worker and publishes a zero-count snapshot for all configured providers.
    /// </summary>
    /// <param name="providers">The providers included in the statistics.</param>
    /// <param name="timeProvider">The clock used for timestamps and publication ticks.</param>
    /// <param name="options">The configured publication interval.</param>
    public InMemoryStatisticsStore(IEnumerable<ProviderDescriptor> providers, TimeProvider timeProvider, IOptions<StatisticsOptions> options)
    {
        _timeProvider = timeProvider;
        _snapshotInterval = options.Value.SnapshotInterval;
        _startedAt = timeProvider.GetUtcNow();
        _accumulators = providers.ToFrozenDictionary(
            provider => provider.Name,
            provider => new ProviderAccumulator(provider.Name),
            StringComparer.Ordinal);
        _snapshot = CreateSnapshot(_startedAt);
    }

    /// <summary>
    /// Queues an attempt observation, waiting asynchronously for space when the channel is full.
    /// </summary>
    /// <param name="observation">The attempt to record for a configured provider.</param>
    /// <returns>An operation that completes when the observation is queued or the channel is closed.</returns>
    public ValueTask RecordAsync(AttemptObservation observation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(observation.Duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)observation.Outcome, (uint)AttemptOutcome.Canceled);

        if (!_accumulators.ContainsKey(observation.Source))
            throw new ArgumentException("Unknown statistics provider.", nameof(observation));

        return _observations.Writer.TryWrite(observation) ? ValueTask.CompletedTask : WaitForSpaceAsync(observation);
    }

    /// <summary>
    /// Returns the latest published immutable statistics snapshot.
    /// </summary>
    public StatisticsSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Stops accepting observations and waits for the worker to drain accepted entries.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for shutdown to complete.</param>
    /// <returns>The shutdown wait for the statistics worker.</returns>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Complete the channel instead of canceling a worker that may still be starting.
        _observations.Writer.TryComplete();
        return ExecuteTask?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Closes the observation channel and disposes the background service.
    /// </summary>
    public override void Dispose()
    {
        _observations.Writer.TryComplete();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_snapshotInterval, _timeProvider);
        var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var dataReady = _observations.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var hasChanges = false;

        // Completing the writer wakes the reader and lets it drain accepted observations on shutdown.
        while (true)
        {
            await Task.WhenAny(tick, dataReady);
            stoppingToken.ThrowIfCancellationRequested();

            if (dataReady.IsCompletedSuccessfully)
            {
                if (!dataReady.Result)
                    break;

                // Bound the batch so continuous traffic cannot starve the publication timer.
                for (var index = 0; index < QueueCapacity && _observations.Reader.TryRead(out var observation); index++)
                {
                    _accumulators[observation.Source].Record(observation);
                    hasChanges = true;
                }

                dataReady = _observations.Reader.WaitToReadAsync(stoppingToken).AsTask();
            }

            if (tick.IsCompletedSuccessfully)
            {
                if (hasChanges)
                {
                    PublishSnapshot();
                    hasChanges = false;
                }

                tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            }
        }

        if (hasChanges)
            PublishSnapshot();
    }

    private async ValueTask WaitForSpaceAsync(AttemptObservation observation)
    {
        try
        {
            await _observations.Writer.WriteAsync(observation);
        }
        catch (ChannelClosedException)
        {
            // Host shutdown has stopped accepting new observations.
        }
    }

    private void PublishSnapshot() => Volatile.Write(ref _snapshot, CreateSnapshot(_timeProvider.GetUtcNow()));

    private StatisticsSnapshot CreateSnapshot(DateTimeOffset snapshotAt)
    {
        ImmutableArray<ProviderStatistics> providers = [.. _accumulators.Values
            .OrderBy(accumulator => accumulator.Source, StringComparer.Ordinal)
            .Select(accumulator => accumulator.Snapshot())];

        return new StatisticsSnapshot(_startedAt, snapshotAt, providers);
    }

    private static double? Mean(double sumMilliseconds, long count)
        => count == 0 ? null : sumMilliseconds / count;

    /// <summary>
    /// Aggregates attempt outcomes and duration buckets for one provider on the statistics worker.
    /// </summary>
    /// <param name="source">The provider name represented by the accumulator.</param>
    private sealed class ProviderAccumulator(string source)
    {
        private readonly BucketAccumulator fast = new();
        private readonly BucketAccumulator average = new();
        private readonly BucketAccumulator slow = new();
        private long successes;
        private long failures;
        private long cancellations;

        public string Source { get; } = source;

        /// <summary>
        /// Updates the outcome count and the applicable latency bucket.
        /// </summary>
        /// <param name="observation">The attempt observed for this provider.</param>
        public void Record(AttemptObservation observation)
        {
            var milliseconds = observation.Duration.TotalMilliseconds;

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
            }

            var bucket = milliseconds switch
            {
                < 100 => fast,
                <= 200 => average,
                _ => slow
            };
            bucket.Add(milliseconds);
        }

        /// <summary>
        /// Creates an immutable view of the provider's counts and average latencies.
        /// </summary>
        public ProviderStatistics Snapshot()
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

    /// <summary>
    /// Tracks the count and total duration of attempts in one latency bucket.
    /// </summary>
    private sealed class BucketAccumulator
    {
        public long Count { get; private set; }
        public double SumMilliseconds { get; private set; }

        /// <summary>
        /// Records one duration in the bucket.
        /// </summary>
        /// <param name="milliseconds">The duration of the attempt in milliseconds.</param>
        public void Add(double milliseconds)
        {
            Count++;
            SumMilliseconds += milliseconds;
        }

        /// <summary>
        /// Creates an immutable view of the bucket count and average latency.
        /// </summary>
        public BucketStatistics Snapshot() => new(Count, Mean(SumMilliseconds, Count));
    }
}
