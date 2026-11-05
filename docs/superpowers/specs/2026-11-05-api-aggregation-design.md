# Developer Discovery API — Design

Date: 2026-11-05
Status: Design agreed in conversation, including controllers, primary constructors, and layers with feature folders. Native execution approved; implementation is in progress.

## Intent and success criteria

Build an ASP.NET Core service for a .NET developer assignment. One endpoint discovers public repositories, articles, and discussions about a developer topic by querying GitHub, DEV Community, and Hacker News via Algolia. A second endpoint exposes external-request performance statistics.

The submission should demonstrate understandable architecture, real asynchronous concurrency, reliable partial results, useful caching, accurate thread-safe statistics, and reproducible tests. Its Git history should show small, working increments, starting with project structure. JWT and performance-anomaly detection come after all required features.

## Decisions agreed with the user

- Developer discovery is the product theme.
- Use a layered solution with Domain, Application, Infrastructure, and Api projects.
- Use ASP.NET Core MVC controllers with attribute routing, not Minimal API handlers.
- Use C# primary constructors for dependency injection in controllers and services where constructors are needed.
- Organize Api and Application by feature within the layers; keep shared integration concerns in Infrastructure.
- Use the Result pattern for expected failures, following the user's Indeavor_project implementation and adapting its HTTP mapping to MVC controllers.
- Keep UnitTests, IntegrationTests, and live SmokeTests in separate projects.
- Use Redis and FusionCache for hybrid caching.
- Do not introduce a SQL database. Request statistics remain in memory.
- Implement JWT authentication and performance-anomaly detection last.
- Build incrementally with small, meaningful commits, beginning with solution structure.

All numerical defaults, API details, and library choices below are proposed engineering decisions, not additional user requirements.

## Goals, non-goals, and constraints

### Goals

- Fetch the three selected providers concurrently and allow adding a provider without editing the aggregation algorithm.
- Normalize their records into a documented common representation.
- Filter by source, content type, and date; sort deterministically.
- Handle timeouts, transient errors, rate limits, malformed responses, and partial outages.
- Reuse successful provider results in memory and Redis; support bounded stale fallback.
- Report per-provider request totals and mean response latency, overall and within performance buckets.
- Provide automated unit and integration coverage, opt-in live smoke checks, OpenAPI, setup instructions, and CI.

### Non-goals

- No UI, persistent content catalogue, exhaustive cross-provider search, or ingestion pipeline.
- No microservices, message broker, CQRS framework, generic repository, or database migrations.
- No global relevance score comparing repository stars, article reactions, and discussion points.
- No cluster-wide statistics or promise of exactly one upstream call across all application instances.
- No custom identity provider, account registration, or token-issuing endpoint.

### Constraints

- Target .NET 10 and ASP.NET Core; compatible SDKs are already installed locally.
- The service is one deployable application. Redis is its only required companion service.
- Public-provider access should work without secrets; an optional GitHub token improves available quota.
- Do not commit credentials or depend on paid accounts for the evaluator's basic setup.
- Automated unit/integration tests must not call the real external APIs.
- Docker is required for Redis-backed integration tests and the Compose environment; document this prerequisite explicitly.
- Pin compatible stable package versions during scaffolding/feature introduction; do not use floating versions or assume features on a library's main branch are already released.

## Approaches considered

1. **Single production project with feature folders:** least setup, but dependency boundaries are enforced by convention. Not selected.
2. **Api, Core, Infrastructure:** a small layered application with few projects. Initially recommended, then refined at the user's request.
3. **Api, Application, Domain, Infrastructure:** selected. Domain holds the common model; Application owns workflows and ports; Infrastructure implements integration details; Api owns HTTP concerns. Keep Domain small rather than inventing business abstractions to fill it.

For caching, in-process memory was the original simplicity-first proposal. Redis was selected for shared cached results, then FusionCache for L1/L2 coordination and fail-safe behavior. SQL storage adds little value for temporary provider snapshots and is not selected.

## Solution boundaries

```text
src/
  ApiAggregation.Domain/
  ApiAggregation.Application/
  ApiAggregation.Infrastructure/
  ApiAggregation.Api/
tests/
  ApiAggregation.UnitTests/
  ApiAggregation.IntegrationTests/
  ApiAggregation.SmokeTests/
docs/
  superpowers/specs/
  superpowers/plans/
```

Dependencies:

- Domain: no dependency on the other projects or ASP.NET Core, HTTP, caching, or Redis libraries.
- Application -> Domain.
- Infrastructure -> Application and Domain.
- Api -> Application and Infrastructure; Infrastructure is referenced for composition and registration.
- Test projects reference the projects they exercise.

Responsibilities:

- **Domain:** immutable discovery items and content types, plus framework-independent Result primitives in `Common/Results`. These are not provider JSON DTOs or HTTP response envelopes. Keep four production projects; do not add a fifth Core project just to house these small shared types.
- **Application:** provider abstraction, aggregation use case, query rules, filtering/sorting, provider outcomes, and statistics-read contract. Depend on narrow, purpose-specific interfaces, not FusionCache types.
- **Infrastructure:** typed HTTP clients, private provider DTOs and mapping, resilience, FusionCache provider decorator, Redis setup/backplane, request measurement, in-memory statistics, and later anomaly analysis/hosting.
- **Api:** thin MVC controllers with `[ApiController]` and attribute routing, HTTP input/output contracts, validation/ProblemDetails mapping, OpenAPI, health endpoints, composition, and later JWT configuration. Register controllers through `AddControllers` and route them with `MapControllers`; no Minimal API route handlers. Health routes are also controller actions, delegating to health-check services as needed.

The provider abstraction exposes a stable provider name, its content type, and an asynchronous fetch operation returning `Result<ProviderData>` with a cancellation token. Application receives all registered providers through dependency injection. Expected provider failures become `AppError` values at the infrastructure boundary; caller cancellation propagates rather than becoming an unavailable-provider result.

Use C# primary constructors for constructor injection in controllers and services, and explicit mappings. Primary-constructor dependencies are implementation details, not public properties on API response models. Additional providers need an adapter, registration, tests, and documentation; no central switch in the orchestrator.

### Organization within the layers — selected

Use feature-oriented organization within the agreed project boundaries: `Discovery` and `Statistics` each group their endpoint-facing contracts and use-case logic; provider integrations, caching, and HTTP resilience remain shared Infrastructure components. This combines layered dependency rules with feature folders rather than claiming full Vertical Slice Architecture. Do not add MediatR, CQRS infrastructure, or duplicate provider/cache code just to adopt the naming. The user selected this approach over restructuring the application around fully independent vertical slices.

### Result pattern and error boundaries

Reference inspected: `E:/STORAGE/Documents/Indeavor_project/src/backend/src/Indeavor_project.Core/Result/` and its Api `Extensions/ResultHttpExtensions.cs`. The registered project's old C: path is stale; no files in that reference project are modified.

Adapt `ResultBase<T>`, `Result<T>`, `VoidResult`, `AppError`, `AppResult`, `ResultErrorCode`, `ValidationIssue`, and `ValidationCodes`. Preserve the familiar `IsSuccess`, `Value`, `Error`, `AppResult.Success(...)`, `AppResult.Failure<T>(...)`, and validation-metadata style. Guard failure factories against null errors so `Failure(null)` cannot appear successful. Retain appropriate explicit constructors for these invariant-bearing primitives; primary constructors remain the default for DI services/controllers. Add `ServiceUnavailable` to the semantic error enum; enum values are not HTTP status codes. HTTP mapping belongs only to Api.

- Query validation returns `Result<DiscoveryQuery>` with structured ValidationIssue entries; it does not throw for invalid user input.
- Raw external clients return `Result<ProviderContent>`; the application-facing provider returns `Result<ProviderData>`. A successful ProviderData contains usable fresh/stale data (including genuinely empty results), original fetched time, and incomplete-result metadata. Failures carry AppError, not empty success values or a second conflicting failure flag.
- Discovery returns `Result<DiscoveryResult>`. At least one successful provider means aggregate success, potentially partial. If all selected providers fail, return ServiceUnavailable with safe provider reports in metadata; no successful value is expected on that failure.
- Provider reports in HTTP responses remain `fresh`/`stale`/`unavailable`, derived from each provider's result and descriptor. This is presentation/diagnostic data, not an alternative result framework.
- Adapt the reference's Minimal API `IResult` mapper to MVC `ActionResult<T>` / `ObjectResult` / ProblemDetails, with centralized category-to-status mapping and allowlisted metadata. Never pass upstream bodies, exception objects, tokens, or arbitrary metadata straight to clients. JWT middleware continues to own authentication challenges.
- Actual cancellation remains OperationCanceledException; expected library/transport exceptions are translated once at their owning boundary; unexpected bugs reach the global exception handler. Ordinary in-memory statistics reads need no Result wrapper because they have no expected failure outcome.
- Cache only successful provider snapshots. Use FusionCache's factory-context `Fail(...)` to signal a failed Result, rather than custom exceptions for normal provider failure. Handle its library-level failure exception inside Infrastructure if there is no fallback, preserving the typed error where available. Do not expose library exceptions or cache a failed Result as data. Add a test that unexpected bugs are not hidden behind a successful stale response.

Introduce and test the Result primitives in their own small commit immediately after the skeleton, before implementing features that consume them.

## External providers and normalized data

| Provider | Operation | Topic semantics | Content type | Common date |
|---|---|---|---|---|
| GitHub | GET /search/repositories | Search term, restricted to public repositories | repository | created_at |
| DEV Community | GET /api/articles?tag=... | Exact article tag | article | published_at |
| Hacker News / Algolia | GET /api/v1/search?tags=story&query=... | Story text search | discussion | created_at |

Fetch at most 30 records per selected provider in one upstream page. Do not crawl all result pages. Provider ordering selects the candidate set; our sorting applies only after that set is fetched. Topic matching is intentionally provider-specific, especially DEV's exact tags. An unknown tag returning no articles is a valid empty result.

Normalized item fields:

- `id`: provider-local stable identifier, represented as a string.
- `source`: stable provider name (`github`, `devto`, `hackernews`).
- `type`: `repository`, `article`, or `discussion`.
- `title`, `url`, optional `description`.
- `publishedAt`: UTC timestamp; for repositories this means repository creation, not last commit.
- `tags`: available provider topics/tags; an empty list is valid.

Use a Hacker News discussion URL for discussion records so the returned resource represents the conversation even when the story has an external link. Keep provider JSON models private to Infrastructure. Require an id, title, valid absolute HTTP(S) URL, and valid date for a usable item. Skip individual invalid records and mark the provider result incomplete; invalid JSON/root shape is a provider failure. GitHub `incomplete_results` also marks the candidate set incomplete. Deduplicate by `(source, id)`, preserving separate cross-source entries.

Proposed cache acceptance rule: an incomplete response with at least one usable item may replace an older snapshot and is explicitly reported as incomplete; do not merge old and new candidate sets. A nonempty payload with no usable items, or an upstream-declared incomplete result with no usable items, is a provider failure and must not replace a successful snapshot. Only a structurally valid, complete, genuinely empty result is cached as an empty success. Apply stale fallback/unavailable rules to the failure cases. Include tests distinguishing all three cases.

## HTTP contract

### GET /api/discovery

Example: `/api/discovery?topic=dotnet&sources=github,devto,hackernews&sortBy=date&sortDirection=desc&limit=30`

Parameters:

- `topic` required: trim and lowercase; 1-50 characters matching `[a-z0-9][a-z0-9-]*`. Examples: `dotnet`, `python`, `react`. Document that spaces, `c#`, and arbitrary provider query syntax are not accepted; use `csharp` as a topic when desired.
- `sources` optional comma-separated provider names; absent means all registered providers. Reject unknown values and empty segments; deduplicate valid values.
- `type` optional content-type filter.
- `from`, `to` optional RFC 3339 timestamps with an explicit offset, normalized to UTC; inclusive bounds on `publishedAt`; reject `from > to`.
- `sortBy`: `date` (default) or `title`.
- `sortDirection`: `desc` (default) or `asc`.
- `limit`: 1-90, default 30, applied after filtering and sorting.

Use ordinal case-insensitive title sorting; date ties and title ties resolve by source then id using ordinal ascending order. Filters and sorting do not mutate cached collections. No pagination or global result count is promised. `matchedCount` means matching records in this bounded candidate set before `limit`, not total upstream matches.

Response contains `topic`, `items`, `matchedCount`, `returnedCount`, `isPartial`, and a `providers` list. Each selected provider reports `source`, `status` (`fresh`, `stale`, `unavailable`), nullable `fetchedAt`, `isIncomplete`, and a safe error code when unavailable. `fresh` includes both a fresh cache hit and a fresh HTTP result; do not infer L1/L2 provenance from shared event callbacks.

HTTP behavior:

- 200 when at least one selected provider succeeds or has an eligible stale snapshot, including valid empty results.
- `isPartial=true` if any selected provider is stale, unavailable, or reports incomplete results.
- 503 ProblemDetails when every selected provider is unavailable with no eligible fallback; include safe provider outcomes in extensions.
- 400 validation ProblemDetails for invalid input, before any provider calls.
- Unexpected bugs remain 500 ProblemDetails with a trace identifier; never expose raw exceptions or upstream credentials.
- Caller disconnect/cancellation stops outstanding work and is not rewritten as a successful partial response.

### GET /api/statistics

Return process start time, snapshot time, and every registered provider, including providers with zero requests. For each provider return total external HTTP attempts, successful/non-success/canceled attempt counts, mean duration in milliseconds, and three buckets with count and mean duration:

- `fast`: duration < 100 ms.
- `average`: 100 ms <= duration <= 200 ms.
- `slow`: duration > 200 ms.

An empty population has count 0 and mean `null`. Each attempt belongs to exactly one performance bucket. Retries count as separate attempts. Cache hits, cache-lock waits, circuit-breaker rejections, and calls suppressed by a rate-limit cooldown do not count as HTTP attempts.

Define latency precisely as elapsed monotonic time from handing an attempt to the HTTP transport until response headers arrive, or transport failure/cancellation. Body parsing, retry delays, and aggregation work are excluded. Record this definition in the endpoint documentation so it is not mistaken for end-to-end request latency. HTTP success means a 2xx status; a later JSON mapping failure is recorded separately in logs and provider outcomes.

### Health

- `/health/live`: process liveness only.
- `/health/ready`: local application readiness, with Redis availability reported as degraded when the application can still serve via L1/upstream; Redis degradation alone does not stop routing traffic.
- Never probe live providers on every health check.

## Concurrency and resilience

- Start selected provider operations before awaiting their combined completion; use async I/O and `Task.WhenAll`, not `Task.Run` for HTTP calls.
- One provider's ordinary failure does not cancel siblings; external cancellation does.
- Use `IHttpClientFactory` typed clients with fixed configured upstream hosts and correctly encoded topics. Do not accept user-supplied upstream URLs.
- Use one configured HTTP resilience pipeline per provider, based on Microsoft.Extensions.Http.Resilience; no nested retry loops in caching or aggregation.
- Initial proposed limits: 2-second transport attempt timeout, maximum 1 retry (2 attempts total), and exponential backoff starting at 200 ms with jitter. Each discovery caller has a 5-second per-provider deadline starting before cache access; it includes Redis operations, waits for a coalesced fetch, retries/backoff, body reading/mapping, and cache writes. Nested HTTP work uses the remaining budget, never a fresh additional five seconds. Every waiter has its own bounded wait, including when another caller owns the shared fetch. A provider-budget expiry becomes stale fallback when still available within the budget, otherwise unavailable; caller-request cancellation propagates. Pass cancellation through every operation and test deadline behavior for cache and coalescing waits. Configure HttpClient timeout so it does not introduce a competing unrelated timeout.
- Retry GET transport failures, attempt timeouts, HTTP 408, and HTTP 5xx. Do not retry malformed content or ordinary 4xx responses.
- Treat 429 and GitHub rate-limit 403 responses specially: do not immediately retry. Respect valid Retry-After / rate-reset information through a per-provider, per-instance cooldown; use 60 seconds when the rate-limit signal supplies no usable future time. Ordinary 403 is an authorization failure, not a rate-limit cooldown.
- Use a per-provider circuit breaker and concurrency limiter with explicit tested settings. Proposed breaker: 50% handled failures, minimum 10 attempts over 30 seconds, open for 15 seconds. Proposed maximum 8 concurrent outbound operations per provider, no unbounded queue.
- Bound upstream response bodies (proposed 1 MiB) and per-provider candidate counts. Failed or oversized responses produce provider failure rather than unbounded buffering.

## Hybrid caching

FusionCache belongs entirely in Infrastructure. Use its asynchronous get-or-create operation around successful provider fetches, with System.Text.Json serialization, Redis as L2, and the Redis backplane for cross-instance change notifications.

Cache normalized provider snapshots, not final aggregate responses. Cache keys include an application/environment prefix, application payload schema version, provider name, normalized topic, and upstream candidate limit. Include any future parameter that changes the upstream candidate set. Local filter/sort/response-limit changes reuse the same snapshot.

Proposed defaults:

- Fresh duration: 5 minutes, configurable per provider.
- Fail-safe maximum age: 30 minutes total since the last successful fetch, including the fresh period.
- Fail-safe throttle: 30 seconds between refresh attempts after failure, subject also to the provider's rate-limit cooldown.
- Redis operation timeout budget: 250 ms; use FusionCache's distributed-cache failure handling to degrade to L1/upstream.
- No eager refresh or background completion initially. Keep fetch lifetime, cancellation, and measurement straightforward; add only if a measured need justifies it.
- Foreground distributed writes for deterministic shared-cache behavior; bound their latency.

The cached snapshot stores immutable items, `fetchedAt`, `freshUntil`, `staleUntil`, and incomplete-result metadata. Preserve these timestamps on fallback; do not refresh age by copying an old snapshot into another instance. Application-visible freshness is derived from this envelope. Enforce the absolute `staleUntil` bound even if library retention/throttling would otherwise make the item accessible.

Successful empty responses may be cached. Provider errors must trigger fail-safe behavior rather than being cached as successful empty snapshots. With no previous successful snapshot, return an explicit unavailable outcome.

Use FusionCache's built-in local stampede protection and test it. Redis sharing and backplane notifications do not guarantee a single simultaneous fetch across nodes. Distributed locking is out of scope initially; document this limitation rather than claiming cluster-wide request coalescing. Do not depend on unreleased locking APIs from the project's main branch.

Bound L1 cache memory through a dedicated size-limited cache with entry sizes and bound Redis memory with an eviction policy in the local environment. TTL alone does not bound peak memory under many distinct topics. Exact resource limits can be set by the implementer and documented in configuration.

## Statistics storage

Use a singleton in-memory store and a bounded channel with one consumer. The consumer owns the provider accumulators and publishes immutable snapshots at the configurable `Statistics:SnapshotInterval` when counts change. Recording waits asynchronously if the channel fills. Graceful shutdown drains accepted observations.

Counters cover the current process lifetime and reset on restart. A snapshot reflects the observations processed by its publication time, so recent attempts can appear on the next publication. Document per-instance statistics even though content caches are shared.

Keep measurement inside the retry pipeline and immediately around actual transport attempts so attempts are neither missed nor double-counted. Record attempts that reached transport and were canceled with a canceled outcome, including their elapsed duration. Circuit-breaker rejection before transport creates no sample.

## Test strategy

### UnitTests

- Domain normalization/identity rules and provider payload mappings with small JSON fixtures.
- Query validation, filter boundaries, stable sorting, limits, and immutability of cached data.
- Parallel dispatch using gates/barriers to prove overlap without fragile wall-clock speed assertions.
- Partial failures, all failures, valid empty results, incomplete payloads, and cancellation propagation.
- Retry classification, rate-limit classification, exact statistics bucket boundaries, and concurrent accumulator correctness.
- Fresh/stale/expired snapshot semantics with controlled time; later anomaly calculations.

### IntegrationTests

Use WebApplicationFactory to exercise the real ASP.NET Core routing, dependency injection, controllers, provider adapters, resilience, caching, and statistics. Replace the external HTTP transport with deterministic stubs; do not replace the application or cache implementation.

Use Testcontainers.Redis for real disposable Redis. Require a Docker-capable CI runner; fail clearly if the required integration environment is missing rather than silently skipping the suite. Isolate cache prefixes per test/fixture.

Verify HTTP validation/contracts, three-provider fan-out, retry attempt counts, cancellation, fallback, absolute stale-age expiry, Redis outage degradation, local coalescing, L2 reuse across independently hosted application instances, and eventual backplane propagation. Use synchronization signals and bounded polling for asynchronous notifications instead of fixed long sleeps. Library-facing time-sensitive cases may use short test-only durations when time cannot be injected.

### SmokeTests

Use a separate project and an explicit opt-in flag. Run provider adapters against the real public APIs with caching bypassed to verify actual connectivity, required headers, and payload compatibility. Assert successful parsing and invariant field shapes; do not assert exact titles, counts, or the existence of a particular live result.

A live rate limit/outage fails an explicitly requested smoke run with a useful diagnostic; never hide it as success. Default unit/integration CI does not execute live checks. Provide a manually triggered smoke workflow and a documented local command. Optional GitHub credentials come from environment/secrets.

## Optional features — implemented last

### JWT bearer authentication

After required features pass, protect discovery and statistics with JWT bearer authentication. Leave health endpoints anonymous. Validate issuer, audience, signature, and expiry. Use an external issuer for deployed configuration and `dotnet user-jwts` for local development; do not build an identity service.

Once this feature lands, authentication is enabled by default; update examples and endpoint integration tests in the same change. Use test-only signing keys and actual token validation for auth integration coverage. Direct-provider smoke checks do not need an API bearer token.

### Performance-anomaly background service

Implement after authentication and the required service. Run once per minute with cancellation-aware shutdown. Compare each provider's last five complete UTC minute buckets against its process-lifetime history preceding that window, using count-weighted mean attempt latency and the same measurement/outcome population as statistics.

Log a warning when the recent mean is strictly greater than 1.5 times baseline, with at least 10 attempts in both recent and baseline populations and a baseline mean above zero. Maintain bounded minute aggregates; older observations remain represented only by lifetime counters. Exclude the current incomplete minute from both recent and baseline populations; retain its counters alongside the five complete recent minutes so subtraction from lifetime totals is correct. Emit once when entering an anomalous state, log recovery, and suppress repeated warnings while the state persists. Do not compare a recent window against a baseline that includes that same window.

## Incremental delivery and commit policy

Each implementation commit should express one coherent change, build successfully, and include relevant tests and documentation. Red/green work happens within a task; do not commit deliberately failing tests. Do not manufacture a human history with backdated commits or split changes solely to inflate commit count.

The first code commit contains only the solution/project skeleton, project references, shared build settings, basic controller-based startup with a liveness controller, and essential repository files. No providers or cache implementation hidden in scaffolding. Record the approved design/planning documents with the initial repository setup; this directory is not a Git repository yet.

Proposed sequence (milestones can contain several focused commits):

1. Solution skeleton and project boundaries; verify clean restore/build and API startup.
2. Result primitives in a dedicated commit, followed by common discovery contracts and query rules with unit tests.
3. Aggregation orchestration, deterministic filtering/sorting, and fake-provider tests.
4. Discovery HTTP endpoint and integration-test host, using controlled providers initially.
5. GitHub adapter and payload/HTTP tests.
6. DEV adapter and payload/HTTP tests.
7. Hacker News adapter and payload/HTTP tests; default fan-out now uses all three live adapters.
8. In-memory statistics accumulator and boundary/concurrency tests.
9. Outbound-attempt measurement and statistics endpoint with HTTP coverage.
10. HTTP resilience, timeouts, rate-limit cooldowns, and failure-path integration tests; split mechanisms into separate commits when useful.
11. FusionCache L1 caching and local coalescing, with tests.
12. Redis L2, serialization, Compose setup, and real-Redis integration tests.
13. Redis backplane and multi-instance integration tests.
14. Bounded stale fallback, Redis degradation, and freshness response metadata, with failure tests.
15. Live smoke checks, their explicit opt-in command, and manual CI workflow.
16. Required-feature delivery checks: CI, complete OpenAPI/examples, operational README, container instructions, and requirement traceability. Add basic CI earlier once the first test suite exists; evolve it alongside dependencies.
17. JWT authentication, auth tests, and updated setup/examples.
18. Anomaly detection, controlled-time tests, and documented logging behavior.

Intermediate endpoints may use test doubles in test hosts, but the shipping application never silently substitutes fabricated provider data. Document temporary incompleteness honestly while features are being added. Every milestone extends the existing implementation; avoid scaffolding unused abstractions for future milestones.

## Documentation and delivery

README covers prerequisites, local dotnet and Docker Compose startup, configuration, optional GitHub token, endpoints and example JSON, provider matching differences, bounded search scope, cache freshness/fallback, request-count and latency definitions, Redis outages, per-instance statistics, test commands, and adding a provider.

OpenAPI describes input enums/ranges, successful/partial responses, validation errors, complete failure, and later bearer authentication. Supply an HTTP request example file. Pin stable dependency and container versions when they are selected.

CI restores, builds, and executes unit/integration suites with Redis available; smoke tests run only when explicitly requested. Verify with Release configuration and a clean working tree review before delivery. Publishing the repository requires a concrete GitHub/GitLab destination; prepare locally first and resolve hosting when delivery is ready.

## Decisions left to implementation planning

- Exact file names, method signatures, selected stable dependency versions, and test framework versions.
- Explicit DI/decorator registration mechanics and the smallest useful cache/statistics contracts.
- Concrete cache memory limits and logging categories.
- Small commit boundaries within the milestones above.

These choices must preserve the behavior and dependency boundaries in this design. The next artifact is a task-by-task implementation plan; this document does not authorize skipping design review and starting product code.

## References consulted

- GitHub repository search: https://docs.github.com/en/rest/search/search#search-repositories
- GitHub rate-limit handling: https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api
- DEV/Forem article API: https://developers.forem.com/api/v1
- Hacker News search: https://hn.algolia.com/api
- FusionCache levels: https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/CacheLevels.md
- FusionCache fail-safe: https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/FailSafe.md
- FusionCache backplane: https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/Backplane.md
- FusionCache coalescing: https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/CacheStampede.md
- .NET HTTP resilience: https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience
- Local development JWTs: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/jwt-authn
- Redis integration-test containers: https://dotnet.testcontainers.org/modules/redis/
- User's Result implementation: `E:/STORAGE/Documents/Indeavor_project/src/backend/src/Indeavor_project.Core/Result/`
- User's HTTP mapper (adapt to MVC): `E:/STORAGE/Documents/Indeavor_project/src/backend/src/Indeavor_project.Api/Extensions/ResultHttpExtensions.cs`
- FusionCache v2.9.0 Result-style failure signaling: https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/docs/FailSafe.md#-even-without-exceptions
