using System.Collections.Immutable;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.UnitTests.Support;

namespace ApiAggregation.UnitTests.Discovery;

public sealed class DiscoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private static ProviderData Data(params DiscoveryItem[] items) => new([.. items], Now, false, false);
    private static DiscoveryItem Item(string id, string source = "github", string title = "dotnet", int daysAgo = 0,
        ContentType type = ContentType.Repository) => new(id, source, type, title,
            new Uri($"https://example.test/{source}/{id}"), null, Now.AddDays(-daysAgo), []);
    private static DiscoveryQuery Query(params string[] sources) => new("dotnet", [.. sources], null, null, null,
        SortField.Date, SortDirection.Desc, 30);

    [Fact]
    public async Task StartsEverySelectedProviderBeforeWaiting()
    {
        var providers = new[] { new GatedProvider("github", Data()), new GatedProvider("devto", Data()), new GatedProvider("hackernews", Data()) };
        var operation = new DiscoveryService(providers).DiscoverAsync(Query("github", "devto", "hackernews"), TestContext.Current.CancellationToken);
        try
        {
            await Task.WhenAll(providers.Select(p => p.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(operation.IsCompleted);
        }
        finally { foreach (var provider in providers) provider.Release.TrySetResult(); }
        Assert.True((await operation).IsSuccess);
    }

    [Fact]
    public async Task PreservesSuccessfulResultsOnPartialFailure()
    {
        IDiscoveryProvider[] providers = [new Stub("github", Data(Item("1"))),
            new Stub("devto", AppResult.ServiceUnavailable<ProviderData>("timeout"))];
        var result = await new DiscoveryService(providers).DiscoverAsync(Query("github", "devto"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsPartial);
        Assert.Single(result.Value.Items);
        Assert.Equal(ProviderStatus.Unavailable, result.Value.Providers.Single(p => p.Source == "devto").Status);
    }

    [Fact]
    public async Task AllFailuresReturnTypedServiceUnavailableWithReports()
    {
        var service = new DiscoveryService([new Stub("github", AppResult.ServiceUnavailable<ProviderData>("timeout"))]);
        var result = await service.DiscoverAsync(Query("github"), TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(ResultErrorCode.ServiceUnavailable, result.Error!.StatusCode);
        Assert.Single(Assert.IsType<ImmutableArray<ProviderReport>>(result.Error.Meta!["providers"]));
    }

    [Fact]
    public async Task EmptySuccessfulProviderIsNotAnOutage()
    {
        var result = await new DiscoveryService([new Stub("github", Data())])
            .DiscoverAsync(Query("github"), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsPartial);
        Assert.Empty(result.Value.Items);
    }

    [Fact]
    public async Task OnlySelectedSourcesAreContacted()
    {
        var excluded = new GatedProvider("devto", Data());
        var result = await new DiscoveryService([new Stub("github", Data()), excluded])
            .DiscoverAsync(Query("github"), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, excluded.CallCount);
        Assert.Single(result.Value!.Providers);
    }

    [Fact]
    public async Task CancellationPropagatesToOutstandingProviders()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new GatedProvider("github", Data());
        var task = new DiscoveryService([provider]).DiscoverAsync(Query("github"), cancellation.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task FiltersInclusiveDatesAndTypeBeforeCountingAndLimiting()
    {
        var data = Data(Item("older", daysAgo: 3), Item("start", daysAgo: 2), Item("end"), Item("article", type: ContentType.Article));
        var query = Query("github") with { From = Now.AddDays(-2), To = Now, Type = ContentType.Repository, Limit = 1 };
        var result = (await new DiscoveryService([new Stub("github", data)]).DiscoverAsync(query, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(1, result.ReturnedCount);
        Assert.Equal("end", Assert.Single(result.Items).Id);
        Assert.Equal(new[] { "older", "start", "end", "article" }, data.Items.Select(i => i.Id));
    }

    [Theory]
    [InlineData(SortDirection.Asc, "a", "b", "c")]
    [InlineData(SortDirection.Desc, "c", "a", "b")]
    public async Task TitleSortUsesOrdinalIgnoreCaseWithStableTies(SortDirection direction, string first, string second, string third)
    {
        var data = Data(Item("b", title: "ALPHA"), Item("c", title: "zeta"), Item("a", title: "alpha"));
        var query = Query("github") with { SortBy = SortField.Title, SortDirection = direction };
        var result = (await new DiscoveryService([new Stub("github", data)]).DiscoverAsync(query, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(new[] { first, second, third }, result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task DeduplicatesWithinSourceButPreservesCrossSourceItemsAndDateTies()
    {
        var github = Data(Item("1"), Item("1"), Item("0"));
        var devto = Data(Item("1", "devto"));
        var result = (await new DiscoveryService([new Stub("github", github), new Stub("devto", devto)])
            .DiscoverAsync(Query("github", "devto"), TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(new[] { "devto:1", "github:0", "github:1" }, result.Items.Select(i => $"{i.Source}:{i.Id}"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StaleOrIncompleteResultsAreSuccessfulButPartial(bool stale, bool incomplete)
    {
        var data = Data() with { IsStale = stale, IsIncomplete = incomplete };
        var result = await new DiscoveryService([new Stub("github", data)])
            .DiscoverAsync(Query("github"), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsPartial);
    }

    private sealed class Stub(string source, Result<ProviderData> result) : IDiscoveryProvider
    {
        public ProviderDescriptor Descriptor { get; } = new(source, ContentType.Repository);
        public Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
