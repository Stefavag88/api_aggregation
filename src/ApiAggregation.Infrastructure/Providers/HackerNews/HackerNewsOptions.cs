namespace ApiAggregation.Infrastructure.Providers.HackerNews;

public sealed class HackerNewsOptions() : ProviderOptions("https://hn.algolia.com/")
{
    public const string SectionName = "Providers:HackerNews";
}
