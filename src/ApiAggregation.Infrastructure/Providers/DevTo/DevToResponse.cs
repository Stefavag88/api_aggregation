using System.Text.Json.Serialization;

namespace ApiAggregation.Infrastructure.Providers.DevTo;

/// <summary>
/// Represents the fields read from one DEV Community article.
/// </summary>
internal sealed record DevToArticle(
    long? Id,
    string? Title,
    string? Url,
    string? Description,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("tag_list")] string?[]? Tags);
