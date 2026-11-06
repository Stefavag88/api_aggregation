namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Contains the provider name, elapsed time, and outcome of an outbound attempt.
/// </summary>
public readonly record struct AttemptObservation(
    string Source,
    TimeSpan Duration,
    AttemptOutcome Outcome,
    DateTimeOffset CompletedAt);
