namespace ApiAggregation.Infrastructure.Statistics;

/// <summary>
/// Classifies how an outbound HTTP attempt ended.
/// </summary>
public enum AttemptOutcome
{
    Success,
    Failure,
    Canceled
}
