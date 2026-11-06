using ApiAggregation.Infrastructure.Statistics;

namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Records the outcome and latency of each outbound attempt through receipt of response headers.
/// </summary>
/// <param name="source">The provider name used to group statistics.</param>
/// <param name="recorder">The sink for attempt observations.</param>
/// <param name="timeProvider">The clock used to measure elapsed time.</param>
public sealed class RequestMetricsHandler(
    string source,
    IRequestStatisticsRecorder recorder,
    TimeProvider timeProvider) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var started = timeProvider.GetTimestamp();
        var outcome = AttemptOutcome.Failure;

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            outcome = response.IsSuccessStatusCode ? AttemptOutcome.Success : AttemptOutcome.Failure;

            return response;
        }
        catch (OperationCanceledException)
        {
            outcome = AttemptOutcome.Canceled;
            throw;
        }
        finally
        {
            var duration = timeProvider.GetElapsedTime(started);
            await recorder.RecordAsync(new AttemptObservation(source, duration, outcome, timeProvider.GetUtcNow()));
        }
    }
}
