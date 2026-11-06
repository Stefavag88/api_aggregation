using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Infrastructure.Providers;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ApiAggregation.Infrastructure.Caching;

public sealed class CachedDiscoveryProvider(
    IDiscoveryProvider inner,
    IFusionCache cache,
    IOptions<ProviderCacheOptions> options,
    TimeProvider timeProvider) : IDiscoveryProvider
{
    private const int CandidateLimit = 30;

    public ProviderDescriptor Descriptor => inner.Descriptor;

    public async Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = ProviderCacheKey.Create(Descriptor.Name, topic, CandidateLimit);
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
