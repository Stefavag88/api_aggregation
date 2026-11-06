using System.Net;

namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Recognizes the standard HTTP rate-limit status.
/// </summary>
internal sealed class DefaultRateLimitPolicy : IRateLimitPolicy
{
    /// <inheritdoc />
    public bool IsRateLimited(HttpResponseMessage response)
        => response.StatusCode == HttpStatusCode.TooManyRequests;

    /// <inheritdoc />
    public DateTimeOffset? GetResetAt(HttpResponseMessage response) => null;
}
