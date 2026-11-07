# Shorty.NET

A high-performance URL shortener built to show system design with measurable results: cache-aside with Redis,
collision-free code generation across many instances, rate limiting, asynchronous click analytics, and load tests
with k6 whose numbers are published together with how they were measured.

> **Status: skeleton only.** The API starts, exposes health probes and has a test for them. **None of the shortener's
> features exist yet** (no link creation, no redirect, no cache, no analytics), and there are no performance numbers.
> The sections below describe the intended design and are updated as each part is built.

## Intended API

```
POST /api/links        { "url": "https://example.com/very/long/url" }  ->  { "shortUrl": "https://.../X7ad91" }
GET  /X7ad91           ->  302 to the original URL
```

## Intended shape

```
Client -> load balancer -> ASP.NET Core API -> Redis (hot links) -> PostgreSQL
                                   \-> ClickRecorded -> message broker -> analytics worker -> analytics store
```

Where it runs: PostgreSQL as Azure Database for PostgreSQL Flexible Server, Redis as a container
([ADR 0001](docs/adr/0001-redis-in-a-container.md)), the API and worker on Azure Container Apps, infrastructure in Terraform. Locally everything
runs in `docker compose`.

## What exists today

| Part | State |
|---|---|
| ASP.NET Core API with `/health/live` and `/health/ready` (checks PostgreSQL and Redis) | works; 2 tests that need no Docker, and checked by hand against the real services: stopping either one gives `503` on readiness while liveness stays `200`, and the API recovers when it returns, without a restart |
| OpenTelemetry (OTLP to the Aspire Dashboard, optional console exporter) | **traces verified** in the dashboard; logs and metrics not yet verified |
| `docker compose` with PostgreSQL, Redis (cache settings) and the Aspire Dashboard | starts healthy; Redis confirmed as `allkeys-lru`, 256 MB, no persistence |
| Link creation, code generation, redirect, cache, rate limiting, analytics, load tests, Azure deployment | not started |

## Running locally

Needs the .NET 10 SDK and Docker.

```bash
cp .env.example .env          # local-only password
docker compose up -d          # PostgreSQL :5433, Redis :6379, Aspire Dashboard http://localhost:18890
dotnet run --project src/Shorty.Api --launch-profile http
```

Set `ASPNETCORE_ENVIRONMENT=Development` so the local connection strings in `appsettings.Development.json` are used.

```bash
dotnet test        # the health tests need no Docker
```

## Repository layout

```
src/Shorty.Api/               ASP.NET Core API
src/Shorty.ServiceDefaults/   OpenTelemetry, health endpoints, graceful shutdown
tests/Shorty.Api.Tests/       tests
docs/adr/                     decision records
docker-compose.yml            local PostgreSQL, Redis, Aspire Dashboard
```
