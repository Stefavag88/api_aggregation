using ApiAggregation.Application.Features.Discovery;
using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Domain.Discovery;

namespace ApiAggregation.UnitTests.Discovery;

public sealed class DiscoveryQueryTests
{
    private static readonly ProviderDescriptor[] Providers = [
        new("github", ContentType.Repository), new("devto", ContentType.Article),
        new("hackernews", ContentType.Discussion)
    ];

    [Fact]
    public void NormalizesTopicAndAppliesDefaults()
    {
        var result = DiscoveryQueryParser.Parse(new() { Topic = " DOTNET " }, Providers);

        Assert.True(result.IsSuccess);
        var query = result.Value!;
        Assert.Equal("dotnet", query.Topic);
        Assert.Equal(30, query.Limit);
        Assert.Equal(SortField.Date, query.SortBy);
        Assert.Equal(SortDirection.Desc, query.SortDirection);
        Assert.Equal(new[] { "github", "devto", "hackernews" }, query.Sources);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void AcceptsTopicLengthBoundaries(int length) =>
        Assert.True(DiscoveryQueryParser.Parse(new() { Topic = new string('a', length) }, Providers).IsSuccess);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("c#")]
    [InlineData("two words")]
    [InlineData("-dotnet")]
    [InlineData("café")]
    [InlineData("line\nbreak")]
    public void RejectsInvalidTopics(string? topic) => AssertInvalid(new() { Topic = topic }, "topic");

    [Fact]
    public void RejectsTopicLongerThanFiftyCharacters() =>
        AssertInvalid(new() { Topic = new string('a', 51) }, "topic");

    [Theory]
    [InlineData("github,")]
    [InlineData(",github")]
    [InlineData("github,,devto")]
    [InlineData("unknown")]
    [InlineData("")]
    public void RejectsInvalidSources(string sources) =>
        AssertInvalid(new() { Topic = "dotnet", Sources = sources }, "sources");

    [Fact]
    public void DeduplicatesSelectedSources()
    {
        var query = DiscoveryQueryParser.Parse(new() { Topic = "dotnet", Sources = "GitHub,github, devto" }, Providers).Value!;
        Assert.Equal(new[] { "github", "devto" }, query.Sources);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("91")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void RejectsInvalidLimits(string limit) =>
        AssertInvalid(new() { Topic = "dotnet", Limit = limit }, "limit");

    [Theory]
    [InlineData("1", 1)]
    [InlineData("90", 90)]
    public void AcceptsLimitBoundaries(string value, int expected) =>
        Assert.Equal(expected, DiscoveryQueryParser.Parse(new() { Topic = "dotnet", Limit = value }, Providers).Value!.Limit);

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("unknown")]
    public void RejectsUnknownOrNumericEnums(string value)
    {
        AssertInvalid(new() { Topic = "dotnet", Type = value }, "type");
        AssertInvalid(new() { Topic = "dotnet", SortBy = value }, "sortBy");
        AssertInvalid(new() { Topic = "dotnet", SortDirection = value }, "sortDirection");
    }

    [Fact]
    public void ParsesExplicitSelectionsAndNormalizesOffsetDates()
    {
        var result = DiscoveryQueryParser.Parse(new() {
            Topic = "dotnet", Type = "article", SortBy = "title", SortDirection = "asc",
            From = "2026-11-05T12:00:00+02:00", To = "2026-11-05T10:00:00Z"
        }, Providers);

        Assert.True(result.IsSuccess);
        Assert.Equal(ContentType.Article, result.Value!.Type);
        Assert.Equal(SortField.Title, result.Value.SortBy);
        Assert.Equal(SortDirection.Asc, result.Value.SortDirection);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), result.Value.From);
        Assert.Equal(result.Value.From, result.Value.To);
    }

    [Theory]
    [InlineData("2026-11-05")]
    [InlineData("2026-11-05T12:00:00")]
    [InlineData("2026-02-30T12:00:00Z")]
    [InlineData("2026-11-05 12:00:00Z")]
    public void RejectsInvalidOrAmbiguousDates(string value)
    {
        AssertInvalid(new() { Topic = "dotnet", From = value }, "from");
        AssertInvalid(new() { Topic = "dotnet", To = value }, "to");
    }

    [Fact]
    public void RejectsReversedDateBounds() => AssertInvalid(new() {
        Topic = "dotnet", From = "2026-11-06T00:00:00Z", To = "2026-11-05T00:00:00Z"
    }, "to");

    private static void AssertInvalid(DiscoveryQueryInput input, string field)
    {
        var result = DiscoveryQueryParser.Parse(input, Providers);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultErrorCode.ValidationError, result.Error!.StatusCode);
        var issues = Assert.IsAssignableFrom<IReadOnlyCollection<ValidationIssue>>(
            result.Error.Meta![AppResult.ValidationErrorsMetaKey]);
        Assert.Contains(issues, issue => issue.Field == field && issue.Code.StartsWith("validation.", StringComparison.Ordinal));
    }
}
