using ApiAggregation.Application.Features.Discovery;

namespace ApiAggregation.Api.Features.Discovery;

public static class DiscoveryResponseMapper
{
    public static DiscoveryResponse Map(DiscoveryResult result) => new(result.Topic, result.Items,
        result.MatchedCount, result.ReturnedCount, result.IsPartial, result.Providers);
}
