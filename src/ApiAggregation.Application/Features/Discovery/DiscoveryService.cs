using System.Collections.Immutable;
using ApiAggregation.Domain.Common.Results;

namespace ApiAggregation.Application.Features.Discovery;

public sealed class DiscoveryService(IEnumerable<IDiscoveryProvider> providers)
{
    private readonly IReadOnlyDictionary<string, IDiscoveryProvider> providersByName =
        providers.ToDictionary(p => p.Descriptor.Name, StringComparer.Ordinal);

    public async Task<Result<DiscoveryResult>> DiscoverAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outcomes = await Task.WhenAll(query.Sources.Distinct(StringComparer.Ordinal)
            .Where(providersByName.ContainsKey).Select(async source =>
                (Source: source, Result: await providersByName[source].FetchAsync(query.Topic, cancellationToken))));

        var reports = outcomes.Select(outcome => outcome.Result.IsSuccess
            ? new ProviderReport(outcome.Source, outcome.Result.Value!.IsStale ? ProviderStatus.Stale : ProviderStatus.Fresh,
                outcome.Result.Value.FetchedAt, outcome.Result.Value.IsIncomplete, null)
            : new ProviderReport(outcome.Source, ProviderStatus.Unavailable, null, false, outcome.Result.Error!.Code))
            .ToImmutableArray();
        if (!outcomes.Any(o => o.Result.IsSuccess))
            return AppResult.ServiceUnavailable<DiscoveryResult>("discovery.providers_unavailable",
                new Dictionary<string, object?> { ["providers"] = reports });

        var matches = outcomes.Where(o => o.Result.IsSuccess).SelectMany(o => o.Result.Value!.Items)
            .DistinctBy(i => (i.Source, i.Id))
            .Where(i => (!query.Type.HasValue || i.Type == query.Type)
                && (!query.From.HasValue || i.PublishedAt >= query.From)
                && (!query.To.HasValue || i.PublishedAt <= query.To)).ToArray();

        var sorted = (query.SortBy, query.SortDirection) switch
        {
            (SortField.Title, SortDirection.Asc) => matches.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            (SortField.Title, _) => matches.OrderByDescending(i => i.Title, StringComparer.OrdinalIgnoreCase),
            (_, SortDirection.Asc) => matches.OrderBy(i => i.PublishedAt),
            _ => matches.OrderByDescending(i => i.PublishedAt)
        };
        var items = sorted.ThenBy(i => i.Source, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal)
            .Take(query.Limit).ToImmutableArray();
        return AppResult.Success(new DiscoveryResult(query.Topic, items, matches.Length, items.Length,
            reports.Any(p => p.Status != ProviderStatus.Fresh || p.IsIncomplete), reports));
    }
}
