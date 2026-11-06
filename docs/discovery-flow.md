# Discovery request flow

`GET /api/discovery` validates the bearer token and query before starting
provider work. The provider branch runs concurrently for every selected source.

```mermaid
flowchart TD
    Request["GET /api/discovery"] --> Auth{"Valid bearer token?"}
    Auth -->|No| Unauthorized["401 Unauthorized"]
    Auth -->|Yes| Query{"Valid discovery query?"}
    Query -->|No| BadRequest["400 with field errors"]
    Query -->|Yes| FanOut["Start selected providers concurrently"]

    subgraph Provider["Per selected provider"]
        Cache["Check FusionCache for provider and topic"] -->|Fresh hit or refresh throttled| Data["Usable snapshot"]
        Cache -->|Fetch or refresh needed| Fetch["Fetch first page with Polly and provider cooldown"]
        Fetch -->|Usable response| Save["Map and cache snapshot"]
        Save --> Data
        Fetch -->|Expected failure or fetch budget expires| Fallback{"Usable cached snapshot?"}
        Fallback -->|Yes| Data
        Fallback -->|No| Unavailable["Provider unavailable"]
    end

    FanOut --> Cache
    Data --> Join{"Any usable provider result?"}
    Unavailable --> Join
    Join -->|Yes| Merge["Deduplicate, filter, sort, and apply limit"]
    Merge --> Ok["200 with results and provider reports"]
    Join -->|No| Outage["503 with provider reports"]
```

A successful provider can return an empty result. Stale, incomplete, or
unavailable provider results make a successful aggregate partial. Unexpected
exceptions go through the API exception handler and return a sanitized
`500` with a trace ID. A caller cancellation stops the request.

Each outbound transport attempt, including a retry, records an observation
in the statistics channel. The single reader publishes immutable snapshots
for `GET /api/statistics` and the latency monitor. Cache hits and requests
rejected before reaching transport add no observations.

See the [API examples](api-examples.md) and the
[cache decision](adr/0001-fusioncache-memory-only.md) for the response contract
and cache policy.
