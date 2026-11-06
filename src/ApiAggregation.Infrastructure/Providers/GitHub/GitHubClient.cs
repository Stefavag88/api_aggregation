using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Http;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers.GitHub;

/// <summary>
/// Searches GitHub for public repositories matching a topic and maps the first page to discovery items.
/// </summary>
/// <param name="httpClient">The HTTP client configured for this provider.</param>
/// <param name="rateLimitPolicy">The policy used to identify GitHub quota failures.</param>
public sealed class GitHubClient(HttpClient httpClient, GitHubRateLimitPolicy rateLimitPolicy) : IExternalContentClient
{
    private const int PageSize = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ProviderDescriptor Metadata { get; } = new("github", ContentType.Repository);

    public ProviderDescriptor Descriptor => Metadata;

    /// <inheritdoc />
    public async Task<Result<ProviderContent>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        var search = Uri.EscapeDataString($"{topic} is:public");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"search/repositories?q={search}&per_page={PageSize}&page=1");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return AppResult.Failure<ProviderContent>(ClassifyFailure(response));

            var result = await BoundedJsonReader.ReadAsync<GitHubResponse>(response.Content, cancellationToken);

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

    private static Result<ProviderContent> MapResponse(GitHubResponse response, CancellationToken cancellationToken)
    {
        if (response.Items is null)
            return AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload);

        var items = ImmutableArray.CreateBuilder<DiscoveryItem>();
        var incomplete = response.IncompleteResults;

        foreach (var record in response.Items.Take(PageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = MapRepository(record);

            if (item is null)
                incomplete = true;
            else
                items.Add(item);
        }

        if (response.Items.Length > 0 && items.Count == 0)
            return AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload);

        return AppResult.Success(new ProviderContent(items.ToImmutable(), incomplete));
    }

    private static DiscoveryItem? MapRepository(JsonElement record)
    {
        GitHubRepository? repository;

        try
        {
            repository = record.Deserialize<GitHubRepository>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (repository?.Id is not > 0
            || string.IsNullOrWhiteSpace(repository.Name)
            || !Uri.TryCreate(repository.HtmlUrl, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")
            || repository.CreatedAt is not { } createdAt)
        {
            return null;
        }

        return new DiscoveryItem(
            repository.Id.Value.ToString(CultureInfo.InvariantCulture),
            Metadata.Name,
            Metadata.Type,
            repository.Name,
            url,
            repository.Description,
            createdAt.ToUniversalTime(),
            repository.Topics is null ? [] : [.. repository.Topics.OfType<string>()]);
    }

    private AppError ClassifyFailure(HttpResponseMessage response)
    {
        if (rateLimitPolicy.IsRateLimited(response))
            return ProviderErrors.RateLimited;

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrors.Unauthorized,
            _ => ProviderErrors.UpstreamError
        };
    }
}
