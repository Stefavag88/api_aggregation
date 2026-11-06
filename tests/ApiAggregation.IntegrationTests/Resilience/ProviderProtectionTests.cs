using ApiAggregation.Application.Features.Statistics;
using ApiAggregation.Infrastructure.Caching;
using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Statistics;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text.Json;
using ZiggyCreatures.Caching.Fusion;

namespace ApiAggregation.IntegrationTests.Resilience;

public sealed class ProviderProtectionTests
{
    [Theory]
    [InlineData(8, false)]
    [InlineData(2, true)]
    public async Task ExcessOperationIsRejectedBeforeTransportAndStaleDataRemainsAvailable(int limit, bool configureLimit)
    {
        var clock = new TestClock();
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var factory = CreateFactory(host, clock, async (_, token) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt > 1)
            {
                if (attempt == limit + 1)
                    allEntered.SetResult();
                await release.Task.WaitAsync(token);
            }

            return GitHubResponse(HttpStatusCode.OK);
        }, configureLimit
            ? new Dictionary<string, string?> { ["ProviderResilience:ConcurrencyLimit"] = limit.ToString(CultureInfo.InvariantCulture) }
            : null);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var warm = await DiscoverAsync(client, "dotnet", cancellationToken);
        warm.EnsureSuccessStatusCode();
        clock.Advance(TimeSpan.FromMinutes(5));
        await factory.Services.GetRequiredService<IFusionCache>().ExpireAsync(
            ProviderCacheKey.Create("github", "dotnet", 30),
            new FusionCacheEntryOptions { IsFailSafeEnabled = true }, token: cancellationToken);

        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = Enumerable.Range(0, limit)
            .Select(index => DiscoverAsync(client, $"held-{index}", pendingCancellation.Token)).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            using var busy = await DiscoverAsync(client, "cold", cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await AssertUnavailableAsync(busy, "busy", cancellationToken);

            using var stale = await DiscoverAsync(client, "dotnet", cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
            using var json = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(cancellationToken));
            Assert.True(json.RootElement.GetProperty("isPartial").GetBoolean());
            var provider = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
            Assert.Equal("stale", provider.GetProperty("status").GetString());
            using var other = await client.GetAsync("/api/discovery?topic=dotnet&sources=devto", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            Assert.Equal(limit + 1, attempts);
        }
        finally
        {
            release.TrySetResult();
            foreach (var response in await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
            {
                using (response)
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        await AssertGitHubAttemptCountAsync(factory, limit + 1, cancellationToken);
    }

    [Theory]
    [InlineData(5, 5, 15, false)]
    [InlineData(1, 2, 2, true)]
    public async Task BreakerUsesConfiguredThresholdAndRecoversAfterBreakDuration(
        int successes, int failures, int breakSeconds, bool configureBreaker)
    {
        var clock = new TestClock();
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var factory = CreateFactory(host, clock, (_, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            return Task.FromResult(GitHubResponse(attempt > successes && attempt <= successes + failures
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK));
        }, configureBreaker ? new Dictionary<string, string?>
        {
            ["ProviderResilience:CircuitFailureRatio"] = "0.6",
            ["ProviderResilience:CircuitMinimumThroughput"] = "3",
            ["ProviderResilience:CircuitSamplingDuration"] = "00:00:03",
            ["ProviderResilience:CircuitBreakDuration"] = "00:00:02"
        } : null);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var index = 0; index < successes; index++)
        {
            using var success = await DiscoverAsync(client, $"success-{index}", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }
        var fullFailureBatches = (failures - 1) / 3;
        for (var index = 0; index < fullFailureBatches; index++)
        {
            using var failure = await DiscoverAsync(client, $"failure-{index}", cancellationToken);
            await AssertUnavailableAsync(failure, "upstream_error", cancellationToken);
        }
        Assert.Equal(successes + fullFailureBatches * 3, attempts);

        using var opens = await DiscoverAsync(client, "opens-breaker", cancellationToken);
        await AssertUnavailableAsync(opens, "circuit_open", cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(breakSeconds - 1));
        using var blocked = await DiscoverAsync(client, "blocked", cancellationToken);
        await AssertUnavailableAsync(blocked, "circuit_open", cancellationToken);
        Assert.Equal(successes + failures, attempts);
        using var other = await client.GetAsync("/api/discovery?topic=dotnet&sources=devto", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        clock.Advance(TimeSpan.FromSeconds(2));
        using var probe = await DiscoverAsync(client, "probe", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        using var closed = await DiscoverAsync(client, "closed", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal(successes + failures + 2, attempts);
        await AssertGitHubAttemptCountAsync(factory, attempts, cancellationToken);
    }

    [Theory]
    [InlineData("ConcurrencyLimit", "0")]
    [InlineData("CircuitFailureRatio", "NaN")]
    [InlineData("CircuitMinimumThroughput", "1")]
    [InlineData("CircuitSamplingDuration", "00:00:00.499")]
    [InlineData("CircuitBreakDuration", "2.00:00:00")]
    public async Task RejectsInvalidProtectionSettingsAtStartup(string setting, string value)
    {
        await using var host = new AuthenticatedApiFactory();
        await using var factory = CreateFactory(host, TimeProvider.System,
            (_, _) => throw new InvalidOperationException("Startup validation should run before transport."),
            new Dictionary<string, string?> { [$"ProviderResilience:{setting}"] = value });

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("ProviderResilience", exception.Message);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        WebApplicationFactory<Program> host, TimeProvider clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Dictionary<string, string?>? overrides = null)
        => host.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Caching:FetchTimeout"] = "00:00:30",
                    ["ProviderResilience:AttemptTimeout"] = "00:00:20",
                    ["ProviderResilience:RetryDelay"] = "00:00:00.001"
                }).AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(clock);
                services.AddHttpClient(nameof(GitHubClient))
                    .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(send));
                services.AddHttpClient(nameof(DevToClient)).ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(_ =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));
            });
        });

    private static HttpResponseMessage GitHubResponse(HttpStatusCode status)
        => new(status) { Content = new StringContent("""{"items":[]}""") };

    private static Task<HttpResponseMessage> DiscoverAsync(HttpClient client, string topic, CancellationToken cancellationToken)
        => client.GetAsync($"/api/discovery?topic={topic}&sources=github", cancellationToken);

    private static async Task AssertUnavailableAsync(HttpResponseMessage response, string code, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var provider = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal(code, provider.GetProperty("errorCode").GetString());
    }

    private static async Task AssertGitHubAttemptCountAsync(WebApplicationFactory<Program> factory, int expected,
        CancellationToken cancellationToken)
    {
        await factory.Services.GetRequiredService<InMemoryStatisticsStore>().StopAsync(cancellationToken);
        var statistics = factory.Services.GetRequiredService<IStatisticsReader>().GetSnapshot().Providers
            .Single(provider => provider.Source == "github");
        Assert.Equal(expected, statistics.TotalRequests);
    }

    private sealed class TestClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        private long _seconds;

        public override DateTimeOffset GetUtcNow() => Start.AddSeconds(Interlocked.Read(ref _seconds));

        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _seconds) * TimestampFrequency;

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _seconds, (long)duration.TotalSeconds);
    }
}
