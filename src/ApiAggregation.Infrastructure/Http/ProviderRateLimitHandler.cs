using System.Net;
using System.Net.Http.Headers;

namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Honors upstream rate limit headers and suppresses transport calls during a provider cooldown.
/// </summary>
internal sealed class ProviderRateLimitHandler(
    string source,
    IRateLimitPolicy policy,
    ProviderRateLimitState state,
    TimeProvider timeProvider) : DelegatingHandler
{
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state.IsBlocked(source, timeProvider.GetUtcNow()))
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };

        var response = await base.SendAsync(request, cancellationToken);
        if (policy.IsRateLimited(response))
            state.BlockUntil(source, GetCooldownDeadline(response, timeProvider.GetUtcNow()));

        return response;
    }

    private DateTimeOffset GetCooldownDeadline(HttpResponseMessage response, DateTimeOffset now)
    {
        var latest = now;
        if (response.Headers.TryGetValues("Retry-After", out var retryAfter))
        {
            foreach (var value in retryAfter)
            {
                if (!RetryConditionHeaderValue.TryParse(value, out var retry))
                    continue;

                var deadline = retry.Delta is { } delta && delta <= DateTimeOffset.MaxValue - now
                    ? now.Add(delta)
                    : retry.Date;
                if (deadline is { } candidate && candidate > latest)
                    latest = candidate;
            }
        }

        if (policy.GetResetAt(response) is { } resetAt && resetAt > latest)
            latest = resetAt;

        if (latest > now)
            return latest;

        return DefaultCooldown <= DateTimeOffset.MaxValue - now ? now.Add(DefaultCooldown) : DateTimeOffset.MaxValue;
    }
}
