using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Infrastructure.Providers;

/// <summary>
/// Adapts an external client to the discovery provider contract and timestamps successful fetches.
/// </summary>
/// <param name="client">The client that fetches and normalizes external content.</param>
/// <param name="timeProvider">The clock used to timestamp successful fetches.</param>
public sealed class DiscoveryProviderAdapter(IExternalContentClient client, TimeProvider timeProvider) : IDiscoveryProvider
{
    public ProviderDescriptor Descriptor => client.Descriptor;

    /// <summary>
    /// Fetches provider content and records the completion time of a successful fetch.
    /// </summary>
    /// <param name="topic">The normalized topic to search for.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The timestamped provider data or the expected failure from the client.</returns>
    public async Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        var result = await client.FetchAsync(topic, cancellationToken);

        if (!result.IsSuccess)
            return AppResult.Failure<ProviderData>(result.Error!);

        var content = result.Value!;
        var data = new ProviderData(
            content.Items,
            timeProvider.GetUtcNow(),
            IsStale: false,
            IsIncomplete: content.IsIncomplete);

        return AppResult.Success(data);
    }
}
