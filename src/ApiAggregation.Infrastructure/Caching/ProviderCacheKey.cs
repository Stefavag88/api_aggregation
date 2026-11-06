namespace ApiAggregation.Infrastructure.Caching;

/// <summary>
/// Builds versioned keys for provider query snapshots.
/// </summary>
public static class ProviderCacheKey
{
    /// <summary>
    /// Builds a key from the inputs that determine an upstream candidate set.
    /// </summary>
    /// <param name="source">The provider name.</param>
    /// <param name="normalizedTopic">The normalized topic sent to the provider.</param>
    /// <param name="candidateLimit">The maximum number of upstream candidates.</param>
    /// <returns>The versioned provider cache key.</returns>
    public static string Create(string source, string normalizedTopic, int candidateLimit)
        => $"discovery:v1:{source}:{normalizedTopic}:limit:{candidateLimit}";
}
