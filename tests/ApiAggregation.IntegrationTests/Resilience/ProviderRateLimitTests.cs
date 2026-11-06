using ApiAggregation.Application.Features.Statistics;
using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Statistics;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Resilience;

public sealed class ProviderRateLimitTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "15", null, 15)]
    [InlineData(HttpStatusCode.TooManyRequests, "date", null, 25)]
    [InlineData(HttpStatusCode.TooManyRequests, null, null, 60)]
    [InlineData(HttpStatusCode.TooManyRequests, "999999999999999999999999", long.MaxValue, 60)]
    [InlineData(HttpStatusCode.TooManyRequests, "0", 0L, 60)]
    [InlineData(HttpStatusCode.Forbidden, null, 40L, 40)]
    [InlineData(HttpStatusCode.Forbidden, "15", 40L, 40)]
    [InlineData(HttpStatusCode.Forbidden, "15", null, 15)]
    public async Task BlocksOnlyThatProviderUntilCooldownExpires(
        HttpStatusCode status, string? retryAfter, long? resetAfterSeconds, int cooldownSeconds)
    {
        var clock = new TestClock();
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddHttpClient(nameof(GitHubClient)).ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(_ =>
            {
                if (Interlocked.Increment(ref attempts) != 1)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"items":[]}""") };

                var response = new HttpResponseMessage(status);
                if (retryAfter is not null)
                    response.Headers.TryAddWithoutValidation("Retry-After", retryAfter == "date"
                        ? clock.GetUtcNow().AddSeconds(25).ToString("R", CultureInfo.InvariantCulture)
                        : retryAfter);
                if (resetAfterSeconds is { } reset)
                {
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                    var epoch = reset == long.MaxValue ? reset : clock.GetUtcNow().ToUnixTimeSeconds() + reset;
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", epoch.ToString(CultureInfo.InvariantCulture));
                }

                return response;
            }));
            services.AddHttpClient(nameof(DevToClient)).ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        }));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        await AssertFailureAsync(client, "dotnet", "rate_limited", cancellationToken);
        Assert.Equal(1, attempts);

        clock.Advance(TimeSpan.FromSeconds(cooldownSeconds - 1));
        // A different topic bypasses the previous topic's cache.
        await AssertFailureAsync(client, "python", "rate_limited", cancellationToken);
        Assert.Equal(1, attempts);
        using var other = await client.GetAsync("/api/discovery?topic=python&sources=devto", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        clock.Advance(TimeSpan.FromSeconds(1));
        using var recovered = await client.GetAsync("/api/discovery?topic=python&sources=github", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, attempts);

        await factory.Services.GetRequiredService<InMemoryStatisticsStore>().StopAsync(cancellationToken);
        var statistics = factory.Services.GetRequiredService<IStatisticsReader>().GetSnapshot().Providers
            .Single(provider => provider.Source == "github");
        Assert.Equal(2, statistics.TotalRequests);
        Assert.Equal(1, statistics.FailureCount);
        Assert.Equal(1, statistics.SuccessCount);
    }

    [Fact]
    public async Task LaterResponseCannotShortenAnActiveCooldown()
    {
        var clock = new TestClock();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddHttpClient(nameof(GitHubClient)).ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(async (request, token) =>
            {
                Interlocked.Increment(ref attempts);
                var delayed = request.RequestUri!.Query.Contains("python", StringComparison.Ordinal);
                if (delayed)
                {
                    secondEntered.SetResult();
                    await releaseSecond.Task.WaitAsync(token);
                }

                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("Retry-After", delayed ? "10" : "60");
                return response;
            }));
        }));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var delayedFailure = AssertFailureAsync(client, "python", "rate_limited", cancellationToken);
        try
        {
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await AssertFailureAsync(client, "dotnet", "rate_limited", cancellationToken);
        }
        finally
        {
            releaseSecond.TrySetResult();
        }

        await delayedFailure;
        clock.Advance(TimeSpan.FromSeconds(20));
        await AssertFailureAsync(client, "csharp", "rate_limited", cancellationToken);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task OrdinaryGitHubForbiddenDoesNotStartCooldown()
    {
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(nameof(GitHubClient)).ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(_ =>
            {
                if (Interlocked.Increment(ref attempts) != 1)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"items":[]}""") };

                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "10");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "2000000000");
                return response;
            }))));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        await AssertFailureAsync(client, "dotnet", "unauthorized", cancellationToken);
        using var response = await client.GetAsync("/api/discovery?topic=python&sources=github", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    private static async Task AssertFailureAsync(HttpClient client, string topic, string code, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/api/discovery?topic={topic}&sources=github", cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var provider = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal(code, provider.GetProperty("errorCode").GetString());
    }

    private sealed class TestClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        private long _seconds;

        public override DateTimeOffset GetUtcNow() => Start.AddSeconds(Interlocked.Read(ref _seconds));

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _seconds, (long)duration.TotalSeconds);
    }
}
