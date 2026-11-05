using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Application.Features.Discovery;

public interface IDiscoveryProvider
{
    ProviderDescriptor Descriptor { get; }
    Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken);
}
