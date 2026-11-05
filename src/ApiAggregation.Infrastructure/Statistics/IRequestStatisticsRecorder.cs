namespace ApiAggregation.Infrastructure.Statistics;

public interface IRequestStatisticsRecorder
{
    ValueTask RecordAsync(AttemptObservation observation);
}
