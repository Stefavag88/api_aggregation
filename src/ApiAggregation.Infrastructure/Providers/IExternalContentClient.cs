using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Infrastructure.Providers;

/// <summary>
/// Fetches and normalizes content from one external provider.
/// </summary>
public interface IExternalContentClient
{
    ProviderDescriptor Descriptor { get; }
    /// <summary>
    /// Retrieves the provider candidates for a topic.
    /// </summary>
    /// <param name="topic">The normalized topic to search for.</param>
    /// <param name="cancellationToken">Cancels the fetch and response processing.</param>
    /// <returns>Normalized content, which can be empty, or an expected provider failure.</returns>
    Task<Result<ProviderContent>> FetchAsync(string topic, CancellationToken cancellationToken);
}
