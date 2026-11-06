using ZiggyCreatures.Caching.Fusion;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Caching;
using ApiAggregation.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace ApiAggregation.UnitTests.Caching;

public sealed class CachedDiscoveryProviderTests
{
    [Fact]
    public async Task RefreshesAtFreshBoundaryAndKeepsKeysIndependent()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var attempts = 0;
        var inner = new Provider((_, _) =>
        {
            attempts++;
            return Task.FromResult(Success(clock));
        });
        var provider = CreateProvider(inner, cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await provider.FetchAsync("dotnet", cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(299));
        var cached = await provider.FetchAsync("dotnet", cancellationToken);
        Assert.Equal(first.Value!.FetchedAt, cached.Value!.FetchedAt);
        Assert.Equal(1, attempts);

        clock.Advance(TimeSpan.FromSeconds(1));
        await ExpireAsync(cache);
        var refreshed = await provider.FetchAsync("dotnet", cancellationToken);
        Assert.Equal(clock.GetUtcNow(), refreshed.Value!.FetchedAt);
        Assert.False(refreshed.Value.IsStale);
        Assert.Equal(2, attempts);

        await provider.FetchAsync("python", cancellationToken);
        var other = CreateProvider(new Provider(inner.FetchAsync, "devto"), cache, clock);
        await other.FetchAsync("dotnet", cancellationToken);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task FailedRefreshUsesOriginalAgeAndThrottlesOnlyWhileFallbackIsUsable()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var attempts = 0;
        var inner = new Provider((_, _) => Task.FromResult(++attempts == 1
            ? Success(clock)
            : AppResult.Failure<ProviderData>(ProviderErrors.UpstreamError)));
        var provider = CreateProvider(inner, cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await provider.FetchAsync("dotnet", cancellationToken);

        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpireAsync(cache);
        var stale = await provider.FetchAsync("dotnet", cancellationToken);
        Assert.True(stale.IsSuccess);
        Assert.True(stale.Value!.IsStale);
        Assert.Equal(first.Value!.FetchedAt, stale.Value.FetchedAt);
        Assert.Equal(2, attempts);

        await provider.FetchAsync("dotnet", cancellationToken);
        Assert.Equal(2, attempts);

        clock.Advance(TimeSpan.FromMinutes(25));
        await ExpireAsync(cache);
        var expired = await provider.FetchAsync("dotnet", cancellationToken);
        Assert.False(expired.IsSuccess);
        Assert.Equal("upstream_error", expired.Error!.Code);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task FollowerCancellationDoesNotCancelOwner()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var provider = CreateProvider(new Provider(async (_, token) =>
        {
            attempts++;
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Success(clock);
        }), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = provider.FetchAsync("dotnet", cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        using var followerCancellation = new CancellationTokenSource();
        var follower = provider.FetchAsync("dotnet", followerCancellation.Token);
        followerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        release.SetResult();
        Assert.True((await owner.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).IsSuccess);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OwnerCancellationLetsFollowerRetryAndRemovesCompletedFlight()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var provider = CreateProvider(new Provider(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return Success(clock);
        }), cache, clock);
        using var ownerCancellation = new CancellationTokenSource();
        var owner = provider.FetchAsync("dotnet", ownerCancellation.Token);
        var cancellationToken = TestContext.Current.CancellationToken;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var follower = provider.FetchAsync("dotnet", cancellationToken);
        ownerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.True((await follower.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).IsSuccess);
        Assert.Equal(2, attempts);

        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpireAsync(cache);
        await provider.FetchAsync("dotnet", cancellationToken);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task DifferentKeysCanFetchConcurrently()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var provider = CreateProvider(new Provider(async (_, token) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.SetResult();
            await release.Task.WaitAsync(token);
            return Success(clock);
        }), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = provider.FetchAsync("dotnet", cancellationToken);
        var second = provider.FetchAsync("python", cancellationToken);

        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        release.SetResult();
        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.IsSuccess));
    }

    [Fact]
    public async Task UnexpectedRefreshFaultIsNotHiddenByStaleData()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var attempts = 0;
        var provider = CreateProvider(new Provider((_, _) => ++attempts == 1
            ? Task.FromResult(Success(clock))
            : throw new InvalidOperationException("bug")), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        await provider.FetchAsync("dotnet", cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpireAsync(cache);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchAsync("dotnet", cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchAsync("dotnet", cancellationToken));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CallerCancellationPropagatesEvenWithFreshData()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var provider = CreateProvider(new Provider((_, _) => Task.FromResult(Success(clock))), cache, clock);
        await provider.FetchAsync("dotnet", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync("dotnet", cancellation.Token));
    }

    [Fact]
    public async Task FollowerDeadlineDoesNotCancelLongerOwnerBudget()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var inner = new Provider(async (_, token) =>
        {
            attempts++;
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Success(clock);
        });
        var ownerProvider = CreateProvider(inner, cache, clock);
        var followerProvider = new CachedDiscoveryProvider(inner, cache,
            Options.Create(new ProviderCacheOptions { FetchTimeout = TimeSpan.FromSeconds(1) }), clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = ownerProvider.FetchAsync("dotnet", cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var follower = followerProvider.FetchAsync("dotnet", cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));

        var timedOut = await follower.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal("timeout", timedOut.Error!.Code);
        Assert.False(owner.IsCompleted);
        release.SetResult();
        Assert.True((await owner.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).IsSuccess);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDeadlineReturnsFailureOrStaleFallback(bool warm)
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var provider = CreateProvider(new Provider(async (_, token) =>
        {
            if (++attempts == 1 && warm)
                return Success(clock);

            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Success(clock);
        }), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        if (warm)
        {
            await provider.FetchAsync("dotnet", cancellationToken);
            clock.Advance(TimeSpan.FromMinutes(5));
            await ExpireAsync(cache);
        }

        var request = provider.FetchAsync("dotnet", cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await request.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(warm, result.IsSuccess);
        if (warm)
        {
            Assert.True(result.Value!.IsStale);
            Assert.Equal(2, attempts);
        }
        else
        {
            Assert.Equal("timeout", result.Error!.Code);
        }
    }

    [Fact]
    public async Task CapacityMissStillReturnsDataAndFetchesAgainWhenEntryWasNotAdmitted()
    {
        var clock = new TestClock();
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 1 });
        using var cache = new FusionCache(new FusionCacheOptions { DisableTagging = true }, memory);
        var attempts = 0;
        var provider = CreateProvider(new Provider((_, _) =>
        {
            attempts++;
            return Task.FromResult(Success(clock));
        }), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True((await provider.FetchAsync("dotnet", cancellationToken)).IsSuccess);
        Assert.True((await provider.FetchAsync("python", cancellationToken)).IsSuccess);
        Assert.True((await provider.FetchAsync("python", cancellationToken)).IsSuccess);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task UnexpectedCancellationFaultPropagatesAndFollowerCanFetch()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var provider = CreateProvider(new Provider(async (_, token) =>
        {
            if (++attempts == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                throw new OperationCanceledException("unrelated cancellation");
            }

            return Success(clock);
        }), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = provider.FetchAsync("dotnet", cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var follower = provider.FetchAsync("dotnet", cancellationToken);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.True((await follower.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).IsSuccess);
        Assert.Equal(2, attempts);
    }

    private static IFusionCache CreateCache() => new FusionCache(new FusionCacheOptions { DisableTagging = true });

    [Fact]
    public async Task ColdFailedFactoryLetsWaitingCallerFetchAndPreservesTypedError()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var provider = CreateProvider(new Provider(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return AppResult.Failure<ProviderData>(ProviderErrors.RateLimited);
            }

            return Success(clock);
        }), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = provider.FetchAsync("dotnet", cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var follower = provider.FetchAsync("dotnet", cancellationToken);
        release.SetResult();

        Assert.Same(ProviderErrors.RateLimited, (await first).Error);
        Assert.True((await follower.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).IsSuccess);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetExpiryWaitsForOwnedFactoryCleanup(bool cleanupFaults)
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var cleaningUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = CreateProvider(new Provider(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return Success(clock);
            }
            finally
            {
                cleaningUp.SetResult();
                await release.Task;
                if (cleanupFaults)
                    throw new InvalidOperationException("cleanup failed");
            }
        }), cache, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = provider.FetchAsync("dotnet", cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));

        try
        {
            await cleaningUp.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(TimeSpan.FromMilliseconds(50), cancellationToken));
        }
        finally
        {
            release.TrySetResult();
        }

        if (cleanupFaults)
            await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        else
            Assert.Equal("timeout", (await request.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).Error!.Code);
    }

    [Fact]
    public async Task RefreshCanRecoverAfterFusionCacheThrottleExpires()
    {
        var clock = new TestClock();
        using var cache = CreateCache();
        var attempts = 0;
        var provider = new CachedDiscoveryProvider(new Provider((_, _) => Task.FromResult(++attempts == 2
            ? AppResult.Failure<ProviderData>(ProviderErrors.UpstreamError)
            : Success(clock))), cache, Options.Create(new ProviderCacheOptions
            {
                Default = new ProviderCachePolicy { RetryInterval = TimeSpan.FromMilliseconds(100) }
            }), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await provider.FetchAsync("dotnet", timeout.Token);
        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpireAsync(cache);
        var result = await provider.FetchAsync("dotnet", timeout.Token);
        Assert.True(result.Value!.IsStale);

        while (result.Value!.IsStale)
        {
            await Task.Delay(10, timeout.Token);
            result = await provider.FetchAsync("dotnet", timeout.Token);
        }

        Assert.Equal(3, attempts);
        Assert.Equal(clock.GetUtcNow(), result.Value.FetchedAt);
    }

    private static ValueTask ExpireAsync(IFusionCache cache)
        => cache.ExpireAsync(ProviderCacheKey.Create("github", "dotnet", 30),
            new FusionCacheEntryOptions { IsFailSafeEnabled = true });

    private static CachedDiscoveryProvider CreateProvider(IDiscoveryProvider inner, IFusionCache cache, TimeProvider clock)
        => new(inner, cache, Options.Create(new ProviderCacheOptions()), clock);

    private static Result<ProviderData> Success(TimeProvider clock)
        => AppResult.Success(new ProviderData([], clock.GetUtcNow(), IsStale: false, IsIncomplete: false));

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            foreach (var timer in _timers.ToArray())
                timer.Tick(_utcNow);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(TestClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _dueAt = DateTimeOffset.MaxValue;
            private TimeSpan _period;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow().Add(dueTime);
                _period = period;
                return true;
            }

            public void Tick(DateTimeOffset now)
            {
                if (now < _dueAt)
                    return;
                _dueAt = _period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now.Add(_period);
                callback(state);
            }

            public void Dispose() => _dueAt = DateTimeOffset.MaxValue;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Provider(
        Func<string, CancellationToken, Task<Result<ProviderData>>> fetch,
        string source = "github") : IDiscoveryProvider
    {
        public ProviderDescriptor Descriptor { get; } = new(source, ContentType.Repository);

        public Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
            => fetch(topic, cancellationToken);
    }
}
