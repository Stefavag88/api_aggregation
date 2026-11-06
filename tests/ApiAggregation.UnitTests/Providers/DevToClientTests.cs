using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Providers;
using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
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

        var result = await new DevToClient(http, NullLogger<DevToClient>.Instance, Options.Create(new ProviderDiscoveryOptions()), TimeProvider.System).FetchAsync("dotnet", TestContext.Current.CancellationToken);

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

        var result = await new DevToClient(http, NullLogger<DevToClient>.Instance, Options.Create(new ProviderDiscoveryOptions()), TimeProvider.System).FetchAsync("dotnet", cancellationToken);

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
    public async Task InvalidRecordsAreLoggedOnceAndDoNotDiscardValidSiblings()
    {
        using var http = CreateClient(_ => Ok("""
            [{"id":1,"published_at":"yesterday"},null,
             {"id":2,"title":"Valid","url":"https://dev.to/example/valid","published_at":"2026-01-02T03:04:05Z"}]
            """));

        var logger = new RecordingLogger();
        var result = await new DevToClient(http, logger, Options.Create(new ProviderDiscoveryOptions()), TimeProvider.System).FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsIncomplete);
        Assert.Equal("2", Assert.Single(result.Value.Items).Id);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        var properties = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(warning.State);
        Assert.Contains(new KeyValuePair<string, object?>("Provider", "devto"), properties);
        Assert.Contains(new KeyValuePair<string, object?>("SkippedCount", 2), properties);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""[null,{"id":1,"title":"Invalid","url":"file:///example","published_at":"2026-01-02T03:04:05Z"}]""")]
    public async Task InvalidPayloadOrAllInvalidRecordsAreFailure(string payload)
    {
        using var http = CreateClient(_ => Ok(payload));

        var result = await new DevToClient(http, NullLogger<DevToClient>.Instance, Options.Create(new ProviderDiscoveryOptions()), TimeProvider.System).FetchAsync("dotnet", TestContext.Current.CancellationToken);

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

    private sealed class RecordingLogger : ILogger<DevToClient>
    {
        public List<(LogLevel Level, object? State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, state));
    }
}
