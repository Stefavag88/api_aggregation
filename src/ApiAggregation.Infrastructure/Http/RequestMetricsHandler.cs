using ApiAggregation.Infrastructure.Statistics;

namespace ApiAggregation.Infrastructure.Http;

public sealed class RequestMetricsHandler(
    string source,
    IRequestStatisticsRecorder recorder,
    TimeProvider timeProvider) : DelegatingHandler
{
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
            recorder.Record(new AttemptObservation(source, duration, outcome, timeProvider.GetUtcNow()));
        }
    }
}
