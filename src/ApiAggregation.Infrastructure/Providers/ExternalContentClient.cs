using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers;

/// <summary>
/// Handles the common HTTP fetch flow for provider clients.
/// </summary>
/// <param name="httpClient">The HTTP client configured for the provider.</param>
/// <param name="descriptor">The provider name and content type.</param>
/// <param name="logger">The logger used to report skipped provider records.</param>
/// <param name="options">The shared provider candidate settings.</param>
/// <param name="timeProvider">The clock used to timestamp successful fetches.</param>
public abstract class ExternalContentClient(
    HttpClient httpClient,
    ProviderDescriptor descriptor,
    ILogger logger,
    IOptions<ProviderDiscoveryOptions> options,
    TimeProvider timeProvider) : IDiscoveryProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    protected int CandidateLimit { get; } = options.Value.ProviderCandidateLimit;

    public ProviderDescriptor Descriptor { get; } = descriptor;

    /// <inheritdoc />
    public async Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        var result = await FetchContentAsync(topic, cancellationToken);
        if (!result.IsSuccess)
            return AppResult.Failure<ProviderData>(result.Error!);

        var content = result.Value!;
        return AppResult.Success(new ProviderData(
            content.Items,
            timeProvider.GetUtcNow(),
            IsStale: false,
            IsIncomplete: content.IsIncomplete));
    }

    private async Task<Result<ProviderContent>> FetchContentAsync(string topic, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestPath(topic));

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return AppResult.Failure<ProviderContent>(ClassifyFailure(response));

            return await ReadContentAsync(response.Content, cancellationToken);
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

    /// <summary>
    /// Builds the relative GET path for the provider's first page of candidates.
    /// </summary>
    protected abstract string BuildRequestPath(string topic);

    /// <summary>
    /// Reads the provider payload and maps it to discovery content.
    /// </summary>
    private protected abstract Task<Result<ProviderContent>> ReadContentAsync(HttpContent content, CancellationToken cancellationToken);

    /// <summary>
    /// Maps an unsuccessful HTTP response to a provider error.
    /// </summary>
    protected virtual AppError ClassifyFailure(HttpResponseMessage response)
        => response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => ProviderErrors.RateLimited,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrors.Unauthorized,
            _ => ProviderErrors.UpstreamError
        };

    /// <summary>
    /// Reads a size-limited JSON response and applies the provider's payload mapper.
    /// </summary>
    private protected static async Task<Result<ProviderContent>> ReadAndMapAsync<TResponse>(
        HttpContent content, Func<TResponse, CancellationToken, Result<ProviderContent>> map, CancellationToken cancellationToken)
    {
        var result = await BoundedJsonReader.ReadAsync<TResponse>(content, cancellationToken);
        return result.IsSuccess
            ? map(result.Value!, cancellationToken)
            : AppResult.Failure<ProviderContent>(result.Error!);
    }

    /// <summary>
    /// Maps up to one page of candidates and marks the result incomplete when records are skipped.
    /// </summary>
    private protected Result<ProviderContent> MapItems(
        JsonElement[] records,
        Func<JsonElement, DiscoveryItem?> map,
        CancellationToken cancellationToken,
        bool incomplete = false)
    {
        var items = ImmutableArray.CreateBuilder<DiscoveryItem>();
        var skipped = 0;

        foreach (var record in records.Take(CandidateLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = map(record);

            if (item is null)
                skipped++;
            else
                items.Add(item);
        }

        if (skipped > 0)
            logger.LogWarning("Provider {Provider} skipped {SkippedCount} unusable records in its response.", Descriptor.Name, skipped);

        if (records.Length > 0 && items.Count == 0)
            return AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload);

        return AppResult.Success(new ProviderContent(items.ToImmutable(), incomplete || skipped > 0));
    }

    /// <summary>
    /// Deserializes an individual record or returns null when its JSON cannot be mapped.
    /// </summary>
    protected static TRecord? DeserializeRecord<TRecord>(JsonElement record) where TRecord : class
    {
        try
        {
            return record.Deserialize<TRecord>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
