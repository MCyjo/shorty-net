# Architecture

How Shorty.NET fits together, as built. Decisions and their reasons are in the [ADRs](adr/README.md); this document is the map. Where
something is only expected from the design and was not tested, it says so.

## Components

| Component | What it is | Owns |
|---|---|---|
| `Shorty.Api` | ASP.NET Core minimal API: creates links, redirects, reports stats, health endpoints | the redirect and create paths, the cache, the rate limits, the click queue and publisher |
| `Shorty.Analytics` | Worker (a small web app, so it has health endpoints): Event Hubs to PostgreSQL | reading clicks, storing them, the monthly partitions, its saved positions |
| `Shorty.Core` | Library without dependencies: code generation, id blocks, URL and alias validation, metrics, `ClickEvent` | the rules that must be right and are tested without Docker |
| `Shorty.ServiceDefaults` | OpenTelemetry, health endpoints, graceful shutdown | what both applications share |
| PostgreSQL | the only durable store | `links`, `id_counter`, `click_events` |
| Redis | a cache that may be lost ([ADR 0001](adr/0001-redis-in-a-container.md)) | `link:{code}` entries, shared by all API instances |
| Event Hubs | the broker for clicks, 4 partitions | clicks in transit |
| Blob storage | the worker's saved position in each partition | checkpoints |

## Creating a link

```
POST /api/links
  1. rate limit "create" (token bucket, per client)            refused: 429 + Retry-After, nothing else runs
  2. validate URL (http/https, no credentials, max 2048), alias, expiry          invalid: 400
  3. alias given?  yes: insert it; the unique index on code decides                taken: 409
                   no:  take the next id from this instance's block
                        (one UPDATE ... RETURNING per 1,000 links, shared by all instances)
                        -> keyed permutation of the id -> 7 characters of Base62
                        -> insert; if the code exists anyway (should never happen): count it, take another, up to 5 times
  4. remove "link:{code}" from the cache (a remembered "does not exist" must not outlive the new link)
  5. 201 { code, shortUrl, url, expiresAt }
```

Different instances reserve different blocks, so they never produce the same id and never coordinate per link; the unique index is the
final judge and `shorty.code.collisions` has to stay 0. The scheme and its limits (the seed must never change, obfuscation is not
encryption) are in [ADR 0002](adr/0002-short-code-generation.md).

## Following a link

```
GET /{code}
  1. rate limit "redirect" (sliding window, per client)                              refused: 429
  2. look up the code, first layer that has it wins:
       memory of this instance (30 s)  ->  Redis (1 h, +-10% jitter)  ->  PostgreSQL
     concurrent requests for a code that is nowhere share ONE database read
     a code that does not exist is remembered for 30 s (negative caching)
  3. unknown: 404.  expired (checked on every request against the cached value): 410
  4. 302 to the original URL, and put a click on the in-memory queue (never waits, never throws)
```

Redirects are temporary (302) so that browsers do not cache them, which would hide later clicks and make a link impossible to retract.
Redis is an optimisation: its commands fail at once while it is disconnected and time out after 250 ms, so a missing or slow Redis costs a
database read, not a slow or failed redirect. See [ADR 0003](adr/0003-cache-aside.md). Other instances can serve an older answer for up to the 30 s of
their memory cache after a change.

## Counting clicks

```
redirect -> bounded in-memory queue -> publisher (batches of up to 200, or 250 ms) -> Event Hubs (no partition key)
         -> Shorty.Analytics: one buffer per partition -> INSERT ... ON CONFLICT DO NOTHING (one statement per batch)
         -> only then: save the position of that partition (checkpoint)
```

- **Into the broker: at most once.** A full queue drops a click (`shorty.clicks.dropped`); a batch the broker refuses after its retries is discarded
  (`shorty.clicks.send_failed`); a crash loses what is queued. Chosen so that counting can never hurt a redirect.
- **Out of the broker: at least once, made idempotent.** The position is saved after the commit, so a crash re-delivers; the table's key `(event_id, clicked_at)` makes a repeat a no-op
  (`shorty.clicks.duplicates`).
- A message that is not a valid click is skipped and counted (`shorty.clicks.malformed`) so that it cannot block a partition.
- No address and no user agent is stored: the code, the time, and the host of the referrer.

`GET /api/links/{code}/stats` reads `click_events` directly, so the newest seconds may be missing. Details and limits: [ADR 0005](adr/0005-click-analytics.md).

## What happens when something fails

| Failure | Effect | Tested? |
|---|---|---|
| Redis stopped | redirects keep working from PostgreSQL, quickly; `/health/ready` answers 503; invalidation on create is skipped with a throttled warning | yes |
| Redis slow (accepts connections, does not answer) | a redirect waits at most about 250 ms for it, then reads PostgreSQL | yes |
| Event Hubs unreachable | redirects unaffected and fast; clicks are counted as dropped or failed | yes |
| Analytics worker stopped | clicks wait in the broker (one day on Basic); a restarted worker resumes from its saved position without re-reading | yes |
| Malformed message in the stream | skipped and counted; the clicks behind it still arrive | yes |
| Same alias requested at once on two instances | one 201, the rest 409, none 5xx | yes |
| Three instances creating links from a cold start | no duplicate code, 0 collisions, migrations run once (advisory lock) | yes |
| Client over its limit | 429 with `Retry-After`; the refused request reaches neither cache nor database | yes |
| PostgreSQL down | links already in a cache keep redirecting until their entries expire; uncached lookups and creating links fail | **no**, expected from the design |
| PostgreSQL down while the worker runs | the worker retries and holds that partition; clicks wait in the broker | **no**, written but untested |
| An API instance dies | the clicks still queued in that instance are lost | **no**, follows from the at-most-once choice |
| Too many instances for the database | the server answers "too many clients" (53300); the pool is capped per instance to keep the sum under `max_connections` | reproduced once in CI, then fixed |

## Data

- **`links`**: `id`, `code` (unique index `ix_links_code`), `url`, `is_custom` (an alias), `created_at`, `expires_at`. **`id_counter`**: one row per kind of id (`links`), holding `next_id`; blocks of ids are taken from it.
- **`click_events`**: `event_id`, `code`, `clicked_at`, `referrer_host`; primary key `(event_id, clicked_at)`; partitioned by month, with the worker creating last month, this month and two ahead,
  and a default partition as a safety net that must stay nearly empty; index on `(code, clicked_at)`.
- **Redis**: `link:{code}` for found links and for "does not exist". Losing all of it only costs reads.
- **Blob storage**: one container with the worker's position for each partition.
- Schema changes are SQL files embedded in the API (`Persistence/Migrations`), applied under an advisory lock; on startup in development and, for now, in the demo deployment.

## Observability

OpenTelemetry in both applications: traces, metrics and logs go to an OTLP endpoint when one is configured (locally the Aspire Dashboard), and optionally to the console. The
application's own counters, under the meter `Shorty`:

`shorty.links.created`, `shorty.code.collisions`, `shorty.id.blocks_allocated`, `shorty.redirects` (by outcome), `shorty.cache.misses`, `shorty.ratelimit.rejected` (by policy),
`shorty.clicks.recorded`, `.dropped`, `.sent`, `.send_failed`, `.stored`, `.duplicates`, `.malformed`.

The tests read these counters to check behaviour. That they appear as series in the dashboard has not been checked; in Azure no OTLP endpoint is set yet, so only logs would arrive.

## Deployment

Locally: `docker compose` (PostgreSQL, Redis, Aspire Dashboard, the Event Hubs emulator with Azurite). For Azure, Terraform describes Container Apps, a private PostgreSQL, Event Hubs and storage without
access keys, a registry, and per-application managed identities; GitHub Actions deploys by hand through OIDC. **None of it has been applied**; what is untested and why is listed in
[ADR 0006](adr/0006-azure-deployment.md). Load tests have a harness but no published result ([loadtest/README.md](../loadtest/README.md)).

## Limits to keep in mind

- Rate limits are per instance, so N instances allow up to N times the limit ([ADR 0004](adr/0004-rate-limiting.md)).
- Clicks can be lost by design; the statistics are not exact.
- There are no performance numbers; whether the cache layers pay for themselves is not measured.
- The stats query counts rows; very busy links would need pre-aggregation.
