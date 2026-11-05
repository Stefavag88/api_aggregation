using ApiAggregation.Api.ErrorHandling;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.IntegrationTests.Discovery;

public sealed class ResultHttpMappingTests
{
    [Theory]
    [InlineData(ResultErrorCode.ValidationError, 400)]
    [InlineData(ResultErrorCode.NotFound, 404)]
    [InlineData(ResultErrorCode.Unauthorized, 401)]
    [InlineData(ResultErrorCode.Forbidden, 403)]
    [InlineData(ResultErrorCode.Conflict, 409)]
    [InlineData(ResultErrorCode.RateLimited, 429)]
    [InlineData(ResultErrorCode.ServiceUnavailable, 503)]
    [InlineData(ResultErrorCode.InternalError, 500)]
    [InlineData(ResultErrorCode.Unspecified, 500)]
    public void MapsErrorCodesAndStripsUnknownMetadata(ResultErrorCode code, int status)
    {
        var result = ResultHttpExtensions.ToProblem(new AppError("safe.code", code,
            new Dictionary<string, object?> { ["exception"] = "secret", ["token"] = "secret" }));
        Assert.Equal(status, result.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(result.Value);
        Assert.False(problem.Extensions.ContainsKey("exception"));
        Assert.False(problem.Extensions.ContainsKey("token"));
    }

    [Fact]
    public void GroupsValidationIssuesAndPreservesTypedAllowlistedMetadata()
    {
        var error = AppResult.ValidationErrors<string>([
            new("topic", "validation.required", "Required."), new("topic", "validation.invalid", "Invalid.")]).Error!;
        var problem = Assert.IsType<ValidationProblemDetails>(ResultHttpExtensions.ToProblem(error).Value);
        Assert.Equal(new[] { "Required.", "Invalid." }, problem.Errors["topic"]);
        Assert.True(problem.Extensions.ContainsKey("validationErrors"));
        var providerError = AppResult.ServiceUnavailable<string>("providers_unavailable", new Dictionary<string, object?>
        {
            ["providers"] = System.Collections.Immutable.ImmutableArray.Create(new ProviderReport("github", ProviderStatus.Unavailable, null, false, "provider.timeout"))
        }).Error!;
        Assert.True(Assert.IsType<ProblemDetails>(ResultHttpExtensions.ToProblem(providerError).Value).Extensions.ContainsKey("providers"));
    }

    [Fact]
    public void RejectsUntypedAllowlistedMetadata()
    {
        var problem = Assert.IsType<ProblemDetails>(ResultHttpExtensions.ToProblem(new AppError("safe", ResultErrorCode.ServiceUnavailable,
            new Dictionary<string, object?> { ["providers"] = new { token = "secret" } })).Value);
        Assert.False(problem.Extensions.ContainsKey("providers"));
    }
}
