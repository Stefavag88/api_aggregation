using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Infrastructure.Caching;

/// <summary>
/// Defines errors returned when a local cache policy prevents serving provider data.
/// </summary>
internal static class CacheErrors
{
    /// <summary>
    /// The cached snapshot has reached its maximum age and can no longer be served.
    /// </summary>
    internal static AppError Expired { get; } = new("cache_expired", ResultErrorCode.ServiceUnavailable);
}
