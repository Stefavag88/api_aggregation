# Developer Discovery API

An ASP.NET Core API aggregation assignment, built in small, verified increments.
The current increment provides controller-based liveness and discovery, shared Result
primitives, validated queries, and concurrent aggregation. GitHub is the first
registered provider. DEV, Hacker News, statistics, caching, and authentication
will follow in later increments.

## Local setup

Install the .NET SDK specified by `global.json` (10.0.301 or a later 10.0.3xx patch).

```powershell
dotnet restore ApiAggregation.slnx
dotnet build ApiAggregation.slnx -c Release --no-restore
dotnet run --project src/ApiAggregation.Api
Invoke-RestMethod http://localhost:5080/health/live
```

The liveness endpoint returns HTTP 200 with `{"status":"healthy"}`.
Liveness works without external services or credentials. Discovery calls GitHub.

## Structure

- `Domain`: framework-independent models and shared Result primitives.
- `Application`: use cases and contracts, grouped into feature folders.
- `Infrastructure`: provider integrations, caching, and statistics implementations.
- `Api`: MVC controllers, HTTP contracts, and composition.
- `UnitTests`, `IntegrationTests`, `SmokeTests`: separate test projects. Meaningful
  tests arrive alongside the features they verify.

## Result pattern and tests

Expected failures use `Result<T>`, `AppError`, and the `AppResult` factories.
Structured validation issues preserve field names and stable error codes.
Caller cancellation and unexpected bugs propagate rather than being converted
into normal failed results. HTTP mapping lives in the controller layer.

```powershell
dotnet test tests/ApiAggregation.UnitTests/ApiAggregation.UnitTests.csproj -c Release
dotnet test tests/ApiAggregation.IntegrationTests/ApiAggregation.IntegrationTests.csproj -c Release
```

Tests use xUnit v3's `mtp-off` package with the VSTest adapter so this command
works consistently with .NET 10. Integration tests host the real MVC pipeline and
stub GitHub at the HTTP transport. Smoke tests arrive later.

See the [design](docs/superpowers/specs/2026-11-05-api-aggregation-design.md) and
[implementation plan](docs/superpowers/plans/2026-11-05-api-aggregation.md).

## Discovery query contract

Topics are trimmed/lowercased and contain 1-50 ASCII letters, digits, or hyphens,
starting with a letter or digit (for example `dotnet`, `python`, `csharp`).
Sources are comma-separated registered names with no empty entries. Dates use
RFC 3339 with an explicit offset and inclusive bounds. Sorting supports date/title
and asc/desc; defaults are date descending and a result limit of 30 (range 1-90).
Invalid input produces structured validation issues rather than exceptions.

Aggregation starts selected providers concurrently, preserves partial successes,
and applies filtering, deterministic sorting, and limits after retrieval. Provider
failures appear in reports; only total unavailability produces a failed Result.

## HTTP discovery endpoint

`GET /api/discovery?topic=dotnet&sources=github&sortBy=date&sortDirection=desc&limit=30`

Optional filters: `type=repository|article|discussion`, `from` and `to` (RFC 3339).
Omit `sources` to search all registered providers. Currently `github` is the only source.

GitHub searches public repositories using the topic as a search term and retrieves
up to 30 candidates from the first page. Repository dates come from `created_at`.
Public search works without credentials. Set `Providers__GitHub__Token` in the
API process environment to use an optional GitHub token for authenticated requests.
See the [GitHub repository search documentation](https://docs.github.com/en/rest/search/search#search-repositories).

Successful responses (including partial or empty results) use HTTP 200:

```json
{
  "topic": "dotnet", "items": [], "matchedCount": 0, "returnedCount": 0,
  "isPartial": false,
  "providers": [{ "source": "github", "status": "fresh", "fetchedAt": "2026-11-05T10:00:00Z", "isIncomplete": false, "errorCode": null }]
}
```

Items expose `id`, `source`, `type`, `title`, `url`, nullable `description`,
`publishedAt`, and `tags`. Provider status is `fresh`, `stale`, or `unavailable`.
Counts describe the bounded retrieved candidates, not total upstream matches.
Validation returns 400 ProblemDetails with grouped `errors` and coded
`validationErrors`; total unavailability returns 503 with `providers` reports.
Unexpected failures return 500 with a trace ID and no exception details.
