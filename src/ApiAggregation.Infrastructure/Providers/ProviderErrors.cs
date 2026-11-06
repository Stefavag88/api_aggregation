using ApiAggregation.Domain.Common.Results;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace ApiAggregation.Infrastructure.Providers;

/// <summary>
/// Defines expected failures when fetching data from an external provider,
/// including transport, payload, and provider resilience failures.
/// </summary>
public static class ProviderErrors
{
    public static AppError UpstreamError { get; } = new("upstream_error", ResultErrorCode.ServiceUnavailable);
    public static AppError InvalidPayload { get; } = new("invalid_payload", ResultErrorCode.ServiceUnavailable);
    public static AppError ResponseTooLarge { get; } = new("response_too_large", ResultErrorCode.ServiceUnavailable);
    public static AppError Timeout { get; } = new("timeout", ResultErrorCode.ServiceUnavailable);
    public static AppError RateLimited { get; } = new("rate_limited", ResultErrorCode.RateLimited);
    public static AppError Unauthorized { get; } = new("unauthorized", ResultErrorCode.Unauthorized);
    public static AppError CircuitOpen { get; } = new("circuit_open", ResultErrorCode.ServiceUnavailable);
    public static AppError Busy { get; } = new("busy", ResultErrorCode.ServiceUnavailable);

    internal static AppError? FromResilienceException(Exception exception)
        => exception switch
        {
            TimeoutRejectedException => Timeout,
            BrokenCircuitException => CircuitOpen,
            RateLimiterRejectedException => Busy,
            _ => null
        };
}
