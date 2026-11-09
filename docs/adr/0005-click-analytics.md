# 0005. Click analytics through a broker, with a worker

Status: Accepted

## Context

Every redirect is a click worth counting, but a redirect must stay fast and must not fail because counting failed. Writing a row to
PostgreSQL inside the request would put the database (and its slowest moment) on the hot path and turn every redirect into a write.

## Decision

```
redirect --> in-memory queue --> publisher --> Event Hubs --> worker --> PostgreSQL (click_events)
 (never waits)   (bounded)       (batches)     (4 partitions)  (batches,   (partitioned by month)
                                                                checkpoint)
```

- **Event Hubs, not Service Bus:** clicks are a stream of small, similar facts that are summed, not commands that each need
  settling. Partitions give parallel consumers; there is no per-message routing to pay for. Locally and in tests the Azure
  emulator is used (it needs Azurite, forgets its data on restart, and has no Entra ID).
- **The redirect only writes to a bounded in-memory queue.** It never waits and never throws. When the queue is full, the click is dropped and
  counted (`shorty.clicks.dropped`); when the broker refuses a batch after its retries, the clicks are discarded and counted
  (`shorty.clicks.send_failed`). **Clicks can be lost** this way, and when the process dies with a non-empty queue. This is
  *at most once* on the producing side, chosen on purpose so that analytics cannot hurt redirects. Losing some clicks during an outage is acceptable
  for statistics; it would not be for billing.
- **The worker is at least once.** It saves its position in each partition (checkpoint, in blob storage) only *after*
  the clicks are committed to PostgreSQL. A crash therefore re-delivers clicks and never skips them. While the database is
  unavailable the worker retries and holds that partition back; the clicks wait in the broker.
- **Repeats are harmless.** Each click has an id (a version 7 GUID), the table's key is `(event_id, clicked_at)`, and the insert
  is `ON CONFLICT DO NOTHING`. `shorty.clicks.duplicates` counts the repeats.
- **No partition key when sending**: clicks are only summed, so spreading them evenly is better than keeping one code on one partition,
  where a viral link would overload that partition. The price is that the order of clicks for one link is not preserved.
- **One bad message does not block a partition:** a message that is not a valid click is skipped and counted (`shorty.clicks.malformed`).
- **First start reads from the oldest event** the broker still has. The SDK default is "only new events", which would drop every click
  published before the worker first ran.
- **PostgreSQL table partitioned by month.** Old months can be dropped as a unit instead of deleted row by row, and a query for
  recent days reads only recent partitions. The worker creates last month, this month and two ahead at startup and every six hours, before it
  reads anything. A default partition catches strays so ingestion cannot fail for lack of a partition; it must stay nearly empty,
  because PostgreSQL will not create a month's partition while the default one holds rows that belong in it.
- **Privacy:** an event holds the code, the time and the host of the referrer. No address, no user agent, no page URL.
- `GET /api/links/{code}/stats` reads PostgreSQL; the newest seconds may not be counted yet (the path is asynchronous).

## What this does not do

- No exactly-once. It is at-most-once into the broker and at-least-once out of it, made idempotent at the end.
- The stats count rows for a code with an index; a link with hundreds of millions of clicks would need pre-aggregation (hourly counts), which is not built.
- Event Hubs Basic (the cheap tier) has one consumer group and one day of retention; a worker that is down for longer loses clicks.
- Delivery lag and consumer lag are not exposed as metrics yet.
- The worker's behaviour while PostgreSQL is down (retry, hold the partition) is written but **not tested**.

## Verified

Against the real emulator and PostgreSQL: 50 redirects become 50 stored clicks with the referrer host and appear in the stats; a restarted worker
resumes from its saved position with no click read twice; a malformed message is skipped and the clicks behind it arrive; the store ignores a repeated click and keeps a click
outside the created partitions; creating partitions from two workers at once is safe; with the broker unreachable 200 redirects take a fraction of a second, dropped
clicks are counted, and failed sends are counted. Each of these was broken on purpose once (checkpoint removed, duplicate protection
removed, malformed handling removed, whole referrer URL stored) and the intended test failed. The test for dropped clicks also found a real
bug: with the channel mode first chosen (`DropWrite`), `TryWrite` reports success when it throws an item away, so nothing was ever counted as dropped.
