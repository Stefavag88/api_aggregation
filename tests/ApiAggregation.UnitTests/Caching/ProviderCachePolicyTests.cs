using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Infrastructure.Caching;

namespace ApiAggregation.UnitTests.Caching;

public sealed class ProviderCachePolicyTests
{
    [Theory]
    [InlineData(299, true, false)]
    [InlineData(300, true, true)]
    [InlineData(1799, true, true)]
    [InlineData(1800, false, false)]
    public void EnforcesFreshnessAndAbsoluteMaximumAge(int seconds, bool success, bool stale)
    {
        var fetchedAt = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var data = new ProviderData([], fetchedAt, IsStale: false, IsIncomplete: false);
        var snapshot = new CachedProviderSnapshot(data, fetchedAt.AddMinutes(5), fetchedAt.AddMinutes(30));

        var result = snapshot.ToResult(fetchedAt.AddSeconds(seconds));

        Assert.Equal(success, result.IsSuccess);
        if (success)
        {
            Assert.Equal(stale, result.Value!.IsStale);
            Assert.Equal(fetchedAt, result.Value.FetchedAt);
        }
        else
        {
            Assert.Equal("cache_expired", result.Error!.Code);
        }
    }

    [Fact]
    public void KeysSeparateProviderTopicAndCandidateLimit()
    {
        Assert.Equal("discovery:v1:github:dotnet:limit:30", ProviderCacheKey.Create("github", "dotnet", 30));
        var keys = new[]
        {
            ProviderCacheKey.Create("github", "dotnet", 30),
            ProviderCacheKey.Create("devto", "dotnet", 30),
            ProviderCacheKey.Create("github", "python", 30),
            ProviderCacheKey.Create("github", "dotnet", 20)
        };

        Assert.Equal(4, keys.Distinct().Count());
    }

    [Fact]
    public void ValidatesDefaultAndProviderSpecificPolicies()
    {
        var options = new ProviderCacheOptions();
        Assert.True(ProviderCacheOptions.HasValidSettings(options));
        Assert.Equal(TimeSpan.FromMinutes(5), options.GetPolicy("github").FreshDuration);

        options.Providers["github"] = new ProviderCachePolicy { FreshDuration = TimeSpan.FromMinutes(1) };
        Assert.Equal(TimeSpan.FromMinutes(1), options.GetPolicy("github").FreshDuration);
        options.Providers["github"].MaximumAge = TimeSpan.FromSeconds(30);
        Assert.False(ProviderCacheOptions.HasValidSettings(options));
    }

    [Theory]
    [InlineData(0, 300, 1800, 30, 5)]
    [InlineData(1, 0, 1800, 30, 5)]
    [InlineData(1, 300, 300, 30, 5)]
    [InlineData(1, 300, 1800, 0, 5)]
    [InlineData(1, 300, 1800, 30, 0)]
    public void RejectsInvalidLimits(int size, int fresh, int maximum, int retry, int timeout)
    {
        var options = new ProviderCacheOptions
        {
            SizeLimit = size,
            FetchTimeout = TimeSpan.FromSeconds(timeout),
            Default = new ProviderCachePolicy
            {
                FreshDuration = TimeSpan.FromSeconds(fresh),
                MaximumAge = TimeSpan.FromSeconds(maximum),
                RetryInterval = TimeSpan.FromSeconds(retry)
            }
        };

        Assert.False(ProviderCacheOptions.HasValidSettings(options));
    }
}
