namespace ApiAggregation.Domain.Common.Results;

public sealed class Result<TValue> : ResultBase<TValue>
{
    public Result(TValue value) : base(value)
    {
    }

    public Result(AppError error) : base(error)
    {
    }

    public static Result<TValue> Success(TValue value)
        => new(value);

    public static implicit operator Result<TValue>(TValue value)
        => Success(value);

    public static Result<TValue> Failure(AppError error)
        => new(error);

    public static Result<TValue> Failure(string? code, ResultErrorCode statusCode)
        => Failure(new AppError(code, statusCode));

    public static implicit operator Result<TValue>(AppError error)
        => Failure(error);
}
