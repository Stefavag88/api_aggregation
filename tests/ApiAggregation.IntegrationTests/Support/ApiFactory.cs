using ApiAggregation.Application.Features.Discovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiAggregation.IntegrationTests.Support;

public sealed class ApiFactory(params IDiscoveryProvider[] providers) : AuthenticatedApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDiscoveryProvider>();
            foreach (var provider in providers) services.AddSingleton(provider);
        });
    }
}
