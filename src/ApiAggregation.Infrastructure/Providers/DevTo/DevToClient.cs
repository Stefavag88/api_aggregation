using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Http;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers.DevTo;

/// <summary>
/// Fetches the first page of DEV Community articles for an exact topic tag and maps them to discovery items.
/// </summary>
/// <param name="httpClient">The HTTP client configured for this provider.</param>
public sealed class DevToClient(HttpClient httpClient) : IExternalContentClient
{
    private const int PageSize = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ProviderDescriptor Metadata { get; } = new("devto", ContentType.Article);

    public ProviderDescriptor Descriptor => Metadata;

    /// <inheritdoc />
    public async Task<Result<ProviderContent>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        var tag = Uri.EscapeDataString(topic);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/articles?tag={tag}&per_page={PageSize}&page=1");

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

            var result = await BoundedJsonReader.ReadAsync<JsonElement[]>(response.Content, cancellationToken);

            if (!result.IsSuccess)
                return AppResult.Failure<ProviderContent>(result.Error!);

            return MapResponse(result.Value!, cancellationToken);
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

    private static Result<ProviderContent> MapResponse(JsonElement[] records, CancellationToken cancellationToken)
    {
        var items = ImmutableArray.CreateBuilder<DiscoveryItem>();
        var incomplete = false;

        foreach (var record in records.Take(PageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = MapArticle(record);

            if (item is null)
                incomplete = true;
            else
                items.Add(item);
        }

        if (records.Length > 0 && items.Count == 0)
            return AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload);

        return AppResult.Success(new ProviderContent(items.ToImmutable(), incomplete));
    }

    private static DiscoveryItem? MapArticle(JsonElement record)
    {
        DevToArticle? article;

        try
        {
            article = record.Deserialize<DevToArticle>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

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
