using System.Net;
using System.Text.Json;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.IntegrationTests.Support;

namespace ApiAggregation.IntegrationTests.Discovery;

public sealed class DiscoveryEndpointTests
{
    private static readonly ProviderData Empty = new([], DateTimeOffset.Parse("2026-11-05T10:00:00Z"), false, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessHasStablePublicContract(bool partial)
    {
        var data = Empty with { Items = [new("1", "github", ContentType.Repository, "example",
            new Uri("https://example.test/repo"), null, Empty.FetchedAt, ["dotnet"])] };
        await using var factory = new ApiFactory(new Provider("github", data),
            new Provider("devto", partial ? AppResult.ServiceUnavailable<ProviderData>("provider.timeout") : Empty));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/discovery?topic=DOTNET", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("dotnet", json.RootElement.GetProperty("topic").GetString());
        Assert.Equal(partial, json.RootElement.GetProperty("isPartial").GetBoolean());
        Assert.Equal("repository", json.RootElement.GetProperty("items")[0].GetProperty("type").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("matchedCount").GetInt32());
        Assert.Equal("fresh", json.RootElement.GetProperty("providers")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task EmptySuccessReturns200()
    {
        await using var factory = new ApiFactory(new Provider("github", Empty));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/discovery?topic=dotnet", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, json.RootElement.GetProperty("returnedCount").GetInt32());
    }

    [Fact]
    public async Task AllUnavailableReturns503WithReports()
    {
        await using var factory = new ApiFactory(new Provider("github", AppResult.ServiceUnavailable<ProviderData>("provider.timeout")));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/discovery?topic=dotnet", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("unavailable", json.RootElement.GetProperty("providers")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvalidTopicDoesNotContactProviders()
    {
        var provider = new Provider("github", Empty);
        await using var factory = new ApiFactory(provider);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/discovery?topic=c%23", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, provider.CallCount);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("topic", out _));
        Assert.True(json.RootElement.TryGetProperty("validationErrors", out _));
    }

    [Fact]
    public async Task LimitAboveMaximumReturnsFieldErrorWithoutContactingProviders()
    {
        var provider = new Provider("github", Empty);
        await using var factory = new ApiFactory(provider);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync("/api/discovery?topic=dotnet&limit=120", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, provider.CallCount);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var error = Assert.Single(json.RootElement.GetProperty("errors").GetProperty("limit").EnumerateArray());
        Assert.Equal("Must be between 1 and 90.", error.GetString());
        var issue = Assert.Single(json.RootElement.GetProperty("validationErrors").EnumerateArray());
        Assert.Equal("limit", issue.GetProperty("field").GetString());
        Assert.Equal("validation.outOfRange", issue.GetProperty("code").GetString());
        Assert.Equal(error.GetString(), issue.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnexpectedFailureReturnsSanitized500()
    {
        await using var factory = new ApiFactory(new Provider("github", Empty, throws: true));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/discovery?topic=dotnet", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret", body);
        using var json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task LivenessReturns200()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class Provider(string name, Result<ProviderData> result, bool throws = false) : IDiscoveryProvider
    {
        public int CallCount { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new(name, ContentType.Repository);
        public Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
        {
            CallCount++;
            if (throws) throw new InvalidOperationException("secret upstream token");
            return Task.FromResult(result);
        }
    }
}
