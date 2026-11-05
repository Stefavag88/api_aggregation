using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Statistics;
using Microsoft.Extensions.Options;

namespace ApiAggregation.UnitTests.Statistics;

public sealed class InMemoryStatisticsStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ObservationsAreVisibleOnlyAfterPublication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = CreateStore();
        var initial = store.GetSnapshot();

        await store.RecordAsync(Observation("github", 20, AttemptOutcome.Success));
        await store.RecordAsync(Observation("github", 50, AttemptOutcome.Success));

        Assert.Same(initial, store.GetSnapshot());
        await store.StartAsync(cancellationToken);
        await store.StopAsync(cancellationToken);

        var published = store.GetSnapshot().Providers.Single(provider => provider.Source == "github");
        Assert.Equal(2, published.TotalRequests);
        Assert.Equal(35d, published.AverageResponseTimeMs);
    }

    [Fact]
    public async Task FullChannelWaitsForSpaceAndRetainsObservations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = CreateStore();
        var observation = Observation("github", 20, AttemptOutcome.Success);

        for (var index = 0; index < 4096; index++)
        {
            var accepted = store.RecordAsync(observation);
            Assert.True(accepted.IsCompletedSuccessfully);
            await accepted;
        }

        var waiting = store.RecordAsync(observation).AsTask();
        Assert.False(waiting.IsCompleted);

        await store.StartAsync(cancellationToken);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await store.StopAsync(cancellationToken);

        var stats = store.GetSnapshot().Providers.Single(provider => provider.Source == "github");
        Assert.Equal(4097, stats.TotalRequests);
    }

    [Fact]
    public void IncludesUncontactedProvidersAndNullAverages()
    {
        var clock = new TestClock();
        using var store = CreateStore(clock);
        clock.UtcNow = Now.AddSeconds(1);

        var snapshot = store.GetSnapshot();

        Assert.Equal(Now, snapshot.StartedAt);
        Assert.Equal(Now, snapshot.SnapshotAt);
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
    public async Task UsesExactBucketBoundaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = CreateStore();
        await store.StartAsync(cancellationToken);
        await store.RecordAsync(Observation("github", 99, AttemptOutcome.Success));
        await store.RecordAsync(Observation("github", 100, AttemptOutcome.Failure));
        await store.RecordAsync(Observation("github", 200, AttemptOutcome.Canceled));
        await store.RecordAsync(Observation("github", 201, AttemptOutcome.Success));
        await store.StopAsync(cancellationToken);

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
    public async Task PublishesOnConfiguredTickAndPreservesEarlierSnapshots()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var clock = new TestClock();
        var interval = TimeSpan.FromSeconds(3);
        using var store = CreateStore(clock, interval);
        var initial = store.GetSnapshot();
        await store.RecordAsync(Observation("github", 10, AttemptOutcome.Success));
        await store.StartAsync(cancellation.Token);
        var timer = await clock.TimerCreated.Task.WaitAsync(cancellation.Token);
        Assert.Equal(interval, timer.Period);
        Assert.Same(initial, store.GetSnapshot());

        clock.UtcNow = Now + interval;
        timer.Tick();
        while (ReferenceEquals(initial, store.GetSnapshot()))
            await Task.Delay(10, cancellation.Token);

        var snapshot = store.GetSnapshot();
        Assert.Equal(clock.UtcNow, snapshot.SnapshotAt);
        var previous = snapshot.Providers.Single(provider => provider.Source == "github");

        await store.RecordAsync(Observation("github", 20, AttemptOutcome.Success));
        await store.RecordAsync(Observation("github", 300, AttemptOutcome.Success));
        Assert.Same(snapshot, store.GetSnapshot());
        await store.StopAsync(cancellation.Token);
        var current = store.GetSnapshot().Providers.Single(provider => provider.Source == "github");

        Assert.Equal(1, previous.TotalRequests);
        Assert.Equal(10d, previous.AverageResponseTimeMs);
        Assert.Equal(110d, current.AverageResponseTimeMs);
        Assert.Equal(15d, current.Fast.AverageResponseTimeMs);
    }

    [Fact]
    public void RejectsInvalidObservationsWithoutChangingTotals()
    {
        using var store = CreateStore();

        Assert.Throws<ArgumentException>(() => { _ = store.RecordAsync(Observation("unknown", 10, AttemptOutcome.Success)); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = store.RecordAsync(Observation("github", -1, AttemptOutcome.Success)); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = store.RecordAsync(Observation("github", 10, (AttemptOutcome)99)); });
        Assert.All(store.GetSnapshot().Providers, provider => Assert.Equal(0, provider.TotalRequests));
    }

    [Fact]
    public async Task ParallelWritesPreserveExactPerProviderTotals()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        using var store = CreateStore();
        await store.StartAsync(cancellation.Token);

        var writes = Parallel.ForEachAsync(Enumerable.Range(0, 10_000), cancellation.Token, (index, _) =>
        {
            var source = index % 2 == 0 ? "github" : "devto";
            var observation = (index % 4) switch
            {
                0 => Observation(source, 20, AttemptOutcome.Success),
                1 => Observation(source, 100, AttemptOutcome.Failure),
                2 => Observation(source, 220, AttemptOutcome.Canceled),
                _ => Observation(source, 200, AttemptOutcome.Success)
            };

            return store.RecordAsync(observation);
        });
        await writes.WaitAsync(cancellation.Token);
        await store.StopAsync(cancellation.Token);

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

    private static InMemoryStatisticsStore CreateStore(TimeProvider? clock = null, TimeSpan? interval = null)
        => new([new ProviderDescriptor("github", ContentType.Repository), new ProviderDescriptor("devto", ContentType.Article)],
            clock ?? new TestClock(), Options.Create(new StatisticsOptions { SnapshotInterval = interval ?? TimeSpan.FromSeconds(1) }));

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public TaskCompletionSource<TestTimer> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(callback, state, period);
            TimerCreated.SetResult(timer);
            return timer;
        }
    }

    private sealed class TestTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; } = period;

        public void Tick() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
