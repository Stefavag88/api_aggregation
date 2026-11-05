namespace ApiAggregation.Domain.Common.Results;

public sealed record ValidationIssue(
    string Field,
    string Code,
    string? Message = null);
