using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Infrastructure.Providers;
using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Providers.HackerNews;
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
        AddDevTo(services, configuration.GetSection(DevToOptions.SectionName));
        AddHackerNews(services, configuration.GetSection(HackerNewsOptions.SectionName));

        return services;
    }

    private static void AddGitHub(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(nameof(ProviderOptions.Enabled), ProviderOptions.EnabledByDefault))
            return;

        services.AddOptions<GitHubOptions>()
            .Bind(configuration)
            .Validate(ProviderOptions.HasValidBaseAddress, "The GitHub base address must be an HTTPS origin.")
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

    private static void AddDevTo(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(nameof(ProviderOptions.Enabled), ProviderOptions.EnabledByDefault))
            return;

        services.AddOptions<DevToOptions>()
            .Bind(configuration)
            .Validate(ProviderOptions.HasValidBaseAddress, "The DEV base address must be an HTTPS origin.")
            .ValidateOnStart();

        services.AddHttpClient<DevToClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<DevToOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseAddress);
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ApiAggregationService/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.forem.api-v1+json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            });

        services.AddSingleton(DevToClient.Metadata);
        services.AddScoped<IDiscoveryProvider>(provider => new DiscoveryProviderAdapter(
            provider.GetRequiredService<DevToClient>(),
            provider.GetRequiredService<TimeProvider>()));
    }

    private static void AddHackerNews(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(nameof(ProviderOptions.Enabled), ProviderOptions.EnabledByDefault))
            return;

        services.AddOptions<HackerNewsOptions>()
            .Bind(configuration)
            .Validate(ProviderOptions.HasValidBaseAddress, "The Hacker News base address must be an HTTPS origin.")
            .ValidateOnStart();

        services.AddHttpClient<HackerNewsClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseAddress);
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ApiAggregationService/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            });

        services.AddSingleton(HackerNewsClient.Metadata);
        services.AddScoped<IDiscoveryProvider>(provider => new DiscoveryProviderAdapter(
            provider.GetRequiredService<HackerNewsClient>(),
            provider.GetRequiredService<TimeProvider>()));
    }
}
