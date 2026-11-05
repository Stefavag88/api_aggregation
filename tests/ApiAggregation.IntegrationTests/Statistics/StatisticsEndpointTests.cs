using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Statistics;

public sealed class StatisticsEndpointTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, """{"items":[]}""", false, 1, 0)]
    [InlineData(HttpStatusCode.OK, "{", true, 1, 0)]
    [InlineData(HttpStatusCode.BadGateway, "", true, 0, 1)]
    public async Task CountsOnlyContactedProvidersAndUsesTransportOutcome(
        HttpStatusCode upstreamStatus, string payload, bool discoveryUnavailable, int successes, int failures)
    {
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(nameof(GitHubClient))
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(_ => new HttpResponseMessage(upstreamStatus)
                {
                    Content = new StringContent(payload)
                }));
        }));

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        using var before = await GetStatisticsAsync(client, cancellationToken);
        Assert.Equal("transportToHeaders", before.RootElement.GetProperty("measurementScope").GetString());
        Assert.Equal("process", before.RootElement.GetProperty("counterScope").GetString());
        Assert.True(before.RootElement.GetProperty("startedAt").GetDateTimeOffset() <= before.RootElement.GetProperty("snapshotAt").GetDateTimeOffset());

        var initial = before.RootElement.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(new[] { "devto", "github", "hackernews" }, initial.Select(provider => provider.GetProperty("source").GetString()));
        Assert.All(initial, provider =>
        {
            Assert.Equal(0, provider.GetProperty("totalRequests").GetInt64());
            Assert.Equal(JsonValueKind.Null, provider.GetProperty("averageResponseTimeMs").ValueKind);
        });

        using var discovery = await client.GetAsync("/api/discovery?topic=dotnet&sources=github", cancellationToken);
        Assert.Equal(discoveryUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, discovery.StatusCode);

        using var after = await GetStatisticsAsync(client, cancellationToken);
        foreach (var provider in after.RootElement.GetProperty("providers").EnumerateArray())
        {
            var contacted = provider.GetProperty("source").GetString() == "github";
            Assert.Equal(contacted ? 1 : 0, provider.GetProperty("totalRequests").GetInt64());
            Assert.Equal(contacted ? successes : 0, provider.GetProperty("successCount").GetInt64());
            Assert.Equal(contacted ? failures : 0, provider.GetProperty("failureCount").GetInt64());
            Assert.Equal(contacted ? 1 : 0, new[] { "fast", "average", "slow" }
                .Sum(bucket => provider.GetProperty(bucket).GetProperty("count").GetInt64()));
        }
    }

    private static async Task<JsonDocument> GetStatisticsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/statistics", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
