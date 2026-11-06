using ApiAggregation.Application.Features.Statistics;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Statistics;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Resilience;

public sealed class ProviderRetryTests
{
    private const string Query = "/api/discovery?topic=dotnet&sources=github";

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.OK, HttpStatusCode.OK, 2, HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.RequestTimeout, HttpStatusCode.OK, HttpStatusCode.OK, 2, HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.OK, 3, HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, 3, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.OK, 1, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.OK, HttpStatusCode.OK, 1, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK, 1, HttpStatusCode.ServiceUnavailable)]
    public async Task RetriesOnlyTransientStatusesAndCountsEachAttempt(
        HttpStatusCode first, HttpStatusCode second, HttpStatusCode third, int expectedAttempts, HttpStatusCode expectedStatus)
    {
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var factory = CreateFactory(host, (_, _) => Task.FromResult(Response(
            Interlocked.Increment(ref attempts) switch
            {
                1 => first,
                2 => second,
                _ => third
            })));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync(Query, cancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedAttempts, attempts);
        await factory.Services.GetRequiredService<InMemoryStatisticsStore>().StopAsync(cancellationToken);
        var stats = factory.Services.GetRequiredService<IStatisticsReader>().GetSnapshot().Providers
            .Single(provider => provider.Source == "github");
        var successes = expectedStatus == HttpStatusCode.OK ? 1 : 0;
        Assert.Equal(expectedAttempts, stats.TotalRequests);
        Assert.Equal(successes, stats.SuccessCount);
        Assert.Equal(expectedAttempts - successes, stats.FailureCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task RetriesTransportFailuresAndAttemptTimeouts(bool stall, bool recover)
    {
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var factory = CreateFactory(host, async (_, token) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 2 && recover)
                return Response(HttpStatusCode.OK);

            if (stall)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);

            throw new HttpRequestException("Connection failed.");
        });
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync(Query, cancellationToken);

        Assert.Equal(recover ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var expectedAttempts = recover ? 2 : 3;
        Assert.Equal(expectedAttempts, attempts);
        if (!recover)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var report = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
            Assert.Equal("timeout", report.GetProperty("errorCode").GetString());
        }

        await factory.Services.GetRequiredService<InMemoryStatisticsStore>().StopAsync(cancellationToken);
        var stats = factory.Services.GetRequiredService<IStatisticsReader>().GetSnapshot().Providers
            .Single(provider => provider.Source == "github");
        Assert.Equal(expectedAttempts, stats.TotalRequests);
        var expectedCancellations = stall ? expectedAttempts - (recover ? 1 : 0) : 0;
        Assert.Equal(expectedCancellations, stats.CanceledCount);
    }

    private static WebApplicationFactory<Program> CreateFactory(WebApplicationFactory<Program> host,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => host.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ProviderResilience:AttemptTimeout"] = "00:00:00.200",
                    ["ProviderResilience:RetryDelay"] = "00:00:00.001"
                }));
            builder.ConfigureTestServices(services => services.AddHttpClient(nameof(GitHubClient))
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(send)));
        });

    private static HttpResponseMessage Response(HttpStatusCode status)
        => new(status) { Content = new StringContent("""{"items":[]}""") };
}
