using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Providers;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace ApiAggregation.UnitTests.Providers;

public sealed class GitHubClientTests
{
    [Fact]
    public async Task RequestsOnePageOfPublicRepositoriesAndMapsFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-repositories.json"), cancellationToken);
        using var http = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/search/repositories?q=c%23%20is%3Apublic&per_page=30&page=1", request.RequestUri!.AbsoluteUri);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
        });

        var result = await new GitHubClient(http, new GitHubRateLimitPolicy(), NullLogger<GitHubClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("c#", cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsIncomplete);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("1296269", item.Id);
        Assert.Equal("github", item.Source);
        Assert.Equal(ContentType.Repository, item.Type);
        Assert.Equal("example", item.Title);
        Assert.Equal(new Uri("https://github.com/octocat/example"), item.Url);
        Assert.Equal("A sample repository", item.Description);
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal(new[] { "dotnet", "csharp" }, item.Tags.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyItemsAreValidAndPreserveUpstreamCompleteness(bool incomplete)
    {
        var flag = incomplete ? "true" : "false";
        using var http = Respond($$"""{"items":[],"incomplete_results":{{flag}}}""");

        var result = await new GitHubClient(http, new GitHubRateLimitPolicy(), NullLogger<GitHubClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(incomplete, result.Value.IsIncomplete);
    }

    [Fact]
    public async Task InvalidRecordsDoNotDiscardValidSiblings()
    {
        using var http = Respond("""
            {"items":[{"id":1,"name":"broken","created_at":"yesterday"},
                      {"id":2,"name":"valid","html_url":"https://github.com/octocat/valid","created_at":"2020-01-02T03:04:05Z"}]}
            """);

        var result = await new GitHubClient(http, new GitHubRateLimitPolicy(), NullLogger<GitHubClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsIncomplete);
        Assert.Equal("2", Assert.Single(result.Value.Items).Id);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"items":{}}""")]
    [InlineData("""{"items":[null,{"id":1,"name":"broken","html_url":"file:///broken","created_at":"2020-01-02T03:04:05Z"}]}""")]
    public async Task InvalidPayloadOrAllInvalidRecordsAreFailure(string payload)
    {
        using var http = Respond(payload);

        var result = await new GitHubClient(http, new GitHubRateLimitPolicy(), NullLogger<GitHubClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_payload", result.Error!.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, false, "upstream_error")]
    [InlineData(HttpStatusCode.Unauthorized, false, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, false, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, true, "rate_limited")]
    [InlineData(HttpStatusCode.TooManyRequests, false, "rate_limited")]
    public async Task ClassifiesExpectedHttpFailures(HttpStatusCode status, bool quotaExhausted, string code)
    {
        using var http = CreateClient(_ =>
        {
            var response = new HttpResponseMessage(status);

            if (quotaExhausted)
                response.Headers.Add("X-RateLimit-Remaining", "0");

            return response;
        });

        var result = await new GitHubClient(http, new GitHubRateLimitPolicy(), NullLogger<GitHubClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.Equal(code, result.Error!.Code);
    }

    private static HttpClient Respond(string payload) => CreateClient(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new StubHttpMessageHandler((request, _) => Task.FromResult(send(request))))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
}
