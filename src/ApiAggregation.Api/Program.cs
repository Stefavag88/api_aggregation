using System.Text.Json;
using System.Text.Json.Serialization;
using ApiAggregation.Api.ErrorHandling;
using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Application.Features.Statistics;
using ApiAggregation.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();
// Start the statistics lifetime before the first request arrives.
_ = app.Services.GetRequiredService<IStatisticsReader>();
app.UseExceptionHandler();
app.MapControllers();
app.Run();

public partial class Program;
