using ApiAggregation.Infrastructure.Caching;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using ZiggyCreatures.Caching.Fusion;

namespace ApiAggregation.IntegrationTests.Discovery;

public sealed class ProviderCandidateLimitTests
{
    [Fact]
    public async Task ConfiguredLimitControlsRequestMappingAndCacheKey()
    {
        var attempts = 0;
        await using var host = new AuthenticatedApiFactory();
        await using var configured = WithCandidateLimit(host, "1");
        await using var factory = configured.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(nameof(GitHubClient)).ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(request =>
            {
                Interlocked.Increment(ref attempts);
                Assert.Contains("&per_page=1&", request.RequestUri!.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"items":[
                          {"id":1,"name":"first","html_url":"https://github.com/example/first","created_at":"2026-01-02T03:04:05Z"},
                          {"id":2,"name":"second","html_url":"https://github.com/example/second","created_at":"2026-01-02T03:04:05Z"}
                        ]}
                        """)
                };
            }))));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync("/api/discovery?topic=dotnet&sources=github&limit=50", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("1", Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        var cache = factory.Services.GetRequiredService<IFusionCache>();
        var cached = await cache.TryGetAsync<CachedProviderSnapshot>(
            ProviderCacheKey.Create("github", "dotnet", 1), token: cancellationToken);
        Assert.True(cached.HasValue);
        Assert.Single(cached.Value.Data.Items);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("31")]
    public async Task RejectsOutOfRangeCandidateLimitAtStartup(string limit)
    {
        await using var host = new AuthenticatedApiFactory();
        await using var factory = WithCandidateLimit(host, limit);
        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Discovery:ProviderCandidateLimit", exception.Message);
    }

    private static WebApplicationFactory<Program> WithCandidateLimit(WebApplicationFactory<Program> host, string limit)
        => host.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Discovery:ProviderCandidateLimit"] = limit
            })));
}
