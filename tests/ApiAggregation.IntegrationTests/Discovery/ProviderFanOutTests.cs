using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Providers.HackerNews;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Discovery;

public sealed class ProviderFanOutTests
{
    private static readonly (string Client, string Source, string Payload)[] Providers =
    [
        (nameof(GitHubClient), "github", """
            {"items":[{"id":42,"name":"example","html_url":"https://github.com/example/repo","created_at":"2026-01-02T03:04:05Z"}]}
            """),
        (nameof(DevToClient), "devto", """
            [{"id":42,"title":"example","url":"https://dev.to/example/repo","published_at":"2026-01-02T03:04:05Z"}]
            """),
        (nameof(HackerNewsClient), "hackernews", """
            {"hits":[{"objectID":"42","title":"example","created_at":"2026-01-02T03:04:05Z"}]}
            """)
    ];

    [Fact]
    public async Task AllThreeTransportsStartBeforeAnyCompletes()
    {
        var entered = new ConcurrentQueue<string>();
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = WithTransports(host, async (source, payload, cancellationToken) =>
        {
            entered.Enqueue(source);

            if (Interlocked.Increment(ref startedCount) == Providers.Length)
                allEntered.TrySetResult();

            await release.Task.WaitAsync(cancellationToken);
            return Ok(payload);
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        var pending = client.GetAsync("/api/discovery?topic=dotnet", cancellationToken);

        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            Assert.Equal(new[] { "devto", "github", "hackernews" }, entered.Order(StringComparer.Ordinal).ToArray());
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        using var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(3, json.RootElement.GetProperty("providers").GetArrayLength());
        Assert.Equal(3, json.RootElement.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("github,devto,hackernews", true)]
    [InlineData("hackernews", false)]
    public async Task ReportsPartialFailureAndContactsOnlySelectedSources(string sources, bool partial)
    {
        var requested = new ConcurrentQueue<string>();
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = WithTransports(host, (source, payload, _) =>
        {
            requested.Enqueue(source);
            var response = partial && source == "hackernews"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Ok(payload);

            return Task.FromResult(response);
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/discovery?topic=dotnet&sources={sources}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expectedAttempts = sources.Split(',').Concat(partial ? ["hackernews"] : Array.Empty<string>());
        Assert.Equal(expectedAttempts.Order(StringComparer.Ordinal), requested.Order(StringComparer.Ordinal));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(partial, json.RootElement.GetProperty("isPartial").GetBoolean());
        Assert.Equal(partial ? 2 : 1, json.RootElement.GetProperty("items").GetArrayLength());
        var hackerNews = json.RootElement.GetProperty("providers").EnumerateArray()
            .Single(provider => provider.GetProperty("source").GetString() == "hackernews");
        Assert.Equal(partial ? "unavailable" : "fresh", hackerNews.GetProperty("status").GetString());
    }

    private static WebApplicationFactory<Program> WithTransports(WebApplicationFactory<Program> host,
        Func<string, string, CancellationToken, Task<HttpResponseMessage>> send)
        => host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var (client, source, payload) in Providers)
            {
                services.AddHttpClient(client)
                    .ConfigurePrimaryHttpMessageHandler(() => new ProviderHttpStub((_, token) => send(source, payload, token)));
            }
        }));

    private static HttpResponseMessage Ok(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload)
    };
}
