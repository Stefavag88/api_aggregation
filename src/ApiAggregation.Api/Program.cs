using System.Text.Json;
using System.Text.Json.Serialization;
using ApiAggregation.Api.Authentication;
using ApiAggregation.Api.ErrorHandling;
using ApiAggregation.Api.OpenApi;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddApiDocumentation();
builder.Services.AddApiAuthentication(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program;
