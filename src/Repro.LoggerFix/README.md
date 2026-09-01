# Repro.LoggerFix

Builds against a **local NServiceBus checkout** rather than the NuGet package, so the fix can
be swapped in by editing `RollingLogger.cs` and rebuilding.

```bash
./repro-logger-fix.sh /path/to/NServiceBus
```

Build the analyzer projects in that checkout once first, or `NServiceBus.Core` fails to pack
them:

```bash
dotnet build src/NServiceBus.Core.Analyzer/NServiceBus.Core.Analyzer.csproj
dotnet build src/NServiceBus.Core.Analyzer.Fixes/NServiceBus.Core.Analyzer.Fixes.csproj
```

## What it does

`ClaimCheckLike` mirrors `BlobStorageClaimCheck`: a static `ILog` written to from
`IDisposable.Dispose`. Container disposal never runs inside an endpoint log slot, so that
write goes to `LogManager`'s fallback rolling file logger. `ShutdownLogFileDeletionRace`
disturbs the log directory at the same moment, so choosing a file name fails.

No Azure resources, no ClaimCheck package, learning transport only.

## Two modes

**`RemoveLogDirectory`** (default) deletes the log directory during shutdown, so
`Directory.EnumerateFiles` inside `SyncFileSystem` throws every run.

**`DeleteLogFileRace`** (`--race`) creates and deletes the file the logger is about to
measure, reproducing the customer's exact `FileNotFoundException` at `CalculateNewFileName`.
It only fires when the deletion lands between the directory listing and the size check, so
it needs many attempts - measured here at roughly 1 in 5, sometimes 0 in 10.

Both exercise the same defect. `SyncFileSystem()` is called outside the try/catch that
guards the write, so anything it throws escapes into the caller.

## Measured

| NServiceBus | Mode | Result |
| --- | --- | --- |
| unpatched | `RemoveLogDirectory` | threw 4 of 4 |
| patched   | `RemoveLogDirectory` | threw 0 of 4 |

Unpatched, the exception matches the customer's shape - `WriteLine` line 21 into
`SyncFileSystem`, surfaced by `Microsoft.Extensions.Logging` as an `AggregateException` out
of `Dispose()`:

```
System.AggregateException: An error occurred while writing to logger(s).
 ---> System.IO.DirectoryNotFoundException: Could not find a part of the path '/tmp/repro-loggerfix-logs'.
   at NServiceBus.RollingLogger.GetNsbLogFiles(String targetDirectory)+MoveNext() RollingLogger.cs:line 92
   at NServiceBus.RollingLogger.SyncFileSystem() RollingLogger.cs:line 53
   at NServiceBus.RollingLogger.WriteLine(String message) RollingLogger.cs:line 21
```

## Note on the log directory

The scenario uses a directory under the temp path, not the build output, and points the
fallback logger at it with:

```csharp
LogManager.Use<DefaultFactory>().Directory(logDirectory);
```

That deprecated API is the only one that reaches the fallback logger.
`Configure<RollingLoggerProviderOptions>` affects only the provider registered in the host
container, which is disabled whenever the host has providers of its own.
