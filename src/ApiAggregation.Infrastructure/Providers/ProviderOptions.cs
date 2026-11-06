namespace ApiAggregation.Infrastructure.Providers;

/// <summary>
/// Defines the settings shared by external provider clients.
/// </summary>
/// <param name="baseAddress">The default HTTPS origin for the provider API.</param>
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
