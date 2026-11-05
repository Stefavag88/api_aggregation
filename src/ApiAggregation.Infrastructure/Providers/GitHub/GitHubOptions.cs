namespace ApiAggregation.Infrastructure.Providers.GitHub;

public sealed class GitHubOptions
{
    public const string SectionName = "Providers:GitHub";

    public bool Enabled { get; set; } = true;
    public string BaseAddress { get; set; } = "https://api.github.com/";
    public string? Token { get; set; }

    internal static bool HasValidBaseAddress(GitHubOptions options)
        => Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.PathAndQuery == "/"
           && uri.UserInfo.Length == 0
           && uri.Fragment.Length == 0;

    internal static bool HasValidToken(GitHubOptions options)
        => string.IsNullOrEmpty(options.Token) || options.Token.All(c => c is >= '!' and <= '~');
}
