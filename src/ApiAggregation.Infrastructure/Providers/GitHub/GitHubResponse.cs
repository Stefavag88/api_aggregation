using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiAggregation.Infrastructure.Providers.GitHub;

/// <summary>
/// Represents one page of GitHub repository search results and its completeness indicator.
/// </summary>
internal sealed record GitHubResponse(
    JsonElement[]? Items,
    [property: JsonPropertyName("incomplete_results")] bool IncompleteResults);

/// <summary>
/// Represents the fields read from one GitHub repository search result.
/// </summary>
internal sealed record GitHubRepository(
    long? Id,
    string? Name,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    string? Description,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    string?[]? Topics);
