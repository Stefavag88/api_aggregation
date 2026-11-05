namespace ApiAggregation.Infrastructure.Providers.HackerNews;

public sealed class HackerNewsOptions
{
    public const string SectionName = "Providers:HackerNews";

    public bool Enabled { get; set; } = true;
    public string BaseAddress { get; set; } = "https://hn.algolia.com/";

    internal static bool HasValidBaseAddress(HackerNewsOptions options)
        => Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.PathAndQuery == "/"
           && uri.UserInfo.Length == 0
           && uri.Fragment.Length == 0;
}
