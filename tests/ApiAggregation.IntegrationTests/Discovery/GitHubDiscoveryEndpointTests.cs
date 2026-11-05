using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Discovery;

public sealed class GitHubDiscoveryEndpointTests
{
    [Fact]
    public async Task DisabledGitHubIsAbsentFromDiscoveryAndProviderMetadata()
    {
        await using var factory = new DisabledGitHubFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<GitHubClient>());
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IDiscoveryProvider>(), provider => provider.Descriptor.Name == "github");
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<ProviderDescriptor>(), descriptor => descriptor.Name == "github");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync("/api/discovery?topic=dotnet&sources=github", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("sources", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("integration-test-token")]
    public async Task ReturnsMappedRepositoryThroughRegisteredHttpClient(string token)
    {
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = host.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { [$"{GitHubOptions.SectionName}:Token"] = token }));
            builder.ConfigureTestServices(services => services.AddHttpClient(nameof(GitHubClient))
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub(request =>
                {
                    Assert.Equal("api.github.com", request.RequestUri!.Host);
                    Assert.Equal("/search/repositories", request.RequestUri.AbsolutePath);
                    Assert.Equal("?q=dotnet%20is%3Apublic&per_page=30&page=1", request.RequestUri.Query);
                    Assert.Equal("ApiAggregationService/1.0", request.Headers.UserAgent.ToString());
                    Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);

                    if (token.Length == 0)
                    {
                        Assert.Null(request.Headers.Authorization);
                    }
                    else
                    {
                        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                        Assert.Equal(token, request.Headers.Authorization?.Parameter);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {"items":[{"id":42,"name":"sample","html_url":"https://github.com/octocat/sample","created_at":"2020-01-02T03:04:05Z"}]}
                            """)
                    };
                })));
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/discovery?sources=github&topic=dotnet", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("42", item.GetProperty("id").GetString());
        Assert.Equal("repository", item.GetProperty("type").GetString());
        Assert.Equal("sample", item.GetProperty("title").GetString());

        var provider = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal("github", provider.GetProperty("source").GetString());
        Assert.Equal("fresh", provider.GetProperty("status").GetString());
    }

    private sealed class DisabledGitHubFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{GitHubOptions.SectionName}:Enabled"] = "false",
                    [$"{GitHubOptions.SectionName}:BaseAddress"] = "invalid"
                }));

            return base.CreateHost(builder);
        }
    }
}
