namespace ApiAggregation.Domain.Common.Results;

public sealed record AppError(
    string? Code,
    ResultErrorCode StatusCode,
    IReadOnlyDictionary<string, object?>? Meta = null);
