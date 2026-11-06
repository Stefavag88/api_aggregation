# Developer Discovery API

An ASP.NET Core API aggregation assignment, built in small, verified increments.
The current increment provides controller-based liveness and discovery, shared Result
primitives, validated queries, and concurrent aggregation across GitHub, DEV,
and Hacker News. The statistics endpoint reports outbound attempts per provider.
Provider results are cached in memory with bounded stale fallback. Polly handles
transient HTTP failures. Provider cooldowns, circuit breakers, and concurrency
limits protect upstream APIs. Opt-in smoke tests check all three live providers.
Authentication remains planned.

## Local setup

Install a stable .NET 10 SDK version 10.0.301 or later. `global.json` allows newer
.NET 10 feature bands, such as 10.0.403.

```powershell
dotnet restore ApiAggregation.slnx
dotnet build ApiAggregation.slnx -c Release --no-restore
dotnet run --project src/ApiAggregation.Api
Invoke-RestMethod http://localhost:5080/health/live
```

The liveness endpoint returns HTTP 200 with `{"status":"healthy"}`.
Liveness works without external services or credentials. Discovery calls the selected providers.

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
stub provider responses at the HTTP transport.

## Live smoke tests

Run the smoke tests from PowerShell at the repository root.

```powershell
./scripts/Invoke-SmokeTests.ps1
```

The script opts in for the duration of the run. A direct `dotnet test` of
`ApiAggregation.SmokeTests` skips the three tests unless
`RUN_LIVE_SMOKE_TESTS=1` is set. The tests call GitHub, DEV, and Hacker News
independently using the registered clients and their HTTP resilience pipelines.
They bypass the cache and check that the response parses and any returned
items have valid fields. Each provider call has a five-second cancellation budget.

The tests need network access. To authenticate GitHub requests, set
`Providers__GitHub__Token` in your environment. A provider outage or quota
error fails an opted-in run with the provider name and error code.

On GitHub, run **Live provider smoke tests** from the Actions tab. Configure
the optional repository secret `SMOKE_GITHUB_TOKEN` to authenticate GitHub
requests in that workflow. The regular CI workflow runs the offline suites.

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

`GET /api/discovery?topic=dotnet&sources=github,devto,hackernews&sortBy=date&sortDirection=desc&limit=30`

Optional filters: `type=repository|article|discussion`, `from` and `to` (RFC 3339).
Omit `sources` to search all enabled providers concurrently. The source names are
`github`, `devto`, and `hackernews`. Each contributes candidates from its first
upstream page. Set `Discovery:ProviderCandidateLimit` in `appsettings.json` or
`Discovery__ProviderCandidateLimit` in the environment. It defaults to 30 and
accepts values from 1 through 30. Restart to apply changes.

The request's `limit` applies after merging, filtering, and sorting. With the
default candidate limit, `limit=50` can return up to 50 combined results or up
to 30 from a single provider. `limit=120` returns HTTP 400 with a
`validation.outOfRange` error for `limit`.

GitHub searches public repositories using the topic as a search term and retrieves
up to the configured candidate limit from the first page. Repository dates come from `created_at`.
Public search works without credentials. Set `Providers__GitHub__Token` in the
API process environment to use an optional GitHub token for authenticated requests.
See the [GitHub repository search documentation](https://docs.github.com/en/rest/search/search#search-repositories).

GitHub is enabled by default in `src/ApiAggregation.Api/appsettings.json`.
Set `Providers:GitHub:Enabled` to `false` there or set the environment variable
`Providers__GitHub__Enabled=false`, then restart the API. Requests that explicitly
select `sources=github` then return 400. Requests without a source filter use the
remaining enabled providers and return 503 if none are available.

DEV uses the topic as an exact tag and fetches articles from the first page
up to the configured candidate limit. Article dates come from `published_at`. A tag with no matches returns a
successful empty result. The public articles endpoint needs no API key.
See the [Forem API reference](https://developers.forem.com/api/v1#tag/articles/operation/getArticles).

DEV is enabled by default. Set `Providers:DevTo:Enabled` to `false` in
`appsettings.json` or set `Providers__DevTo__Enabled=false`, then restart the API.

Hacker News searches for stories through Algolia and returns links to their Hacker
News discussions. Dates come from each story's `created_at`. The search requires
no API key. Set `Providers:HackerNews:Enabled` to `false` or set
`Providers__HackerNews__Enabled=false`, then restart the API to disable it.
See the [Hacker News Search API](https://hn.algolia.com/api).

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

## Provider HTTP resilience

Each provider uses a Polly pipeline through `Microsoft.Extensions.Http.Resilience`.
It retries a GET up to twice after a transport failure, an attempt timeout, HTTP 408,
or HTTP 5xx. HTTP 429 and other client errors return directly. JSON parsing
runs after the HTTP pipeline, so malformed responses produce provider failures.

`ProviderResilience:AttemptTimeout` defaults to two seconds through response
headers. `ProviderResilience:RetryDelay` defaults to 200 ms as the base for
exponential backoff with jitter. All attempts, retry delays, and body
processing consume the caller's five-second provider budget. Each retry counts
as a separate transport attempt. A transport canceled by its attempt timeout
counts in the canceled bucket.

A provider enters cooldown after HTTP 429 or a GitHub HTTP 403 with a quota
signal. The deadline uses the latest valid future value from `Retry-After`
or, for GitHub, `X-RateLimit-Reset`. Without a usable deadline, the cooldown
lasts 60 seconds. An ordinary HTTP 403 is reported as `unauthorized`.

Cooldowns apply to all topics for that provider in the current process.
While blocked, a fetch returns `rate_limited` and adds zero transport
attempts to statistics. Discovery can serve an eligible cached snapshot,
and the other providers continue independently.

By default, each provider allows up to eight concurrent HTTP operations through response
headers, including any retry within an operation. A request above that limit
fails immediately with `busy`. The circuit breaker opens for 15 seconds when
at least half of its sampled executions fail within a 30-second window with
at least 10 executions. Only transient failures count as breaker failures.
Cooldown responses can contribute to the sample count when they reach the
breaker. After the break, a probe without a transient failure closes the
circuit. Requests rejected by the breaker return `circuit_open`.

Both rejections occur before transport and add zero attempt observations.
Discovery can use an eligible stale snapshot for the affected provider.

Set these values under `ProviderResilience` in `appsettings.json` or through
environment variables such as `ProviderResilience__ConcurrencyLimit`. Each
provider applies the settings to its own pipeline. Restart to apply changes.
Invalid settings fail startup.

| Setting | Default | Valid range |
| --- | --- | --- |
| `ConcurrencyLimit` | `8` | Positive integer |
| `CircuitFailureRatio` | `0.5` | Greater than 0 and at most 1 |
| `CircuitMinimumThroughput` | `10` | Integer of at least 2 |
| `CircuitSamplingDuration` | `00:00:30` | 500 ms through 1 day |
| `CircuitBreakDuration` | `00:00:15` | 500 ms through 1 day |

## Provider caching

The API uses FusionCache in memory-only mode with a dedicated, bounded memory cache in Infrastructure. Local setup and integration tests run with the .NET SDK.
The cache stores immutable provider snapshots keyed by provider, normalized topic,
schema version and upstream candidate limit. Filters, sorting and response limits
reuse those snapshots; they do not create separate cache entries.

The defaults in `appsettings.json` are:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Caching:SizeLimit` | `1000` | Maximum entry-size units; each snapshot costs one unit |
| `Caching:FetchTimeout` | `00:00:05` | Each caller's cancellation budget, including fetch, body parsing and waiting for a shared fetch |
| `Caching:Default:FreshDuration` | `00:05:00` | Serve cached data without contacting the provider |
| `Caching:Default:MaximumAge` | `00:30:00` | Absolute maximum age from the last successful fetch, including the fresh interval |
| `Caching:Default:RetryInterval` | `00:00:30` | Delay further refreshes after a failed refresh while usable stale data exists |

Override a provider's policy under `Caching:Providers:<source>`, for example
`Caching__Providers__github__FreshDuration=00:02:00`. Unspecified properties in
an override use the standard policy defaults (5 minutes / 30 minutes / 30 seconds).
Providers without an override use `Caching:Default`. Restart to apply changes.
Size and timeout must be positive; timeout cannot exceed the .NET timer maximum.
Policies require `0 < FreshDuration < MaximumAge <= 365 days` and
`0 < RetryInterval <= MaximumAge`; invalid configuration fails startup.

After the five-minute freshness window, the next request attempts a refresh. If the provider
fails, usable prior data is returned with `status: "stale"`, its original
`fetchedAt`, and `isPartial: true`. At exactly thirty minutes it is unusable;
continued failure makes that provider unavailable. The existing response rules
apply: partial success returns 200, while all providers unavailable returns 503.
Eviction or restart can remove fallback earlier, so stale availability is best effort.

Valid empty responses are cached and replace older data. Malformed, all-invalid,
or incomplete-empty responses cannot overwrite a good snapshot. Failed Results
are never cached as empty successes. Unexpected exceptions still produce 500;
caller cancellation propagates.

FusionCache coordinates same-key fetches. Concurrent callers can reuse a
successful fill. After a cold-cache failure, waiting callers may attempt their
own fetch within their budgets. Different keys can proceed independently.
A canceled follower leaves the active fetch running. A canceled owner releases
the key so another caller can fetch. Cache hits add zero transport observations.

The five-second cancellation budget covers the wait, transport and body parsing.
On expiry, the caller can use an available snapshot within its maximum age or
receive a timeout failure. This read leaves the refresh schedule alone. The
owning request waits for factory cleanup and lossless statistics recording, so
statistics channel backpressure can extend completion beyond the budget.

The size limit counts snapshots. Provider responses are capped at 1 MiB and 30
candidates. Capacity pressure can evict a snapshot or decline to store a new one.
The caller still receives successfully fetched data.

Each application instance has its own cache, which resets on restart. For
production we would evaluate Redis as a shared second-level cache based on
replica count, duplicated upstream traffic, quotas, freshness requirements and
operating cost. We would evaluate a backplane separately if local cache
invalidation needs coordination across instances.

## HTTP statistics endpoint

`GET /api/statistics` returns counts and mean response times in milliseconds for
every enabled provider, including providers with zero attempts. Each outbound
HTTP attempt counts once. A 2xx response counts as a transport success even if
body parsing later fails. Non-2xx responses and transport exceptions count as
failures, and in-flight cancellations have their own count.

Latency measures the time from transport entry to response headers, failure, or
cancellation. It excludes body reading and parsing, aggregation, retry delays,
and cache access. Fast is below 100 ms, average is 100 through 200 ms inclusive,
and slow is above 200 ms. An empty bucket has a null mean. Counters are local to
each process and reset on restart.

A background worker drains a bounded channel and publishes a new snapshot on
the configured interval when counts have changed. `snapshotAt` is the publication
time. Recent attempts may appear on the next refresh. Set
`Statistics:SnapshotInterval` in `appsettings.json` or use
`Statistics__SnapshotInterval=00:00:05` for a five-second interval. The default
is one second. Restart the API to apply a change. When the channel fills,
recording waits asynchronously for space. This wait is excluded from the measured
latency. On graceful shutdown the worker drains observations it has accepted.

Example response with only GitHub enabled:

```json
{
  "startedAt": "2026-11-05T10:00:00Z",
  "snapshotAt": "2026-11-05T10:00:03Z",
  "measurementScope": "transportToHeaders",
  "counterScope": "process",
  "providers": [
    {
      "source": "github",
      "totalRequests": 1,
      "successCount": 1,
      "failureCount": 0,
      "canceledCount": 0,
      "averageResponseTimeMs": 135,
      "fast": { "count": 0, "averageResponseTimeMs": null },
      "average": { "count": 1, "averageResponseTimeMs": 135 },
      "slow": { "count": 0, "averageResponseTimeMs": null }
    }
  ]
}
```
