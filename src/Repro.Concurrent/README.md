# Repro.Concurrent

Tests whether NServiceBus can produce the customer's failure on its own, with several
endpoints sharing one log directory and shutting down at overlapping times - which is what
an Azure App Service recycle does. No synthetic deletion, unpatched NServiceBus.

```bash
./repro-concurrent.sh 12 4
```

## Result

    0 failing process exits across 12 rounds of 4 workers

The path is exercised, not skipped. The first worker finds an empty directory, so it creates
`nsb_log_<today>_0.txt` without stat-ing anything. Every later worker enumerates, finds that
file, and reads its size - the call that fails in the customer's stack. It succeeds every
time, because nothing deletes it.

## Why it cannot fail this way

`PurgeOldFiles` is the only place NServiceBus deletes a log file, and it can never delete the
file the size check is about to read. Both use the same ordering:

```csharp
GetTodaysNewest:   today's files, OrderByDescending(SequenceNumber), First
GetFilesToDelete:  all files, OrderByDescending(DatePart).ThenByDescending(SequenceNumber), Skip(10)
```

`GetTodaysNewest` takes the top of that ordering; `GetFilesToDelete` deletes from the bottom.
Today is the highest date, so today's highest sequence ranks first overall and is always
inside the ten that are kept. Seeding the directory with more files does not change this -
purge removes the oldest, never the selection target.

So NServiceBus cannot delete its own stat target, at any file count, with any number of
concurrent processes. Whatever removed `nsb_log_2026-07-21_0.txt` was outside NServiceBus.
