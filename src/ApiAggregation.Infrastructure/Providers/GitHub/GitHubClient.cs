using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Http;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers.GitHub;

public sealed class GitHubClient(HttpClient httpClient) : IExternalContentClient
{
    private const int PageSize = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ProviderDescriptor Metadata { get; } = new("github", ContentType.Repository);

    public ProviderDescriptor Descriptor => Metadata;

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

    private static AppError ClassifyFailure(HttpResponseMessage response)
    {
        var quotaExhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.Any(value => value.Trim() == "0");

        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => ProviderErrors.RateLimited,
            HttpStatusCode.Forbidden when quotaExhausted || response.Headers.RetryAfter is not null => ProviderErrors.RateLimited,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrors.Unauthorized,
            _ => ProviderErrors.UpstreamError
        };
    }
}
