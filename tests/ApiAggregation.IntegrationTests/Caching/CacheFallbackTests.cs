using ApiAggregation.Infrastructure.Caching;
using ZiggyCreatures.Caching.Fusion;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Caching;

public sealed class CacheFallbackTests
{
    private const string Repository = """
        {"items":[{"id":42,"name":"original","html_url":"https://github.com/octocat/sample","created_at":"2020-01-02T03:04:05Z"}]}
        """;

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "")]
    [InlineData(HttpStatusCode.OK, "{")]
    [InlineData(HttpStatusCode.OK, "{\"items\":[{}]}")]
    [InlineData(HttpStatusCode.OK, "{\"items\":[],\"incomplete_results\":true}")]
    public async Task FailedRefreshPreservesOriginalSnapshotUntilAbsoluteExpiry(HttpStatusCode status, string payload)
    {
        var clock = new TestClock();
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = CreateFactory(host, clock, () => ++attempts == 1
            ? Response(HttpStatusCode.OK, Repository)
            : Response(status, payload));
        using var client = factory.CreateClient();
        using var first = await DiscoverAsync(client, HttpStatusCode.OK);
        var fetchedAt = Report(first).GetProperty("fetchedAt").GetDateTimeOffset();

        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpireAsync(factory);
        using var stale = await DiscoverAsync(client, HttpStatusCode.OK);
        Assert.True(stale.RootElement.GetProperty("isPartial").GetBoolean());
        Assert.Equal("stale", Report(stale).GetProperty("status").GetString());
        Assert.Equal(fetchedAt, Report(stale).GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.Equal("original", Assert.Single(stale.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());

        using var throttled = await DiscoverAsync(client, HttpStatusCode.OK);
        var attemptsPerRefresh = status == HttpStatusCode.BadGateway ? 2 : 1;
        Assert.Equal(1 + attemptsPerRefresh, attempts);
        clock.Advance(TimeSpan.FromMinutes(25));
        await ExpireAsync(factory);
        using var expired = await DiscoverAsync(client, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1 + 2 * attemptsPerRefresh, attempts);
    }

    [Theory]
    [InlineData("{\"items\":[]}", 0, false)]
    [InlineData("{\"items\":[{\"id\":43,\"name\":\"replacement\",\"html_url\":\"https://github.com/octocat/replacement\",\"created_at\":\"2021-01-01T00:00:00Z\"},{}]}", 1, true)]
    public async Task ValidRefreshReplacesOldDataIncludingEmptyAndUsableIncompleteResults(string payload, int count, bool incomplete)
    {
        var clock = new TestClock();
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = CreateFactory(host, clock, () => Response(HttpStatusCode.OK, ++attempts == 1 ? Repository : payload));
        using var client = factory.CreateClient();
        using var first = await DiscoverAsync(client, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpireAsync(factory);
        using var refreshed = await DiscoverAsync(client, HttpStatusCode.OK);
        using var cached = await DiscoverAsync(client, HttpStatusCode.OK);

        Assert.Equal(count, cached.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(incomplete, Report(cached).GetProperty("isIncomplete").GetBoolean());
        Assert.Equal("fresh", Report(cached).GetProperty("status").GetString());
        Assert.Equal(clock.GetUtcNow(), Report(cached).GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.Equal(2, attempts);
        if (count > 0)
            Assert.Equal("43", Assert.Single(cached.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task UnexpectedFaultWithWarmCacheStillReturns500()
    {
        var clock = new TestClock();
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = CreateFactory(host, clock, () => ++attempts == 1
            ? Response(HttpStatusCode.OK, Repository)
            : throw new InvalidOperationException("private upstream implementation detail"));
        using var client = factory.CreateClient();
        using var first = await DiscoverAsync(client, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpireAsync(factory);
        using var fault = await DiscoverAsync(client, HttpStatusCode.InternalServerError);

        Assert.DoesNotContain("private upstream", fault.RootElement.ToString());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task IndependentHostsHaveIndependentCaches()
    {
        var clock = new TestClock();
        var attempts = 0;
        await using var host = new WebApplicationFactory<Program>();
        HttpResponseMessage Send()
        {
            Interlocked.Increment(ref attempts);
            return Response(HttpStatusCode.OK, Repository);
        }

        await using var firstFactory = CreateFactory(host, clock, Send);
        await using var secondFactory = CreateFactory(host, clock, Send);
        using var firstClient = firstFactory.CreateClient();
        using var secondClient = secondFactory.CreateClient();
        var firstCache = firstFactory.Services.GetRequiredService<IFusionCache>();
        Assert.False(firstCache.HasDistributedCache);
        Assert.False(firstCache.HasBackplane);
        using var first = await DiscoverAsync(firstClient, HttpStatusCode.OK);
        using var second = await DiscoverAsync(secondClient, HttpStatusCode.OK);
        await secondFactory.DisposeAsync();
        using var repeated = await DiscoverAsync(firstClient, HttpStatusCode.OK);

        Assert.Equal(2, attempts);
    }

    private static ValueTask ExpireAsync(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<IFusionCache>().ExpireAsync(
            ProviderCacheKey.Create("github", "dotnet", 30),
            new FusionCacheEntryOptions { IsFailSafeEnabled = true });

    private static WebApplicationFactory<Program> CreateFactory(
        WebApplicationFactory<Program> host,
        TimeProvider clock,
        Func<HttpResponseMessage> send)
        => host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(clock);
            services.AddHttpClient(nameof(GitHubClient))
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(_ => send()));
        }));

    private static HttpResponseMessage Response(HttpStatusCode status, string payload)
        => new(status) { Content = new StringContent(payload) };

    private static JsonElement Report(JsonDocument json)
        => Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());

    private static async Task<JsonDocument> DiscoverAsync(HttpClient client, HttpStatusCode expectedStatus)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync("/api/discovery?topic=dotnet&sources=github", cancellationToken);
        Assert.Equal(expectedStatus, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
