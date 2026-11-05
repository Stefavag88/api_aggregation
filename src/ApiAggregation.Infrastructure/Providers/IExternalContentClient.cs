using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Infrastructure.Providers;

public interface IExternalContentClient
{
    ProviderDescriptor Descriptor { get; }
    Task<Result<ProviderContent>> FetchAsync(string topic, CancellationToken cancellationToken);
}
