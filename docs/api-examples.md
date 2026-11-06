# API examples

Start the API with `dotnet run --project src/ApiAggregation.Api`. The local
launch profile uses `http://localhost:5080` in Development. Swagger UI is at
`/swagger/index.html`, and the OpenAPI document is at `/swagger/v1/swagger.json`.
The [HTTP request file](../src/ApiAggregation.Api/ApiAggregation.Api.http) includes
requests you can run from an editor with an HTTP client.
Create a development token using the [local setup instructions](../README.md#local-authentication).
Discovery and statistics return HTTP 401 for missing or invalid bearer credentials.

```powershell
$api = 'http://localhost:5080'
$headers = @{ Authorization = "Bearer $token" }
Invoke-RestMethod "$api/health/live"
Invoke-RestMethod "$api/api/discovery?topic=dotnet&limit=10" -Headers $headers
Invoke-RestMethod "$api/api/discovery?topic=dotnet&sources=github&type=repository&from=2025-01-01T00:00:00Z&sortBy=title&sortDirection=asc" -Headers $headers
Invoke-RestMethod "$api/api/statistics" -Headers $headers
```

## Discovery

Omit `sources` to query all enabled providers. Supply comma-separated source
names to select specific providers. Date filters are inclusive and require an
explicit offset. Encode a positive offset with `%2B` in a query string.

Each selected provider contributes candidates from its first page. Filtering,
sorting, and the response limit apply to those candidates. Counts describe
that bounded set. Here is a sample HTTP 200 response with one stale provider.

```json
{
  "topic": "dotnet",
  "items": [
    {
      "id": "42",
      "source": "github",
      "type": "repository",
      "title": "example",
      "url": "https://github.com/example/repository",
      "description": null,
      "publishedAt": "2026-01-02T03:04:05Z",
      "tags": ["dotnet"]
    }
  ],
  "matchedCount": 1,
  "returnedCount": 1,
  "isPartial": true,
  "providers": [
    {
      "source": "github",
      "status": "stale",
      "fetchedAt": "2026-11-06T10:00:00Z",
      "isIncomplete": false,
      "errorCode": null
    }
  ]
}
```

Provider status is `fresh`, `stale`, or `unavailable`. Stale results retain
the time of their original fetch. Empty successful responses use HTTP 200.
When all selected providers are unavailable, the API returns HTTP 503 with
a report for each provider.

```json
{
  "title": "Service unavailable",
  "status": 503,
  "code": "discovery.providers_unavailable",
  "providers": [
    {
      "source": "github",
      "status": "unavailable",
      "fetchedAt": null,
      "isIncomplete": false,
      "errorCode": "rate_limited"
    }
  ]
}
```

Invalid input returns HTTP 400. For example, `limit=120` produces the following
problem details fields.

```json
{
  "title": "Invalid request",
  "status": 400,
  "code": "validation.failed",
  "errors": {
    "limit": ["Must be between 1 and 90."]
  },
  "validationErrors": [
    {
      "field": "limit",
      "code": "validation.outOfRange",
      "message": "Must be between 1 and 90."
    }
  ]
}
```

The error responses use `application/problem+json`. Validation issues can
carry `validation.required`, `validation.invalid`, or `validation.outOfRange`.
An unexpected failure returns HTTP 500 with a `traceId` for log correlation.

| Provider error code | Meaning |
| --- | --- |
| `upstream_error` | Upstream HTTP or transport failure |
| `timeout` | Attempt timeout or caller budget expired |
| `rate_limited` | Upstream quota response or active cooldown |
| `unauthorized` | Upstream authentication or authorization failure |
| `invalid_payload` | Invalid response shape or no usable records in a nonempty payload |
| `response_too_large` | Response exceeded the body size limit |
| `circuit_open` | Circuit breaker rejected the request |
| `busy` | Provider concurrency limit was reached |
| `cache_expired` | Snapshot reached its maximum usable age |

## Statistics and liveness

Statistics are per process and reset on restart. Each retry counts as a
separate outbound attempt. Latency runs from transport entry until headers,
failure, or cancellation. Snapshots publish periodically, so the newest
observations may appear on the next refresh. Empty buckets report a null mean.
See the [statistics response example](../README.md#http-statistics-endpoint).

Liveness returns HTTP 200 with `{"status":"healthy"}`.
