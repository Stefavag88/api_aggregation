namespace ApiAggregation.Application.Features.Statistics;

public sealed record BucketStatistics(long Count, double? AverageResponseTimeMs);
