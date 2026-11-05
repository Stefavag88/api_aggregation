using ApiAggregation.Application.Features.Discovery;

namespace ApiAggregation.Api.Features.Discovery;

public sealed record DiscoveryRequest
{
    public string? Topic { get; init; }
    public string? Sources { get; init; }
    public string? Type { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? SortBy { get; init; }
    public string? SortDirection { get; init; }
    public string? Limit { get; init; }

    public DiscoveryQueryInput ToInput() => new()
    {
        Topic = Topic, Sources = Sources, Type = Type, From = From, To = To,
        SortBy = SortBy, SortDirection = SortDirection, Limit = Limit
    };
}
