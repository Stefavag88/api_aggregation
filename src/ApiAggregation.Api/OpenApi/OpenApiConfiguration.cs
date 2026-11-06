using Microsoft.OpenApi;

namespace ApiAggregation.Api.OpenApi;

internal static class OpenApiConfiguration
{
    internal static IServiceCollection AddApiDocumentation(this IServiceCollection services)
        => services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Developer Discovery API",
                Version = "v1",
                Description = "Aggregates developer content and reports outbound provider request statistics."
            });
            options.DescribeAllParametersInCamelCase();
            options.OperationFilter<DiscoveryOperationFilter>();
        });
}
