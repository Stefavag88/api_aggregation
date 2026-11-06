using System.Collections.Concurrent;

namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Tracks the latest cooldown deadline for each provider in the current process.
/// </summary>
internal sealed class ProviderRateLimitState
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);

    internal bool IsBlocked(string source, DateTimeOffset now)
        => _blockedUntil.TryGetValue(source, out var until) && now < until;

    internal void BlockUntil(string source, DateTimeOffset until)
        => _blockedUntil.AddOrUpdate(source, until, (_, current) => until > current ? until : current);
}
