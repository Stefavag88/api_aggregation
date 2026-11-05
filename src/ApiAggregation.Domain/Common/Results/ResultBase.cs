namespace ApiAggregation.Domain.Common.Results;

public abstract class ResultBase<TValue>
{
    public AppError? Error { get; }
    public bool IsSuccess => Error is null;
    public TValue? Value { get; }

    protected ResultBase(TValue value)
    {
        Value = value;
    }

    protected ResultBase(AppError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }
}
