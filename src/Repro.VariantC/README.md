# Repro.VariantC

Shows the customer's failure happening in a **single process, single run** - no second
process, no previous recycle, no shared storage.

```bash
dotnet run --project src/Repro.VariantC              # structural proof, deterministic
dotnet run --project src/Repro.VariantC -- --race    # the failure
```

## Why one process is enough here

The host is built with `Host.CreateEmptyApplicationBuilder`, so it registers no logging
providers. `ShouldBeEnabled()` therefore returns true in the host container and NServiceBus'
own rolling file provider stays active there.

That gives the process **two** `RollingLogger` instances pointed at the same directory:

- one in the host container, used by in-slot logging
- one in `FallbackLoggerFactory`'s private container, used by out-of-slot logging

They are separate objects with separate `lastWriteDate` fields, so each does its own
first-write directory scan. The host one creates the file during start-up; the fallback one
scans and stats it at disposal.

Without `--race`, one run produces one file containing both:

```
2026-09-02 14:18:26.181 INFO  No valid license could be found...      <- in-slot, host container
2026-09-02 14:18:26.294 INFO  Application started...                  <- in-slot
2026-09-02 14:18:28.182 INFO  Blob storage data bus stopped           <- out-of-slot, fallback
```

## Measured

| NServiceBus | Result |
| --- | --- |
| master | threw 4 of 15 |
| `fix/rolling-logger-retry-on-vanished-file` | threw 0 of 15 |

The exception is the customer's:

```
FileNotFoundException: Could not find file '/tmp/repro-variantc-logs/nsb_log_2026-09-02_0.txt'
```

## Caveat

The deletion is still synthetic - `FileChurn` creates and deletes the file during shutdown.
What is *not* synthetic is that the file was created by this same process at start-up, so the
two directory scans, and the window between them, are entirely NServiceBus' own behaviour.

An app with App Insights or any other provider registered does not have this shape: the
host-container provider is disabled, leaving one `RollingLogger`, and the file must then come
from a different process.
