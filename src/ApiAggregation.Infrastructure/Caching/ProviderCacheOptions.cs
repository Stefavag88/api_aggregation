namespace ApiAggregation.Infrastructure.Caching;

/// <summary>
/// Configures cache capacity, fetch budgets, and default and per-provider freshness policies.
/// </summary>
public sealed class ProviderCacheOptions
{
    public const string SectionName = "Caching";

    public int SizeLimit { get; set; } = 1000;
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public ProviderCachePolicy Default { get; set; } = new();
    public Dictionary<string, ProviderCachePolicy> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Selects the configured provider policy or the default policy.
    /// </summary>
    /// <param name="source">The provider name to look up.</param>
    /// <returns>The effective policy for the provider.</returns>
    public ProviderCachePolicy GetPolicy(string source)
        => Providers.GetValueOrDefault(source, Default);

    /// <summary>
    /// Checks that capacity, fetch timeout, and all configured policy durations are valid.
    /// </summary>
    /// <param name="options">The cache settings to validate.</param>
    /// <returns><see langword="true"/> when all settings are valid.</returns>
    public static bool HasValidSettings(ProviderCacheOptions options)
        => options.SizeLimit > 0
            && options.FetchTimeout > TimeSpan.Zero
            && options.FetchTimeout <= TimeSpan.FromMilliseconds(uint.MaxValue - 1)
            && options.Default.IsValid
            && options.Providers.Values.All(policy => policy.IsValid);
}
