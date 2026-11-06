using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.Infrastructure.Statistics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace ApiAggregation.UnitTests.Statistics;

public sealed class PerformanceAnomalyServiceTests
{
    [Fact]
    public async Task LogsOnlyMeasuredTransitionsForEachProvider()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var cancellationToken = timeout.Token;
        var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var clock = new MonitorClock { UtcNow = now };
        var options = Options.Create(new StatisticsOptions { AnomalyCheckInterval = TimeSpan.FromSeconds(30) });
        using var store = new InMemoryStatisticsStore(
            [
                new ProviderDescriptor("github", ContentType.Repository),
                new ProviderDescriptor("devto", ContentType.Article),
                new ProviderDescriptor("hackernews", ContentType.Discussion),
                new ProviderDescriptor("threshold", ContentType.Article),
                new ProviderDescriptor("zero", ContentType.Article)
            ], clock, options);

        await RecordAsync("github", now.AddMinutes(-10), 10, 100);
        await RecordAsync("github", now.AddMinutes(-5), 10, 150);
        await RecordAsync("github", now, 10, 200);
        await RecordAsync("github", now.AddMinutes(2), 20, 100);
        await RecordAsync("devto", now.AddMinutes(-10), 10, 100);
        await RecordAsync("devto", now, 10, 200);
        await RecordAsync("hackernews", now.AddMinutes(-10), 10, 100);
        await RecordAsync("hackernews", now, 9, 900);
        await RecordAsync("threshold", now.AddMinutes(-10), 10, 10, extraTicks: 4);
        await RecordAsync("threshold", now, 10, 15, extraTicks: 6);
        await RecordAsync("zero", now.AddMinutes(-10), 10, 0);
        await RecordAsync("zero", now, 10, 900);
        await store.StartAsync(cancellationToken);
        await store.StopAsync(cancellationToken);

        clock.TrackChecks = true;
        var logger = new RecordingLogger();
        using var service = new PerformanceAnomalyService(store, clock, options, logger);
        await service.StartAsync(cancellationToken);
        var timer = await clock.TimerCreated.Task.WaitAsync(cancellationToken);
        Assert.Equal(options.Value.AnomalyCheckInterval, timer.Period);

        foreach (var minute in new[] { 0, 1, 2, 3, 8 })
        {
            clock.UtcNow = now.AddMinutes(minute);
            timer.Tick();
            Assert.Equal(clock.UtcNow, await clock.CheckTimes.Reader.ReadAsync(cancellationToken));
        }

        await service.StopAsync(cancellationToken);

        Assert.Collection(logger.Entries,
            warning => AssertEntry(warning, LogLevel.Warning, "devto", 10, 10, 200, 100),
            warning => AssertEntry(warning, LogLevel.Warning, "github", 10, 20, 200, 125),
            recovery => AssertEntry(recovery, LogLevel.Information, "github", 30, 20, 4000d / 30, 125));

        async Task RecordAsync(string source, DateTimeOffset completedAt, int count, int milliseconds, int extraTicks = 0)
        {
            var duration = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond + extraTicks);
            for (var index = 0; index < count; index++)
                await store.RecordAsync(new AttemptObservation(source, duration, AttemptOutcome.Success, completedAt));
        }
    }

    private static void AssertEntry(LogEntry entry, LogLevel level, string source, long recentCount, long baselineCount,
        double recentMean, double baselineMean)
    {
        Assert.Equal(level, entry.Level);
        Assert.Equal(source, entry.Properties["Source"]);
        Assert.Equal(recentCount, entry.Properties["RecentCount"]);
        Assert.Equal(baselineCount, entry.Properties["BaselineCount"]);
        Assert.Equal(recentMean, entry.Properties["RecentMeanMs"]);
        Assert.Equal(baselineMean, entry.Properties["BaselineMeanMs"]);
        Assert.Equal(recentMean / baselineMean, entry.Properties["Ratio"]);
    }

    private sealed class MonitorClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public bool TrackChecks { get; set; }
        public TaskCompletionSource<MonitorTimer> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<DateTimeOffset> CheckTimes { get; } = Channel.CreateUnbounded<DateTimeOffset>();

        public override DateTimeOffset GetUtcNow()
        {
            var now = UtcNow;
            if (TrackChecks)
                CheckTimes.Writer.TryWrite(now);
            return now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!TrackChecks)
                return base.CreateTimer(callback, state, dueTime, period);

            var timer = new MonitorTimer(callback, state, period);
            TimerCreated.SetResult(timer);
            return timer;
        }
    }

    private sealed class MonitorTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; } = period;

        public void Tick() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record LogEntry(LogLevel Level, Dictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger<PerformanceAnomalyService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Entries.Add(new LogEntry(logLevel, properties.ToDictionary()));
        }
    }
}
