namespace ApiAggregation.Infrastructure.Caching;

public sealed class ProviderCacheOptions
{
    public const string SectionName = "Caching";

    public int SizeLimit { get; set; } = 1000;
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public ProviderCachePolicy Default { get; set; } = new();
    public Dictionary<string, ProviderCachePolicy> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ProviderCachePolicy GetPolicy(string source)
        => Providers.GetValueOrDefault(source, Default);

    public static bool HasValidSettings(ProviderCacheOptions options)
        => options.SizeLimit > 0
            && options.FetchTimeout > TimeSpan.Zero
            && options.FetchTimeout <= TimeSpan.FromMilliseconds(uint.MaxValue - 1)
            && options.Default.IsValid
            && options.Providers.Values.All(policy => policy.IsValid);
}
