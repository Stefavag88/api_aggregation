using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Application.Features.Statistics;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Statistics;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Caching;

public sealed class MemoryCacheTests
{
    private const string Repository = """
        {"items":[{"id":42,"name":"sample","html_url":"https://github.com/octocat/sample","created_at":"2020-01-02T03:04:05Z"}]}
        """;

    [Fact]
    public async Task ReusesSnapshotAcrossFiltersAndCountsOnlyActualTransport()
    {
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = CreateFactory(host, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(Response(Repository));
        });
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var first = await client.GetAsync("/api/discovery?topic=DOTNET&sources=github", cancellationToken);
        using var filtered = await client.GetAsync("/api/discovery?topic=dotnet&sources=github&from=2025-01-01T00:00:00Z&sortBy=title&sortDirection=asc&limit=1", cancellationToken);
        using var repeated = await client.GetAsync("/api/discovery?topic=dotnet&sources=github", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var filteredJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync(cancellationToken));
        Assert.Empty(filteredJson.RootElement.GetProperty("items").EnumerateArray());
        using var repeatedJson = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync(cancellationToken));
        Assert.Single(repeatedJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, attempts);

        // Drain accepted observations before inspecting the published snapshot.
        await factory.Services.GetRequiredService<InMemoryStatisticsStore>().StopAsync(cancellationToken);
        var statistics = factory.Services.GetRequiredService<IStatisticsReader>().GetSnapshot();
        Assert.Equal(1, statistics.Providers.Single(provider => provider.Source == "github").TotalRequests);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneFetch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = CreateFactory(host, async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Response(Repository);
        });
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var requests = Enumerable.Range(0, 20)
            .Select(_ => client.GetAsync("/api/discovery?topic=dotnet&sources=github", cancellationToken))
            .ToArray();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        release.SetResult();
        var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        foreach (var response in responses)
        {
            using (response)
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("{\"items\":[]}", HttpStatusCode.OK, 1)]
    [InlineData("{", HttpStatusCode.ServiceUnavailable, 2)]
    public async Task CachesValidEmptyResultsButNotFailures(string payload, HttpStatusCode expectedStatus, int expectedAttempts)
    {
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = CreateFactory(host, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(Response(payload));
        });
        using var client = factory.CreateClient();

        for (var index = 0; index < 2; index++)
        {
            using var response = await client.GetAsync("/api/discovery?topic=dotnet&sources=github", TestContext.Current.CancellationToken);
            Assert.Equal(expectedStatus, response.StatusCode);
        }

        Assert.Equal(expectedAttempts, attempts);
    }

    [Fact]
    public async Task BudgetCancelsStalledHttpResponseBody()
    {
        var clock = new DeadlineClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new WebApplicationFactory<Program>();
        await using var transportFactory = CreateFactory(host, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream(entered, bodyCanceled))
        }));
        await using var factory = transportFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<TimeProvider>(clock)));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = client.GetAsync("/api/discovery?topic=dotnet&sources=github", cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        clock.ExpireBudget();

        using var response = await request.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await bodyCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var provider = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal("timeout", provider.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task CancelingOwnerScopeLetsAnotherScopeRetryThroughItsOwnClient()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = CreateFactory(host, async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return Response(Repository);
        });
        using var client = factory.CreateClient();
        await using var ownerScope = factory.Services.CreateAsyncScope();
        await using var followerScope = factory.Services.CreateAsyncScope();
        var ownerProvider = ownerScope.ServiceProvider.GetServices<IDiscoveryProvider>().Single(provider => provider.Descriptor.Name == "github");
        var followerProvider = followerScope.ServiceProvider.GetServices<IDiscoveryProvider>().Single(provider => provider.Descriptor.Name == "github");
        using var ownerCancellation = new CancellationTokenSource();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = ownerProvider.FetchAsync("dotnet", ownerCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var follower = followerProvider.FetchAsync("dotnet", cancellationToken);
        ownerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        var result = await follower.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("42", Assert.Single(result.Value!.Items).Id);
        Assert.Equal(2, attempts);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        WebApplicationFactory<Program> host,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(nameof(GitHubClient))
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(send))));

    private static HttpResponseMessage Response(string payload)
        => new(HttpStatusCode.OK) { Content = new StringContent(payload) };

    private sealed class DeadlineClock : TimeProvider
    {
        private Action? _expire;

        public void ExpireBudget() => Assert.IsType<Action>(_expire).Invoke();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                return base.CreateTimer(callback, state, dueTime, period);

            _expire = () => callback(state);
            return new DeadlineTimer(() => _expire = null);
        }

        private sealed class DeadlineTimer(Action dispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => dispose();
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StalledStream(TaskCompletionSource entered, TaskCompletionSource canceled) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        }
    }
}
