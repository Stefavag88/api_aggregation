namespace ApiAggregation.Infrastructure.Providers.DevTo;

public sealed class DevToOptions
{
    public const string SectionName = "Providers:DevTo";

    public bool Enabled { get; set; } = true;
    public string BaseAddress { get; set; } = "https://dev.to/";

    internal static bool HasValidBaseAddress(DevToOptions options)
        => Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.PathAndQuery == "/"
           && uri.UserInfo.Length == 0
           && uri.Fragment.Length == 0;
}
