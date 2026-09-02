#!/usr/bin/env bash
# Runs several endpoints that share one log directory and shut down at overlapping times,
# which is what an App Service recycle does. No synthetic deletion.
#
#   ./repro-concurrent.sh [rounds] [workers]
set -uo pipefail

cd "$(dirname "$0")"
ROUNDS="${1:-15}"
WORKERS="${2:-4}"
LOGDIR="${TMPDIR:-/tmp}/repro-concurrent-logs"

dotnet build ConcurrentRepro.slnx -v q || exit 1
app=src/Repro.Concurrent/bin/Debug/net10.0/Repro.Concurrent.dll

failures=0
for round in $(seq 1 "$ROUNDS"); do
    rm -rf "$LOGDIR"; mkdir -p "$LOGDIR"
    pids=()
    for w in $(seq 1 "$WORKERS"); do
        # stagger shutdown by a few ms so the writes overlap rather than align
        dotnet "$app" "$LOGDIR" $((1500 + w * 7)) "w$w" > "/tmp/rc-$round-$w.out" 2>&1 &
        pids+=($!)
    done
    for p in "${pids[@]}"; do wait "$p" || failures=$((failures + 1)); done
    if grep -lq "FAILURE" /tmp/rc-$round-*.out 2>/dev/null; then
        echo "round $round:"
        grep -h -A3 "FAILURE" /tmp/rc-$round-*.out
    fi
    rm -f /tmp/rc-$round-*.out
done

echo
echo "$failures failing process exits across $ROUNDS rounds of $WORKERS workers"
