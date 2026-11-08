# FusionCache Memory-Only Migration Plan

> **For agentic workers:** Use superpowers:executing-plans for native execution, task by task. Do not commit without the user's instruction.

**Goal:** Replace custom memory-cache coordination and refresh throttling with FusionCache's local features while retaining the discovery contract and bounded stale fallback.

**Architecture:** Keep the Infrastructure provider decorator and immutable snapshot policy. A singleton FusionCache uses a dedicated, size-limited memory cache. Application, Domain and controllers remain library-independent.

**Tech stack:** .NET 10, FusionCache 2.9.0, Microsoft.Extensions.Caching.Memory 10.0.14, existing xUnit/WebApplicationFactory suites.

**Spec:** This approved direction supersedes the plain-IMemoryCache mechanism in `../specs/2026-11-05-api-aggregation-design.md`. Preserve that design's observable cache policy except for the explicit coordination differences below. Migration implemented and ready for review. Release build passed with zero warnings and errors. All 130 unit tests and 44 integration tests passed.

## Decisions and boundaries

- Chosen approach: FusionCache with memory only. Plain IMemoryCache requires our custom coordination; FusionCache plus Redis adds deployment and distributed-test work that is unnecessary for the exercise.
- Preserve keys, normalized provider snapshots, post-cache filtering/sorting, per-provider configuration, Result failures and response schemas.
- Preserve defaults: 1,000 entry units, one unit per snapshot, 5 minutes fresh, 30 minutes maximum total age, 30 seconds failed-refresh throttle, 5-second caller cancellation budget.
- No Redis, distributed-cache registration, serializer, backplane, container requirement, eager refresh or background factory completion. Evaluate Redis for production; do not implement it now.
- Preserve user formatting and all existing uncommitted work. Do not reset, discard or commit the current memory-cache implementation to manufacture a migration history.
- Subsequent increments completed HTTP resilience and the idle statistics-worker cancellation test, as recorded in the main implementation plan.

## Responsibility changes

- `CacheRegistration`: register and own the singleton FusionCache and a dedicated underlying memory cache; translate existing policy into entry options.
- `CachedDiscoveryProvider`: call `IFusionCache.GetOrSetAsync<CachedProviderSnapshot>`, bridge Result failures, preserve cancellation and validate the returned envelope. Keep primary-constructor injection.
- Delete `ProviderMemoryCache`, including its dictionary, TaskCompletionSource values, ownership loop and completion machinery. Do not replace it with another generic cache abstraction.
- Keep `ProviderCacheKey`, `ProviderCacheOptions`, `ProviderCachePolicy` and `CachedProviderSnapshot`. Remove snapshot `RetryAfter`; FusionCache owns throttling. Preserve original FetchedAt/FreshUntil/StaleUntil and `ToResult(now)` for response metadata and the hard maximum-age check.

## Behavioral details

Configure per-call `Duration`, `FailSafeMaxDuration`, `FailSafeThrottleDuration` and `Size` from the existing policy. Enable fail-safe; disable jitter, eager refresh and timed-out-factory background completion. Keep FusionCache factory soft/hard timeouts infinite and use the existing caller budget instead of introducing competing timeout rules. Leave lock waiting cancellation-aware without a separate early lock timeout.

The factory caches successful snapshots, including valid empty and usable incomplete responses. Invalid/incomplete-empty payloads remain failures. For expected failures, retain the AppError locally for this invocation and return `context.Fail(safeErrorCode)`. When no fallback exists, translate only the corresponding FusionCacheFactoryException back to that captured AppError; do not catch arbitrary exceptions as normal provider failures or cache failed Results.

For unexpected factory faults, disable fail-safe on that factory context before rethrowing, so a warm cache cannot hide a programming bug. Cancellation propagates separately. All returned snapshots pass through the original-age check; fail-safe reactivation must never advance FetchedAt or permit data at/after StaleUntil.

Keep caller cancellation distinct from budget expiry. On budget expiry, perform only an immediate local read with stale reads allowed, validate the envelope, and return usable fallback or a typed timeout. Never start another fetch or grant a fresh budget. Caller cancellation must not return fallback.

FusionCache can unwind on cancellation before the factory finishes cleanup. Track only the factory Task started by this invocation and observe/await it before allowing the owning scoped operation to exit. Followers have no owned factory to drain. Keep this lifetime handling local; no shared task dictionary or detached scoped operations. Unexpected cleanup faults must not become successful fallback. Preserve the documented cooperative-budget limitation under lossless statistics backpressure.

Successful same-key concurrent requests reuse the cached result. Different keys remain independent. Owner cancellation releases the library's coordination so a remaining caller can execute its own factory. Unlike the current promise-sharing implementation, a cold-cache failed or faulted factory need not broadcast one outcome to every waiting caller: waiters may execute sequential factories within their own budgets. Accept and document this library behavior; do not add negative caching or rebuild promise sharing to imitate the old implementation.

## Review focus

1. Cold failed Results retain safe error codes without poisoning the cache.
2. Warm-cache unexpected exceptions still produce HTTP 500.
3. Fail-safe throttling never extends the original absolute age.
4. Cancellation never strands followers or lets scoped factory work outlive its owner.
5. Library clock behavior does not turn controlled-time tests into misleading passes.

## Increment 1: Register and characterize FusionCache

**Files:** central package manifest, Infrastructure project and cache registration, integration caching test support.

- [x] Pin `ZiggyCreatures.FusionCache` 2.9.0 centrally; retain the direct MemoryCache dependency because registration still configures it.
- [x] Add focused tests using a real FusionCache instance for memory-only operation, capacity, Result-style factory failure, successful concurrent fills, cold failed factories and cancellation. Verify relevant behavior with the pinned library and the real provider wiring.
- [x] Register a dedicated provider cache using the supported builder, with explicit memory-cache ownership/disposal. Do not impose its size limit on the application's shared framework cache. Verify no distributed cache/backplane is configured and disposing one host does not affect another.
- [x] Restore/build and run the focused integration suite. Preserve the existing provider interface and dependency lifetimes.

## Increment 2: Replace the custom cache implementation

**Files:** Infrastructure caching types and DependencyInjection; existing unit/integration caching tests.

- [x] Adapt tests first to construct the decorator with real IFusionCache, then verify the expected failures before implementing the new dependency.
- [x] Switch the scoped decorator to IFusionCache; implement the Result bridge, option mapping, original-age validation and scoped factory cleanup described above.
- [x] Remove ProviderMemoryCache and RetryAfter. Remove tests concerned only with deleted dictionary internals; retain externally observable cancellation, isolation, expiry and capacity coverage.
- [x] Retain controlled TimeProvider unit tests for envelope boundaries and budgets. For integration refresh tests, advance the envelope clock and explicitly expire the actual FusionCache entry through its public API. Do not assume advancing our TimeProvider advances FusionCache's internal clock.
- [x] Test library throttle expiry with short test-specific durations and bounded polling; do not introduce a production clock shim or fixed multi-second sleeps.
- [x] Verify: fresh repeat and filter reuse cause one transport attempt; twenty same-key successful callers cause one fetch; unrelated keys overlap; empty success is cached; cold failure stays uncached; malformed/incomplete-empty refresh preserves good data; usable incomplete/empty success replaces it.
- [x] Verify: original timestamps survive repeated fallback; stale data is rejected exactly at maximum age; follower cancellation/budget does not cancel the owner; owner cancellation permits another scope to fetch; stalled HTTP bodies cancel; owner cleanup is awaited even under delayed telemetry; faults are not hidden by warm data.
- [x] Adjust the old unexpected-cancellation-fault test: assert the faulting caller receives its exception, and a waiting caller may perform its own independent attempt. Do not assert shared exception delivery, which was specific to the deleted promises.
- [x] Run all unit and integration tests and a Release build. Review the diff for a net removal of custom coordination/throttling logic, not merely renamed helpers.

## Increment 3: Documentation and final verification

**Files:** README, existing design/implementation plan, and this plan's status.

- [x] Explain memory-only FusionCache, unchanged setup/configuration, fail-safe limits, cancellation and the cold-failure coordination difference.
- [x] Replace the plain-IMemoryCache decision in the existing documents while preserving the statistics coverage notes and unrelated work. Redis/backplane implementation tasks stay deferred.
- [x] Document that production deployment would evaluate Redis based on instance count, duplicated upstream traffic, quotas, freshness needs and operating cost. Shared L2 and backplane invalidation are separate choices; neither alone implies cluster-wide single-fetch guarantees.
- [x] Record local caches/statistics as per-process and reset on restart. No Docker or Redis prerequisite for unit/integration tests.
- [x] Run final checks below, inspect `git diff --check`, and leave changes uncommitted for review. No live smoke run is necessary for this internal cache change.

## Verification commands

Run from the repository root; stop on a failing command. A clean build and zero test failures are required; test counts may change when implementation-specific tests are removed.

```powershell
dotnet restore ApiAggregation.slnx
dotnet build ApiAggregation.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test tests/ApiAggregation.UnitTests/ApiAggregation.UnitTests.csproj -c Release --no-build --no-restore
dotnet test tests/ApiAggregation.IntegrationTests/ApiAggregation.IntegrationTests.csproj -c Release --no-build --no-restore
git diff --check
```

## Verified library references

- [FusionCache 2.9.0 package](https://www.nuget.org/packages/ZiggyCreatures.FusionCache/2.9.0).
- [Memory-only DI and custom memory cache](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/docs/DependencyInjection.md).
- [Fail-safe and Result-style factory failure](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/docs/FailSafe.md).
- [Factory options and failure/cancellation flow](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/src/ZiggyCreatures.FusionCache/FusionCache_Async.cs).
- [Cancellation can unwind before factory cleanup finishes](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/src/ZiggyCreatures.FusionCache/Internals/RunUtils.cs).
