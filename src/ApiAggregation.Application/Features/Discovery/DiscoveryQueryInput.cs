namespace ApiAggregation.Application.Features.Discovery;

public sealed record DiscoveryQueryInput
{
    public string? Topic { get; init; }
    public string? Sources { get; init; }
    public string? Type { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? SortBy { get; init; }
    public string? SortDirection { get; init; }
    public string? Limit { get; init; }
}
