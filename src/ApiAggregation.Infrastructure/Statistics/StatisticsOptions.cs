namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Configures how often statistics are published and checked for anomalies.
/// </summary>
public sealed class StatisticsOptions
{
    public const string SectionName = "Statistics";

    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan AnomalyCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
}
