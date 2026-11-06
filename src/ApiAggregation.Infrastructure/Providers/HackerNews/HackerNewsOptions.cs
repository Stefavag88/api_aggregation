namespace ApiAggregation.Infrastructure.Providers.HackerNews;

/// <summary>
/// Configures the Hacker News client and its default Algolia API origin.
/// </summary>
public sealed class HackerNewsOptions() : ProviderOptions("https://hn.algolia.com/")
{
    public const string SectionName = "Providers:HackerNews";
}
