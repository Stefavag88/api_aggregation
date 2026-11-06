using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Providers;
using ApiAggregation.Infrastructure.Providers.HackerNews;
using ApiAggregation.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace ApiAggregation.UnitTests.Providers;

public sealed class HackerNewsClientTests
{
    [Fact]
    public async Task SearchesStoriesAndReturnsDiscussionLinks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hackernews-stories.json");
        var payload = await File.ReadAllTextAsync(path, cancellationToken);
        using var http = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://hn.algolia.com/api/v1/search?tags=story&query=c%23&hitsPerPage=30&page=0", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Ok(payload));
        })) { BaseAddress = new Uri("https://hn.algolia.com/") };

        var result = await new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("c#", cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsIncomplete);
        Assert.Collection(result.Value.Items, item =>
        {
            Assert.Equal("12345", item.Id);
            Assert.Equal("hackernews", item.Source);
            Assert.Equal(ContentType.Discussion, item.Type);
            Assert.Equal("Ask HN: Building with .NET", item.Title);
            Assert.Equal(new Uri("https://news.ycombinator.com/item?id=12345"), item.Url);
            Assert.Equal("What are you building?", item.Description);
            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), item.PublishedAt);
            Assert.Equal(new[] { "story", "author_example", "story_12345" }, item.Tags.ToArray());
        }, item => Assert.Equal(new Uri("https://news.ycombinator.com/item?id=12346"), item.Url));
    }

    [Fact]
    public async Task EmptyHitsAreSuccessful()
    {
        using var http = Respond("""{"hits":[]}""");

        var result = await new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
        Assert.False(result.Value.IsIncomplete);
    }

    [Fact]
    public async Task InvalidHitsDoNotDiscardValidSiblings()
    {
        using var http = Respond("""
            {"hits":[{"objectID":"1","created_at":"yesterday"},
                     {"objectID":"2","title":"Valid","created_at":"2026-01-02T03:04:05Z"}]}
            """);

        var result = await new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsIncomplete);
        Assert.Equal("2", Assert.Single(result.Value.Items).Id);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"hits":{}}""")]
    [InlineData("""{"hits":[null,{"objectID":"abc","title":"Invalid","created_at":"2026-01-02T03:04:05Z"}]}""")]
    public async Task InvalidPayloadOrAllInvalidHitsAreFailure(string payload)
    {
        using var http = Respond(payload);

        var result = await new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance, Options.Create(new ProviderDiscoveryOptions())).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_payload", result.Error!.Code);
    }

    private static HttpClient Respond(string payload)
        => new(new StubHttpMessageHandler((_, _) => Task.FromResult(Ok(payload))))
        {
            BaseAddress = new Uri("https://hn.algolia.com/")
        };

    private static HttpResponseMessage Ok(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload)
    };
}
