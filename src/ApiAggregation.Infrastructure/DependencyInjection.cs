using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Infrastructure.Providers;
using ApiAggregation.Infrastructure.Providers.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;

namespace ApiAggregation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        AddGitHub(services, configuration.GetSection(GitHubOptions.SectionName));

        return services;
    }

    private static void AddGitHub(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GitHubOptions>()
            .Configure(options =>
            {
                options.BaseAddress = configuration[nameof(GitHubOptions.BaseAddress)] ?? options.BaseAddress;
                options.Token = configuration[nameof(GitHubOptions.Token)];
            })
            .Validate(GitHubOptions.HasValidBaseAddress, "The GitHub base address must be an HTTPS origin.")
            .Validate(GitHubOptions.HasValidToken, "The GitHub token contains invalid characters.")
            .ValidateOnStart();

        services.AddHttpClient<GitHubClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<GitHubOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseAddress);
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ApiAggregationService/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                if (!string.IsNullOrEmpty(options.Token))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            });

        services.AddSingleton(GitHubClient.Metadata);
        services.AddScoped<IDiscoveryProvider>(provider => new DiscoveryProviderAdapter(
            provider.GetRequiredService<GitHubClient>(),
            provider.GetRequiredService<TimeProvider>()));
    }
}
