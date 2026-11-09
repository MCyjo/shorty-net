# Load tests

**No results are published yet.** The harness works (a 200 req/s smoke run and several short discovery runs behaved as expected), but
the full comparison was stopped before it produced data. Nothing here is a claim about performance.

## What it measures

`k6` against the API in an isolated Docker network, with PostgreSQL and Redis beside it (`docker-compose.yml`):

- **Open model** (`constant-arrival-rate`): requests arrive at a fixed rate whether or not the server keeps up. A closed model (fixed number of looping users) slows down
  with the server and hides the problem.
- A 15 s warm-up that is not counted, then the measured phase. Skewed popularity: 46% of requests go to the top 10% of 5,000 links.
- `redirects: 0`: the redirect itself is measured, not the page it points to.
- A run with no requests fails instead of printing zeros; dropped iterations are reported (they mean the generator, not the server, ran out of capacity).
- After each run: average CPU of every container, and how often the API container was throttled by its CPU limit.

The comparison to run (`matrix-cache-modes.sh`): no cache, memory only, memory plus Redis, at 3,000, 6,000 and 9,000 requests per second, twice each.

## Running it

```bash
dotnet publish src/Shorty.Api -c Release -o artifacts/api
docker build --target prebuilt --build-arg PROJECT=Shorty.Api --build-arg PUBLISH_DIR=artifacts/api -t shorty-api:local .
loadtest/run.sh smoke redirect 200 CACHE_MODE=Hybrid DURATION_S=10 WARMUP_S=5 LINKS=500
```

(`--target prebuilt` publishes on the host because restoring packages inside the build fails behind a proxy that inspects TLS. Without such a proxy, drop it and the publish step.)

## What setting it up showed

- A one-core API container is **throttled** by its CPU quota in bursts (206 of 757 scheduling periods in one run), which shows up as huge latency with low average CPU.
  Those runs measured the limit, not the code, and were discarded; the stack now gives the API two cores.
- **k6 itself saturates**: above about 18,000 requests per second on this machine it used its whole CPU allowance before the API did, and at 30,000 it collapsed.
  Numbers above what the generator can drive are about the generator.
- `discardResponseBodies` also discards the bodies in `setup()`; the seeding step needs `responseType: 'text'`.
- Everything runs on one laptop, so any number from here is a local number, not a capacity of the design. The meaningful run is against Azure, from a separate machine.

Short discovery runs (20 s, **not** a result, only the reason the rates above were chosen): without a cache, 6,000 req/s was held with a p95 of about 14 ms and 9,000 was not;
with the memory plus Redis cache both were held with a p95 around 1 ms.
