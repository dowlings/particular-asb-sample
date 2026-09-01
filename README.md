# NServiceBus logs written to disk — repro for the AICPA / Strasz ticket

> "NServicebus logs getting written to file system and causing exception when process shuts down"

Reproduces the customer's environment (NServiceBus 10.2.8, Azure Service Bus transport,
Azure App Service) and demonstrates why the `NullLoggerProvider` workaround we sent them
does not fix it.

## What the customer is most likely running

The `Program.cs` fragment on the ticket is **not** an Azure Function. It is an ASP.NET Core
app:

- `WebApplication.CreateBuilder(args)` → `WebApplicationBuilder`
- `ConfigureMvc(services)` → MVC
- `services.AddNServiceBusEndpoint(endpointConfiguration)` → the **core NServiceBus 10.2** API
  (`NServiceBus.ServiceCollectionExtensions.AddNServiceBusEndpoint(IServiceCollection, EndpointConfiguration, object?)`),
  not anything from `NServiceBus.AzureFunctions.Worker.ServiceBus`
- "our Azure Web app environments" + app recycling → **Azure App Service**
- NServiceBus 10.2.8 targets `net10.0` only, so they are on **.NET 10**

`NServiceBus.AzureFunctions.Worker.ServiceBus 7.1.2` is on their package list because the
list is the union across their solution; it belongs to a sibling Functions project. This
sample therefore models the **web app**, and does not reference that package.

Their package set is internally consistent and current — `Persistence.CosmosDB 4.1.0` and
`Transport.AzureServiceBus 6.5.0` both require `NServiceBus >= 10.2.8`, so nothing here is
a stale-package problem.

## Root cause

NServiceBus 10.2 routes logging through `Microsoft.Extensions.Logging`, and ships its own
`ColoredConsoleLoggerProvider` and `RollingLoggerProvider`. Both self-disable when the
container holds any other `ILoggerProvider`:

```csharp
// RollingLoggerProvider.cs / ColoredConsoleLoggerProvider.cs
bool ShouldBeEnabled()
{
    var providers = serviceProvider.GetServices<ILoggerProvider>();
    return providers.All(p => p is RollingLoggerProvider or ColoredConsoleLoggerProvider);
}
```

That is the behaviour our advice relied on, and within the host container it works.

**The catch is that not every NServiceBus log statement goes through the host container.**

`LogManager` resolves loggers per *log slot*. A slot is established by the endpoint via
`LogManager.BeginSlotScope(...)`, tracked in an `AsyncLocal`. When a log statement executes
with no slot on the current async context, `SlotAwareLogger.GetDefaultLogger()` falls
through to `LogManager.FallbackLoggerFactory`, which **builds its own private
`ServiceCollection`**:

```csharp
// LogManager.cs — FallbackLoggerFactory.Create()
var services = new ServiceCollection();
services.AddLogging(builder =>
{
    builder.Services.Configure<RollingLoggerProviderOptions>(o => { o.Directory = directory; o.LogLevel = level; });
    builder.AddNServiceBusLoggingProviders(directory, level);
    builder.SetMinimumLevel(level);
});
var serviceProvider = services.BuildServiceProvider();
```

In that private container the only registered providers **are** the two NServiceBus ones,
so `ShouldBeEnabled()` returns `true` and the rolling file logger writes
`nsb_log_yyyy-MM-dd_N.txt` into `Host.GetOutputDirectory()` — on .NET 10 that is
`AppDomain.CurrentDomain.BaseDirectory`, i.e. the deployed app directory on App Service.

`builder.Logging.AddProvider(NullLoggerProvider.Instance)` only ever touches the **host**
container. It has no effect on this private one. That is why the fix worked in the
GenericHost sample — nothing there logs outside a slot — and did nothing in the customer's
app, where plenty does.

Their App Insights registration is in the same position: `ApplicationInsightsLoggerProvider`
suppresses the file logger for in-slot logs (which is why they *do* see NServiceBus entries
in App Insights) while out-of-slot logs keep going to disk. Both observations on the ticket
are true at the same time, and this explains why.

### Why it throws on shutdown

`RollingLogger.WriteLine` calls `SyncFileSystem()` **outside** the try/catch that guards the
actual append:

```csharp
public void WriteLine(string message)
{
    SyncFileSystem();   // not guarded
    InnerWrite(message); // guarded — swallows IOException and Trace.WriteLine's it
}
```

`SyncFileSystem()` calls `Directory.EnumerateFiles(targetDirectory, "nsb_log_*.txt")` and
`new FileInfo(logFile.Path).Length`, and it always runs on the first write because
`lastWriteDate` starts at `default`. During an App Service overlapped recycle the outgoing
process can hit a directory or file that is being swapped out, and the resulting
`IOException` / `FileNotFoundException` / `UnauthorizedAccessException` propagates out of the
log statement rather than being swallowed. The append itself uses `File.AppendAllText`,
which opens `FileShare.Read` — two processes writing the same file during overlapped recycle
also collide, though that path is caught.

Shutdown is the worst case because `SlotUnregisterer` removes the endpoint's slot during
container disposal, so anything logged after that point is by definition out-of-slot.

## The fix to give the customer

Point `LogManager` at Microsoft.Extensions.Logging **before anything else runs**:

```csharp
#pragma warning disable CS0618
LogManager.UseFactory(new ExtensionsLoggerFactory(loggerFactory));
#pragma warning restore CS0618
```

This sets `defaultLoggerFactoryDefinition = null`, which makes `IsExternalFactoryConfigured`
true. Two things follow:

1. `LogManager.GetLoggingConfiguration()` returns `null`, so `EndpointCreator` skips
   `AddNServiceBusLoggingProviders` entirely — the providers are never registered.
2. `GetDefaultLogger()` uses the supplied factory instead of `FallbackLoggerFactory`, so
   out-of-slot logs go to MEL too.

This is the `UseLogger` / `NServiceBus.Extensions.Logging` approach the customer explicitly
chose *not* to adopt. Worth telling them that on 10.2.8 it is currently the only wire-up that
suppresses the file, and that both `LogManager.UseFactory` and `ExtensionsLoggerFactory` are
marked obsolete (error from v11, removed in v12) — so this is a workaround with a shelf life,
and the underlying fallback behaviour looks like something we should raise with the
NServiceBus team rather than leave as guidance.

## Running it

```bash
dotnet run --project src/Repro.WebApp
```

Hit `Ctrl+C` and read the probe report printed after shutdown, or `GET /` while it runs.
`POST /ping` sends a message through the endpoint.

Select a variant with the `LoggingMode` environment variable:

```bash
LoggingMode=NullLoggerProvider dotnet run --project src/Repro.WebApp
```

| `LoggingMode`             | What it does                                                    | Result                          |
| ------------------------- | --------------------------------------------------------------- | ------------------------------- |
| `Customer` (default)      | The ticket as filed                                              | **file written** to output dir  |
| `NullLoggerProvider`      | The fix we suggested                                             | **file still written**          |
| `RollingLoggerOptions`    | `Configure<RollingLoggerProviderOptions>` — host container only   | **file still written**          |
| `DefaultFactoryDirectory` | `LogManager.Use<DefaultFactory>().Directory(...)`                 | redirected, but still on disk   |
| `LogManagerUseFactory`    | `LogManager.UseFactory(new ExtensionsLoggerFactory(...))`         | **no file**, logs go to MEL     |

Verified on .NET SDK 10.0.400, NServiceBus 10.2.8. Each run starts the web app,
sends it `SIGINT` after 12s and prints the probe report after graceful shutdown.

### The tell in the console output

In every failing mode the three out-of-slot messages print as bare lines while
every other NServiceBus message carries an `info:` prefix:

```
Web app starting up                       <-- fallback factory's console provider
info: NServiceBus.LicenseManager[0]       <-- host's MEL pipeline
      No valid license could be found...
ApplicationStopping                       <-- fallback again
```

Under `LogManagerUseFactory` they join the host pipeline instead, and nothing is
lost:

```
info: Repro.OutOfSlotComponent[0] Web app starting up
info: Repro.OutOfSlotComponent[0] ApplicationStopping
info: Repro.OutOfSlotComponent[0] ApplicationStopped
```

The contents of the file confirm only out-of-slot logs land there:

```
2026-09-01 03:28:50.970 INFO  Web app starting up
2026-09-01 03:29:05.503 INFO  ApplicationStopping
2026-09-01 03:29:05.564 INFO  ApplicationStopped
```

### Pointing it at real Azure

No Azure resources are needed to reproduce this; the file logger is transport-agnostic and
the sample defaults to `LearningTransport`. To match the customer more closely:

```bash
export AppSettings__AzureWebJobsServiceBus="Endpoint=sb://...;SharedAccessKeyName=...;SharedAccessKey=..."
export AppSettings__APPLICATIONINSIGHTS_CONNECTION_STRING="InstrumentationKey=...;IngestionEndpoint=..."
```

With an App Insights connection string set, `AddApplicationInsightsTelemetry` registers a
real external `ILoggerProvider`, which is the customer's actual situation.

Reproducing the *exception* (rather than just the file) needs an App Service deployment and
an overlapped recycle, since it depends on the file system being pulled out from under the
outgoing process.

## Status

Verified. Built and run on .NET SDK 10.0.400 against the customer's exact package
versions; the results table above is measured, not predicted.

> Note for this container specifically: `libicu` is not installed, so runs need
> `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. That is an artefact of this box, not
> of the repro.

## Deliberately left out

The customer's `SendGridEmailSender`, `ConnectionMultiplexer` (Redis), `CallbackQueueConfiguration`,
`TopologyOptionsLoader`, OpenTelemetry wire-up and MVC registration. None are needed to
reproduce the log file. `Callbacks`, `ClaimCheck`, `DataBus.AzureBlobStorage`,
`Metrics.ServiceControl`, `Persistence.CosmosDB` and `SagaAudit` are referenced but not
configured, so assembly scanning still sees them.
