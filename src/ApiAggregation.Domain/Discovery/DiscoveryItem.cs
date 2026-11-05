using System.Collections.Immutable;

namespace ApiAggregation.Domain.Discovery;

public sealed record DiscoveryItem(
    string Id, string Source, ContentType Type, string Title, Uri Url,
    string? Description, DateTimeOffset PublishedAt, ImmutableArray<string> Tags);
