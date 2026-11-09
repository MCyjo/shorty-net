#!/usr/bin/env bash
# Runs one load-test configuration against a fresh stack and prints one result row.
#   loadtest/run.sh <name> <scenario> <rate> [VAR=value ...]
# Variables for the stack: CACHE_MODE=Hybrid|MemoryOnly|None, RATE_LIMITING=true|false, REDIRECT_LIMIT=<n>
# Variables for k6: DURATION_S, WARMUP_S, LINKS.
# Needs the images shorty-api:local and grafana/k6: see loadtest/README.md.
set -euo pipefail
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"

NAME=$1; SCENARIO=$2; RATE=$3; shift 3
for kv in "$@"; do export "${kv?}"; done
DURATION_S=${DURATION_S:-60}; WARMUP_S=${WARMUP_S:-15}
COMPOSE="docker compose -f docker-compose.yml"

$COMPOSE down -v --remove-orphans >/dev/null 2>&1 || true
$COMPOSE up -d postgres redis >/dev/null
for _ in $(seq 1 60); do $COMPOSE exec -T postgres pg_isready -U shorty -d shorty >/dev/null 2>&1 && break; sleep 1; done
$COMPOSE up -d api >/dev/null
for _ in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:18080/health/ready)" = 200 ] && break; sleep 1; done
[ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:18080/health/ready)" = 200 ] || { echo "API did not become ready"; exit 1; }

# CPU of each container during the measured phase: it shows which part runs out first.
STATS=$(mktemp)
( sleep $((WARMUP_S + 8)); for _ in 1 2 3 4; do
    docker stats --no-stream --format '{{.Name}} {{.CPUPerc}}' $(docker ps --filter name=shortyload- --format '{{.Names}}') 2>/dev/null >> "$STATS" || true
    sleep 5; done ) &
SAMPLER=$!

OUT=$($COMPOSE run --rm k6 run /loadtest/k6/load.js --quiet \
  -e NAME="$NAME" -e SCENARIO="$SCENARIO" -e RATE="$RATE" -e DURATION_S="$DURATION_S" -e WARMUP_S="$WARMUP_S" -e LINKS="${LINKS:-5000}" 2>&1) || true
wait $SAMPLER || true

if echo "$OUT" | grep -q "^FAILED" || ! echo "$OUT" | grep -E "^RESULT" | sed 's/^RESULT //'; then echo "$OUT" | tail -20; exit 1; fi
# Average CPU per container over the samples (100% = one full core; limits: API 2 cores, Postgres 2, Redis 1, k6 6).
awk '{ gsub("%","",$2); sum[$1]+=$2; n[$1]++ } END { for (c in sum) printf "  cpu %s: %.0f%% (avg of %d samples)\n", c, sum[c]/n[c], n[c] }' "$STATS" | sed -E 's/shortyload-//; s/k6-run-[0-9a-f]+/k6/; s/-1:/:/' | sort
# Throttling: when the container used its whole CPU quota in a scheduling period it is stopped until the next one, which shows up as latency
# without high average CPU. A run with much throttling measures the limit, not the code.
$COMPOSE exec -T api cat /sys/fs/cgroup/cpu/cpu.stat 2>/dev/null \
  | awk '/^nr_periods/ { p = $2 } /^nr_throttled/ { t = $2 } END { if (p > 0) printf "  api throttled in %d of %d CPU periods (%.0f%%)\n", t, p, 100 * t / p }' || true
rm -f "$STATS"
