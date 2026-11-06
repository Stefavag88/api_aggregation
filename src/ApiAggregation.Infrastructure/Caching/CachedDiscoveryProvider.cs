using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Infrastructure.Providers;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ApiAggregation.Infrastructure.Caching;

/// <summary>
/// Serves cached provider snapshots and coordinates refreshes within each caller's cancellation budget.
/// </summary>
/// <param name="inner">The provider used to refresh snapshots.</param>
/// <param name="cache">The local FusionCache instance shared by the provider decorators.</param>
/// <param name="options">The cache limits and per-provider policies.</param>
/// <param name="discoveryOptions">The shared candidate limit used to identify cached snapshots.</param>
/// <param name="timeProvider">The clock used for cancellation budgets and snapshot age checks.</param>
public sealed class CachedDiscoveryProvider(
    IDiscoveryProvider inner,
    IFusionCache cache,
    IOptions<ProviderCacheOptions> options,
    IOptions<ProviderDiscoveryOptions> discoveryOptions,
    TimeProvider timeProvider) : IDiscoveryProvider
{
    public ProviderDescriptor Descriptor => inner.Descriptor;

    /// <summary>
    /// Returns a usable provider snapshot, fetching or refreshing it through FusionCache as needed.
    /// </summary>
    /// <param name="topic">The normalized topic used to look up provider candidates.</param>
    /// <param name="cancellationToken">Cancels this caller's operation.</param>
    /// <returns>Fresh or eligible stale provider data, or an expected failure when no usable data is available.</returns>
    public async Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = ProviderCacheKey.Create(Descriptor.Name, topic, discoveryOptions.Value.ProviderCandidateLimit);
        var policy = options.Value.GetPolicy(Descriptor.Name);
        var entryOptions = CacheRegistration.CreateEntryOptions(policy);

        using var budget = new CancellationTokenSource(options.Value.FetchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

        AppError? factoryError = null;
        Task<CachedProviderSnapshot>? ownedFactory = null;

        try
        {
            var snapshot = await cache.GetOrSetAsync<CachedProviderSnapshot>(
                key, (context, token) => ownedFactory = FetchSnapshotAsync(context, token),
                options: entryOptions, token: linked.Token);

            cancellationToken.ThrowIfCancellationRequested();

            return snapshot.ToResult(timeProvider.GetUtcNow());
        }
        catch (FusionCacheFactoryException) when (factoryError is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return AppResult.Failure<ProviderData>(factoryError);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // FusionCache can end its wait before the scoped factory finishes cleanup.
            if (ownedFactory is not null)
            {
                try
                {
                    await ownedFactory;
                }
                catch (OperationCanceledException)
                {
                    // The linked token already requested cancellation.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            entryOptions.AllowStaleOnReadOnly = true;
            var fallback = await cache.TryGetAsync<CachedProviderSnapshot>(key, entryOptions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var now = timeProvider.GetUtcNow();

            if (fallback.HasValue && now < fallback.Value.StaleUntil)
                return fallback.Value.ToResult(now);

            return AppResult.Failure<ProviderData>(ProviderErrors.Timeout);
        }

        async Task<CachedProviderSnapshot> FetchSnapshotAsync(
            FusionCacheFactoryExecutionContext<CachedProviderSnapshot> context,
            CancellationToken token)
        {
            try
            {
                var result = await inner.FetchAsync(topic, token);
                token.ThrowIfCancellationRequested();

                if (result.IsSuccess && !(result.Value!.IsIncomplete && result.Value.Items.IsEmpty))
                {
                    var data = result.Value;
                    return new CachedProviderSnapshot(data,
                        data.FetchedAt.Add(policy.FreshDuration), data.FetchedAt.Add(policy.MaximumAge));
                }

                factoryError = result.IsSuccess ? ProviderErrors.InvalidPayload : result.Error!;
                if (context.StaleValue.HasValue && timeProvider.GetUtcNow() >= context.StaleValue.Value.StaleUntil)
                    context.Options.IsFailSafeEnabled = false;

                return context.Fail(factoryError.Code ?? ProviderErrors.UpstreamError.Code!);
            }
            catch
            {
                context.Options.IsFailSafeEnabled = false;
                throw;
            }
        }
    }
}
