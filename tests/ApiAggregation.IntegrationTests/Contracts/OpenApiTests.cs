using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace ApiAggregation.IntegrationTests.Contracts;

public sealed class OpenApiTests
{
    [Fact]
    public async Task DocumentsDiscoveryStatisticsAndLivenessContracts()
    {
        await using var host = new WebApplicationFactory<Program>();
        await using var factory = host.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync("/swagger/v1/swagger.json", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var document = json.RootElement;
        var paths = document.GetProperty("paths");
        var bearer = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        var discovery = paths.GetProperty("/api/discovery").GetProperty("get");
        AssertRequiresBearer(discovery);
        var parameters = discovery.GetProperty("parameters").EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!);

        Assert.Equal(8, parameters.Count);
        Assert.True(parameters["topic"].GetProperty("required").GetBoolean());
        var limit = parameters["limit"].GetProperty("schema");
        Assert.Equal("integer", limit.GetProperty("type").GetString());
        Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
        Assert.Equal(90, limit.GetProperty("maximum").GetInt32());
        Assert.Equal(30, limit.GetProperty("default").GetInt32());
        AssertEnum(parameters["type"], "repository", "article", "discussion");
        AssertEnum(parameters["sortBy"], "date", "title");
        AssertEnum(parameters["sortDirection"], "asc", "desc");
        Assert.Equal("date-time", parameters["from"].GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal("date-time", parameters["to"].GetProperty("schema").GetProperty("format").GetString());

        var data = Resolve(document, ResponseSchema(discovery, "200", "application/json")).GetProperty("properties");
        Assert.True(data.TryGetProperty("isPartial", out _));
        Assert.True(data.TryGetProperty("matchedCount", out _));
        var reports = Resolve(document, data.GetProperty("providers").GetProperty("items")).GetProperty("properties");
        Assert.True(reports.TryGetProperty("status", out _));
        Assert.True(reports.TryGetProperty("fetchedAt", out _));
        Assert.True(reports.TryGetProperty("errorCode", out _));
        AssertProblemExtension(discovery, "400", "validationErrors");
        AssertProblemExtension(discovery, "503", "providers");

        var statistics = paths.GetProperty("/api/statistics").GetProperty("get");
        AssertRequiresBearer(statistics);
        var statisticsProperties = Resolve(document, ResponseSchema(statistics, "200", "application/json")).GetProperty("properties");
        Assert.True(statisticsProperties.TryGetProperty("measurementScope", out _));
        var provider = Resolve(document, statisticsProperties.GetProperty("providers").GetProperty("items")).GetProperty("properties");
        Assert.True(provider.GetProperty("averageResponseTimeMs").GetProperty("nullable").GetBoolean());
        var bucket = Resolve(document, provider.GetProperty("fast")).GetProperty("properties");
        Assert.True(bucket.GetProperty("averageResponseTimeMs").GetProperty("nullable").GetBoolean());

        var liveness = paths.GetProperty("/health/live").GetProperty("get");
        Assert.False(liveness.TryGetProperty("security", out _));
        var health = Resolve(document, ResponseSchema(liveness, "200", "application/json")).GetProperty("properties");
        Assert.Equal("string", health.GetProperty("status").GetProperty("type").GetString());
    }

    [Fact]
    public async Task SwaggerIsAvailableOnlyInDevelopment()
    {
        await using var host = new WebApplicationFactory<Program>();
        await using var development = host.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        await using var production = host.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = "https://identity.example.test",
                    ["Authentication:Audience"] = "discovery-api"
                })));
        using var developmentClient = development.CreateClient();
        using var productionClient = production.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var ui = await developmentClient.GetAsync("/swagger/index.html", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        using var document = await productionClient.GetAsync("/swagger/v1/swagger.json", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        using var productionUi = await productionClient.GetAsync("/swagger/index.html", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, productionUi.StatusCode);
    }

    private static void AssertEnum(JsonElement parameter, params string[] expected)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), parameter.GetProperty("schema").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));

    private static void AssertRequiresBearer(JsonElement operation)
    {
        var security = Assert.Single(operation.GetProperty("security").EnumerateArray());
        Assert.Equal(0, security.GetProperty("Bearer").GetArrayLength());
        Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
    }

    private static JsonElement ResponseSchema(JsonElement operation, string status, string contentType)
        => operation.GetProperty("responses").GetProperty(status).GetProperty("content")
            .GetProperty(contentType).GetProperty("schema");

    private static void AssertProblemExtension(JsonElement operation, string status, string name)
    {
        var schema = ResponseSchema(operation, status, "application/problem+json");
        Assert.Contains(schema.GetProperty("allOf").EnumerateArray(),
            part => part.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("code", out _)
                && properties.TryGetProperty(name, out _));
    }

    private static JsonElement Resolve(JsonElement document, JsonElement schema)
        => schema.TryGetProperty("$ref", out var reference)
            ? document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1])
            : schema;
}
