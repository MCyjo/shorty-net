# 0004. Rate limiting: which algorithm where, and what it does not do

Status: Accepted

## Context

Two things need protecting. Creating links writes to PostgreSQL and uses up codes. Following links is public and cheap, but one
client can hammer it, or scan the code space looking for links that exist (code enumeration). Neither should be able to take
the service away from everyone else.

## Decision

The limit is applied before anything else in the request (before the cache, the database or validation), per client, and
refused requests get `429` with `Retry-After`. Health endpoints are never limited, so an orchestrator probing them is not turned away.

| Algorithm | How it counts | Allows | Cost | Used for |
|---|---|---|---|---|
| Fixed window | requests per consecutive window | up to **2x the limit** around a window boundary | one counter | not used by default |
| Sliding window | requests per window that slides in segments | the limit, with no boundary burst | one counter per segment | **redirects** |
| Token bucket | bucket refilled at a steady rate | a burst up to the bucket size, then the steady rate | one counter and a timestamp | **creating links** |

- **Creating links, token bucket (20 per minute):** a person or a script legitimately creates several links at once, then
  nothing for a while. A bucket accepts that burst and still holds a sustained flood to the refill rate (one token every 3 s).
- **Redirects, sliding window (600 per minute):** a steady stream is normal here, and a limit that can be doubled by timing
  the requests around a boundary protects the cache path and the database less than it appears to. The sliding window removes
  that cost for a little extra memory.
- **Fixed window** is implemented and selectable, because it is the cheapest and a fair choice where the boundary burst does not matter.
  It is kept in the tests as the measured counter-example.
- **Who is a client:** the remote address; an IPv6 address is reduced to its /64, because one subscriber owns the whole range
  and could otherwise rotate through addresses for free.
- **`X-Forwarded-For` is ignored unless `RateLimiting:TrustForwardedHeaders` is on.** Behind a proxy every request seems to come
  from the proxy, so the setting is needed there; but if the application can be reached directly, a client can write any address in the
  header and never be limited. Only the entry added by the nearest proxy is used.
- The numbers are starting points, not measured values. They are to be reviewed against the load tests.

## What it does not do

- **The limits are per instance.** The built-in limiter keeps its counters in the memory of each instance. With N instances
  and requests spread evenly, a client can make up to N times the limit. That is acceptable for protection against abuse and
  wrong for a strict quota.
- A shared limit needs shared state. Counting in Redis adds a Redis call to every request and makes Redis part of the request path,
  which [ADR 0003](0003-cache-aside.md) deliberately avoided; a limit at the edge (Azure Front Door with WAF rate rules) keeps
  it off the application altogether. Neither is built; the second is the likely answer for the Azure deployment.
- It does not stop a distributed attack from many addresses; it limits each address.
- Limits are keyed by address, so many users behind one NAT share an allowance.

## Verified

Tests with the real limiters and real time show the boundary burst of the fixed window (9 + 10 accepted in half a second around
a boundary, for a limit of 10 per 2 s), its absence in the sliding window (at most 2 of 10 in the same position), and the bucket's burst and
steady rate. HTTP tests cover `429` with `Retry-After`, nothing created by a refused request, isolation between clients,
spoofed headers ignored by default, trusted headers separating clients, and health endpoints unlimited. Five of these were
broken on purpose once to confirm the test fails (redirect policy removed, headers always trusted, IPv6 not grouped, no `Retry-After`,
sliding window reduced to one segment). Not mutation-checked: that health endpoints stay unlimited. Not measured: the cost of the limiter per request.
