using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiAggregation.Infrastructure.Providers.GitHub;

internal sealed record GitHubResponse(
    JsonElement[]? Items,
    [property: JsonPropertyName("incomplete_results")] bool IncompleteResults);

internal sealed record GitHubRepository(
    long? Id,
    string? Name,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    string? Description,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    string?[]? Topics);
