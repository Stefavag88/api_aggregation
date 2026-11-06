using ApiAggregation.Infrastructure.Http;
using System.Globalization;
using System.Net;

namespace ApiAggregation.Infrastructure.Providers.GitHub;

/// <summary>
/// Recognizes GitHub quota responses and reads reset times from GitHub headers.
/// </summary>
public sealed class GitHubRateLimitPolicy : IRateLimitPolicy
{
    private static readonly long MinimumResetEpoch = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaximumResetEpoch = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <inheritdoc />
    public bool IsRateLimited(HttpResponseMessage response)
        => response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden
                && (response.Headers.RetryAfter is not null
                    || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                        && remaining.Any(value => value.Trim() == "0"))));

    /// <inheritdoc />
    public DateTimeOffset? GetResetAt(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var resets))
            return null;

        DateTimeOffset? latest = null;
        foreach (var value in resets)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                || epoch < MinimumResetEpoch || epoch > MaximumResetEpoch)
                continue;

            var resetAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (latest is null || resetAt > latest.Value)
                latest = resetAt;
        }

        return latest;
    }
}
