using ApiAggregation.Application.Features.Discovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiAggregation.IntegrationTests.Support;

public sealed class ApiFactory(params IDiscoveryProvider[] providers) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDiscoveryProvider>();
            foreach (var provider in providers) services.AddSingleton(provider);
        });
    }
}
