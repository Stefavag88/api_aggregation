using System.Text.Json;
using System.Text.Json.Serialization;
using ApiAggregation.Api.ErrorHandling;
using ApiAggregation.Application.Features.Discovery;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();
app.UseExceptionHandler();
app.MapControllers();
app.Run();

public partial class Program;
