namespace ApiAggregation.Infrastructure.Providers;

public abstract class ProviderOptions(string baseAddress)
{
    internal const bool EnabledByDefault = true;

    public bool Enabled { get; set; } = EnabledByDefault;
    public string BaseAddress { get; set; } = baseAddress;

    internal static bool HasValidBaseAddress(ProviderOptions options)
        => Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.PathAndQuery == "/"
           && uri.UserInfo.Length == 0
           && uri.Fragment.Length == 0;
}
