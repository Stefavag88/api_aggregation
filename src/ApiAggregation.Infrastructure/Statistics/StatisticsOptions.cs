namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Configures how often the statistics worker publishes changes.
/// </summary>
public sealed class StatisticsOptions
{
    public const string SectionName = "Statistics";

    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(1);
}
