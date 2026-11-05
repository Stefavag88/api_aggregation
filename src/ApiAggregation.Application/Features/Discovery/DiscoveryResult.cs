using System.Collections.Immutable;
using ApiAggregation.Domain.Discovery;

namespace ApiAggregation.Application.Features.Discovery;

public sealed record DiscoveryResult(string Topic, ImmutableArray<DiscoveryItem> Items,
    int MatchedCount, int ReturnedCount, bool IsPartial, ImmutableArray<ProviderReport> Providers);
