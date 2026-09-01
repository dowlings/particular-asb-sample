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

## Debugging it

Clone this repo and NServiceBus side by side - the project's default
`NServiceBusPath` is `../../../NServiceBus`, a sibling of this repo, so no configuration is
needed:

```
some-folder/
  particular-asb-sample/
  NServiceBus/
```

Build the analyzer projects in the NServiceBus clone once, then open
`Repro.LoggerFix.csproj` and run the **Debug the failure** profile. It passes `--debug`,
which leaves the exception unhandled and waits for Ctrl+C instead of self-terminating, so the
debugger breaks on the throw.

Because NServiceBus is a `ProjectReference`, breakpoints work directly in its source. Useful
places:

- `RollingLogger.SyncFileSystem()` - where the failure happens
- `RollingLogger.WriteLine()` - where it should have been caught
- `LogManager.SlotAwareLogger.Write()` - watch it fall through to `GetDefaultLogger()`
  because no endpoint slot is active
- `ClaimCheckLike.Dispose()` - the caller

To break on the original `DirectoryNotFoundException` rather than the `AggregateException`
that Microsoft.Extensions.Logging rethrows, enable first-chance breaking: in Visual Studio,
**Debug > Windows > Exception Settings**, tick **Common Language Runtime Exceptions**.

Remember the failure only happens on shutdown, so trigger Ctrl+C once the endpoint has
started.

## NU1105: Unable to find project information for NServiceBus.Core.csproj

NuGet cannot evaluate a `ProjectReference` to a project that is not in the loaded solution.
`Repro.LoggerFix` deliberately references NServiceBus by path, so in an IDE you need a
solution containing both. Create one - it is gitignored, since the path is machine-specific:

```
dotnet new sln -n LoggerFixDebug
dotnet sln LoggerFixDebug.slnx add src/Repro.LoggerFix/Repro.LoggerFix.csproj
dotnet sln LoggerFixDebug.slnx add C:\Repos-Particular\NServiceBus\src\NServiceBus.Core\NServiceBus.Core.csproj
dotnet sln LoggerFixDebug.slnx add C:\Repos-Particular\NServiceBus\src\NServiceBus.Core.Analyzer\NServiceBus.Core.Analyzer.csproj
dotnet sln LoggerFixDebug.slnx add C:\Repos-Particular\NServiceBus\src\NServiceBus.Core.Analyzer.Fixes\NServiceBus.Core.Analyzer.Fixes.csproj
```

Open `LoggerFixDebug.slnx` and debug `Repro.LoggerFix` from there. This is also nicer for
swapping the fix, since `RollingLogger.cs` is then in the same solution.

From the command line there is no solution involved and it restores without any of this:

```
dotnet run --project src/Repro.LoggerFix -p:NServiceBusPath=C:\Repos-Particular\NServiceBus -- --debug
```

## Setting the path

The project defaults `NServiceBusPath` to `../../../NServiceBus`, a sibling of this repo. If
your clone is elsewhere, copy `nservicebus.local.props.example` to `nservicebus.local.props`
in the repo root and set the path there - no need to edit the csproj. That file is
gitignored.
