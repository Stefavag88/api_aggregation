using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ApiAggregation.Api.Authentication;

internal static class AuthenticationRegistration
{
    internal static IServiceCollection AddApiAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var authentication = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
        services.AddAuthorization();

        if (environment.IsDevelopment())
        {
            // The SDK's user-jwts tool configures the Bearer scheme and stores its signing key in user secrets.
            authentication.AddJwtBearer(options => options.TokenValidationParameters.ClockSkew = TimeSpan.Zero);
            return services;
        }

        authentication.AddJwtBearer(options =>
        {
            var authority = configuration["Authentication:Authority"];
            if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer)
                || issuer.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(issuer.Host)
                || issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0)
                throw new InvalidOperationException("Authentication:Authority must be an absolute HTTPS issuer URL.");

            var audience = configuration["Authentication:Audience"];
            if (string.IsNullOrWhiteSpace(audience))
                throw new InvalidOperationException("Authentication:Audience is required.");

            options.Authority = authority;
            options.Audience = audience;
            options.RequireHttpsMetadata = true;
            options.MetadataAddress = string.Empty;
            options.IncludeErrorDetails = false;
            options.Configuration = null;
            options.ConfigurationManager = null;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudience = audience,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).ValidateOnStart();

        return services;
    }
}
