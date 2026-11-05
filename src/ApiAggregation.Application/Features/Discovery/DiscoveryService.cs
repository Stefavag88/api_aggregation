using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace ApiAggregation.Application.Features.Discovery;

public sealed class DiscoveryService(IEnumerable<IDiscoveryProvider> providers)
{
    private readonly FrozenDictionary<string, IDiscoveryProvider> providersByName =
        providers.ToFrozenDictionary(p => p.Descriptor.Name, StringComparer.Ordinal);

    public async Task<Result<DiscoveryResult>> DiscoverAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outcomes = await FetchSelectedProvidersAsync(query, cancellationToken);
        ImmutableArray<ProviderReport> reports = [.. outcomes.Select(ToProviderReport)];

        var noneSuccessful = !outcomes.Any(outcome => outcome.Result.IsSuccess);
        if (noneSuccessful)
        {
            return AppResult.ServiceUnavailable<DiscoveryResult>(
                "discovery.providers_unavailable",
                new Dictionary<string, object?> { ["providers"] = reports });
        }

        var (items, matchedCount) = SelectItems(outcomes, query);
        var isPartial = reports.Any(report => report.Status != ProviderStatus.Fresh || report.IsIncomplete);

        return AppResult.Success(new DiscoveryResult(
            query.Topic,
            items,
            matchedCount,
            items.Length,
            isPartial,
            reports));
    }

    private Task<ProviderOutcome[]> FetchSelectedProvidersAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        var relevantSources = query.Sources
            .Distinct(StringComparer.Ordinal)
            .Where(providersByName.ContainsKey);

        var fetches = relevantSources.Select(async source =>
        {
            var result = await providersByName[source].FetchAsync(query.Topic, cancellationToken);
            return new ProviderOutcome(source, result);
        });

        return Task.WhenAll(fetches);
    }

    private static ProviderReport ToProviderReport(ProviderOutcome outcome)
    {
        if (!outcome.Result.IsSuccess)
        {
            return new ProviderReport(
                outcome.Source,
                ProviderStatus.Unavailable,
                FetchedAt: null,
                IsIncomplete: false,
                ErrorCode: outcome.Result.Error!.Code);
        }

        var data = outcome.Result.Value!;
        var status = data.IsStale ? ProviderStatus.Stale : ProviderStatus.Fresh;

        return new ProviderReport(outcome.Source, status, data.FetchedAt, data.IsIncomplete, ErrorCode: null);
    }

    private static (ImmutableArray<DiscoveryItem> Items, int MatchedCount) SelectItems(
        IEnumerable<ProviderOutcome> outcomes,
        DiscoveryQuery query)
    {
        var matches = outcomes
            .Where(outcome => outcome.Result.IsSuccess)
            .SelectMany(outcome => outcome.Result.Value!.Items)
            .DistinctBy(item => (item.Source, item.Id))
            .Where(item => (!query.Type.HasValue || item.Type == query.Type)
                && (!query.From.HasValue || item.PublishedAt >= query.From)
                && (!query.To.HasValue || item.PublishedAt <= query.To))
            .ToArray();

        var sorted = (query.SortBy, query.SortDirection) switch
        {
            (SortField.Title, SortDirection.Asc) => matches.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            (SortField.Title, _) => matches.OrderByDescending(i => i.Title, StringComparer.OrdinalIgnoreCase),
            (_, SortDirection.Asc) => matches.OrderBy(i => i.PublishedAt),
            _ => matches.OrderByDescending(i => i.PublishedAt)
        };

        var items = sorted
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(query.Limit)
            .ToImmutableArray();

        return (items, matches.Length);
    }

    private readonly record struct ProviderOutcome(string Source, Result<ProviderData> Result);
}
