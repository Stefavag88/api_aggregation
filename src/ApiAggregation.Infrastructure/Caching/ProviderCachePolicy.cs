namespace ApiAggregation.Infrastructure.Caching;

public sealed class ProviderCachePolicy
{
    public TimeSpan FreshDuration { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumAge { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsValid => FreshDuration > TimeSpan.Zero
        && MaximumAge > FreshDuration
        && MaximumAge <= TimeSpan.FromDays(365)
        && RetryInterval > TimeSpan.Zero
        && RetryInterval <= MaximumAge;
}
