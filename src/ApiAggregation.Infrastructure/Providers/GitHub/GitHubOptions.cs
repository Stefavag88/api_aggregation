namespace ApiAggregation.Infrastructure.Providers.GitHub;

/// <summary>
/// Configures the GitHub client, including its API origin and optional access token.
/// </summary>
public sealed class GitHubOptions() : ProviderOptions("https://api.github.com/")
{
    public const string SectionName = "Providers:GitHub";

    public string? Token { get; set; }

    internal static bool HasValidToken(GitHubOptions options)
        => string.IsNullOrEmpty(options.Token) || options.Token.All(c => c is >= '!' and <= '~');
}
