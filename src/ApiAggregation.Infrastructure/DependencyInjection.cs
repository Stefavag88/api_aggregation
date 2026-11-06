using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Application.Features.Statistics;
using ApiAggregation.Infrastructure.Caching;
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
using ZiggyCreatures.Caching.Fusion;
using System.Net;
using System.Net.Http.Headers;

namespace ApiAggregation.Infrastructure;

/// <summary>
/// Registers the provider clients, cache, and statistics services used by Infrastructure.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure services and the enabled provider clients.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration used to bind settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        AddProviders(services, configuration);
        AddStatistics(services, configuration);

        return services;
    }

    private static void AddProviders(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProviderDiscoveryOptions>()
            .Bind(configuration.GetSection(ProviderDiscoveryOptions.SectionName))
            .Validate(options => options.ProviderCandidateLimit is >= 1 and <= ProviderDiscoveryOptions.MaximumCandidateLimit,
                $"Discovery:ProviderCandidateLimit must be between 1 and {ProviderDiscoveryOptions.MaximumCandidateLimit}.")
            .ValidateOnStart();

        services.AddProviderCaching(configuration);
        AddProviderResilience(services, configuration);

        AddGitHub(services, configuration.GetSection(GitHubOptions.SectionName));
        AddDevTo(services, configuration.GetSection(DevToOptions.SectionName));
        AddHackerNews(services, configuration.GetSection(HackerNewsOptions.SectionName));
    }

    private static void AddProviderResilience(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ProviderRateLimitState>();

        services.AddOptions<ProviderResilienceOptions>()
            .Bind(configuration.GetSection(ProviderResilienceOptions.SectionName))
            .Validate(ProviderResilienceOptions.HasValidSettings,
                "ProviderResilience requires an attempt timeout from 10 milliseconds to one day and a retry delay from zero to one day.")
            .Validate(options => options.ConcurrencyLimit > 0, "ProviderResilience:ConcurrencyLimit must be positive.")
            .Validate(ProviderResilienceOptions.HasValidCircuitSettings,
                "ProviderResilience circuit settings require a failure ratio above zero and at most one, a minimum throughput of at least two, and durations from 500 milliseconds to one day.")
            .ValidateOnStart();
    }

    private static void AddStatistics(IServiceCollection services, IConfiguration configuration)
    {
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

        AddProviderClient<GitHubClient, GitHubOptions, GitHubRateLimitPolicy>(
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

        AddProviderClient<DevToClient, DevToOptions, DefaultRateLimitPolicy>(
            services,
            DevToClient.Metadata,
            "application/vnd.forem.api-v1+json");
    }

    private static void AddHackerNews(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(nameof(ProviderOptions.Enabled), ProviderOptions.EnabledByDefault))
            return;

        services.AddOptions<HackerNewsOptions>()
            .Bind(configuration)
            .Validate(ProviderOptions.HasValidBaseAddress, "The Hacker News base address must be an HTTPS origin.")
            .ValidateOnStart();

        AddProviderClient<HackerNewsClient, HackerNewsOptions, DefaultRateLimitPolicy>(
            services,
            HackerNewsClient.Metadata,
            "application/json");
    }

    private static void AddProviderClient<TClient, TOptions, TRateLimitPolicy>(
        IServiceCollection services,
        ProviderDescriptor descriptor,
        string accept,
        Action<HttpClient, TOptions>? configureClient = null)
        where TClient : class, IExternalContentClient
        where TOptions : ProviderOptions
        where TRateLimitPolicy : class, IRateLimitPolicy
    {
        services.TryAddSingleton<TRateLimitPolicy>();

        var builder = services.AddHttpClient<TClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<TOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseAddress);
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ApiAggregationService/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd(accept);

                configureClient?.Invoke(client, options);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            });

        ProviderHttpPipeline.Configure(builder, descriptor.Name)
            .AddHttpMessageHandler(provider => new ProviderRateLimitHandler(
                descriptor.Name,
                provider.GetRequiredService<TRateLimitPolicy>(),
                provider.GetRequiredService<ProviderRateLimitState>(),
                provider.GetRequiredService<TimeProvider>()))
            .AddHttpMessageHandler(provider => new RequestMetricsHandler(
                descriptor.Name,
                provider.GetRequiredService<IRequestStatisticsRecorder>(),
                provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton(descriptor);
        services.AddScoped<IDiscoveryProvider>(provider => new CachedDiscoveryProvider(
            new DiscoveryProviderAdapter(
                provider.GetRequiredService<TClient>(),
                provider.GetRequiredService<TimeProvider>()),
            provider.GetRequiredService<IFusionCache>(),
            provider.GetRequiredService<IOptions<ProviderCacheOptions>>(),
            provider.GetRequiredService<IOptions<ProviderDiscoveryOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
    }
}
