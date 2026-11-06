# Adding a provider

The discovery service uses the provider interface and its descriptor to select
and report sources. Add a provider in Infrastructure by following the existing
[DEV client](../src/ApiAggregation.Infrastructure/Providers/DevTo/DevToClient.cs).

1. Define a stable lowercase source name and a supported `ContentType` in the
   client's `ProviderDescriptor`. Keep the provider's response types private to
   Infrastructure.
2. Derive the typed client from
   [ExternalContentClient](../src/ApiAggregation.Infrastructure/Providers/ExternalContentClient.cs).
   Implement `BuildRequestPath` and `ReadContentAsync`. Use `CandidateLimit`
   for the first page request and `ReadAndMapAsync` for bounded JSON reading.
   Map usable records into `DiscoveryItem` values with stable IDs, HTTP URLs,
   and UTC publication dates.
3. Add a `ProviderOptions` subclass with an HTTPS origin and a section under
   `Providers` in `appsettings.json`. Bind and validate it in
   [DependencyInjection](../src/ApiAggregation.Infrastructure/DependencyInjection.cs).
   Check its enable switch before registering the client. If the provider needs
   credentials, use environment variables or deployment secrets.
4. Call `AddProviderClient<TClient, TOptions, TRateLimitPolicy>` with the
   descriptor and accepted media type. Use `DefaultRateLimitPolicy` for standard
   HTTP 429 handling. A provider with custom quota signals can supply its own
   policy and use the same classification in its client. The registration
   helper applies the Polly pipeline and metrics, then wraps the client in the cache.
5. Add unit tests for request construction and payload mapping. Use a stubbed
   HTTP transport in integration tests to check that selecting the source by
   name reaches the registered client. Add an opt-in live smoke test for
   connectivity and payload compatibility.

Once registered and enabled, the source is available through the existing
`sources` query parameter and appears in `/api/statistics`.
