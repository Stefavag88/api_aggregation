using ApiAggregation.Api.Features.Discovery;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Globalization;
using System.Text.Json.Nodes;

namespace ApiAggregation.Api.OpenApi;

internal sealed class DiscoveryOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(DiscoveryController))
            return;

        operation.Summary = "Discover developer content by topic";
        operation.Description = "Combines the first page of candidates from each selected provider, then filters, sorts, and limits the results. Partial responses can include stale data or unavailable providers.";

        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
            DescribeParameter(parameter);

        operation.Responses!["200"].Description = "Aggregated results, including empty and partial results.";
        DescribeProblem<ValidationIssue[]>(operation, context, "400", "validationErrors", "One or more query parameters are invalid.");
        DescribeProblem<ProviderReport[]>(operation, context, "503", "providers", "No selected provider completed successfully.");
    }

    private static void DescribeParameter(OpenApiParameter parameter)
    {
        switch (parameter.Name)
        {
            case "topic":
                parameter.Required = true;
                parameter.Description = "Topic with 1–50 ASCII letters, digits, or hyphens, starting with a letter or digit.";
                break;
            case "sources":
                parameter.Description = "Comma-separated enabled provider names. Omit to query all enabled providers.";
                break;
            case "type":
                parameter.Description = "Keep only items of this content type.";
                parameter.Schema = EnumSchema<ContentType>();
                break;
            case "from":
                parameter.Description = "Inclusive publication date lower bound in RFC 3339 with an explicit offset.";
                parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" };
                break;
            case "to":
                parameter.Description = "Inclusive publication date upper bound in RFC 3339 with an explicit offset.";
                parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" };
                break;
            case "sortBy":
                parameter.Description = "Field used to sort merged results.";
                parameter.Schema = EnumSchema<SortField>("date");
                break;
            case "sortDirection":
                parameter.Description = "Direction used to sort merged results.";
                parameter.Schema = EnumSchema<SortDirection>("desc");
                break;
            case "limit":
                parameter.Description = "Maximum items returned after filtering and sorting.";
                parameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Minimum = DiscoveryQueryParser.MinimumLimit.ToString(CultureInfo.InvariantCulture),
                    Maximum = DiscoveryQueryParser.MaximumLimit.ToString(CultureInfo.InvariantCulture),
                    Default = JsonValue.Create(DiscoveryQueryParser.DefaultLimit)
                };
                break;
        }
    }

    private static OpenApiSchema EnumSchema<TEnum>(string? defaultValue = null) where TEnum : struct, Enum
        => new()
        {
            Type = JsonSchemaType.String,
            Enum = [.. Enum.GetNames<TEnum>().Select(name => JsonValue.Create(name.ToLowerInvariant()))],
            Default = JsonValue.Create(defaultValue)
        };

    private static void DescribeProblem<T>(OpenApiOperation operation, OperationFilterContext context,
        string status, string extension, string description)
    {
        var response = operation.Responses![status];
        response.Description = description;
        var content = response.Content!["application/problem+json"];
        content.Schema = new OpenApiSchema
        {
            AllOf =
            [
                content.Schema!,
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        [extension] = context.SchemaGenerator.GenerateSchema(typeof(T), context.SchemaRepository)
                    }
                }
            ]
        };
    }
}
