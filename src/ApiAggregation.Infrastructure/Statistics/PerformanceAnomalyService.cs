using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Numerics;

namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Periodically compares each provider's recent attempt latency with its earlier baseline.
/// </summary>
/// <param name="store">The source of published provider latency windows.</param>
/// <param name="timeProvider">The clock used for scheduling checks.</param>
/// <param name="options">The configured check interval.</param>
/// <param name="logger">The logger for anomaly and recovery transitions.</param>
public sealed class PerformanceAnomalyService(
    InMemoryStatisticsStore store,
    TimeProvider timeProvider,
    IOptions<StatisticsOptions> options,
    ILogger<PerformanceAnomalyService> logger) : BackgroundService
{
    private const int MinimumSamples = 10;
    private const int ThresholdNumerator = 3;
    private const int ThresholdDenominator = 2;
    private readonly HashSet<string> _anomalousProviders = new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.AnomalyCheckInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var window in store.GetPerformanceWindows())
                Check(window);
        }
    }

    private void Check(ProviderPerformanceWindow window)
    {
        if (window.RecentCount < MinimumSamples || window.BaselineCount < MinimumSamples || window.BaselineDurationTicks <= 0)
        {
            _anomalousProviders.Remove(window.Source);
            return;
        }

        // Compare the means without rounding at the strict threshold.
        var anomalous = (BigInteger)window.RecentDurationTicks * window.BaselineCount * ThresholdDenominator
            > (BigInteger)window.BaselineDurationTicks * window.RecentCount * ThresholdNumerator;
        var changed = anomalous ? _anomalousProviders.Add(window.Source) : _anomalousProviders.Remove(window.Source);
        if (!changed)
            return;

        var recentMean = (double)window.RecentDurationTicks / TimeSpan.TicksPerMillisecond / window.RecentCount;
        var baselineMean = (double)window.BaselineDurationTicks / TimeSpan.TicksPerMillisecond / window.BaselineCount;

        logger.Log(anomalous ? LogLevel.Warning : LogLevel.Information,
            "Provider {Source} latency {Transition}. Recent mean {RecentMeanMs:F2} ms over {RecentCount} attempts, baseline mean {BaselineMeanMs:F2} ms over {BaselineCount} attempts, ratio {Ratio:F2}.",
            window.Source, anomalous ? "anomaly detected" : "recovered", recentMean, window.RecentCount, baselineMean,
            window.BaselineCount, recentMean / baselineMean);
    }
}
