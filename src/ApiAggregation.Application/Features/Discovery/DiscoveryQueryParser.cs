using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ApiAggregation.Application.Features.Discovery;

public static partial class DiscoveryQueryParser
{
    public const int MinimumLimit = 1;
    public const int MaximumLimit = 90;
    public const int DefaultLimit = 30;

    public static Result<DiscoveryQuery> Parse(
        DiscoveryQueryInput input, IReadOnlyCollection<ProviderDescriptor> providers)
    {
        List<ValidationIssue> issues = [];
        var topic = input.Topic?.Trim().ToLowerInvariant() ?? "";

        if (topic.Length == 0)
            issues.Add(new("topic", ValidationCodes.Required));
        else if (!TopicPattern().IsMatch(topic))
            issues.Add(new("topic", ValidationCodes.Invalid, "Use 1-50 letters, digits, or hyphens; start with a letter or digit."));

        var knownSources = providers
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        ImmutableArray<string> sources = input.Sources is null
            ? [.. providers.Select(p => p.Name)]
            : [.. input.Sources.Split(',').Select(s => s.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

        if (sources.Any(s => !knownSources.Contains(s)))
            issues.Add(new("sources", ValidationCodes.Invalid, "Select registered provider names without empty entries."));

        ContentType? type = input.Type is null
            ? null
            : ParseEnum<ContentType>(input.Type, "type", issues);

        var sortBy = input.SortBy is null
            ? SortField.Date
            : ParseEnum<SortField>(input.SortBy, "sortBy", issues);

        var direction = input.SortDirection is null
            ? SortDirection.Desc
            : ParseEnum<SortDirection>(input.SortDirection, "sortDirection", issues);

        var from = ParseDate(input.From, "from", issues);
        var to = ParseDate(input.To, "to", issues);

        if (from > to)
            issues.Add(new("to", ValidationCodes.Invalid, "Must be greater than or equal to from."));

        var limit = DefaultLimit;
        if (input.Limit is not null)
        {
            if (!int.TryParse(input.Limit, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
                issues.Add(new("limit", ValidationCodes.Invalid));
            else if (limit is < MinimumLimit or > MaximumLimit)
                issues.Add(new("limit", ValidationCodes.OutOfRange, $"Must be between {MinimumLimit} and {MaximumLimit}."));
        }

        return issues.Count > 0
            ? AppResult.ValidationErrors<DiscoveryQuery>(issues)
            : AppResult.Success(new DiscoveryQuery(topic, sources, type, from, to, sortBy, direction, limit));
    }

    private static T ParseEnum<T>(string input, string field, List<ValidationIssue> issues) where T : struct, Enum
    {
        if (Enum.GetNames<T>().Any(name => string.Equals(name, input, StringComparison.OrdinalIgnoreCase)))
            return Enum.Parse<T>(input, ignoreCase: true);

        issues.Add(new(field, ValidationCodes.Invalid));

        return default;
    }

    private static DateTimeOffset? ParseDate(string? input, string field, List<ValidationIssue> issues)
    {
        if (input is null)
            return null;

        if (TimestampPattern().IsMatch(input)
             && DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return timestamp.ToUniversalTime();
        }

        issues.Add(new(field, ValidationCodes.Invalid, "Use an RFC 3339 timestamp with an explicit UTC offset."));
        return null;
    }

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,49}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}
