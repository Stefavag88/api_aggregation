using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ApiAggregation.IntegrationTests.Support;

internal static class TestTokenIssuer
{
    internal const string Issuer = "api-aggregation-integration-tests";
    internal const string Audience = "api-aggregation-test-api";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("integration-test-only-signing-key-0001"));
    private static readonly SymmetricSecurityKey WrongKey = new(Encoding.UTF8.GetBytes("integration-test-only-signing-key-0002"));

    internal static TokenValidationParameters CreateValidationParameters()
        => new()
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = SigningKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

    internal static string Create(string issuer = Issuer, string audience = Audience, bool expired = false, bool wrongSignature = false)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "integration-test")],
            notBefore: now.AddMinutes(-5),
            expires: expired ? now.AddMinutes(-1) : now.AddMinutes(10),
            signingCredentials: new SigningCredentials(wrongSignature ? WrongKey : SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
