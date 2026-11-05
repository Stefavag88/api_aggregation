namespace ApiAggregation.Infrastructure.Statistics;

public interface IRequestStatisticsRecorder
{
    void Record(AttemptObservation observation);
}
