using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.UnitTests.Support;
using System.Net;

namespace ApiAggregation.UnitTests.Providers;

public sealed class DevToClientTests
{
    [Fact]
    public async Task UsesExactTagMatching()
    {
        using var http = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://dev.to/api/articles?tag=dotnet&per_page=30&page=1", request.RequestUri!.AbsoluteUri);
            return Ok("[]");
        });

        var result = await new DevToClient(http).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
        Assert.False(result.Value.IsIncomplete);
    }

    [Fact]
    public async Task MapsPublishedDateAndArticleFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "devto-articles.json");
        var payload = await File.ReadAllTextAsync(path, cancellationToken);
        using var http = CreateClient(_ => Ok(payload));

        var result = await new DevToClient(http).FetchAsync("dotnet", cancellationToken);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("123", item.Id);
        Assert.Equal("devto", item.Source);
        Assert.Equal(ContentType.Article, item.Type);
        Assert.Equal("Building with .NET", item.Title);
        Assert.Equal(new Uri("https://dev.to/example/building-with-dotnet"), item.Url);
        Assert.Equal("A sample article", item.Description);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal(TimeSpan.Zero, item.PublishedAt.Offset);
        Assert.Equal(new[] { "dotnet", "csharp" }, item.Tags.ToArray());
    }

    [Fact]
    public async Task InvalidRecordsDoNotDiscardValidSiblings()
    {
        using var http = CreateClient(_ => Ok("""
            [{"id":1,"published_at":"yesterday"},
             {"id":2,"title":"Valid","url":"https://dev.to/example/valid","published_at":"2026-01-02T03:04:05Z"}]
            """));

        var result = await new DevToClient(http).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsIncomplete);
        Assert.Equal("2", Assert.Single(result.Value.Items).Id);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""[null,{"id":1,"title":"Invalid","url":"file:///example","published_at":"2026-01-02T03:04:05Z"}]""")]
    public async Task InvalidPayloadOrAllInvalidRecordsAreFailure(string payload)
    {
        using var http = CreateClient(_ => Ok(payload));

        var result = await new DevToClient(http).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_payload", result.Error!.Code);
    }

    private static HttpResponseMessage Ok(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload)
    };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new StubHttpMessageHandler((request, _) => Task.FromResult(send(request))))
        {
            BaseAddress = new Uri("https://dev.to/")
        };
}
