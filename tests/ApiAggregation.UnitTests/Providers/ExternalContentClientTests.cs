using ApiAggregation.Infrastructure.Providers;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace ApiAggregation.UnitTests.Providers;

public sealed class ExternalContentClientTests
{
    [Fact]
    public async Task SuccessIsStampedAfterTheResponseIsProcessed()
    {
        var clock = new TestClock();
        var completedAt = clock.GetUtcNow().AddSeconds(1);
        using var http = CreateClient((_, _) => Task.FromResult<HttpResponseMessage>(
            new CompletionResponse(() => clock.UtcNow = completedAt)
            {
                Content = new StringContent("""{"items":[],"incomplete_results":true}""")
            }));
        var provider = CreateProvider(http, clock);

        var result = await provider.FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(completedAt, result.Value!.FetchedAt);
        Assert.True(result.Value.IsIncomplete);
        Assert.False(result.Value.IsStale);
    }

    [Fact]
    public async Task PreservesExpectedFailure()
    {
        using var http = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var provider = CreateProvider(http, TimeProvider.System);

        var result = await provider.FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.Same(ProviderErrors.RateLimited, result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task DoesNotTurnCancellationIntoUnavailable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = CreateClient((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        var provider = CreateProvider(http, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync("dotnet", cancellation.Token));
    }

    [Fact]
    public async Task DoesNotHideProgrammingErrors()
    {
        using var http = CreateClient((_, _) => throw new InvalidOperationException("bug"));
        var provider = CreateProvider(http, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchAsync("dotnet", TestContext.Current.CancellationToken));
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new StubHttpMessageHandler(send)) { BaseAddress = new Uri("https://api.github.com/") };

    private static GitHubClient CreateProvider(HttpClient http, TimeProvider clock)
        => new(http, new GitHubRateLimitPolicy(), NullLogger<GitHubClient>.Instance,
            Options.Create(new ProviderDiscoveryOptions()), clock);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CompletionResponse(Action onDispose) : HttpResponseMessage(HttpStatusCode.OK)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                onDispose();
        }
    }
}
