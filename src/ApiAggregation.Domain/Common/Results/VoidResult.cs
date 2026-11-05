namespace ApiAggregation.Domain.Common.Results;

public sealed class VoidResult
{
    public AppError? Error { get; }
    public bool IsSuccess => Error is null;

    private VoidResult()
    {
    }

    private VoidResult(AppError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public static VoidResult Success() => new();

    public static VoidResult Failure(AppError error)
        => new(error);

    public static VoidResult Failure(string? code, ResultErrorCode statusCode)
        => Failure(new AppError(code, statusCode));
}
