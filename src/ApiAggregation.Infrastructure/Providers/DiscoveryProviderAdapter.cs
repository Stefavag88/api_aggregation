using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Infrastructure.Providers;

public sealed class DiscoveryProviderAdapter(IExternalContentClient client, TimeProvider timeProvider) : IDiscoveryProvider
{
    public ProviderDescriptor Descriptor => client.Descriptor;

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
