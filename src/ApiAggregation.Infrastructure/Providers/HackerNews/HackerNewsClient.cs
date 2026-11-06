using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Http;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers.HackerNews;

/// <summary>
/// Searches the Hacker News Algolia API for stories and maps the first page to discovery items.
/// </summary>
/// <param name="httpClient">The HTTP client configured for this provider.</param>
public sealed class HackerNewsClient(HttpClient httpClient) : IExternalContentClient
{
    private const int PageSize = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ProviderDescriptor Metadata { get; } = new("hackernews", ContentType.Discussion);

    public ProviderDescriptor Descriptor => Metadata;

    /// <inheritdoc />
    public async Task<Result<ProviderContent>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(topic);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/search?tags=story&query={query}&hitsPerPage={PageSize}&page=0");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => ProviderErrors.RateLimited,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrors.Unauthorized,
                    _ => ProviderErrors.UpstreamError
                };

                return AppResult.Failure<ProviderContent>(error);
            }

            var result = await BoundedJsonReader.ReadAsync<HackerNewsResponse>(response.Content, cancellationToken);

            if (!result.IsSuccess)
                return AppResult.Failure<ProviderContent>(result.Error!);

            return MapResponse(result.Value!, cancellationToken);
        }
        catch (Exception exception) when (ProviderErrors.FromResilienceException(exception) is { } error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AppResult.Failure<ProviderContent>(error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<ProviderContent>(ProviderErrors.Timeout);
        }
        catch (HttpRequestException)
        {
            return AppResult.Failure<ProviderContent>(ProviderErrors.UpstreamError);
        }
        catch (IOException)
        {
            return AppResult.Failure<ProviderContent>(ProviderErrors.UpstreamError);
        }
    }

    private static Result<ProviderContent> MapResponse(HackerNewsResponse response, CancellationToken cancellationToken)
    {
        if (response.Hits is null)
            return AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload);

        var items = ImmutableArray.CreateBuilder<DiscoveryItem>();
        var incomplete = false;

        foreach (var hit in response.Hits.Take(PageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = MapStory(hit);

            if (item is null)
                incomplete = true;
            else
                items.Add(item);
        }

        if (response.Hits.Length > 0 && items.Count == 0)
            return AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload);

        return AppResult.Success(new ProviderContent(items.ToImmutable(), incomplete));
    }

    private static DiscoveryItem? MapStory(JsonElement hit)
    {
        HackerNewsStory? story;

        try
        {
            story = hit.Deserialize<HackerNewsStory>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

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
