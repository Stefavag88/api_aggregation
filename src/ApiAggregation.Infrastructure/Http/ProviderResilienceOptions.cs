namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Configures HTTP attempt timeouts, retry delays, and provider protection limits.
/// </summary>
internal sealed class ProviderResilienceOptions
{
    internal const string SectionName = "ProviderResilience";

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public int ConcurrencyLimit { get; set; } = 8;
    public double CircuitFailureRatio { get; set; } = 0.5;
    public int CircuitMinimumThroughput { get; set; } = 10;
    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    internal static bool HasValidSettings(ProviderResilienceOptions options)
        => options.AttemptTimeout >= TimeSpan.FromMilliseconds(10)
            && options.AttemptTimeout <= TimeSpan.FromDays(1)
            && options.RetryDelay >= TimeSpan.Zero
            && options.RetryDelay <= TimeSpan.FromDays(1);

    internal static bool HasValidCircuitSettings(ProviderResilienceOptions options)
        => options.CircuitFailureRatio is > 0 and <= 1
            && options.CircuitMinimumThroughput >= 2
            && IsValidCircuitDuration(options.CircuitSamplingDuration)
            && IsValidCircuitDuration(options.CircuitBreakDuration);

    private static bool IsValidCircuitDuration(TimeSpan duration)
        => duration >= TimeSpan.FromMilliseconds(500) && duration <= TimeSpan.FromDays(1);
}
