using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Discovery;

public sealed class DevToDiscoveryEndpointTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task CombinesRegisteredProvidersAndPreservesGitHubResultsWhenDevFails(HttpStatusCode devStatus, bool empty)
    {
        var devAvailable = devStatus == HttpStatusCode.OK;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(nameof(GitHubClient))
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"items":[{"id":42,"name":"repository","html_url":"https://github.com/octocat/sample","created_at":"2020-01-02T03:04:05Z"}]}
                        """)
                }));

            services.AddHttpClient(nameof(DevToClient))
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(request =>
                {
                    Assert.Equal("https://dev.to/api/articles?tag=dotnet&per_page=30&page=1", request.RequestUri!.AbsoluteUri);
                    Assert.Equal("application/vnd.forem.api-v1+json", Assert.Single(request.Headers.Accept).MediaType);
                    Assert.False(request.Headers.Contains("api-key"));
                    Assert.Null(request.Headers.Authorization);

                    var payload = empty ? "[]" : """
                        [{"id":42,"title":"article","url":"https://dev.to/example/sample","published_at":"2021-01-02T03:04:05Z","tag_list":["dotnet"]}]
                        """;

                    return new HttpResponseMessage(devStatus) { Content = new StringContent(payload) };
                }));
        }));

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/discovery?topic=dotnet&sources=github,devto", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(!devAvailable, json.RootElement.GetProperty("isPartial").GetBoolean());

        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(devAvailable && !empty ? new[] { "article", "repository" } : ["repository"],
            items.Select(item => item.GetProperty("type").GetString()));

        var devReport = json.RootElement.GetProperty("providers").EnumerateArray()
            .Single(provider => provider.GetProperty("source").GetString() == "devto");
        Assert.Equal(devAvailable ? "fresh" : "unavailable", devReport.GetProperty("status").GetString());
        Assert.Equal(devAvailable ? null : "upstream_error", devReport.GetProperty("errorCode").GetString());
    }
}
