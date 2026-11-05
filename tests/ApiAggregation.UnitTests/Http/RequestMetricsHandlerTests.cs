using ApiAggregation.Infrastructure.Http;
using ApiAggregation.Infrastructure.Statistics;
using ApiAggregation.UnitTests.Support;
using System.Net;
using System.Text;

namespace ApiAggregation.UnitTests.Http;

public sealed class RequestMetricsHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, AttemptOutcome.Success)]
    [InlineData(HttpStatusCode.BadGateway, AttemptOutcome.Failure)]
    public async Task RecordsTransportStatusAndHeaderDuration(HttpStatusCode status, AttemptOutcome outcome)
    {
        var clock = new TestClock();
        var recorder = new Recorder();
        var duration = TimeSpan.FromMilliseconds(120);
        using var invoker = CreateInvoker(recorder, clock, (_, _) =>
        {
            clock.Advance(duration);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new DelayedContent(clock) });
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        using var response = await invoker.SendAsync(request, cancellationToken);
        var completedAt = clock.GetUtcNow();
        Assert.Equal("body", await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal(new AttemptObservation("github", duration, outcome, completedAt), Assert.Single(recorder.Observations));
    }

    [Fact]
    public async Task RecordsTransportExceptionExactlyOnceAndPropagatesIt()
    {
        var clock = new TestClock();
        var recorder = new Recorder();
        var exception = new HttpRequestException("transport failure");
        using var invoker = CreateInvoker(recorder, clock, (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(45));
            return Task.FromException<HttpResponseMessage>(exception);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Same(exception, thrown);
        Assert.Equal(new AttemptObservation("github", TimeSpan.FromMilliseconds(45), AttemptOutcome.Failure, clock.GetUtcNow()),
            Assert.Single(recorder.Observations));
    }

    [Fact]
    public async Task RecordsCanceledAttemptExactlyOnce()
    {
        var clock = new TestClock();
        var recorder = new Recorder();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var invoker = CreateInvoker(recorder, clock, (_, token) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(request, cancellation.Token));

        Assert.Equal(new AttemptObservation("github", TimeSpan.FromMilliseconds(60), AttemptOutcome.Canceled, clock.GetUtcNow()),
            Assert.Single(recorder.Observations));
    }

    [Fact]
    public async Task PreCanceledRequestDoesNotReachTransportOrIncrementCounters()
    {
        var recorder = new Recorder();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var invoker = CreateInvoker(recorder, new TestClock(), (_, _) => throw new InvalidOperationException("Unexpected transport call."));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(request, cancellation.Token));

        Assert.Empty(recorder.Observations);
    }

    private static HttpMessageInvoker CreateInvoker(Recorder recorder, TimeProvider clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new RequestMetricsHandler("github", recorder, clock) { InnerHandler = new StubHttpMessageHandler(send) });

    private sealed class Recorder : IRequestStatisticsRecorder
    {
        public List<AttemptObservation> Observations { get; } = [];

        public ValueTask RecordAsync(AttemptObservation observation)
        {
            Observations.Add(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Start.AddTicks(ticks);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private sealed class DelayedContent(TestClock clock) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 4;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return stream.WriteAsync(Encoding.UTF8.GetBytes("body")).AsTask();
        }
    }
}
