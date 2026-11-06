namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Interprets a provider's rate-limit responses and optional quota reset time.
/// </summary>
internal interface IRateLimitPolicy
{
    /// <summary>
    /// Determines whether the response signals a rate limit.
    /// </summary>
    bool IsRateLimited(HttpResponseMessage response);

    /// <summary>
    /// Reads a provider-specific quota reset time when the response supplies one.
    /// </summary>
    DateTimeOffset? GetResetAt(HttpResponseMessage response);
}
