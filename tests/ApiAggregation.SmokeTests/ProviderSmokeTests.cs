using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure;
using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Providers.HackerNews;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiAggregation.SmokeTests;

public sealed class ProviderSmokeTests
{
    [LiveApiFact]
    public Task GitHubReturnsRepositoryContent()
        => VerifyProviderAsync<GitHubClient>("github", ContentType.Repository);

    [LiveApiFact]
    public Task DevToReturnsArticleContent()
        => VerifyProviderAsync<DevToClient>("devto", ContentType.Article);

    [LiveApiFact]
    public Task HackerNewsReturnsDiscussionContent()
        => VerifyProviderAsync<HackerNewsClient>("hackernews", ContentType.Discussion);

    private static async Task VerifyProviderAsync<TClient>(string source, ContentType contentType)
        where TClient : class, IDiscoveryProvider
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
        builder.Services.AddInfrastructure(builder.Configuration);

        using var host = builder.Build();
        var cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken);

        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var provider = scope.ServiceProvider.GetRequiredService<TClient>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

            var result = await FetchAsync(provider, source, linked.Token, cancellationToken);
            Assert.True(result.IsSuccess, $"{source} live request failed with provider error '{result.Error?.Code}'.");
            Assert.Equal(source, provider.Descriptor.Name);
            Assert.Equal(contentType, provider.Descriptor.Type);
            Assert.All(result.Value!.Items, item =>
            {
                Assert.Equal(source, item.Source);
                Assert.Equal(contentType, item.Type);
                Assert.False(string.IsNullOrWhiteSpace(item.Id));
                Assert.False(string.IsNullOrWhiteSpace(item.Title));
                Assert.True(item.Url.IsAbsoluteUri && item.Url.Scheme is "https" or "http");
                Assert.NotEqual(default, item.PublishedAt);
                Assert.Equal(TimeSpan.Zero, item.PublishedAt.Offset);
            });
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Result<ProviderData>> FetchAsync(
        IDiscoveryProvider provider, string source, CancellationToken budgetToken, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.FetchAsync("dotnet", budgetToken);
        }
        catch (OperationCanceledException) when (budgetToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{source} live request exceeded the five-second provider budget.");
        }
    }
}
