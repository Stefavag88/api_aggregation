using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Application.Features.Statistics;
using ApiAggregation.Infrastructure.Http;
using ApiAggregation.Infrastructure.Providers;
using ApiAggregation.Infrastructure.Providers.DevTo;
using ApiAggregation.Infrastructure.Providers.GitHub;
using ApiAggregation.Infrastructure.Providers.HackerNews;
using ApiAggregation.Infrastructure.Statistics;
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
        services.AddOptions<StatisticsOptions>()
            .Bind(configuration.GetSection(StatisticsOptions.SectionName))
            .Validate(options => options.SnapshotInterval >= TimeSpan.FromMilliseconds(1)
                && options.SnapshotInterval <= TimeSpan.FromMilliseconds(uint.MaxValue - 1),
                "Statistics:SnapshotInterval must be between 1 millisecond and the maximum supported timer period.")
            .ValidateOnStart();
        services.AddSingleton<InMemoryStatisticsStore>();
        services.AddHostedService(provider => provider.GetRequiredService<InMemoryStatisticsStore>());
        services.AddSingleton<IStatisticsReader>(provider => provider.GetRequiredService<InMemoryStatisticsStore>());
        services.AddSingleton<IRequestStatisticsRecorder>(provider => provider.GetRequiredService<InMemoryStatisticsStore>());
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

        AddProviderClient<GitHubClient, GitHubOptions>(
            services,
            GitHubClient.Metadata,
            "application/vnd.github+json",
            static (client, options) =>
            {
                if (!string.IsNullOrEmpty(options.Token))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            });
    }

    private static void AddDevTo(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(nameof(ProviderOptions.Enabled), ProviderOptions.EnabledByDefault))
            return;

        services.AddOptions<DevToOptions>()
            .Bind(configuration)
            .Validate(ProviderOptions.HasValidBaseAddress, "The DEV base address must be an HTTPS origin.")
            .ValidateOnStart();

        AddProviderClient<DevToClient, DevToOptions>(services, DevToClient.Metadata, "application/vnd.forem.api-v1+json");
    }

    private static void AddHackerNews(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(nameof(ProviderOptions.Enabled), ProviderOptions.EnabledByDefault))
            return;

        services.AddOptions<HackerNewsOptions>()
            .Bind(configuration)
            .Validate(ProviderOptions.HasValidBaseAddress, "The Hacker News base address must be an HTTPS origin.")
            .ValidateOnStart();

        AddProviderClient<HackerNewsClient, HackerNewsOptions>(services, HackerNewsClient.Metadata, "application/json");
    }

    private static void AddProviderClient<TClient, TOptions>(
        IServiceCollection services,
        ProviderDescriptor descriptor,
        string accept,
        Action<HttpClient, TOptions>? configureClient = null)
        where TClient : class, IExternalContentClient
        where TOptions : ProviderOptions
    {
        services.AddHttpClient<TClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<TOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseAddress);
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ApiAggregationService/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd(accept);

                configureClient?.Invoke(client, options);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            })
            .AddHttpMessageHandler(provider => new RequestMetricsHandler(
                descriptor.Name,
                provider.GetRequiredService<IRequestStatisticsRecorder>(),
                provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton(descriptor);
        services.AddScoped<IDiscoveryProvider>(provider => new DiscoveryProviderAdapter(
            provider.GetRequiredService<TClient>(),
            provider.GetRequiredService<TimeProvider>()));
    }
}
