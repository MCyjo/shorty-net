# Shorty.NET

A high-performance URL shortener built to show system design with measurable results: cache-aside with Redis,
collision-free code generation across many instances, rate limiting, asynchronous click analytics, and load tests
with k6 whose numbers are published together with how they were measured.

> **Status: creating links and following them works; there is no cache yet and no performance numbers.**
> Created links are stored in PostgreSQL and redirects read PostgreSQL directly, which is the baseline the cache will be
> measured against. Rate limiting, click analytics, load tests and the Azure deployment are not started. The sections
> below say which parts exist and are updated as each is built.

## API

```
POST /api/links     { "url": "https://example.com/very/long/url", "alias": "my-docs", "expiresAt": "2026-12-31T00:00:00Z" }
                    ->  201 { "code": "jSJvui7", "shortUrl": "https://host/jSJvui7", "url": "...", "expiresAt": null }
                    (alias and expiresAt are optional; 400 for an invalid URL or alias, 409 when the alias is taken)
GET  /jSJvui7       ->  302 to the original URL   (404 unknown, 410 expired)
```

Only absolute `http` and `https` URLs without embedded credentials are accepted. Redirects are temporary (302) on purpose: a 301 is cached
by browsers for good, which would hide every later click from analytics and make a link impossible to retract.

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
| OpenTelemetry (OTLP to the Aspire Dashboard, optional console exporter) | traces, logs and metrics all reach the dashboard; that the custom counters (`shorty.links.created`, `shorty.code.collisions`, `shorty.id.blocks_allocated`) show up as series was not checked, only that metrics arrive |
| `docker compose` with PostgreSQL, Redis (cache settings) and the Aspire Dashboard | starts healthy; Redis confirmed as `allkeys-lru`, 256 MB, no persistence |
| Link creation with unique 7-character codes, custom aliases, expiry, URL validation | works; the code scheme is in [ADR 0002](docs/adr/0002-short-code-generation.md) |
| Redirect (`302`) read straight from PostgreSQL | works; this is the **no-cache baseline**, not the final design |
| Tests | 62: 47 unit (permutation, allocator, validation), 13 integration on real PostgreSQL (three instances, parallel creation, alias race, redirects), 2 health. Every property above was checked by breaking the code on purpose |
| Cache-aside with Redis, rate limiting, click analytics, load tests, Azure deployment | not started |

## Running locally

Needs the .NET 10 SDK and Docker.

```bash
cp .env.example .env          # local-only password
docker compose up -d          # PostgreSQL :5433, Redis :6379, Aspire Dashboard http://localhost:18890
dotnet run --project src/Shorty.Api --launch-profile http
```

Set `ASPNETCORE_ENVIRONMENT=Development` so the local connection strings in `appsettings.Development.json` are used.

```bash
dotnet test        # the integration tests start PostgreSQL with Testcontainers, so Docker must be running
```

## Repository layout

```
src/Shorty.Core/              code generation, id allocation, URL and alias validation (no dependencies)
src/Shorty.Api/               ASP.NET Core API and PostgreSQL access
src/Shorty.ServiceDefaults/   OpenTelemetry, health endpoints, graceful shutdown
tests/                        unit, integration and health tests
docs/adr/                     decision records
docker-compose.yml            local PostgreSQL, Redis, Aspire Dashboard
```
