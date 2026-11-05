namespace ApiAggregation.Application.Features.Discovery;

public sealed record ProviderReport(
    string Source,
    ProviderStatus Status,
    DateTimeOffset? FetchedAt,
    bool IsIncomplete,
    string? ErrorCode);
