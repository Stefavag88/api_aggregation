namespace ApiAggregation.Infrastructure.Providers;

/// <summary>
/// Configures the number of candidates fetched from the first page of each provider.
/// </summary>
public sealed class ProviderDiscoveryOptions
{
    internal const string SectionName = "Discovery";
    internal const int MaximumCandidateLimit = 30;

    public int ProviderCandidateLimit { get; set; } = MaximumCandidateLimit;
}
