# ADR 0001 — Use FusionCache in memory-only mode

**Status** Accepted and implemented in `ca1a627`.

## Context

Discovery queries with the same topic often differ only by filters, sort order,
or result limit. The API can reuse the same provider candidates across those
requests. It also needs to limit concurrent refreshes of a popular topic and
serve eligible cached data when a provider temporarily fails.

## Decision

Use one process-local FusionCache instance backed by a dedicated, size-limited
memory cache. Keep it in Infrastructure behind the `IDiscoveryProvider`
decorator. Cache immutable provider snapshots by source, normalized topic,
schema version, and candidate limit. Apply request filters, sorting, and the
result limit after retrieval.

FusionCache coordinates refreshes for the same key and throttles repeated
refresh attempts while stale data is usable. The decorator retains the
original fetch time and enforces the maximum age. The default policy serves
fresh results for five minutes and allows stale fallback for up to thirty
minutes from the original fetch, subject to eviction.

## Alternatives considered

| Option | Trade-off |
| --- | --- |
| Plain `IMemoryCache` | Keeps the cache local and dependency count lower, while the application owns refresh coordination and failure throttling. |
| FusionCache with Redis | Can share snapshots across replicas, with the additional cost of a Redis deployment, serialization, and distributed integration tests. |

## Consequences

- The API runs locally with the .NET SDK. Application and Domain stay independent of FusionCache.
- Each replica has its own cache and can fetch the same key independently. A restart or eviction can remove data before the fallback window ends.
- Revisit Redis as a shared second-level cache when duplicate upstream calls, quota usage, or freshness requirements justify the operational cost. Evaluate a backplane separately if replicas need coordinated local invalidation.

See [cache configuration and behavior](../../README.md#provider-caching)
and the [Infrastructure registration](../../src/ApiAggregation.Infrastructure/Caching/CacheRegistration.cs).
