using System.Collections.Immutable;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Discovery;

namespace ApiAggregation.Api.Features.Discovery;

public sealed record DiscoveryResponse(
    string Topic,
    ImmutableArray<DiscoveryItem> Items,
    int MatchedCount,
    int ReturnedCount,
    bool IsPartial,
    ImmutableArray<ProviderReport> Providers);
