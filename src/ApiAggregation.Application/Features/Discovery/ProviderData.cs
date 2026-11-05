using System.Collections.Immutable;
using ApiAggregation.Domain.Discovery;

namespace ApiAggregation.Application.Features.Discovery;

public sealed record ProviderData(ImmutableArray<DiscoveryItem> Items, DateTimeOffset FetchedAt,
    bool IsStale, bool IsIncomplete);
