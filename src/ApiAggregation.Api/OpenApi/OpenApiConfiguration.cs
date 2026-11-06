using Microsoft.AspNetCore.Authentication.JwtBearer;
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
            options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste the JWT access token."
            });
            options.OperationFilter<BearerAuthorizationOperationFilter>();
            options.OperationFilter<DiscoveryOperationFilter>();
        });
}
