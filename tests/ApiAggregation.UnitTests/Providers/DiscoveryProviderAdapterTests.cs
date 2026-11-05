using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Providers;

namespace ApiAggregation.UnitTests.Providers;

public sealed class DiscoveryProviderAdapterTests
{
    [Fact]
    public async Task SuccessIsStampedAtCompletion()
    {
        var time = new TestClock();
        var completedAt = time.GetUtcNow().AddSeconds(1);
        var client = new Client((_, _) =>
        {
            time.UtcNow = completedAt;
            return Task.FromResult(AppResult.Success(new ProviderContent([], IsIncomplete: true)));
        });
        var adapter = new DiscoveryProviderAdapter(client, time);

        var result = await adapter.FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(completedAt, result.Value!.FetchedAt);
        Assert.True(result.Value.IsIncomplete);
        Assert.False(result.Value.IsStale);
    }

    [Fact]
    public async Task PreservesExpectedFailure()
    {
        var error = new AppError("timeout", ResultErrorCode.ServiceUnavailable);
        var client = new Client((_, _) => Task.FromResult(AppResult.Failure<ProviderContent>(error)));
        var adapter = new DiscoveryProviderAdapter(client, TimeProvider.System);

        var result = await adapter.FetchAsync("dotnet", TestContext.Current.CancellationToken);

        Assert.Same(error, result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task DoesNotTurnCancellationIntoUnavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new Client((_, ct) => Task.FromCanceled<Result<ProviderContent>>(ct));
        var adapter = new DiscoveryProviderAdapter(client, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.FetchAsync("dotnet", cts.Token));
    }

    [Fact]
    public async Task DoesNotHideProgrammingErrors()
    {
        var client = new Client((_, _) => throw new InvalidOperationException("bug"));
        var adapter = new DiscoveryProviderAdapter(client, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.FetchAsync("dotnet", TestContext.Current.CancellationToken));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Client(Func<string, CancellationToken, Task<Result<ProviderContent>>> fetch) : IExternalContentClient
    {
        public ProviderDescriptor Descriptor { get; } = new("github", ContentType.Repository);

        public Task<Result<ProviderContent>> FetchAsync(string topic, CancellationToken cancellationToken)
            => fetch(topic, cancellationToken);
    }
}
