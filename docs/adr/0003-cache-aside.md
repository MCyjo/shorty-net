# 0003. Cache-aside with memory and Redis layers

Status: Accepted

## Context

A redirect is one key lookup that is read far more often than links are created. Reading PostgreSQL for each one makes
the database the bottleneck. Redis is a cache here, not a source of truth ([ADR 0001](0001-redis-in-a-container.md)).

## Decision

- **Cache-aside** through `HybridCache`: each instance's memory (L1), then Redis shared by all instances (L2), then
  PostgreSQL. Concurrent requests for a key that is not cached share one database read.
- **TTL** of one hour in Redis with ±10 % jitter, so entries created together do not expire together; 30 s in memory.
- **Negative caching**: an unknown code is remembered for 30 s, so scanning for codes costs one query per code, not one
  per request. It is short so a link created under that name appears quickly.
- **Invalidation on create**: the entry for the new code is removed in Redis and in the creating instance's memory.
- **Expiry is checked on every request** against the cached value, so a cached link that has expired answers `410`.
- **Redis is optional**: fail-fast commands while disconnected, a 250 ms command timeout, one connection attempt,
  connection made at startup. Redis down or slow means redirects come from PostgreSQL, and `/health/ready` reports it.

## Consequences

- Other instances do not learn about an invalidation at once: a stale answer (for example a remembered "not found" for a
  name that was just created elsewhere) can last up to `LocalTtl`, 30 s.
- With Redis down every redirect reads PostgreSQL, so the database must be able to carry that load, or the service needs
  its own protection (rate limiting, next phase).
- The 250 ms timeout may mark a busy Redis as unhealthy on the readiness check.
- Not measured yet: the benefit of each layer is to be shown by the load tests, not assumed.

## Verified

Integration tests on real Redis and PostgreSQL cover hits, a second instance, 300 concurrent requests on a cold key (one
query), negative caching, expiry from the cache, Redis stopped and Redis paused. Each of these was broken on purpose
once to confirm the test fails. Not verified: other failure modes of the L2 layer beyond those two.
