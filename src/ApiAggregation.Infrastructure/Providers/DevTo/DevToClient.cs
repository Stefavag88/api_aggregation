using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers.DevTo;

/// <summary>
/// Fetches the first page of DEV Community articles for an exact topic tag and maps them to discovery items.
/// </summary>
/// <param name="httpClient">The HTTP client configured for this provider.</param>
/// <param name="logger">The logger for this provider client.</param>
/// <param name="options">The shared provider candidate settings.</param>
/// <param name="timeProvider">The clock used to timestamp successful fetches.</param>
public sealed class DevToClient(
    HttpClient httpClient,
    ILogger<DevToClient> logger,
    IOptions<ProviderDiscoveryOptions> options,
    TimeProvider timeProvider)
    : ExternalContentClient(httpClient, Metadata, logger, options, timeProvider)
{
    internal static ProviderDescriptor Metadata { get; } = new("devto", ContentType.Article);

    /// <inheritdoc />
    protected override string BuildRequestPath(string topic)
    {
        var tag = Uri.EscapeDataString(topic);
        return $"api/articles?tag={tag}&per_page={CandidateLimit}&page=1";
    }

    /// <inheritdoc />
    private protected override Task<Result<ProviderContent>> ReadContentAsync(HttpContent content, CancellationToken cancellationToken)
        => ReadAndMapAsync<JsonElement[]>(content, MapResponse, cancellationToken);

    private Result<ProviderContent> MapResponse(JsonElement[] records, CancellationToken cancellationToken)
        => MapItems(records, MapArticle, cancellationToken);

    private static DiscoveryItem? MapArticle(JsonElement record)
    {
        var article = DeserializeRecord<DevToArticle>(record);

        if (article?.Id is not > 0
            || string.IsNullOrWhiteSpace(article.Title)
            || !Uri.TryCreate(article.Url, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")
            || article.PublishedAt is not { } publishedAt)
        {
            return null;
        }

        return new DiscoveryItem(
            article.Id.Value.ToString(CultureInfo.InvariantCulture),
            Metadata.Name,
            Metadata.Type,
            article.Title,
            url,
            article.Description,
            publishedAt.ToUniversalTime(),
            article.Tags is null ? [] : [.. article.Tags.OfType<string>()]);
    }
}
