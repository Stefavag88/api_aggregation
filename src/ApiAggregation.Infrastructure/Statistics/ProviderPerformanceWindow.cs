namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Contains the sample counts and total durations for a provider's recent and baseline windows.
/// </summary>
public readonly record struct ProviderPerformanceWindow(
    string Source,
    long RecentCount,
    Int128 RecentDurationTicks,
    long BaselineCount,
    Int128 BaselineDurationTicks);
