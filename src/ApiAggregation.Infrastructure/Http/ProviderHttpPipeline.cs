using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;
using System.Net;

namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Configures the Polly pipeline for a provider's outbound HTTP requests.
/// </summary>
internal static class ProviderHttpPipeline
{
    internal static IHttpClientBuilder Configure(IHttpClientBuilder builder, string source)
    {
        builder.AddResilienceHandler(source, static (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<ProviderResilienceOptions>>().Value;
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = options.RetryDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldRetryAfterHeader = false,
                ShouldHandle = static args => ValueTask.FromResult(
                    args.Context.GetRequestMessage()?.Method == HttpMethod.Get
                    && IsTransient(args.Outcome))
            });
            pipeline.AddTimeout(options.AttemptTimeout);
        });

        return builder;
    }

    private static bool IsTransient(Outcome<HttpResponseMessage> outcome)
        => outcome.Exception is HttpRequestException or IOException or TimeoutRejectedException
            || outcome.Result is { StatusCode: HttpStatusCode.RequestTimeout or (>= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599) };
}
