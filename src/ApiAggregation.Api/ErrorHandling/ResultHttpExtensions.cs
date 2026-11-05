using System.Collections.Immutable;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.Api.ErrorHandling;

public static class ResultHttpExtensions
{
    public static ActionResult<TResponse> ToActionResult<TValue, TResponse>(this Result<TValue> result,
        Func<TValue, TResponse> onSuccess) => result.IsSuccess
            ? new OkObjectResult(onSuccess(result.Value!)) : ToProblem(result.Error!);

    public static ObjectResult ToProblem(AppError error)
    {
        var (status, title) = error.StatusCode switch
        {
            ResultErrorCode.ValidationError => (400, "Invalid request"),
            ResultErrorCode.NotFound => (404, "Not found"),
            ResultErrorCode.Unauthorized => (401, "Unauthorized"),
            ResultErrorCode.Forbidden => (403, "Forbidden"),
            ResultErrorCode.Conflict => (409, "Conflict"),
            ResultErrorCode.RateLimited => (429, "Too many requests"),
            ResultErrorCode.ServiceUnavailable => (503, "Service unavailable"),
            _ => (500, "An unexpected error occurred")
        };
        var issues = error.Meta?.GetValueOrDefault(AppResult.ValidationErrorsMetaKey) as IReadOnlyCollection<ValidationIssue>;
        ProblemDetails problem = status == 400 && issues is not null
            ? new ValidationProblemDetails(issues.GroupBy(i => i.Field).ToDictionary(g => g.Key, g => g.Select(i => i.Message ?? i.Code).ToArray()))
            : new ProblemDetails();
        problem.Status = status;
        problem.Title = title;
        problem.Extensions["code"] = error.Code;
        if (status == 400 && issues is not null) problem.Extensions[AppResult.ValidationErrorsMetaKey] = issues;
        if (error.Meta?.GetValueOrDefault("providers") is ImmutableArray<ProviderReport> reports)
            problem.Extensions["providers"] = reports;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
