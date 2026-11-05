# Developer Discovery API

An ASP.NET Core API aggregation assignment, built in small, verified increments.
The initial scaffold provides controller-based liveness; discovery, providers,
statistics, caching, and authentication are not implemented yet.

## Local setup

Install the .NET SDK specified by `global.json` (10.0.301 or a later 10.0.3xx patch).

```powershell
dotnet restore ApiAggregation.slnx
dotnet build ApiAggregation.slnx -c Release --no-restore
dotnet run --project src/ApiAggregation.Api
Invoke-RestMethod http://localhost:5080/health/live
```

The liveness endpoint returns HTTP 200 with `{"status":"healthy"}`.
No external services or credentials are needed for this initial scaffold.

## Structure

- `Domain`: framework-independent models and shared Result primitives.
- `Application`: use cases and contracts, grouped into feature folders.
- `Infrastructure`: provider integrations, caching, and statistics implementations.
- `Api`: MVC controllers, HTTP contracts, and composition.
- `UnitTests`, `IntegrationTests`, `SmokeTests`: separate test projects. They start
  empty; meaningful tests arrive alongside the features they verify.

See the [design](docs/superpowers/specs/2026-11-05-api-aggregation-design.md) and
[implementation plan](docs/superpowers/plans/2026-11-05-api-aggregation.md).
