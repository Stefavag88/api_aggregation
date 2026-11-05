using System.Text.Json.Serialization;

namespace ApiAggregation.Infrastructure.Providers.DevTo;

internal sealed record DevToArticle(
    long? Id,
    string? Title,
    string? Url,
    string? Description,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("tag_list")] string?[]? Tags);
