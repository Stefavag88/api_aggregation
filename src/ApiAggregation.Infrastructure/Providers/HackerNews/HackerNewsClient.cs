using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers.HackerNews;

/// <summary>
/// Searches the Hacker News Algolia API for stories and maps the first page to discovery items.
/// </summary>
/// <param name="httpClient">The HTTP client configured for this provider.</param>
/// <param name="logger">The logger for this provider client.</param>
/// <param name="options">The shared provider candidate settings.</param>
/// <param name="timeProvider">The clock used to timestamp successful fetches.</param>
public sealed class HackerNewsClient(
    HttpClient httpClient,
    ILogger<HackerNewsClient> logger,
    IOptions<ProviderDiscoveryOptions> options,
    TimeProvider timeProvider)
    : ExternalContentClient(httpClient, Metadata, logger, options, timeProvider)
{
    internal static ProviderDescriptor Metadata { get; } = new("hackernews", ContentType.Discussion);

    /// <inheritdoc />
    protected override string BuildRequestPath(string topic)
    {
        var query = Uri.EscapeDataString(topic);
        return $"api/v1/search?tags=story&query={query}&hitsPerPage={CandidateLimit}&page=0";
    }

    /// <inheritdoc />
    private protected override Task<Result<ProviderContent>> ReadContentAsync(HttpContent content, CancellationToken cancellationToken)
        => ReadAndMapAsync<HackerNewsResponse>(content, MapResponse, cancellationToken);

    private Result<ProviderContent> MapResponse(HackerNewsResponse response, CancellationToken cancellationToken)
        => response.Hits is null
            ? AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload)
            : MapItems(response.Hits, MapStory, cancellationToken);

    private static DiscoveryItem? MapStory(JsonElement hit)
    {
        var story = DeserializeRecord<HackerNewsStory>(hit);

        if (story?.Id is not { } id
            || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var storyId)
            || storyId <= 0
            || string.IsNullOrWhiteSpace(story.Title)
            || story.CreatedAt is not { } createdAt)
        {
            return null;
        }

        return new DiscoveryItem(
            id,
            Metadata.Name,
            Metadata.Type,
            story.Title,
            new Uri($"https://news.ycombinator.com/item?id={id}"),
            story.StoryText,
            createdAt.ToUniversalTime(),
            story.Tags is null ? [] : [.. story.Tags.OfType<string>()]);
    }
}
