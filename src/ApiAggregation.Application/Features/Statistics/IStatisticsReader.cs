namespace ApiAggregation.Application.Features.Statistics;

public interface IStatisticsReader
{
    StatisticsSnapshot GetSnapshot();
}
