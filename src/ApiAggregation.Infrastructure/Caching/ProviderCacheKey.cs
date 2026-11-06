namespace ApiAggregation.Infrastructure.Caching;

public static class ProviderCacheKey
{
    public static string Create(string source, string normalizedTopic, int candidateLimit)
        => $"discovery:v1:{source}:{normalizedTopic}:limit:{candidateLimit}";
}
