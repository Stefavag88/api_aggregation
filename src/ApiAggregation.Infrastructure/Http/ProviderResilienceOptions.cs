namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Configures the timeout for each HTTP attempt and the base delay before a retry.
/// </summary>
internal sealed class ProviderResilienceOptions
{
    internal const string SectionName = "ProviderResilience";

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    internal static bool HasValidSettings(ProviderResilienceOptions options)
        => options.AttemptTimeout >= TimeSpan.FromMilliseconds(10)
            && options.AttemptTimeout <= TimeSpan.FromDays(1)
            && options.RetryDelay >= TimeSpan.Zero
            && options.RetryDelay <= TimeSpan.FromDays(1);
}
