using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiAggregation.Infrastructure.Providers.HackerNews;

internal sealed record HackerNewsResponse(JsonElement[]? Hits);

internal sealed record HackerNewsStory(
    [property: JsonPropertyName("objectID")] string? Id,
    string? Title,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("story_text")] string? StoryText,
    [property: JsonPropertyName("_tags")] string?[]? Tags);
