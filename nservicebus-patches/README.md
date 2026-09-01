# NServiceBus patches

Proposed changes to NServiceBus itself, kept here so they can be reviewed alongside the
repro before anything is opened upstream.

## 0001 - guard log file preparation

`RollingLogger.WriteLine` calls `SyncFileSystem()` outside the try/catch that already guards
the write. `SyncFileSystem()` enumerates the target directory and reads the size of the
newest matching file, and both fail if another process removes files concurrently. The
exception then propagates out of the log statement and can take down the caller.

Branch pushed for review:
https://github.com/claudedowling/NServiceBus/tree/fix/rolling-logger-syncfilesystem-throws

Based on `release-10.2`, the branch tag `10.2.8` was cut from.

### Applying it to your own fork

```bash
git clone git@github.com:<you>/NServiceBus.git
cd NServiceBus
git checkout -b fix/rolling-logger-syncfilesystem-throws origin/release-10.2
git am --reset-author ../nservicebus-patches/0001-rolling-logger-syncfilesystem-guard.patch
```

`--reset-author` reattributes the commit to you, which is what you want if you are opening
the PR under your own account.

### Verifying it

```bash
dotnet build src/NServiceBus.Core.Analyzer/NServiceBus.Core.Analyzer.csproj
dotnet build src/NServiceBus.Core.Analyzer.Fixes/NServiceBus.Core.Analyzer.Fixes.csproj
dotnet test src/NServiceBus.Core.Tests/NServiceBus.Core.Tests.csproj --filter "FullyQualifiedName~RollingLogger"
```

The analyzer projects must be built first or `NServiceBus.Core` fails to pack them.

Measured on this branch:

| | Result |
| --- | --- |
| `RollingLogger` tests before the fix | Failed 1, Passed 17 |
| `RollingLogger` tests after the fix  | Passed 28 |
| Full `NServiceBus.Core.Tests`        | Failed 3, Passed 1142 |

The 3 failures are environmental, not caused by this change - they fail identically on an
unmodified checkout. `Culture` needs a real ICU, `ApproveNullableTypes` is a line-ending
mismatch on Linux, and `LoggersShouldBeStaticField` trips on a compiler-generated async
state machine field.

## Not yet written

The second change - routing out-of-slot logs to the host's `ILoggerFactory` so they reach
App Insights instead of a file - introduces process-wide static state and needs a decision
about multiple endpoints per process. Worth an issue and a discussion before code.
