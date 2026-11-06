using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using ApiAggregation.IntegrationTests.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Authentication;

public sealed class JwtAuthenticationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public async Task RejectsInvalidCredentialsBeforeProviderCalls(string fault)
    {
        var provider = new CountingProvider();
        await using var factory = new ApiFactory(provider);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = fault == "missing" ? null : new("Bearer", fault switch
        {
            "expired" => TestTokenIssuer.Create(expired: true),
            "signature" => TestTokenIssuer.Create(wrongSignature: true),
            "issuer" => TestTokenIssuer.Create(issuer: "untrusted"),
            "audience" => TestTokenIssuer.Create(audience: "another-api"),
            _ => "not-a-jwt"
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        using var discovery = await client.GetAsync("/api/discovery?topic=dotnet", cancellationToken);
        using var statistics = await client.GetAsync("/api/statistics", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, discovery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, statistics.StatusCode);
        Assert.Contains(discovery.Headers.WwwAuthenticate, challenge => challenge.Scheme == "Bearer");
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ValidTokenGrantsAccessAndLivenessAllowsAnonymousRequests()
    {
        var provider = new CountingProvider();
        await using var factory = new ApiFactory(provider);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokenIssuer.Create());
        var cancellationToken = TestContext.Current.CancellationToken;
        using var discovery = await client.GetAsync("/api/discovery?topic=dotnet", cancellationToken);
        using var statistics = await client.GetAsync("/api/statistics", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.Equal(HttpStatusCode.OK, statistics.StatusCode);
        Assert.Equal(1, provider.Calls);

        client.DefaultRequestHeaders.Authorization = null;
        using var liveness = await client.GetAsync("/health/live", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [Theory]
    [InlineData("", "discovery-api")]
    [InlineData("http://identity.example.test", "discovery-api")]
    [InlineData("https://identity.example.test?tenant=example", "discovery-api")]
    [InlineData("https://identity.example.test#issuer", "discovery-api")]
    [InlineData("https://user@identity.example.test", "discovery-api")]
    [InlineData("https://identity.example.test", "")]
    public async Task ProductionRequiresHttpsAuthorityAndAudience(string authority, string audience)
    {
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = host.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = authority,
                    ["Authentication:Audience"] = audience
                })));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Authentication:", exception.Message);
    }

    [Fact]
    public async Task ProductionUsesIssuerMetadataKeysInsteadOfDevelopmentSigningKeys()
    {
        const string authority = "https://identity.example.test";
        const string audience = "discovery-api";
        const string keyId = "issuer-key";
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportParameters(false);
        var developmentKey = (SymmetricSecurityKey)TestTokenIssuer.CreateValidationParameters().IssuerSigningKey!;
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = host.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = authority,
                    ["Authentication:Audience"] = audience,
                    ["Authentication:Schemes:Bearer:ValidIssuer"] = authority,
                    ["Authentication:Schemes:Bearer:ValidAudience"] = audience,
                    ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = authority,
                    ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(developmentKey.Key)
                }))
            .ConfigureTestServices(services => services.Configure<JwtBearerOptions>("Bearer", options =>
            {
                options.BackchannelHttpHandler = new ProviderHttpStub(request =>
                {
                    var payload = request.RequestUri!.AbsolutePath switch
                    {
                        "/.well-known/openid-configuration" => JsonSerializer.Serialize(
                            new { issuer = authority, jwks_uri = $"{authority}/keys" }),
                        "/keys" => JsonSerializer.Serialize(new
                        {
                            keys = new[] { new
                            {
                                kty = "RSA",
                                kid = keyId,
                                n = Base64UrlEncoder.Encode(publicKey.Modulus!),
                                e = Base64UrlEncoder.Encode(publicKey.Exponent!)
                            } }
                        }),
                        _ => throw new InvalidOperationException("Unexpected issuer metadata request.")
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
                });
            })));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        var jwtHandler = new JwtSecurityTokenHandler();
        var jwt = new JwtSecurityToken(authority, audience, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: signingCredentials);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtHandler.WriteToken(jwt));
        using var authorized = await client.GetAsync("/api/statistics", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        var wrongAudienceJwt = new JwtSecurityToken(authority, $"{audience}/", expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: signingCredentials);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtHandler.WriteToken(wrongAudienceJwt));
        using var wrongAudience = await client.GetAsync("/api/statistics", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokenIssuer.Create(authority, audience));
        using var rejected = await client.GetAsync("/api/statistics", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    private sealed class CountingProvider : IDiscoveryProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("github", ContentType.Repository);
        public int Calls { get; private set; }

        public Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AppResult.Success(new ProviderData([], DateTimeOffset.UtcNow, false, false)));
        }
    }
}
