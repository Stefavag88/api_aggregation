using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;

namespace ApiAggregation.UnitTests.Support;

internal sealed class GatedProvider(string name, Result<ProviderData> result) : IDiscoveryProvider
{
    public ProviderDescriptor Descriptor { get; } = new(name, ContentType.Repository);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CallCount { get; private set; }

    public async Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        CallCount++;
        Entered.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return result;
    }
}
