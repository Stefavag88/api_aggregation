using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Infrastructure.Caching;

public sealed record CachedProviderSnapshot(
    ProviderData Data,
    DateTimeOffset FreshUntil,
    DateTimeOffset StaleUntil)
{
    public Result<ProviderData> ToResult(DateTimeOffset now)
        => now < StaleUntil
            ? AppResult.Success(Data with { IsStale = now >= FreshUntil })
            : AppResult.Failure<ProviderData>(CacheErrors.Expired);
}
