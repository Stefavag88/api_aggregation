namespace ApiAggregation.Infrastructure.Statistics;

public readonly record struct AttemptObservation(
    string Source,
    TimeSpan Duration,
    AttemptOutcome Outcome,
    DateTimeOffset CompletedAt);
