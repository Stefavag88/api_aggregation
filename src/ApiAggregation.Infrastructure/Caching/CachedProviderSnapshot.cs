using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Infrastructure.Caching;

/// <summary>
/// Stores provider data with its original freshness and maximum age boundaries.
/// </summary>
/// <param name="Data">The normalized provider data and its original fetch timestamp.</param>
/// <param name="FreshUntil">The instant at which the snapshot becomes stale.</param>
/// <param name="StaleUntil">The instant at which the snapshot becomes unusable.</param>
public sealed record CachedProviderSnapshot(
    ProviderData Data,
    DateTimeOffset FreshUntil,
    DateTimeOffset StaleUntil)
{
    /// <summary>
    /// Returns the snapshot with its current stale status or rejects it if its maximum age has elapsed.
    /// </summary>
    /// <param name="now">The current time used to evaluate the snapshot boundaries.</param>
    /// <returns>The provider data or a cache expiry error.</returns>
    public Result<ProviderData> ToResult(DateTimeOffset now)
        => now < StaleUntil
            ? AppResult.Success(Data with { IsStale = now >= FreshUntil })
            : AppResult.Failure<ProviderData>(CacheErrors.Expired);
}
