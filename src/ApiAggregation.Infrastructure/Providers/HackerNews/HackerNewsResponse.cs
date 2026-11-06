using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiAggregation.Infrastructure.Providers.HackerNews;

/// <summary>
/// Represents a page of story search hits returned by the Hacker News Algolia API.
/// </summary>
internal sealed record HackerNewsResponse(JsonElement[]? Hits);

/// <summary>
/// Represents the fields read from one Hacker News story hit.
/// </summary>
internal sealed record HackerNewsStory(
    [property: JsonPropertyName("objectID")] string? Id,
    string? Title,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("story_text")] string? StoryText,
    [property: JsonPropertyName("_tags")] string?[]? Tags);
