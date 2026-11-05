using ApiAggregation.Domain.Discovery;
using System.Collections.Immutable;

namespace ApiAggregation.Infrastructure.Providers;

public sealed record ProviderContent(
    ImmutableArray<DiscoveryItem> Items,
    bool IsIncomplete);
