using System.ComponentModel.DataAnnotations;

namespace ApiAggregation.Application.Features.Discovery;

public sealed record DiscoveryQueryInput
{
    public string? Topic { get; init; }

    // Preserve explicit blanks so the parser can distinguish them from omitted query values.
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Sources { get; init; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Type { get; init; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? From { get; init; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? To { get; init; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? SortBy { get; init; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? SortDirection { get; init; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Limit { get; init; }
}
