namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Accepts observations from outbound HTTP attempts.
/// </summary>
public interface IRequestStatisticsRecorder
{
    /// <summary>
    /// Submits an attempt observation for aggregation.
    /// </summary>
    /// <param name="observation">The measured outbound attempt.</param>
    /// <returns>An operation that completes after the recorder has handled submission.</returns>
    ValueTask RecordAsync(AttemptObservation observation);
}
