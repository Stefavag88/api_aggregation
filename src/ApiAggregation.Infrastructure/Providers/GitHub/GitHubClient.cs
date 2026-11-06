using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Providers.GitHub;

/// <summary>
/// Searches GitHub for public repositories matching a topic and maps the first page to discovery items.
/// </summary>
/// <param name="httpClient">The HTTP client configured for this provider.</param>
/// <param name="rateLimitPolicy">The policy used to identify GitHub quota failures.</param>
/// <param name="logger">The logger for this provider client.</param>
/// <param name="options">The shared provider candidate settings.</param>
public sealed class GitHubClient(
    HttpClient httpClient,
    GitHubRateLimitPolicy rateLimitPolicy,
    ILogger<GitHubClient> logger,
    IOptions<ProviderDiscoveryOptions> options)
    : ExternalContentClient(httpClient, Metadata, logger, options)
{
    internal static ProviderDescriptor Metadata { get; } = new("github", ContentType.Repository);

    /// <inheritdoc />
    protected override string BuildRequestPath(string topic)
    {
        var search = Uri.EscapeDataString($"{topic} is:public");
        return $"search/repositories?q={search}&per_page={CandidateLimit}&page=1";
    }

    /// <inheritdoc />
    protected override Task<Result<ProviderContent>> ReadContentAsync(HttpContent content, CancellationToken cancellationToken)
        => ReadAndMapAsync<GitHubResponse>(content, MapResponse, cancellationToken);

    private Result<ProviderContent> MapResponse(GitHubResponse response, CancellationToken cancellationToken)
        => response.Items is null
            ? AppResult.Failure<ProviderContent>(ProviderErrors.InvalidPayload)
            : MapItems(response.Items, MapRepository, cancellationToken, response.IncompleteResults);

    private static DiscoveryItem? MapRepository(JsonElement record)
    {
        var repository = DeserializeRecord<GitHubRepository>(record);

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

    /// <inheritdoc />
    protected override AppError ClassifyFailure(HttpResponseMessage response)
        => rateLimitPolicy.IsRateLimited(response)
            ? ProviderErrors.RateLimited
            : base.ClassifyFailure(response);
}
