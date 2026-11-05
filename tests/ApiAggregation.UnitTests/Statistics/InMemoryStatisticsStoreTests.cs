using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Statistics;

namespace ApiAggregation.UnitTests.Statistics;

public sealed class InMemoryStatisticsStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncludesUncontactedProvidersAndNullAverages()
    {
        var clock = new TestClock();
        var store = CreateStore(clock);
        clock.UtcNow = Now.AddSeconds(1);

        var snapshot = store.GetSnapshot();

        Assert.Equal(Now, snapshot.StartedAt);
        Assert.Equal(clock.UtcNow, snapshot.SnapshotAt);
        Assert.Equal(new[] { "devto", "github" }, snapshot.Providers.Select(provider => provider.Source));
        Assert.All(snapshot.Providers, provider =>
        {
            Assert.Equal(0, provider.TotalRequests);
            Assert.Null(provider.AverageResponseTimeMs);
            Assert.All(new[] { provider.Fast, provider.Average, provider.Slow }, bucket =>
            {
                Assert.Equal(0, bucket.Count);
                Assert.Null(bucket.AverageResponseTimeMs);
            });
        });
    }

    [Fact]
    public void UsesExactBucketBoundaries()
    {
        var store = CreateStore();
        store.Record(Observation("github", 99, AttemptOutcome.Success));
        store.Record(Observation("github", 100, AttemptOutcome.Failure));
        store.Record(Observation("github", 200, AttemptOutcome.Canceled));
        store.Record(Observation("github", 201, AttemptOutcome.Success));

        var stats = store.GetSnapshot().Providers.Single(provider => provider.Source == "github");

        Assert.Equal(4, stats.TotalRequests);
        Assert.Equal(2, stats.SuccessCount);
        Assert.Equal(1, stats.FailureCount);
        Assert.Equal(1, stats.CanceledCount);
        Assert.Equal(150d, stats.AverageResponseTimeMs);
        Assert.Equal((1L, 99d), (stats.Fast.Count, stats.Fast.AverageResponseTimeMs));
        Assert.Equal((2L, 150d), (stats.Average.Count, stats.Average.AverageResponseTimeMs));
        Assert.Equal((1L, 201d), (stats.Slow.Count, stats.Slow.AverageResponseTimeMs));
    }

    [Fact]
    public void SnapshotsPreserveEarlierValuesAndUseWeightedMeans()
    {
        var store = CreateStore();
        store.Record(Observation("github", 10, AttemptOutcome.Success));
        var previous = store.GetSnapshot().Providers.Single(provider => provider.Source == "github");

        store.Record(Observation("github", 20, AttemptOutcome.Success));
        store.Record(Observation("github", 300, AttemptOutcome.Success));
        var current = store.GetSnapshot().Providers.Single(provider => provider.Source == "github");

        Assert.Equal(1, previous.TotalRequests);
        Assert.Equal(10d, previous.AverageResponseTimeMs);
        Assert.Equal(110d, current.AverageResponseTimeMs);
        Assert.Equal(15d, current.Fast.AverageResponseTimeMs);
    }

    [Fact]
    public void RejectsInvalidObservationsWithoutChangingTotals()
    {
        var store = CreateStore();

        Assert.Throws<ArgumentException>(() => store.Record(Observation("unknown", 10, AttemptOutcome.Success)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Record(Observation("github", -1, AttemptOutcome.Success)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Record(Observation("github", 10, (AttemptOutcome)99)));
        Assert.All(store.GetSnapshot().Providers, provider => Assert.Equal(0, provider.TotalRequests));
    }

    [Fact]
    public void ParallelWritesPreserveExactPerProviderTotals()
    {
        var store = CreateStore();

        Parallel.For(0, 10_000, new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken }, index =>
        {
            var source = index % 2 == 0 ? "github" : "devto";
            var observation = (index % 4) switch
            {
                0 => Observation(source, 20, AttemptOutcome.Success),
                1 => Observation(source, 100, AttemptOutcome.Failure),
                2 => Observation(source, 220, AttemptOutcome.Canceled),
                _ => Observation(source, 200, AttemptOutcome.Success)
            };

            store.Record(observation);
        });

        var providers = store.GetSnapshot().Providers;
        var github = providers.Single(provider => provider.Source == "github");
        var devTo = providers.Single(provider => provider.Source == "devto");
        Assert.Equal((5000L, 2500L, 0L, 2500L), (github.TotalRequests, github.SuccessCount, github.FailureCount, github.CanceledCount));
        Assert.Equal((2500L, 0L, 2500L), (github.Fast.Count, github.Average.Count, github.Slow.Count));
        Assert.Equal(120d, github.AverageResponseTimeMs);
        Assert.Equal((5000L, 2500L, 2500L, 0L), (devTo.TotalRequests, devTo.SuccessCount, devTo.FailureCount, devTo.CanceledCount));
        Assert.Equal((0L, 5000L, 0L), (devTo.Fast.Count, devTo.Average.Count, devTo.Slow.Count));
        Assert.Equal(150d, devTo.AverageResponseTimeMs);
    }

    private static AttemptObservation Observation(string source, int milliseconds, AttemptOutcome outcome)
        => new(source, TimeSpan.FromMilliseconds(milliseconds), outcome, Now);

    private static InMemoryStatisticsStore CreateStore(TimeProvider? clock = null)
        => new([new ProviderDescriptor("github", ContentType.Repository), new ProviderDescriptor("devto", ContentType.Article)],
            clock ?? new TestClock());

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
