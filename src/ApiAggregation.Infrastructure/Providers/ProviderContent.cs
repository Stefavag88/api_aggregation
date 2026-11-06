using ApiAggregation.Domain.Discovery;
using System.Collections.Immutable;

namespace ApiAggregation.Infrastructure.Providers;

/// <summary>
/// Contains normalized items returned by an external provider and indicates whether the result is incomplete.
/// </summary>
public sealed record ProviderContent(
    ImmutableArray<DiscoveryItem> Items,
    bool IsIncomplete);
