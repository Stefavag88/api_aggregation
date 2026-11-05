namespace ApiAggregation.Application.Features.Statistics;

public sealed record ProviderStatistics(
    string Source,
    long TotalRequests,
    long SuccessCount,
    long FailureCount,
    long CanceledCount,
    double? AverageResponseTimeMs,
    BucketStatistics Fast,
    BucketStatistics Average,
    BucketStatistics Slow);
