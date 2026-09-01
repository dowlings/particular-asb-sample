#!/usr/bin/env bash
# Runs the repro in every LoggingMode and prints whether nsb_log_*.txt appeared.
set -uo pipefail

cd "$(dirname "$0")"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"

dotnet build src/Repro.WebApp/Repro.WebApp.csproj -v q || exit 1

app=src/Repro.WebApp/bin/Debug/net10.0/Repro.WebApp.dll

for mode in Customer NullLoggerProvider RollingLoggerOptions DefaultFactoryDirectory LogManagerUseFactory DefaultFactoryLevelFatal CustomFactoryDefinition HostFactoryAfterBuild; do
    rm -f src/Repro.WebApp/bin/Debug/net10.0/nsb_log_*.txt "${TMPDIR:-/tmp}"/nsb-logging-repro/nsb_log_*.txt
    printf '\n########## %s ##########\n' "$mode"
    LoggingMode="$mode" timeout -s INT 12 dotnet "$app" 2>&1 \
        | sed -n '/NServiceBus log file probe/,/^====/p' \
        | grep -E "REPRODUCED|Redirected|nsb_log_"
done
