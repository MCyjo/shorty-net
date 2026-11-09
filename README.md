# Shorty.NET

A high-performance URL shortener built to show system design with measurable results: cache-aside with Redis,
collision-free code generation across many instances, rate limiting, asynchronous click analytics, and load tests
with k6 whose numbers are published together with how they were measured.

> **Status: creating links, following them, a two-layer cache, rate limiting and click analytics work and are tested locally.
> There are no performance numbers, and nothing has been deployed to Azure.**
> Redirects go through memory, then Redis, then PostgreSQL. Whether that is actually faster, and by how much, is not
> known: the load-test harness is written but its comparison run never completed, so nothing here claims a speed-up. The
> Azure infrastructure is written and validated offline but was never applied. The table below says which parts exist and how
> each was checked. How the pieces fit together is in [docs/architecture.md](docs/architecture.md).

## API

```
POST /api/links     { "url": "https://example.com/very/long/url", "alias": "my-docs", "expiresAt": "2026-12-31T00:00:00Z" }
                    ->  201 { "code": "jSJvui7", "shortUrl": "https://host/jSJvui7", "url": "...", "expiresAt": null }
                    (alias and expiresAt are optional; 400 for an invalid URL or alias, 409 when the alias is taken)
GET  /jSJvui7       ->  302 to the original URL   (404 unknown, 410 expired)
GET  /api/links/jSJvui7/stats?days=7
                    ->  200 { "code": "jSJvui7", "totalClicks": 42, "days": 7, "daily": [ { "date": "2026-11-09", "clicks": 42 } ] }
                    (days 1 to 90; 404 for an unknown code; the newest seconds may not be counted yet, see ADR 0005)

Any of them can answer 429 with a Retry-After header when a client exceeds its limit (ADR 0004).
GET  /health/live, /health/ready    liveness (no dependencies) and readiness (PostgreSQL and Redis)
```

Only absolute `http` and `https` URLs without embedded credentials are accepted. Redirects are temporary (302) on purpose: a 301 is cached
by browsers for good, which would hide every later click from analytics and make a link impossible to retract.

## Shape

```
                       ┌──────────── Shorty.Api ─────────────┐
Client ─► rate limit ─►│ redirect ─► memory ─► Redis ─► PostgreSQL (links)
                       │      └─► in-memory queue                │
                       └──────────────┬──────────────────────────┘
                                      ▼
                                 Event Hubs ─► Shorty.Analytics ─► PostgreSQL (click_events, by month)
                                                                        ▲
                                  GET /api/links/{code}/stats ──────────┘
```

[docs/architecture.md](docs/architecture.md) walks through each path, what happens when a part fails, and where the data lives.

Where it is meant to run: PostgreSQL as Azure Database for PostgreSQL Flexible Server (private access), Redis as a container
([ADR 0001](docs/adr/0001-redis-in-a-container.md)), the API and the worker on Azure Container Apps, infrastructure in Terraform
([ADR 0006](docs/adr/0006-azure-deployment.md); **not applied yet**). Locally everything runs in `docker compose`.

## What exists today

| Part | State |
|---|---|
| ASP.NET Core API with `/health/live` and `/health/ready` (checks PostgreSQL and Redis) | works; 2 tests that need no Docker, and checked by hand against the real services: stopping either one gives `503` on readiness while liveness stays `200`, and the API recovers when it returns, without a restart |
| OpenTelemetry (OTLP to the Aspire Dashboard, optional console exporter) | traces, logs and metrics all reach the dashboard; that the custom counters (`shorty.links.created`, `shorty.code.collisions`, `shorty.id.blocks_allocated`) show up as series was not checked, only that metrics arrive |
| `docker compose` with PostgreSQL, Redis (cache settings) and the Aspire Dashboard | starts healthy; Redis confirmed as `allkeys-lru`, 256 MB, no persistence |
| Link creation with unique 7-character codes, custom aliases, expiry, URL validation | works; the code scheme is in [ADR 0002](docs/adr/0002-short-code-generation.md) |
| Redirect (`302`) with cache-aside: memory (HybridCache) → Redis → PostgreSQL | works; TTL with jitter, negative caching, invalidation on create, one database read for concurrent requests on a cold key, Redis outage and slowness do not fail or stall redirects. Design and limits in [ADR 0003](docs/adr/0003-cache-aside.md). Not measured yet |
| Rate limiting per client: token bucket for creating links (20/min), sliding window for redirects (600/min), fixed window selectable | works; `429` with `Retry-After`, applied before the cache, health endpoints exempt. Limits are **per instance**, so N instances allow up to N times the limit; the numbers are starting points; `X-Forwarded-For` is ignored unless trusted by configuration. Why each algorithm, and what this does not do, in [ADR 0004](docs/adr/0004-rate-limiting.md) |
| Click analytics: redirect → in-memory queue → Event Hubs → `Shorty.Analytics` worker → PostgreSQL (partitioned by month), `GET /api/links/{code}/stats` | works against the Azure Event Hubs **emulator**; not run against real Event Hubs. **Clicks can be lost** (queue full, broker down: counted, never silent); the worker is at-least-once and repeats are ignored; no IP or user agent stored. Design and limits in [ADR 0005](docs/adr/0005-click-analytics.md). The worker's behaviour while PostgreSQL is down is not tested |
| Tests | 95: 54 unit (permutation, allocator, validation, which kind of broker setting is given), 9 on the API (health without Docker; the three limiting algorithms measured with real time), 32 integration on real PostgreSQL, Redis and the Event Hubs emulator (three instances, parallel creation, alias race, redirects, the three cache modes, 429, clicks end to end, worker restart, malformed message, broker down). The code-generation, cache, rate-limiting and analytics properties were checked by breaking the code on purpose. Not covered by any test: signing in with a managed identity, the worker while PostgreSQL is down, the Terraform |
| Load tests (k6, open model, in an isolated compose stack): harness in [`loadtest/`](loadtest/README.md) | the harness works; **no results are published**: the full comparison run was stopped (low memory on the machine) before it produced data, so there is no throughput or latency number in this repository yet |
| Azure deployment: Container Apps, private PostgreSQL, Event Hubs and storage without keys, ACR, Terraform, manual deploy workflow with OIDC | **written, never applied**: validated offline only. Design, cost estimate and everything unverified in [ADR 0006](docs/adr/0006-azure-deployment.md) |

## Running locally

Needs the .NET 10 SDK and Docker.

```bash
cp .env.example .env          # local-only password
docker compose up -d          # PostgreSQL :5433, Redis :6379, Aspire Dashboard http://localhost:18890
dotnet run --project src/Shorty.Api --launch-profile http
```

Set `ASPNETCORE_ENVIRONMENT=Development` so the local connection strings in `appsettings.Development.json` are used.

**Click analytics** needs the Azure Event Hubs emulator, which only starts if you accept Microsoft's
[licence terms](https://github.com/Azure/azure-event-hubs-emulator-installer/blob/main/EMULATOR_EULA.md): read them, then set
`ACCEPT_EULA=Y` in your `.env`. The integration tests start the emulator themselves and also need that acceptance to be true for you.
Without `ConnectionStrings:EventHubs` the API runs normally and records no clicks.

```bash
export ConnectionStrings__EventHubs="Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
export ConnectionStrings__Postgres="<the same string as in appsettings.Development.json>"
export ConnectionStrings__Checkpoints="UseDevelopmentStorage=true"
dotnet run --project src/Shorty.Analytics     # the worker; the API publishes when ConnectionStrings:EventHubs is set
```

The emulator forgets its data when it restarts, but the worker's saved positions (in Azurite) do not. After restarting one, recreate both:
`docker compose rm -sf eventhubs azurite && docker compose up -d azurite eventhubs`, or the worker reports an invalid offset.

```bash
dotnet test        # the integration tests start PostgreSQL with Testcontainers, so Docker must be running
```

## Repository layout

```
src/Shorty.Core/              code generation, id allocation, URL and alias validation (no dependencies)
src/Shorty.Api/               ASP.NET Core API and PostgreSQL access
src/Shorty.Analytics/         worker: Event Hubs to PostgreSQL click storage
infra/                        Terraform (Azure) and the scripts that prepare state and GitHub sign-in
loadtest/                     k6 scripts and an isolated compose stack for load tests
src/Shorty.ServiceDefaults/   OpenTelemetry, health endpoints, graceful shutdown
tests/                        unit, integration and health tests
docs/adr/                     decision records
docker-compose.yml            local PostgreSQL, Redis, Aspire Dashboard, Event Hubs emulator
```
