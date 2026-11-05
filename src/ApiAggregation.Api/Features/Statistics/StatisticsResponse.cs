using ApiAggregation.Application.Features.Statistics;
using System.Collections.Immutable;

namespace ApiAggregation.Api.Features.Statistics;

public sealed record StatisticsResponse(
    DateTimeOffset StartedAt,
    DateTimeOffset SnapshotAt,
    ImmutableArray<ProviderStatistics> Providers)
{
    public string MeasurementScope => "transportToHeaders";
    public string CounterScope => "process";
}
