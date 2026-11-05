namespace ApiAggregation.Infrastructure.Statistics;

public sealed class StatisticsOptions
{
    public const string SectionName = "Statistics";

    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(1);
}
