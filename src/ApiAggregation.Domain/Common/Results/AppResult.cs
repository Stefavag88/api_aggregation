namespace ApiAggregation.Domain.Common.Results;

public static class AppResult
{
    public const string ValidationErrorsMetaKey = "validationErrors";

    public static Result<T> Success<T>(T value)
        => Result<T>.Success(value);

    public static VoidResult Success()
        => VoidResult.Success();

    public static Result<T> Failure<T>(AppError error)
        => Result<T>.Failure(error);

    public static Result<T> Failure<T>(string? code, ResultErrorCode errorCode)
        => Failure<T>(new AppError(code, errorCode));

    public static VoidResult Failure(AppError error)
        => VoidResult.Failure(error);

    public static VoidResult Failure(string? code, ResultErrorCode errorCode)
        => Failure(new AppError(code, errorCode));

    public static Result<T> NotFound<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.NotFound);

    public static VoidResult NotFound(string? appCode = null)
        => Failure(appCode, ResultErrorCode.NotFound);

    public static Result<T> ValidationError<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.ValidationError);

    public static VoidResult ValidationError(string? appCode = null)
        => Failure(appCode, ResultErrorCode.ValidationError);

    public static Result<T> ValidationErrors<T>(
        IReadOnlyCollection<ValidationIssue> errors,
        string? appCode = null)
    {
        if (errors.Count == 0)
            throw new ArgumentException("At least one validation error is required.", nameof(errors));

        return Failure<T>(new AppError(
            appCode ?? ValidationCodes.Failed,
            ResultErrorCode.ValidationError,
            new Dictionary<string, object?>
            {
                [ValidationErrorsMetaKey] = errors
            }));
    }

    public static VoidResult ValidationErrors(
        IReadOnlyCollection<ValidationIssue> errors,
        string? appCode = null)
    {
        if (errors.Count == 0)
            throw new ArgumentException("At least one validation error is required.", nameof(errors));

        return Failure(new AppError(
            appCode ?? ValidationCodes.Failed,
            ResultErrorCode.ValidationError,
            new Dictionary<string, object?>
            {
                [ValidationErrorsMetaKey] = errors
            }));
    }

    public static Result<T> Unauthorized<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.Unauthorized);

    public static VoidResult Unauthorized(string? appCode = null)
        => Failure(appCode, ResultErrorCode.Unauthorized);

    public static Result<T> Forbidden<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.Forbidden);

    public static VoidResult Forbidden(string? appCode = null)
        => Failure(appCode, ResultErrorCode.Forbidden);

    public static Result<T> Conflict<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.Conflict);

    public static VoidResult Conflict(string? appCode = null)
        => Failure(appCode, ResultErrorCode.Conflict);

    public static Result<T> RateLimited<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.RateLimited);

    public static VoidResult RateLimited(string? appCode = null)
        => Failure(appCode, ResultErrorCode.RateLimited);

    public static Result<T> InternalError<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.InternalError);

    public static VoidResult InternalError(string? appCode = null)
        => Failure(appCode, ResultErrorCode.InternalError);

    public static Result<T> Unspecified<T>(string? appCode = null)
        => Failure<T>(appCode, ResultErrorCode.Unspecified);

    public static VoidResult Unspecified(string? appCode = null)
        => Failure(appCode, ResultErrorCode.Unspecified);
    public static Result<T> ServiceUnavailable<T>(
        string? code = null,
        IReadOnlyDictionary<string, object?>? meta = null)
        => Failure<T>(new AppError(code, ResultErrorCode.ServiceUnavailable, meta));

    public static VoidResult ServiceUnavailable(
        string? code = null,
        IReadOnlyDictionary<string, object?>? meta = null)
        => Failure(new AppError(code, ResultErrorCode.ServiceUnavailable, meta));
}
