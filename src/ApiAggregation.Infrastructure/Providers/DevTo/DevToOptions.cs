namespace ApiAggregation.Infrastructure.Providers.DevTo;

/// <summary>
/// Configures the DEV Community client and its default API origin.
/// </summary>
public sealed class DevToOptions() : ProviderOptions("https://dev.to/")
{
    public const string SectionName = "Providers:DevTo";
}
