using System.Collections.Immutable;

namespace ApiAggregation.Application.Features.Statistics;

public sealed record StatisticsSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset SnapshotAt,
    ImmutableArray<ProviderStatistics> Providers);
