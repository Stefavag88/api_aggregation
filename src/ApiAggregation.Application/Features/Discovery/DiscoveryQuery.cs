using System.Collections.Immutable;
using ApiAggregation.Domain.Discovery;

namespace ApiAggregation.Application.Features.Discovery;

public sealed record DiscoveryQuery(
    string Topic, ImmutableArray<string> Sources, ContentType? Type,
    DateTimeOffset? From, DateTimeOffset? To, SortField SortBy,
    SortDirection SortDirection, int Limit);
