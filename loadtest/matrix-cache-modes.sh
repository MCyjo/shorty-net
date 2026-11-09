#!/usr/bin/env bash
# The redirect comparison: no cache vs memory only vs memory + Redis, at three arrival rates, repeated.
#   loadtest/matrix-cache-modes.sh [repetitions]
# About 2.3 minutes per run (15 s warm-up, 60 s measured, plus starting a fresh stack): 18 runs for 2 repetitions.
# Leave the machine idle while it runs: the load generator, the API and the databases all share it.
set -euo pipefail
cd "$(dirname "$0")"

REPS=${1:-2}
STAMP=$(date +%Y%m%d-%H%M)
OUT="results/$STAMP-redirect-cache-modes.md"
mkdir -p results

{
  echo "# Redirect throughput and latency by cache mode"
  echo
  echo "- Date: $(date -u +%Y-%m-%dT%H:%MZ)"
  echo "- Commit: $(git rev-parse --short HEAD)$(git diff --quiet && git diff --cached --quiet || echo ' (+ uncommitted changes)')"
  echo "- Machine: $(powershell -NoProfile -Command "(Get-CimInstance Win32_Processor | Select -First 1).Name; '{0:N0} GB RAM' -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB)" | tr '\r\n' ' ')"
  echo "- Docker: $(docker info --format '{{.OperatingSystem}}, {{.NCPU}} CPUs, {{.ServerVersion}}')"
  echo "- Limits: API 2 CPUs / 1 GB, PostgreSQL 2 CPUs (data in memory), Redis 1 CPU, k6 6 CPUs; all on this one machine"
  echo "- Load: open model (constant arrival rate), 15 s warm-up not counted, 60 s measured, 5000 links, skewed popularity (46% of requests on the top 10%)"
  echo "- Columns: name | target req/s | achieved req/s | p50 ms | p95 ms | p99 ms | max ms | errors % | dropped iterations"
  echo
} > "$OUT"

for rep in $(seq 1 "$REPS"); do
  for rate in 3000 6000 9000; do
    for mode in None MemoryOnly Hybrid; do
      echo "== rep $rep, $mode @ $rate" >&2
      {
        echo "### rep $rep: $mode @ $rate req/s"
        DURATION_S=60 WARMUP_S=15 ./run.sh "$mode-$rate-r$rep" redirect "$rate" CACHE_MODE="$mode" 2>&1 | grep -E "^\||cpu|throttled|FAILED|did not"
        echo
      } >> "$OUT"
    done
  done
done

./docker-down.sh 2>/dev/null || docker compose -f docker-compose.yml down -v --remove-orphans >/dev/null 2>&1 || true
echo "$OUT"
