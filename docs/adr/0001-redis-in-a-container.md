# 0001. Redis as a container, not a managed service

**Status:** Accepted

## Context
The redirect path (`GET /{code}`) is read-heavy and has to answer in milliseconds, so hot links are served from Redis
(cache-aside, with PostgreSQL as the source of truth). In Azure, Redis can be a managed service (Azure Cache for Redis, or
its successor Azure Managed Redis) or a container the platform runs for you.

## Decision
Run Redis as a **container**: in `docker compose` locally, and as a container app in Azure Container Apps.

It is configured as a cache and nothing else: no persistence (`--save ""`, `--appendonly no`), bounded memory
(`--maxmemory 256mb`) and least-recently-used eviction (`allkeys-lru`).

## Why this is acceptable here
A cache holds only data that can be rebuilt. If the container restarts, every key is gone and the next request for each
link reads PostgreSQL and repopulates it. The service gets slower for a moment; it does not lose data. That property is
what makes a single container a legitimate choice, and it is also why the redirect path must work when Redis is
unavailable (the cache decision record will cover the fallback and the stampede protection).

## Consequences
- **Cheaper:** no managed-Redis monthly fee, which would be the largest line of the environment. The environment is meant to
  be created for a working session and destroyed afterwards.
- **No high availability, no replication, no SLA, no managed upgrades.** Nothing is persisted, so there is nothing to
  protect; availability is traded for cost.
- **Not the production answer.** In production this would be Azure Managed Redis (or Azure Cache for Redis) with replication
  and a private endpoint. The application only needs a connection string, so switching is a configuration change, not a
  code change. That statement will be tested against the managed service before the performance numbers are published.
- **Benchmarks must say which Redis they measured.** A container next to the API and a managed service across a network
  hop have different latencies, so every published result will name the setup.
