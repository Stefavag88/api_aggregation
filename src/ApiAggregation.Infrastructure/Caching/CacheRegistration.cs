using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ApiAggregation.Infrastructure.Caching;

public static class CacheRegistration
{
    private const string ProviderMemory = "DiscoveryProviderMemory";

    public static IServiceCollection AddProviderCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProviderCacheOptions>()
            .Bind(configuration.GetSection(ProviderCacheOptions.SectionName))
            .Validate(ProviderCacheOptions.HasValidSettings,
                "Caching requires a positive size and timeout, and policies with 0 < fresh duration < maximum age <= 365 days and 0 < retry interval <= maximum age.")
            .ValidateOnStart();

        services.AddKeyedSingleton<IMemoryCache>(ProviderMemory, (provider, _) => new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = provider.GetRequiredService<IOptions<ProviderCacheOptions>>().Value.SizeLimit
        }));

        services.AddFusionCache()
            .WithOptions(options => options.DisableTagging = true)
            .WithMemoryCache(provider => provider.GetRequiredKeyedService<IMemoryCache>(ProviderMemory));

        return services;
    }

    internal static FusionCacheEntryOptions CreateEntryOptions(ProviderCachePolicy policy) => new()
    {
        Duration = policy.FreshDuration,
        Size = 1,
        IsFailSafeEnabled = true,
        FailSafeMaxDuration = policy.MaximumAge,
        FailSafeThrottleDuration = policy.RetryInterval,
        JitterMaxDuration = TimeSpan.Zero,
        EagerRefreshThreshold = null,
        LockTimeout = Timeout.InfiniteTimeSpan,
        FactorySoftTimeout = Timeout.InfiniteTimeSpan,
        FactoryHardTimeout = Timeout.InfiniteTimeSpan,
        AllowTimedOutFactoryBackgroundCompletion = false
    };
}
