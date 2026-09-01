#!/usr/bin/env bash
# Runs Repro.LoggerFix against a local NServiceBus checkout.
#
#   ./repro-logger-fix.sh /path/to/NServiceBus [attempts] [--race]
#
# Default mode is deterministic: it fails every run without the fix and never with it.
# --race reproduces the customer's exact FileNotFoundException instead, but only lands
# occasionally, so give it plenty of attempts.
set -uo pipefail

cd "$(dirname "$0")"
NSB="${1:?usage: $0 /path/to/NServiceBus [attempts] [--race]}"
ATTEMPTS="${2:-4}"
MODE="${3:-}"

dotnet build src/Repro.LoggerFix/Repro.LoggerFix.csproj -p:NServiceBusPath="$NSB" -v q || exit 1

app=src/Repro.LoggerFix/bin/Debug/net10.0/Repro.LoggerFix.dll
threw=0

for i in $(seq 1 "$ATTEMPTS"); do
    if dotnet "$app" $MODE 2>&1 | grep -q "NOT guarded"; then
        threw=$((threw + 1))
        printf 'attempt %2d: THREW\n' "$i"
    else
        printf 'attempt %2d: clean\n' "$i"
    fi
done

echo
echo "threw $threw of $ATTEMPTS"
if [ "$threw" -eq 0 ]; then
    echo "=> RollingLogger.WriteLine guards SyncFileSystem. The fix is in place."
else
    echo "=> RollingLogger.WriteLine lets SyncFileSystem throw. The fix is NOT in place."
fi
