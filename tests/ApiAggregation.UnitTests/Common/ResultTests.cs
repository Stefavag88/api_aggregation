using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.UnitTests.Common;

public sealed class ResultTests
{
    [Fact]
    public void SuccessPreservesValueWithoutAnError()
    {
        var result = AppResult.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FailurePreservesErrorWithoutASuccessfulValue()
    {
        var error = new AppError("provider.unavailable", ResultErrorCode.ServiceUnavailable);
        var result = AppResult.Failure<string>(error);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Same(error, result.Error);
        Assert.Equal(0, Result<int>.Failure(error).Value);
    }

    [Fact]
    public void ImplicitConversionsPreserveSuccessAndFailure()
    {
        Result<int> success = 42;
        var error = new AppError("invalid", ResultErrorCode.ValidationError);
        Result<int> failure = error;

        Assert.True(success.IsSuccess);
        Assert.Equal(42, success.Value);
        Assert.False(failure.IsSuccess);
        Assert.Same(error, failure.Error);
    }

    [Fact]
    public void VoidResultsDistinguishSuccessFromFailure()
    {
        var error = new AppError("conflict", ResultErrorCode.Conflict);

        Assert.True(AppResult.Success().IsSuccess);
        Assert.Null(AppResult.Success().Error);
        Assert.False(AppResult.Failure(error).IsSuccess);
        Assert.Same(error, AppResult.Failure(error).Error);
    }

    [Fact]
    public void NullErrorCannotCreateASuccessfulFailure()
    {
        Assert.Throws<ArgumentNullException>(() => new Result<int>((AppError)null!));
        Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => VoidResult.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => { Result<int> result = (AppError)null!; });
    }

    [Fact]
    public void ValidationPreservesAllFieldIssues()
    {
        ValidationIssue[] issues = [
            new("topic", ValidationCodes.Required),
            new("limit", ValidationCodes.OutOfRange, "Must be between 1 and 90.")
        ];
        var result = AppResult.ValidationErrors<string>(issues);

        Assert.False(result.IsSuccess);
        Assert.Equal(ValidationCodes.Failed, result.Error!.Code);
        Assert.Equal(ResultErrorCode.ValidationError, result.Error.StatusCode);
        Assert.Equal(issues, Assert.IsAssignableFrom<IReadOnlyCollection<ValidationIssue>>(
            result.Error.Meta![AppResult.ValidationErrorsMetaKey]));
        Assert.Equal("custom", AppResult.ValidationErrors(issues, "custom").Error!.Code);
    }

    [Fact]
    public void EmptyValidationIssuesAreProgrammerMisuse()
    {
        Assert.Throws<ArgumentException>(() => AppResult.ValidationErrors<string>([]));
        Assert.Throws<ArgumentException>(() => AppResult.ValidationErrors([]));
    }

    [Fact]
    public void ServiceUnavailablePreservesSafeMetadata()
    {
        var meta = new Dictionary<string, object?> { ["providers"] = "reports" };
        var result = AppResult.ServiceUnavailable<string>("discovery.providers_unavailable", meta);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultErrorCode.ServiceUnavailable, result.Error!.StatusCode);
        Assert.Same(meta, result.Error.Meta);
        Assert.False(AppResult.ServiceUnavailable().IsSuccess);
    }
}
