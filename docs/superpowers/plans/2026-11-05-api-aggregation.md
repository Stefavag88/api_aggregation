# Developer Discovery API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a controller-based developer discovery API that concurrently combines three external providers, caches their results with FusionCache/Redis, and exposes accurate request statistics.

**Architecture:** Four projects enforce layered dependencies; feature folders group Discovery and Statistics within Api and Application. Infrastructure owns external adapters, HTTP resilience, hybrid caching, and telemetry. One application process and Redis are deployed; unit, integration, and live smoke tests have separate projects.

**Tech Stack:** .NET 10, ASP.NET Core MVC controllers, C# primary constructors, System.Text.Json, IHttpClientFactory, Microsoft.Extensions.Http.Resilience, FusionCache, Redis, xUnit v3, WebApplicationFactory, Testcontainers.Redis, Docker Compose, GitHub Actions.

**Spec:** [2026-11-05-api-aggregation-design.md](../specs/2026-11-05-api-aggregation-design.md). Read this together with the plan. Status: native execution approved; implementation in progress. See task checkboxes and Git history.

## Global Constraints

- Use ASP.NET Core MVC controllers with attribute routing, not Minimal API handlers.
- Use C# primary constructors for dependency injection in controllers and services where constructors are needed.
- Organize Api and Application by feature within the layers; keep shared integration concerns in Infrastructure.
- Keep UnitTests, IntegrationTests, and live SmokeTests in separate projects.
- Use Redis and FusionCache for hybrid caching.
- Use the Result pattern for expected failures, following the user's Indeavor_project implementation and adapting its HTTP mapping to MVC controllers.
- Do not introduce a SQL database. Request statistics remain in memory.
- Implement JWT authentication and performance-anomaly detection last.
- Build incrementally with small, meaningful commits, beginning with solution structure.
- Target .NET 10 and ASP.NET Core; compatible SDKs are already installed locally.
- Automated unit/integration tests must not call the real external APIs.
- Pin compatible stable package versions during scaffolding/feature introduction; do not use floating versions or assume features on a library's main branch are already released.
- Honor all numeric limits and HTTP/cache/statistics semantics in the spec; exact values are repeated in the owning tasks below.
- Do not introduce MediatR, an ORM, generic repositories, or fabricated live provider responses.

## Review Focus

1. Simultaneous callers and cancellation: a canceled follower must not cancel another caller's fetch or leave a waiter hanging; each caller's provider budget is bounded. Tasks 12 and 15.
2. Malformed-but-successful upstream payloads: distinguish a valid empty result from a nonempty payload with no usable records; never overwrite good fallback with invalid data. Tasks 7-9 and 18.
3. Cache age across instances: L2 reads, backplane events, and fail-safe throttling must not reset original freshness or exceed the absolute 30-minute age. Tasks 16-18.
4. Telemetry under failure: retries are separate HTTP attempts, cache hits/rejections are not attempts, and every reached-transport cancellation gets exactly one measurement. Tasks 10-13 and 15.
5. Query determinism: offset dates, empty source segments, sort ties, duplicate provider records, and post-cache filtering must behave consistently without mutating shared data. Tasks 3-5 and 15.

---

## Execution and commit discipline

This plan builds the application incrementally; do not generate all files up front. Each task normally ends in one commit. Tasks 12-13 and 21-22 may be split at the explicitly described behavior boundaries, with tests passing at each boundary. Never commit a deliberately failing test. Test-first steps may fail to compile while their production type is absent; run them and confirm that is the relevant failure, then implement and rerun.

The current directory contains planning documents only and is not a Git repository. At execution time, inspect its state again before initializing Git; preserve any new user work. The first commit is the scaffold, including these planning documents. Do not create an unrelated docs-only initial commit. Do not backdate or rewrite commits to simulate human activity. No remote publishing is part of the scaffold; obtain the intended repository destination when publication is ready.

For each commit: run its focused checks and `dotnet build ApiAggregation.slnx -c Release --no-restore`; inspect `git diff --check` and the staged diff; stage only that task's files and commit with the proposed message. Run a relevant full project test suite whenever a change affects shared behavior. Do not rerun live smoke checks routinely.

**Commands:** Run from repository root in PowerShell. For brevity, a verification instruction such as **Unit / DiscoveryQueryTests** means the exact command `dotnet test tests/ApiAggregation.UnitTests/ApiAggregation.UnitTests.csproj -c Release --filter FullyQualifiedName~DiscoveryQueryTests`; **Integration / DiscoveryEndpointTests** uses `tests/ApiAggregation.IntegrationTests/ApiAggregation.IntegrationTests.csproj` with the same filter syntax. **Unit all** / **Integration all** omit `--filter`. Expected output is a nonzero discovered test count, zero failures, and exit code 0. The scaffold is the exception: it has no feature tests yet and is verified by build/startup.

**Dependencies:** Use Central Package Management in `Directory.Packages.props`, adding a package only when its feature lands. Planning-time NuGet candidates are xunit.v3.mtp-off 4.0.2, xunit.runner.visualstudio 4.0.1, Microsoft.NET.Test.Sdk 18.11.0, FusionCache/core/SystemTextJson/backplane 2.9.0, Testcontainers.Redis 4.14.0, Microsoft.Extensions.Http.Resilience 10.11.0, and Microsoft.AspNetCore.Mvc.Testing / Microsoft.Extensions.Caching.StackExchangeRedis 10.0.14. Revalidate availability and compatibility during restore; record any necessary stable-version correction in this plan. Align other Microsoft ASP.NET Core packages to the selected 10.0 patch. Use VSTest-compatible `dotnet test` execution with the xUnit adapter; do not silently switch test-runner command conventions. Select and pin a supported stable Redis image tag and digest when Task 16 introduces Docker, using the same image for tests and Compose.

## File responsibility map

```text
src/ApiAggregation.Domain/
  Common/Results/                    # Result<T>, AppError, factories, validation issues
  Discovery/                         # Common immutable items and content types
src/ApiAggregation.Application/
  Features/Discovery/                # Query, validation, provider port, aggregation
  Features/Statistics/               # Read-only statistics contracts
src/ApiAggregation.Infrastructure/
  Providers/                         # Transport DTOs, typed clients, outcome adapter
    GitHub/
    DevTo/
    HackerNews/
  Http/                              # Attempt metrics, resilience, bounded body reading
  Caching/                           # FusionCache decorator, Redis/backplane, envelopes
  Statistics/                        # Thread-safe counters; later anomaly history/worker
  DependencyInjection.cs             # Composition helpers, not application logic
src/ApiAggregation.Api/
  Features/Discovery/                # Controller, query binding, response mapping
  Features/Statistics/               # Controller and HTTP response contracts
  Features/Health/                   # Controller-based liveness/readiness
  Authentication/                    # Added only in the final authentication milestone
  ErrorHandling/                     # Safe ProblemDetails for unexpected failures
tests/ApiAggregation.UnitTests/       # Mirrored feature/provider tests and local helpers
tests/ApiAggregation.IntegrationTests/# WebApplicationFactory, HTTP stubs, Redis fixture
tests/ApiAggregation.SmokeTests/       # Explicitly enabled live provider checks
```

File paths below are repository-relative. Each type lives in its named file except small private/nested transport DTOs. Test helper types live in the indicated test project's `Support/` folder and are introduced only when first needed. Documentation/configuration changes belong to their feature's commit even where a Files list focuses on production/test files.

## Task 1: Scaffold the solution and controller host

**Files — create:** `ApiAggregation.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitignore`, `README.md`; one `<project-name>.csproj` in each of the seven project directories above; `src/ApiAggregation.Api/Program.cs`, `src/ApiAggregation.Api/appsettings.json`, `src/ApiAggregation.Api/Features/Health/HealthController.cs`, `src/ApiAggregation.Api/Properties/launchSettings.json`.

**Interfaces:** Produces `public partial class Program` for later WebApplicationFactory use, and `HealthController.Live(): IActionResult` at `GET /health/live`, returning 200 with `{ "status": "healthy" }`. No application/provider contracts exist yet.

- [x] Inspect `git status`, SDKs, and files; initialize the empty directory with branch `feat/api-aggregation` only if it is still not a repository. Check Git identity without changing global identity settings.
- [x] Create the seven net10.0 projects and `.slnx`; use an empty web host and register MVC controllers. Remove template WeatherForecast, placeholder Class1, and dummy passing tests. Add only Application -> Domain, Infrastructure -> Application/Domain, Api -> Application/Infrastructure references; UnitTests references Domain/Application/Infrastructure, IntegrationTests references Api/Infrastructure, SmokeTests references Infrastructure.
- [x] Add nullable reference types, implicit usings, warnings-as-errors, stable default language version, CPM, and SDK `10.0.301` with `latestPatch` roll-forward. Configure test projects for xUnit v3 and VSTest, without inventing tests to fill them. Prefer primary constructors when dependencies are first introduced.
- [x] Implement the liveness controller with attribute routing; `Program` uses `AddControllers` and `MapControllers`. README documents restore/build/run and explicitly says features are not implemented yet. Include the accepted design and this plan in the scaffold commit.
- [x] Run `dotnet restore ApiAggregation.slnx` and `dotnet build ApiAggregation.slnx -c Release --no-restore`; expect zero errors. Run `dotnet run --project src/ApiAggregation.Api --no-launch-profile --urls http://127.0.0.1:5080`, GET `/health/live`, confirm 200 and the documented body, then stop only the process launched for this check.
- [x] Commit: `chore: scaffold layered solution and controller host`.

## Task 2: Introduce the shared Result primitives

**Files — create:** `src/ApiAggregation.Domain/Common/Results/{ResultBase,Result,VoidResult,AppError,AppResult,ResultErrorCode,ValidationIssue,ValidationCodes}.cs`; `tests/ApiAggregation.UnitTests/Common/ResultTests.cs`; `.github/workflows/ci.yml`.

**Interfaces:** Adapt the inspected Indeavor_project implementation: Result<T> exposes IsSuccess, nullable Value, and nullable Error; static Success(T)/Failure(AppError), and implicit conversions from T/AppError. VoidResult exposes IsSuccess/Error and success/failure factories. AppError is `(string? Code, ResultErrorCode StatusCode, IReadOnlyDictionary<string,object?>? Meta = null)`; StatusCode is a semantic enum, not HTTP. Preserve Unspecified=0, NotFound=10, ValidationError=20, Unauthorized=30, Forbidden=40, Conflict=50, RateLimited=60, InternalError=70; add ServiceUnavailable=80. ValidationIssue is `(string Field, string Code, string? Message = null)`. Preserve ValidationCodes and AppResult.ValidationErrorsMetaKey=`validationErrors`. Provide the reference factories and `AppResult.ServiceUnavailable<T>(string? code = null, IReadOnlyDictionary<string,object?>? meta = null): Result<T>` plus its VoidResult equivalent.

- [x] Write ResultTests for success values (including value types), failure error preservation/default Value, implicit conversions, VoidResult, validation metadata, multiple validation issues, and ServiceUnavailable. Assert `Success(42).IsSuccess`, `Success(42).Value == 42`, and failure.Error is the original AppError. Explicitly assert null AppError values throw ArgumentNullException through error constructors, failure factories, and implicit error conversions rather than constructing false success; empty ValidationErrors throws ArgumentException (programmer misuse, not user validation).
- [x] Run **Unit / ResultTests**; confirm missing primitives cause the expected failure.
- [x] Adapt only the reference Result folder into Domain/Common/Results with project namespaces, null-error guards, and the ServiceUnavailable extension. Do not import the reference Core project, tenancy types, persistence abstractions, or Minimal API mapper. Use explicit constructors where necessary to preserve valid factory-based states; primary constructors remain the DI convention. Result types contain no ASP.NET Core or infrastructure dependency. Preserve nullable Value semantics and require callers to check IsSuccess before reading Value.
- [x] Run **Unit all**. Add the initial restore/build/unit CI workflow now that real tests exist. Document that Result is for expected failure paths; cancellation and unexpected exceptions are not normalized into ordinary failures.
- [x] Commit: `feat: add shared Result and typed error primitives`.

## Task 3: Define discovery items and validated queries

**Files — create:** `src/ApiAggregation.Domain/Discovery/{DiscoveryItem,ContentType}.cs`; `src/ApiAggregation.Application/Features/Discovery/{DiscoveryQuery,DiscoveryQueryInput,DiscoveryQueryParser,SortField,SortDirection,ProviderDescriptor}.cs`; `tests/ApiAggregation.UnitTests/Discovery/DiscoveryQueryTests.cs`; modify `.github/workflows/ci.yml` if needed.

**Interfaces:** `DiscoveryItem` is an immutable record with `string Id, string Source, ContentType Type, string Title, Uri Url, string? Description, DateTimeOffset PublishedAt, ImmutableArray<string> Tags`. `ContentType` has Repository, Article, Discussion. `ProviderDescriptor(string Name, ContentType Type)` identifies registered providers. `DiscoveryQueryInput` carries nullable raw strings for topic/sources/type/from/to/sortBy/sortDirection/limit so timestamps can be validated before permissive framework conversion. `DiscoveryQueryParser.Parse(DiscoveryQueryInput input, IReadOnlyCollection<ProviderDescriptor> providers): Result<DiscoveryQuery>` returns `AppResult.ValidationErrors<DiscoveryQuery>(issues)` for invalid input, preserving field/code/message details under `AppResult.ValidationErrorsMetaKey`. `DiscoveryQuery` contains normalized `Topic`, `ImmutableArray<string> Sources`, nullable `Type/From/To`, typed `SortBy/SortDirection`, and `int Limit`.

- [x] Write query tests for trimmed/lowercase `" DOTNET "`, defaults (date/desc/30), 1 and 50 character topics, invalid 51-character topics, punctuation/spaces, explicit UTC offsets, missing timestamp offsets, reversed date bounds, empty source segments, unknown source/type/sort, limits 0/91, and source deduplication. Include these assertions in `NormalizesTopicAndAppliesDefaults` after asserting `parsed.IsSuccess` and assigning `query = parsed.Value!`; invalid-query cases assert `!parsed.IsSuccess`, `ResultErrorCode.ValidationError`, and the exact ValidationIssue field/code without expecting an exception:
  ```csharp
  Assert.Equal("dotnet", query.Topic);
  Assert.Equal(30, query.Limit);
  Assert.Equal(SortField.Date, query.SortBy);
  Assert.Equal(SortDirection.Desc, query.SortDirection);
  ```
- [x] Run **Unit / DiscoveryQueryTests**; confirm the missing contracts/parser cause failure.
- [x] Implement these types and parser. Enforce the spec's exact validation rules; reject undefined numeric enum strings, avoid culture-dependent parsing, and retain supplied-source order only for presentation (sorting uses stable source names). Timestamps normalize to UTC. No FluentValidation dependency is needed for this small parser.
- [x] Run **Unit all**. Extend the existing CI workflow as needed; the Result task already established unit-test execution. Document query restrictions.
- [x] Commit: `feat: define discovery model and query validation`.

## Task 4: Implement concurrent aggregation and deterministic results

**Files — create:** `src/ApiAggregation.Application/Features/Discovery/{IDiscoveryProvider,ProviderData,ProviderReport,ProviderStatus,DiscoveryResult,DiscoveryService}.cs`; `tests/ApiAggregation.UnitTests/Discovery/DiscoveryServiceTests.cs`, `tests/ApiAggregation.UnitTests/Support/GatedProvider.cs`.

**Interfaces:** `IDiscoveryProvider.Descriptor: ProviderDescriptor` and `Task<Result<ProviderData>> FetchAsync(string topic, CancellationToken cancellationToken)`. `ProviderData(ImmutableArray<DiscoveryItem> Items, DateTimeOffset FetchedAt, bool IsStale, bool IsIncomplete)` represents usable data only. `ProviderReport` contains Source, Status (ProviderStatus.Fresh/Stale/Unavailable), nullable FetchedAt, IsIncomplete, nullable safe ErrorCode. `DiscoveryResult` contains Topic, Items, MatchedCount, ReturnedCount, IsPartial, and immutable ProviderReport entries. `DiscoveryService(IEnumerable<IDiscoveryProvider> providers).DiscoverAsync(DiscoveryQuery query, CancellationToken cancellationToken): Task<Result<DiscoveryResult>>`. All-provider failure returns `AppResult.ServiceUnavailable<DiscoveryResult>("discovery.providers_unavailable", metadata)` with `providers` reports in metadata; it has no successful DiscoveryResult value.

- [x] Write `StartsEverySelectedProviderBeforeWaiting` using a gate for each provider: all must signal entry before any is released. Write tests for one unavailable provider, all unavailable, successful empty results, requested-source selection, cancellation, inclusive date/type filters, duplicate `(source,id)` values, ordinal title ordering, tie-breaking, counts before limit, and source-array immutability. In `PreservesSuccessfulResultsOnPartialFailure` assert:
  ```csharp
  Assert.True(result.IsSuccess);
  var discovery = result.Value!;
  Assert.True(discovery.IsPartial);
  Assert.Single(discovery.Items);
  Assert.Equal(ProviderStatus.Unavailable, discovery.Providers.Single(p => p.Source == "devto").Status);
  ```
- [x] Run **Unit / DiscoveryServiceTests**; confirm the service is absent.
- [x] Implement the service with concurrent async operations and deterministic post-fetch processing. Expected provider failures are Result failures; pair each result with its provider descriptor to build reports. Partial success and valid empty responses return successful Results; all-provider failure (or no registered providers during scaffolding) returns ServiceUnavailable with safe provider reports. Add an explicit all-failed assertion for `!result.IsSuccess`, `result.Value is null`, and `result.Error.StatusCode == ResultErrorCode.ServiceUnavailable`. Unexpected programming exceptions still propagate to HTTP 500. Filter and sort copies, never shared cached storage.
- [x] Run **Unit all**. Verify the gate test proves overlap and makes no flaky elapsed-speed assertion.
- [x] Commit: `feat: aggregate provider results concurrently`.

## Task 5: Expose discovery through MVC and establish integration tests

**Files — create:** `src/ApiAggregation.Api/Features/Discovery/{DiscoveryController,DiscoveryRequest,DiscoveryResponse,DiscoveryResponseMapper}.cs`, `src/ApiAggregation.Api/ErrorHandling/{ApiExceptionHandler,ResultHttpExtensions}.cs`; `tests/ApiAggregation.IntegrationTests/Support/ApiFactory.cs`, `tests/ApiAggregation.IntegrationTests/Discovery/DiscoveryEndpointTests.cs`; modify `Program.cs`, `Directory.Packages.props`, `.github/workflows/ci.yml`.

**Interfaces:** `DiscoveryController(DiscoveryService service, IEnumerable<IDiscoveryProvider> providers)` exposes `Task<ActionResult<DiscoveryResponse>> Get([FromQuery] DiscoveryRequest request, CancellationToken cancellationToken)`. `DiscoveryRequest` maps query names to the raw input from Task 3. `DiscoveryResponse` mirrors the spec JSON, without serializing internal dependency descriptors or entire duplicate provider item lists. `DiscoveryResponseMapper.Map(DiscoveryResult result): DiscoveryResponse`. `ResultHttpExtensions.ToActionResult<TValue,TResponse>(this Result<TValue> result, Func<TValue,TResponse> onSuccess): ActionResult<TResponse>` maps the Result at the controller boundary; `ToProblem(AppError error): ObjectResult` produces ProblemDetails or ValidationProblemDetails. This replaces the reference project's Minimal API IResult mapper. `ApiFactory : WebApplicationFactory<Program>` permits test-only service overrides.

- [x] Write HTTP tests with controlled providers for 200, partial 200, empty 200, all-unavailable 503, 400 with zero provider calls, unexpected exception 500 with trace id and no exception text, plus liveness. Assert the Result mapper groups ValidationIssue records into validation ProblemDetails, emits only allowlisted `validationErrors`/`providers` metadata, and strips deliberately supplied exception/token metadata. Include `InvalidTopicDoesNotContactProviders`:
  ```csharp
  Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  Assert.Equal(0, provider.CallCount);
  ```
- [x] Run **Integration / DiscoveryEndpointTests**; confirm routing/controller behavior is missing.
- [x] Register the service/parser/controller, MVC Result mapper, and unexpected-exception handler. Centralize mapping of ValidationError -> 400, NotFound -> 404, Unauthorized -> 401, Forbidden -> 403, Conflict -> 409, RateLimited -> 429, ServiceUnavailable -> 503, and InternalError/Unspecified -> 500. The aggregate owns the all-provider ServiceUnavailable decision, so an upstream authentication failure never incorrectly challenges our API caller. Return 503 ProblemDetails with sanitized provider outcome extensions; preserve request cancellation. Set camelCase JSON and string enum serialization consistently. Register `TimeProvider.System` for later controlled-time injection. Add Mvc.Testing only to IntegrationTests.
- [x] Run **Integration all** and **Unit all**. CI now runs both projects explicitly. Provider test doubles are confined to this early test host; Tasks 7-9 progressively replace them with transport-only stubs. Add `.http` examples and current JSON contracts to README.
- [x] Commit: `feat: expose discovery controller and response contracts`.

## Task 6: Establish the provider HTTP boundary

**Files — create:** `src/ApiAggregation.Infrastructure/Providers/{IExternalContentClient,ProviderContent,ProviderErrors,DiscoveryProviderAdapter}.cs`, `src/ApiAggregation.Infrastructure/Http/BoundedJsonReader.cs`, `src/ApiAggregation.Infrastructure/DependencyInjection.cs`; `tests/ApiAggregation.UnitTests/Providers/DiscoveryProviderAdapterTests.cs`, `tests/ApiAggregation.UnitTests/Http/BoundedJsonReaderTests.cs`, `tests/ApiAggregation.UnitTests/Support/StubHttpMessageHandler.cs`.

**Interfaces:** Infrastructure-local port `IExternalContentClient` has `ProviderDescriptor Descriptor` and `Task<Result<ProviderContent>> FetchAsync(string topic, CancellationToken cancellationToken)`; make these types public for DI/test-assembly access, without referencing them from Application. `ProviderContent(ImmutableArray<DiscoveryItem> Items, bool IsIncomplete)` contains mapped usable records only. `DiscoveryProviderAdapter(IExternalContentClient client, TimeProvider timeProvider)` implements Application's `IDiscoveryProvider`. `ProviderErrors` creates AppError values for expected upstream failures, with stable safe codes and semantic categories; diagnostic exceptions are logged and never stored in response metadata. `BoundedJsonReader.ReadAsync<T>(HttpContent content, CancellationToken cancellationToken): Task<Result<T>>` caps decoded entity bytes at 1,048,576 before unbounded JSON allocation; enforce the cap while reading, including absent/false Content-Length.

- [x] Write adapter tests for success timestamping, raw-client Result failure -> the same typed failure, caller cancellation propagation, and unexpected exception propagation. Write bounded-reader tests at/above the 1 MiB boundary and for chunked streams. In `DoesNotTurnCancellationIntoUnavailable` assert:
  ```csharp
  await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.FetchAsync("dotnet", canceledToken));
  ```
- [x] Run **Unit / DiscoveryProviderAdapterTests** and **Unit / BoundedJsonReaderTests**; confirm missing implementations.
- [x] Implement the adapter and bounded reader. Timestamp successful normalized results at completion and wrap usable data with AppResult.Success; propagate raw-client AppError values unchanged. Raw clients translate known HTTP/network/JSON/library failures once into failed Results after the HTTP retry pipeline has finished. BoundedJsonReader returns failed Results for invalid JSON and oversized bodies. Introduce safe codes `upstream_error`, `invalid_payload`, `response_too_large`, `timeout`, `rate_limited`, `unauthorized`, `circuit_open`, `busy`; use ServiceUnavailable for dependency outages/timeouts/circuit rejections/invalid payloads, RateLimited for quota rejection, and Unauthorized for upstream credentials failures. Do not catch arbitrary exceptions as provider failures. Caller cancellation propagates; no custom exception type is needed for ordinary provider or validation failures.
- [x] Run **Unit all**. Keep helpers focused; introduce no abstract generic base provider. Raw typed clients return Result<ProviderContent>, the adapter produces Result<ProviderData>, and later caching decorates the application port.
- [x] Commit: `feat: add bounded provider transport boundary`.

## Task 7: Integrate GitHub repositories

**Files — create:** `src/ApiAggregation.Infrastructure/Providers/GitHub/{GitHubClient,GitHubResponse,GitHubOptions}.cs`; `tests/ApiAggregation.UnitTests/Providers/GitHubClientTests.cs`, `tests/ApiAggregation.UnitTests/Fixtures/github-repositories.json`; `tests/ApiAggregation.IntegrationTests/Support/ProviderHttpStub.cs`; modify Infrastructure registration, Api startup/config, and discovery integration tests.

**Interfaces:** `GitHubClient(HttpClient httpClient)` implements `IExternalContentClient` with descriptor `github/Repository`. Configure typed client headers/base address through validated `GitHubOptions` (`https://api.github.com/`, optional Token). `AddInfrastructure(IServiceCollection services, IConfiguration configuration): IServiceCollection` registers clients, adapters, TimeProvider, and later decorators; every IDiscoveryProvider descriptor must be unique. Register each ProviderDescriptor as singleton metadata independently of its client/decorator factory. Statistics consumes only this metadata, never the provider objects, avoiding a metrics-handler -> store -> provider -> metrics-handler DI cycle. Use TryAdd for TimeProvider so test overrides remain possible.

- [ ] Write mapping/request tests: URL-encoded topic with `is:public`, `per_page=30`, page 1; User-Agent and GitHub JSON accept headers; optional token; id/name/html_url/created_at/topics mapping. Test valid empty, bad JSON, incomplete_results, partially invalid items, and all invalid items. `AllInvalidRecordsAreFailure` asserts:
  ```csharp
  var result = await client.FetchAsync("dotnet", cancellationToken);
  Assert.False(result.IsSuccess);
  Assert.Equal("invalid_payload", result.Error!.Code);
  ```
- [ ] Run **Unit / GitHubClientTests**; confirm absence.
- [ ] Implement DTOs/client using `SendAsync(..., ResponseHeadersRead, cancellationToken)` and bounded body parsing. Select at most 30 items, apply the spec's incomplete-response rules, dispose responses, never log tokens. Register this real adapter in the application; tests substitute only its primary HTTP handler.
- [ ] Run **Unit all** and **Integration all**. Prove `/api/discovery?sources=github&topic=dotnet` passes through real DI, client, mapping, and controller under the HTTP stub. Add GitHub setup/topic semantics to README.
- [ ] Commit: `feat: integrate GitHub repository discovery`.

## Task 8: Integrate DEV articles

**Files — create:** `src/ApiAggregation.Infrastructure/Providers/DevTo/{DevToClient,DevToResponse,DevToOptions}.cs`; `tests/ApiAggregation.UnitTests/Providers/DevToClientTests.cs`, `tests/ApiAggregation.UnitTests/Fixtures/devto-articles.json`; modify registration/config and transport-only integration stubs.

**Interfaces:** `DevToClient(HttpClient httpClient)` implements the same raw client port with descriptor `devto/Article`; base address `https://dev.to/`. Private DTOs map article id/title/url/description/published_at/tag_list.

- [ ] Write request/mapping tests for `/api/articles?tag=dotnet&per_page=30&page=1`, the documented Forem v1 accept header, no required API key, valid empty arrays, malformed root, mixed valid/invalid items, and entirely unusable items. `UsesExactTagMatching` asserts the outgoing `tag` is `dotnet` and no unsupported free-text query parameter is sent; `MapsPublishedDate` asserts the fixture's exact UTC timestamp.
- [ ] Run **Unit / DevToClientTests**; confirm absence.
- [ ] Implement the client and register it alongside GitHub. Apply the same body limit, cancellation, and response-acceptance rules without sharing provider-specific DTOs. Update HTTP integration tests to exercise both registered providers through primary-handler stubs.
- [ ] Run **Unit all** and **Integration all**; document exact-tag semantics and empty-tag behavior.
- [ ] Commit: `feat: integrate DEV article discovery`.

## Task 9: Integrate Hacker News and verify complete fan-out

**Files — create:** `src/ApiAggregation.Infrastructure/Providers/HackerNews/{HackerNewsClient,HackerNewsResponse,HackerNewsOptions}.cs`; `tests/ApiAggregation.UnitTests/Providers/HackerNewsClientTests.cs`, `tests/ApiAggregation.UnitTests/Fixtures/hackernews-stories.json`; `tests/ApiAggregation.IntegrationTests/Discovery/ProviderFanOutTests.cs`; modify registration/config.

**Interfaces:** `HackerNewsClient(HttpClient httpClient)` implements the raw client port with descriptor `hackernews/Discussion`; base `https://hn.algolia.com/`, request `/api/v1/search?tags=story&query=dotnet&hitsPerPage=30&page=0`. Map objectID, title, created_at, story_text when present, and available tags; URL is `https://news.ycombinator.com/item?id={objectID}`.

- [ ] Write mapping/error tests equivalent to the previous providers, including null external story URL. Write gated `AllThreeTransportsStartBeforeAnyCompletes` in the real host and assert:
  ```csharp
  Assert.Equal(new[] { "devto", "github", "hackernews" }, startedProviders.Order().ToArray());
  Assert.True(result.IsSuccess);
  Assert.Equal(3, result.Value!.Providers.Length);
  ```
- [ ] Run **Unit / HackerNewsClientTests** and **Integration / ProviderFanOutTests**; confirm expected failures.
- [ ] Implement/register the client and complete transport-only integration fixtures. Default endpoint selects all three; source filtering selects only requested providers. Keep cross-source records even when URLs/titles resemble each other.
- [ ] Run **Unit all** and **Integration all**. README explains candidate-window limits and creation/publication date semantics across all sources.
- [ ] Commit: `feat: integrate Hacker News and complete provider fan-out`.

## Task 10: Implement thread-safe request statistics

**Files — create:** `src/ApiAggregation.Application/Features/Statistics/{IStatisticsReader,StatisticsSnapshot,ProviderStatistics,BucketStatistics}.cs`; `src/ApiAggregation.Infrastructure/Statistics/{AttemptOutcome,AttemptObservation,IRequestStatisticsRecorder,InMemoryStatisticsStore}.cs`; `tests/ApiAggregation.UnitTests/Statistics/InMemoryStatisticsStoreTests.cs`.

**Interfaces:** `IStatisticsReader.GetSnapshot(): StatisticsSnapshot`. Snapshot carries `StartedAt`, `SnapshotAt`, immutable provider statistics. Provider statistics contain Source, TotalRequests, SuccessCount, FailureCount, CanceledCount, nullable AverageResponseTimeMs, and named Fast/Average/Slow BucketStatistics (Count and nullable AverageResponseTimeMs). `AttemptObservation(string Source, TimeSpan Duration, AttemptOutcome Outcome, DateTimeOffset CompletedAt)`; outcome is Success/Failure/Canceled. `IRequestStatisticsRecorder.Record(AttemptObservation observation): void`. One singleton store implements both ports and receives registered descriptors and TimeProvider.

- [ ] Write tests for empty providers (count 0/mean null), exact 100/200 ms bucket boundaries, outcome totals, weighted means, immutable snapshots, and parallel writes to one and multiple providers. For samples 99,100,200,201 ms in `UsesExactBucketBoundaries`, assert:
  ```csharp
  Assert.Equal(4, stats.TotalRequests);
  Assert.Equal(150d, stats.AverageResponseTimeMs);
  Assert.Equal(1, stats.Fast.Count);
  Assert.Equal(2, stats.Average.Count);
  Assert.Equal(1, stats.Slow.Count);
  ```
- [ ] Run **Unit / InMemoryStatisticsStoreTests**; confirm absence.
- [ ] Implement per-provider locked accumulators for totals/sums/buckets/outcomes. Validate nonnegative duration and known provider names; use a fixed provider-key set. Return independent snapshots with null means when empty. Do not introduce unbounded sample lists or queues.
- [ ] Run **Unit all**. A parallel test records 10,000 known observations and asserts exact total/bucket/outcome sums; it must not merely assert no exception.
- [ ] Commit: `feat: add thread-safe provider statistics store`.

## Task 11: Measure HTTP attempts and expose statistics

**Files — create:** `src/ApiAggregation.Infrastructure/Http/RequestMetricsHandler.cs`; `src/ApiAggregation.Api/Features/Statistics/{StatisticsController,StatisticsResponse}.cs`; `tests/ApiAggregation.UnitTests/Http/RequestMetricsHandlerTests.cs`, `tests/ApiAggregation.IntegrationTests/Statistics/StatisticsEndpointTests.cs`; modify registration and README.

**Interfaces:** `RequestMetricsHandler(string source, IRequestStatisticsRecorder recorder, TimeProvider timeProvider) : DelegatingHandler` measures `SendAsync` with monotonic timestamps. `StatisticsController(IStatisticsReader reader).Get(): ActionResult<StatisticsResponse>` serves `/api/statistics`; response explicitly reports the `transportToHeaders` measurement scope and process-local counters.

- [ ] Write transport tests for 2xx/non-2xx/transport exception/cancellation, asserting exactly one observation and elapsed duration from the fake TimeProvider. Delayed body consumption must not change header-duration measurement. HTTP tests assert all providers appear before any calls and only contacted providers increment. `RecordsCanceledAttemptExactlyOnce` asserts:
  ```csharp
  Assert.Single(observations);
  Assert.Equal(AttemptOutcome.Canceled, observations[0].Outcome);
  ```
- [ ] Run **Unit / RequestMetricsHandlerTests** and **Integration / StatisticsEndpointTests**; confirm missing behavior.
- [ ] Implement the handler immediately outside the actual transport, inside future retries/timeouts, recording in a single completion/finally path. 2xx is transport success even if later parsing fails. Register one store instance for both interfaces. Map immutable statistics to HTTP contracts with null averages retained.
- [ ] Run **Unit all** and **Integration all**. Document that latency excludes body parsing, backoff, cache access, and aggregation; add a statistics request/response example.
- [ ] Commit: `feat: expose measured external request statistics`.

## Task 12: Add bounded retries and provider deadlines

**Files — create:** `src/ApiAggregation.Infrastructure/Http/{ProviderResilienceOptions,ProviderHttpPipeline,DeadlineDiscoveryProvider}.cs`; `tests/ApiAggregation.IntegrationTests/Resilience/ProviderRetryTests.cs`, `tests/ApiAggregation.UnitTests/Providers/DeadlineDiscoveryProviderTests.cs`; modify provider registration and expected-error mapping.

**Interfaces:** `DeadlineDiscoveryProvider(IDiscoveryProvider inner, TimeProvider timeProvider, IOptions<ProviderResilienceOptions> options)` implements the same application port and becomes the outermost per-provider decorator. `ProviderHttpPipeline.Configure(IHttpClientBuilder builder, string source): IHttpClientBuilder` configures one Microsoft.Extensions.Http.Resilience pipeline per typed client, resolving options through DI. Options: attempt timeout 2 s, provider deadline 5 s, max retries 1, base retry delay 200 ms, exponential backoff with jitter.

- [ ] Write `RetriesOneTransientFailureAndCountsBothAttempts`, 500->200 (2 requests), 500->500->200 (only first 2 sent), 400->200 (only 1 sent), network failure retry, timeout retry, malformed body (no retry), and body-read deadline tests. Assert:
  ```csharp
  Assert.Equal(2, transport.CallCount);
  Assert.Equal(2, statistics.TotalRequests);
  Assert.Equal(1, statistics.SuccessCount);
  Assert.Equal(1, statistics.FailureCount);
  ```
  Write gated deadline tests: timeout returns a failed Result with ServiceUnavailable/`timeout`; caller cancellation propagates; a stalled inner provider cannot leave the caller waiting indefinitely.
- [ ] Run **Integration / ProviderRetryTests** and **Unit / DeadlineDiscoveryProviderTests**; confirm retry/deadline failures.
- [ ] Implement one retry policy for GET transport failures, attempt timeouts, 408, and 5xx. Disable HttpClient's competing timeout. Use a linked, TimeProvider-backed cancellation budget starting before the inner provider, plus a bounded await; body parsing receives the same token. The deadline wrapper stays outside future caching. Observe and cancel owned operations when returning early; never swallow a faulted detached task. Retry parsing neither in adapters nor in cache factories.
- [ ] Run **Unit all** and **Integration all**; verify retries execute through metrics separately. If splitting commits, land retries and their tests first, then the deadline and cancellation tests.
- [ ] Commit: `feat: bound provider retries and execution time`.

## Task 13: Respect rate limits and bound provider load

**Files — create:** `src/ApiAggregation.Infrastructure/Http/{ProviderRateLimitState,ProviderRateLimitHandler}.cs`; `tests/ApiAggregation.UnitTests/Http/ProviderRateLimitTests.cs`, `tests/ApiAggregation.IntegrationTests/Resilience/ProviderProtectionTests.cs`; modify ProviderHttpPipeline, options, registration, and error mapping.

**Interfaces:** `ProviderRateLimitState.IsBlocked(string source, DateTimeOffset now): bool`, `BlockUntil(string source, DateTimeOffset until): void`; state is per instance/provider and updates atomically to the later deadline. `ProviderRateLimitHandler(string source, ProviderRateLimitState state, TimeProvider timeProvider)` inspects status/headers and suppresses transport while blocked by returning a synthetic 429 before metrics/transport. The client converts that response to a typed RateLimited Result; no custom exception is thrown for cooldown control flow. Pipeline order: concurrency limiter -> retry -> circuit breaker -> attempt timeout -> cooldown handler -> request metrics -> transport. The outer provider deadline already covers all of this.

- [ ] Write tests for 429 with delta/date Retry-After, GitHub rate-limit 403/reset, ordinary unauthorized 403, malformed/missing header fallback 60 s, and cooldown expiry under controlled time. No 429/403 retry is sent. Write gated tests for the ninth simultaneous operation being rejected when eight permits are held and breaker opening after 5 handled failures among 10 completed attempts in 30 s. `SuppressedCallDoesNotIncreaseStatistics` asserts:
  ```csharp
  Assert.Equal(1, transport.CallCount);
  Assert.Equal(1, statistics.TotalRequests);
  Assert.False(secondResult.IsSuccess);
  Assert.Equal("rate_limited", secondResult.Error!.Code);
  ```
- [ ] Run **Unit / ProviderRateLimitTests** and **Integration / ProviderProtectionTests**; confirm missing behavior.
- [ ] Implement cooldown parsing: use the latest valid future time if multiple headers apply; checked parsing must not overflow on enormous values. Only 429 and positively identified GitHub rate-limit 403 activate cooldown. Exclude cooldown rejections and caller cancellation from retries/breaker failure samples. Configure circuit breaker (ratio .5, minimum 10, sampling 30 s, break 15 s), per-provider concurrency 8, queue 0. Exclude ordinary 4xx from handled breaker failures; include transient failures from Task 12.
- [ ] Run **Unit all** and **Integration all**; assert rejected requests create no transport measurements. Add settings and failure codes to README. Rate-limit handling and breaker/limiter can be separate passing commits if needed.
- [ ] Commit: `feat: respect provider quotas and bound outbound load`.

## Task 14: Add cache keys and immutable snapshot policy

**Files — create:** `src/ApiAggregation.Infrastructure/Caching/{ProviderCacheOptions,ProviderCacheKey,CachedProviderSnapshot}.cs`; `tests/ApiAggregation.UnitTests/Caching/ProviderCachePolicyTests.cs`.

**Interfaces:** `ProviderCacheKey.Create(string prefix, string source, string normalizedTopic, int candidateLimit): string` yields `{prefix}:discovery:v1:{source}:{normalizedTopic}:limit:{candidateLimit}`. `CachedProviderSnapshot(ImmutableArray<DiscoveryItem> Items, DateTimeOffset FetchedAt, DateTimeOffset FreshUntil, DateTimeOffset StaleUntil, bool IsIncomplete)`; `ToResult(DateTimeOffset now): Result<ProviderData>` returns success with IsStale=false iff now < FreshUntil, success with IsStale=true iff FreshUntil <= now < StaleUntil, and ServiceUnavailable/`cache_expired` failure otherwise. `ProviderCacheOptions` supports validated per-provider policies with fresh 5 min, maximum age 30 min, throttle 30 s, Redis timeout 250 ms, environment prefix. Maximum age includes the fresh interval.

- [ ] Write key and boundary tests; filtering/sorting/response limit do not appear in keys. `ExpiresAtAbsoluteMaximumAge` uses fetchedAt = `2026-11-05T12:00:00Z` and asserts:
  ```csharp
  Assert.False(snapshot.ToResult(fetchedAt.AddMinutes(4)).Value!.IsStale);
  Assert.True(snapshot.ToResult(fetchedAt.AddMinutes(5)).Value!.IsStale);
  Assert.False(snapshot.ToResult(fetchedAt.AddMinutes(30)).IsSuccess);
  Assert.Equal("cache_expired", snapshot.ToResult(fetchedAt.AddMinutes(30)).Error!.Code);
  ```
- [ ] Run **Unit / ProviderCachePolicyTests**; confirm absence.
- [ ] Implement key/policy/snapshot types and configuration validation (positive durations, maximum age greater than fresh duration, safe nonempty prefix). Use actual original fetched timestamps; conversion never advances them. No runtime cache dependency yet.
- [ ] Run **Unit all**. Document exact freshness and stale-age boundaries.
- [ ] Commit: `feat: define provider cache keys and freshness policy`.

## Task 15: Add FusionCache L1 and local request coalescing

**Files — create:** `src/ApiAggregation.Infrastructure/Caching/{CachedDiscoveryProvider,CacheRegistration}.cs`; `tests/ApiAggregation.IntegrationTests/Caching/MemoryCacheTests.cs`; modify provider registration, configuration, and CPM.

**Interfaces:** `CachedDiscoveryProvider(IDiscoveryProvider inner, IFusionCache cache, IOptions<ProviderCacheOptions> options, TimeProvider timeProvider)` implements `IDiscoveryProvider`. Successful inner Result values become CachedProviderSnapshot inside `GetOrSetAsync<CachedProviderSnapshot>`. On a failed inner Result, use the verified v2.9.0 factory-context `Fail(error.Code ?? "upstream_error")` API to activate fail-safe; do not cache Result failures or throw a custom exception for expected refresh failures. When the library reports factory failure without fallback, catch only its documented factory-failure exception inside this decorator and return the original typed error if this invocation observed it, otherwise reconstruct a sanitized ProviderErrors value from an allowlisted safe error code. This must also work for coalesced followers; unknown codes become generic ServiceUnavailable. Keep error capture local to each invocation, never on the shared provider instance. Register the chain `DeadlineDiscoveryProvider -> CachedDiscoveryProvider -> DiscoveryProviderAdapter -> typed external client`. Only Infrastructure sees FusionCache.

- [ ] Write tests for first fetch, fresh repeat with no transport increment, key isolation by provider/topic, reuse across sort/filter/response-limit changes, successful empty caching, unavailability not cached, and 20 same-key concurrent callers producing one local fetch. Add follower-cancellation and cache-lock deadline tests with gates. Assert:
  ```csharp
  Assert.Equal(1, transport.CallCount);
  Assert.Equal(1, statistics.TotalRequests);
  Assert.Equal(originalOrder, cachedItems.Select(x => x.Id).ToArray());
  ```
- [ ] Run **Integration / MemoryCacheTests**; confirm redundant calls before caching exists.
- [ ] Implement FusionCache L1 with a dedicated size-limited MemoryCache: 1,000 entry-size units, each provider snapshot size 1, combined with the upstream body/candidate caps. Keep cached values immutable; do not set a size limit on the shared framework cache. Disable eager refresh/background factory completion and distributed background operations. Fail-safe is disabled until Task 18. Translate library-level factory failure back to failed Results; preserve caller cancellation. Distinguish unexpected faults from explicit context.Fail signaling: capture unexpected factory exceptions locally and rethrow with preserved stack even if FusionCache tries to serve stale data. Test this when fail-safe is enabled; no unexpected bug becomes a successful fallback response.
- [ ] Run **Unit all** and **Integration all**. Ensure a canceled follower does not cancel the leader. If the factory owner cancels, shared fill may be canceled; other callers must complete or retry within their own deadlines, without indefinite wait or invented success. Confirm no cache hit increments provider request metrics.
- [ ] Commit: `feat: cache provider results with FusionCache`.

## Task 16: Add Redis L2 and reproducible container setup

**Files — create:** `compose.yaml`, `redis.conf`, `.env.example`, `tests/ApiAggregation.IntegrationTests/Support/RedisFixture.cs`, `tests/ApiAggregation.IntegrationTests/Caching/RedisCacheTests.cs`; modify CacheRegistration, Api config, CPM, CI, README.

**Interfaces:** `RedisFixture : IAsyncLifetime` owns a disposable real Redis container and exposes its connection string and a per-test cache prefix. `CacheRegistration.AddProviderCaching(IServiceCollection services, IConfiguration configuration): IServiceCollection` configures the dedicated FusionCache instance, SystemTextJson serializer, and RedisCache L2 with a shared singleton connection multiplexer where supported. Connection ownership/disposal belongs to DI; never construct a connection per request.

- [ ] Write `SecondHostReadsFirstHostsSnapshot`: create independent hosts with separate L1 instances, common Redis prefix, and different transport call counters. After host A completes a successful call, host B must read the same content and fetched timestamp with zero provider calls. Add serialization of immutable collections/Uri/DateTimeOffset and Redis-degraded startup tests. Assert:
  ```csharp
  Assert.Equal(0, secondTransport.CallCount);
  Assert.Equal(firstFetchedAt, secondFetchedAt);
  ```
- [ ] Run **Integration / RedisCacheTests**; confirm missing L2 behavior. A missing Docker engine is an explicit environment failure, not an ignored/skipped integration suite.
- [ ] Add real RedisCache and FusionCache SystemTextJson serialization. Configure 250 ms distributed operation timeout, bounded connection attempts, and foreground writes. Configure failed Redis operations to preserve successful upstream results. Compose initially starts Redis alone; bind local access to loopback, set `maxmemory 128mb` and `maxmemory-policy allkeys-lru`, and omit persistence because this is disposable cached data. Pin the verified image/digest in both fixture and Compose.
- [ ] Run **Integration all** and **Unit all**. Extend CI for Docker-capable Redis tests with isolated prefixes; unrelated HTTP tests may use L1-only configuration. README documents `docker compose up -d redis`, configuration and prerequisite checks, and why missing Redis degrades caching rather than crashes startup.
- [ ] Commit: `feat: add Redis distributed cache and container setup`.

## Task 17: Synchronize local caches with the Redis backplane

**Files — create:** `tests/ApiAggregation.IntegrationTests/Caching/BackplaneTests.cs`; modify CacheRegistration, CPM, Redis fixture helpers, README.

**Interfaces:** Reuse the existing cache registration and application port; no new Domain/Application interface. Configure the FusionCache StackExchangeRedis backplane with isolated channels consistent with the application/environment prefix.

- [ ] Write `UpdatedSnapshotEventuallyReachesOtherHostsL1`: warm host A/B, replace A's cache entry with newer data through its actual IFusionCache, and use bounded polling until B reads the update. Also verify different prefixes do not cross-invalidate. Assert fetchedAt is the timestamp of the actual refreshed snapshot, not notification time.
- [ ] Run **Integration / BackplaneTests**; confirm B retains its old L1 entry without the backplane.
- [ ] Configure the official Redis backplane using the existing connection ownership model. Propagate cache changes without requiring clients to restart. Use event/gate-assisted bounded polling in tests, not fixed multi-second sleeps; do not assert immediate/strong consistency.
- [ ] Run **Integration / BackplaneTests** and **Integration / RedisCacheTests**. Document eventual synchronization and that backplane sharing is not distributed locking.
- [ ] Commit: `feat: coordinate cache updates through Redis backplane`.

## Task 18: Enable bounded stale fallback and degraded health

**Files — create:** `src/ApiAggregation.Infrastructure/Caching/RedisHealthCheck.cs`, `tests/ApiAggregation.IntegrationTests/Caching/CacheFallbackTests.cs`, `tests/ApiAggregation.IntegrationTests/Health/ReadinessTests.cs`; modify CachedDiscoveryProvider, CacheRegistration, HealthController, README.

**Interfaces:** Add `HealthController.Ready(CancellationToken cancellationToken): Task<IActionResult>` using `HealthCheckService`, reporting degraded Redis status with HTTP 200 while local service remains usable. Health checks do not contact providers. Existing provider outcomes already expose stale status and original fetchedAt.

- [ ] Write cases for failed refresh with prior good data, expired maximum age, no prior data, partial usable refresh replacing an old snapshot, all-invalid payload preserving old snapshot, valid empty refresh replacing old data, and Redis failure with/without an L1 snapshot. Add two-host fallback age preservation, malformed Redis payload, Result failure triggering fail-safe, cold failed Result never being cached, and warm-cache unexpected exception still producing HTTP 500 cases. Assert in `RefreshFailurePreservesOriginalAge`:
  ```csharp
  Assert.True(result.IsSuccess);
  Assert.True(result.Value!.IsStale);
  Assert.Equal(originalFetchedAt, result.Value.FetchedAt);
  Assert.True(discovery.IsPartial);
  ```
  At the absolute stale deadline assert unavailable; a corrupted serialized entry must cause a safe cache miss/refetch or unavailable outcome, never a 500 exposing serialization internals.
- [ ] Run **Integration / CacheFallbackTests** and **Integration / ReadinessTests**; confirm fail-safe/degraded health are absent.
- [ ] Enable fail-safe when saving and reading: fresh 5 min, total retention 30 min, throttle 30 s. Stamp the cached envelope once after successful upstream retrieval; on any L2 copy keep its original timestamps. Never cache unavailable results as empty successes. Check the envelope's absolute expiry after GetOrSet even if FusionCache throttling has prolonged logical accessibility. Reserve no extra work beyond the outer provider deadline; eligible immediately available fallback may be used, otherwise return timeout/unavailable.
- [ ] Implement Redis health degradation with bounded checks and safe diagnostics. Run **Unit all** and **Integration all**, including Redis loss after warmup and cold unavailable Redis. Use controlled TimeProvider for our policy and short test-specific TTLs only where FusionCache's clock cannot be replaced; wait on state conditions with bounded deadlines.
- [ ] Commit: `feat: serve bounded stale data during provider outages`.

## Task 19: Add explicitly requested live smoke checks

**Files — create:** `tests/ApiAggregation.SmokeTests/{LiveApiFactAttribute,ProviderSmokeTests}.cs`, `.github/workflows/smoke.yml`, `scripts/Invoke-SmokeTests.ps1`; modify SmokeTests csproj and README.

**Interfaces:** `LiveApiFactAttribute : FactAttribute` skips by an explicit message unless `RUN_LIVE_SMOKE_TESTS=1`; normal CI never runs this project. Smoke checks use `DeadlineDiscoveryProvider -> DiscoveryProviderAdapter -> real typed client`, bypassing the cache decorator while retaining the five-second budget and actual HTTP resilience pipeline; use `dotnet` as topic. `Invoke-SmokeTests.ps1` sets/restores this environment flag in its own process and invokes the SmokeTests project, propagating failure exit code.

- [ ] Add one smoke test per provider: the call returns IsSuccess=true and parsed items, if any, have required ids/titles/absolute URLs and valid dates. Do not require a nonzero result set. With the flag absent, assert through a dry run that all three are reported skipped with the opt-in reason, rather than contacting the internet.
- [ ] Implement the opt-in attribute, bounded provider checks, and workflow_dispatch-only workflow with optional GitHub token from secrets. Reuse provider implementation; no production cache hits are allowed to masquerade as connectivity success.
- [ ] Run `dotnet test tests/ApiAggregation.SmokeTests/ApiAggregation.SmokeTests.csproj -c Release` without opt-in and confirm three explicit skips. Then run `./scripts/Invoke-SmokeTests.ps1` once when network access is available; expect three passes or report precise live quota/network failures. Never mark a failed live run successful or automatically turn it into a skip.
- [ ] Commit: `test: add opt-in live provider smoke checks`.

## Task 20: Complete required-feature documentation and deployment checks

**Files — create:** `src/ApiAggregation.Api/OpenApi/OpenApiConfiguration.cs`, `Dockerfile`, `.dockerignore`, `docs/api-examples.md`, `docs/adding-a-provider.md`; modify README, `.http` examples, CI, Compose, Api Program, CPM; create `tests/ApiAggregation.IntegrationTests/Contracts/OpenApiTests.cs`.

**Interfaces:** Use an OpenAPI generator that supports MVC controllers; select and pin compatible Swashbuckle.AspNetCore versions at this task. `OpenApiConfiguration.AddApiDocumentation(IServiceCollection services)` registers controller document generation. Serve Swagger JSON/UI through middleware in Development only; there are no Minimal API business/health endpoints. Document the expected development document URL in README.

- [ ] Write `DocumentsDiscoveryAndStatisticsContracts`: fetch generated OpenAPI and assert discovery/statistics paths, query enums/ranges, 200/400/503 responses, provider status fields, and nullable empty statistics averages are represented. Verify the document uses bounded candidate counts rather than advertising global pagination.
- [ ] Run **Integration / OpenApiTests**; confirm documentation is missing.
- [ ] Add controller-aware OpenAPI and annotations/transformations, complete README/examples, and add API container to Compose with Redis service discovery, health checks, and the same config defaults. Pin supported .NET 10 base image digests when introducing Dockerfile. Keep local HTTP and deployment TLS responsibilities clear. Document all safe error codes, cache scope/age, metric scope, test commands, and how an additional provider registers without changing DiscoveryService.
- [ ] Run `dotnet restore ApiAggregation.slnx`, Release build, **Unit all**, **Integration all**, `docker compose config`, and `docker compose build api`; expect success. Start the task-owned Compose services and verify live/ready endpoints plus one discovery/statistics example where network permits; stop services created for verification. Review generated OpenAPI and serialized examples against the spec. Do not run a second full live smoke suite without a new reason.
- [ ] Commit: `docs: complete API contracts and reproducible setup` (split Docker packaging into `build: package API with Redis` if the diff is independently reviewable).

## Task 21: Add JWT bearer authentication — after required features

**Files — create:** `src/ApiAggregation.Api/Authentication/AuthenticationRegistration.cs`; `tests/ApiAggregation.IntegrationTests/Authentication/JwtAuthenticationTests.cs`, `tests/ApiAggregation.IntegrationTests/Support/TestTokenIssuer.cs`; modify Program, discovery/statistics/health controllers, OpenAPI configuration, Api project user-secrets metadata, README/examples, CPM, test host authentication setup.

**Interfaces:** `AuthenticationRegistration.AddApiAuthentication(IServiceCollection services, IConfiguration configuration): IServiceCollection`. Discovery/statistics require authenticated bearer access; health explicitly allows anonymous access. Use standard JwtBearer configuration, production issuer/audience/HTTPS metadata, and local `dotnet user-jwts`; no custom token endpoint or production symmetric signing secret in source control.

- [ ] Write HTTP tests for missing bearer token, expired token, invalid signature, wrong issuer/audience (all 401), and valid token (200). Use actual bearer validation and deterministic test-only signing keys. Assert anonymous `/health/live` and `/health/ready` remain usable; unauthorized discovery requests cause zero provider calls.
- [ ] Run **Integration / JwtAuthenticationTests**; confirm protected endpoints currently allow anonymous requests.
- [ ] Implement authentication and `[Authorize]`/`[AllowAnonymous]`, secure defaults, and OpenAPI security declarations. Update existing successful endpoint tests to send a real valid test JWT; do not globally replace JWT authentication with a fake authentication handler. Add user-jwts setup and curl/.http instructions in this same commit. Ensure production startup validates required issuer/audience configuration rather than accidentally accepting local-development tokens.
- [ ] Run **Unit all** and **Integration all**. Exercise local user-jwts flow once without printing signing keys or persisting tokens in committed examples. Direct-provider smoke tests remain unchanged.
- [ ] Commit: `feat: secure discovery and statistics with JWT bearer tokens`.

## Task 22: Add performance-anomaly analysis — last feature

**Files — create:** `src/ApiAggregation.Infrastructure/Statistics/{PerformanceWindow,PerformanceHistory,AnomalyEvaluator,PerformanceAnomalyService,AnomalyOptions}.cs`; `tests/ApiAggregation.UnitTests/Statistics/{PerformanceHistoryTests,AnomalyEvaluatorTests,PerformanceAnomalyServiceTests}.cs`; modify store/registration/config/README.

**Interfaces:** Per-provider `PerformanceHistory(string source)` exposes `Record(AttemptObservation observation): void` and `GetWindow(DateTimeOffset now, long lifetimeCount, double lifetimeDurationMs): PerformanceWindow`; each window contains source, baseline count/duration sum, recent count/duration sum. Add `InMemoryStatisticsStore.GetPerformanceWindows(DateTimeOffset now): ImmutableArray<PerformanceWindow>` to snapshot history with that provider's lifetime totals under its existing lock. `AnomalyEvaluator.IsAnomalous(PerformanceWindow window): bool` is pure. `PerformanceAnomalyService(InMemoryStatisticsStore store, TimeProvider timeProvider, IOptions<AnomalyOptions> options, ILogger<PerformanceAnomalyService> logger): BackgroundService` checks every minute, logs transitions, and observes stoppingToken. `AnomalyOptions.CheckInterval` defaults to one minute. Keep the specified five-minute window, 1.5 ratio, and 10-sample minimum in the evaluator. Do not maintain an unsynchronized second lifetime total.

- [ ] Write count-weighted baseline/window tests for five complete UTC minutes, exclusion of the current minute, bounded storage after long idle periods, concurrent recording/snapshot, and samples exactly on minute boundaries. `RequiresStrictlyMoreThanFiftyPercentIncrease` asserts:
  ```csharp
  Assert.False(AnomalyEvaluator.IsAnomalous(windowWithBaseline100AndRecent150));
  Assert.True(AnomalyEvaluator.IsAnomalous(windowWithBaseline100AndRecent151));
  ```
  Both populations contain at least 10 samples; separately assert insufficient samples and zero baseline never alert. Test one warning on entry, no repeat during the same anomaly, one recovery, and prompt shutdown.
- [ ] Run **Unit / PerformanceHistoryTests**, **Unit / AnomalyEvaluatorTests**, and **Unit / PerformanceAnomalyServiceTests**; confirm missing behavior.
- [ ] Add a bounded six-minute ring (current plus previous five), preserving older data only in lifetime totals. Attribute observations by completion time. Compute baseline as lifetime minus current/recent retained totals; handle ring rollover and idle gaps without subtracting unrelated overwritten slots. Delayed recordings outside the retained window still contribute to lifetime/baseline. Only the last five complete minute slots contribute to recent data. Snapshot history and lifetime totals together.
- [ ] Implement the evaluator (recent mean > 1.5 * baseline mean, each count >= 10, baseline > 0) and one-minute worker with per-provider anomaly transition state. Log provider, both sample counts, both means, and ratio; no secrets or full upstream payloads. Insufficient data should not be labeled a measured recovery; clear/reset tracking explicitly and document that behavior. This task can use separate passing history and worker commits.
- [ ] Run **Unit all** and **Integration all**; existing statistics responses must remain correct after history is added. Verify shutdown and minute scheduling with controlled time, not five-minute wall-clock waits.
- [ ] Commit: `feat: log sustained provider performance anomalies`.

## Task 23: Final review and repository handoff

**Files:** Modify README/design/plan only if final verification reveals documentation drift; any production fixes receive their own focused regression-tested commits.

**Interfaces:** No new product behavior. Deliver the source repository with reproducible commands and transparent test results.

- [ ] Confirm every acceptance criterion maps to the tasks below and all implemented checkboxes reflect real evidence. Inspect the commit history: scaffold first, each provider separate, caching incremental, JWT/anomalies last. Do not squash it into a single implementation commit.
- [ ] Run final Release restore/build and Unit/Integration suites, validate Compose and published OpenAPI, inspect `git diff --check` and repository status, and review the full implementation for dependency direction, cancellation, secret leakage, malformed responses, stale-age handling, and per-attempt metric correctness. Reuse prior verification evidence if no code has changed since the same full checks.
- [ ] Record the latest explicitly run smoke result and its environment; rerun only if relevant provider code changed or its result is unresolved. Describe external limitations instead of claiming tests ran when they could not.
- [ ] Resolve the user's intended remote repository destination if still unknown, then publish when authorized. Do not invent a GitHub/GitLab owner or visibility. Give the repository link, setup/test commands, and any concrete remaining limitations.

## Requirement coverage

| Requirement | Tasks |
|---|---|
| ASP.NET Core controllers, primary constructors, four layers/features | 1-6; maintained throughout |
| Easy provider extension and parallel fan-out | 4, 6-9 |
| Three external APIs | 7-9 |
| Unified endpoint, filtering, sorting | 3-5 |
| Result pattern, typed validation/provider errors, MVC mapping | 2-6, 14-15, 18 |
| Error handling, transient faults, fallback | 5-9, 12-13, 18 |
| In-memory thread-safe request statistics and buckets | 10-11 |
| Hybrid caching and performance | 14-18 |
| Unit tests and integration tests | Alongside every owning behavior |
| Live smoke checks | 19 |
| Documentation, reproducible setup, CI, repository delivery | 1 onward, 20, 23 |
| JWT bearer authentication (last phase) | 21 |
| Background anomaly detection (last feature) | 22 |

## Self-review record

- Coverage: every assignment requirement and accepted user preference has an owning task.
- Dependency order: controllers/ports precede adapters; metrics precede retries; cache policy precedes L1/L2/fallback; optional features follow all required features.
- Contract consistency: Application providers return Result<ProviderData>; raw Infrastructure clients return Result<ProviderContent>; validation returns Result<DiscoveryQuery>; aggregation returns Result<DiscoveryResult>; only cache decorators use CachedProviderSnapshot. ProviderDescriptor is the common source/type identity.
- Test boundaries: unit tests use controlled collaborators; integration tests progress from early fake providers to real adapters with stubbed HTTP and real Redis; smoke tests alone use live providers.
- Review Focus: all five listed risks have explicit tests in their owning tasks.
- Scope at plan authoring: no application code, dependencies, repository initialization, or tests had been created/run. Native execution is now in progress.

## Reference links for implementation

- [Design references and behavioral decisions](../specs/2026-11-05-api-aggregation-design.md#references-consulted)
- [FusionCache stable package](https://www.nuget.org/packages/ZiggyCreatures.FusionCache/2.9.0)
- [FusionCache Redis backplane package](https://www.nuget.org/packages/ZiggyCreatures.FusionCache.Backplane.StackExchangeRedis/2.9.0)
- [FusionCache SystemTextJson serializer](https://www.nuget.org/packages/ZiggyCreatures.FusionCache.Serialization.SystemTextJson/2.9.0)
- [xUnit v3 package](https://www.nuget.org/packages/xunit.v3.mtp-off/4.0.2)
- [Testcontainers.Redis package](https://www.nuget.org/packages/Testcontainers.Redis/4.14.0)
- [Microsoft HTTP resilience package](https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience/10.11.0)

- [Reference Result folder](E:/STORAGE/Documents/Indeavor_project/src/backend/src/Indeavor_project.Core/Result/Result.cs)
- [Reference HTTP mapper, to adapt for MVC](E:/STORAGE/Documents/Indeavor_project/src/backend/src/Indeavor_project.Api/Extensions/ResultHttpExtensions.cs)
- [FusionCache v2.9.0 explicit factory failure](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/docs/FailSafe.md#-even-without-exceptions)
