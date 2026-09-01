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

This is the `NServiceBus.Extensions.Logging` bridge. The ticket says "we are not using
UseLogger, as suggested by NServicebus, since AddNServiceBusEndpoint would use the default
.net Logger" — there is no `UseLogger` API in any Particular repo, so they were most likely
referring to this bridge, the one package on their list they reference but never call.

Their reasoning was half-right, and the correct half matters: `AddNServiceBusEndpoint`
**does** use the .NET logger, for every in-slot log statement. That is why their App Insights
data looks right. The inference only fails for out-of-slot statements, and nothing in the
documentation flags that case. This is a reasonable read of the docs that hits an
undocumented edge, not a customer mistake.

Worth telling them that on 10.2.8 this is currently the only wire-up that suppresses the
file. Nothing forces them off it: pinned to NServiceBus 10.2.8 and
NServiceBus.Extensions.Logging 4.1.0, the fix keeps working indefinitely.

The problem is what happens when they do upgrade. Both halves are deprecated, in different
packages on different schemes:

| API                       | Package                          | Error from | Removed in |
| ------------------------- | -------------------------------- | ---------- | ---------- |
| `LogManager.UseFactory`   | `NServiceBus`                    | 11         | 12         |
| `ExtensionsLoggerFactory` | `NServiceBus.Extensions.Logging` | 5          | 6          |

Both obsolete messages direct users to "configure logging on the host builder" — which is
exactly what this repro shows does not work for out-of-slot statements. The bridge is being
retired *because* core now uses Microsoft.Extensions.Logging natively, but the native path
does not cover the fallback case the bridge covers.

So the deprecation removes the escape hatch without replacing its function. On the day they
move to NServiceBus 11, `LogManager.UseFactory` becomes a compile error and there is no
supported fix left. That is worth raising with the NServiceBus team on its own merits, not
because of any deadline — I have no information about the release timing of either package.

## The two conditions

A file appears only when **one of these two** is true. Measured with three minimal
generic-host programs:

| Variant                      | Host builder                  | Out-of-slot logging | Console            | File          |
| ---------------------------- | ----------------------------- | ------------------- | ------------------ | ------------- |
| A — plain sample             | `CreateApplicationBuilder`     | none                | 20 lines, `info:`  | none          |
| B — A plus one `LogManager` call | `CreateApplicationBuilder` | one line            | same               | that one line |
| C — Particular's validation harness | `CreateEmptyApplicationBuilder` | none            | bare, unprefixed   | everything    |

**Condition 1 — the host registers no logging providers of its own.**
`Host.CreateEmptyApplicationBuilder` registers none, so NServiceBus' own file and console
providers stay enabled and capture everything (variant C). `Host.CreateApplicationBuilder`
and `WebApplication.CreateBuilder` both register Console/Debug/EventSource, which disables
them. The `info:` prefix is the tell: prefixed means the host's Console provider, bare means
NServiceBus' colored console provider.

**Condition 2 — something logs outside an endpoint slot.** Adding a single
`LogManager.GetLogger("X").Info(...)` to variant A produces a file containing exactly that
one line (variant B).

### Why the suggested fix passed locally and failed in production

The harness Particular used to validate `NullLoggerProvider` before sending it - a modified
docs.particular.net GenericHost sample using `Host.CreateEmptyApplicationBuilder` - fails
**condition 1**, and `builder.Logging.AddProvider` genuinely fixes condition 1.

The customer's app fails **condition 2**. Their host is `WebApplicationBuilder` - the declared
parameter type in the code on the ticket, so this is evidenced rather than inferred - and it
already registers providers: App Insights plus the ASP.NET Core defaults. Condition 1 was
never their problem. Their file comes from the fallback factory, which `AddProvider` cannot
reach.

The two reproduce the same symptom through different mechanisms, which is why the fix
validated against one did nothing for the other.

This also means the customer's problem is not about their choice of host. Something in their
application logs through `LogManager` outside an endpoint slot. Worth asking them what.

## Why the Azure Functions host does not have this problem

An `NServiceBusTriggerFunction` app on the same NServiceBus version never writes the file.
`NServiceBus.AzureFunctions.Worker.ServiceBus` 7.1.2 does the fix in a static constructor:

```csharp
// ServiceBusTriggeredEndpointConfiguration.cs, line 18
#pragma warning disable CS0618 // Type or member is obsolete
static ServiceBusTriggeredEndpointConfiguration() => LogManager.UseFactory(FunctionsLoggerFactory.Instance);
#pragma warning restore CS0618
```

That runs on first touch of the type, during `builder.AddNServiceBus()`, before any
NServiceBus logging happens — so `EndpointCreator` never registers the file provider and
`GetDefaultLogger()` never reaches `FallbackLoggerFactory`. Out-of-slot entries are not lost
either: `FunctionsLoggerFactory` holds an `AsyncLocal<ILogger>` set per invocation, and
queues entries when none is set, flushing them into the next invocation's logger.

Two things follow.

Particular suppress `CS0618` in their own shipping code to make this work, which is good
evidence that there is currently no non-deprecated way to achieve it.

And the asymmetry is the real bug story: same NServiceBus version, same transport, but the
Functions host is immune and the generic/web host is not — purely because one package sets
the factory in a static constructor and `AddNServiceBusEndpoint` does not.

### The recommended fix

Pass the **host's own** `ILoggerFactory` — the one App Insights registered into — rather than
a separate factory built by hand:

```csharp
var app = builder.Build();

#pragma warning disable CS0618
LogManager.UseFactory(new ExtensionsLoggerFactory(app.Services.GetRequiredService<ILoggerFactory>()));
#pragma warning restore CS0618

await app.RunAsync();
```

Building a standalone `LoggerFactory.Create(...)` also stops the file, but
`ExternalLoggerFactoryAdapter.GetLogger` routes **every** NServiceBus log through whatever
factory is supplied — in-slot included. A console-only factory therefore silently stops
NServiceBus logs reaching App Insights, which is the behaviour the ticket asks to preserve.

Calling this after `builder.Build()` is not too late. The providers `EndpointCreator`
registered are already inert, because App Insights and the ASP.NET Core defaults disable
them, and nothing logs out-of-slot between `AddNServiceBusEndpoint` and this line.

Measured: no file; out-of-slot entries arrive on the host pipeline as
`info: Repro.OutOfSlotComponent[0]`; in-slot NServiceBus logs continue unchanged; shutdown
completes cleanly with an App Insights provider registered.

There is in principle a window between `AddNServiceBusEndpoint` and this call. Nothing logs
out-of-slot in it here, but a deferred factory that resolves the host's `ILoggerFactory`
lazily would close it - the same pattern `FunctionsLoggerFactory` uses.

## Every way to control the default logging

The complete public surface for influencing NServiceBus logging in 10.2.8 is five
things. All of them are measured below; only the last two are worth using.

| Approach                                                | Stops the file? | Notes                                                   |
| ------------------------------------------------------- | --------------- | ------------------------------------------------------- |
| `builder.Logging.AddProvider(...)`                       | no              | host container only — cannot reach the fallback          |
| `Configure<RollingLoggerProviderOptions>(...)`            | no              | host container only — cannot reach the fallback          |
| `LogManager.Use<DefaultFactory>().Directory(x)`           | no              | still on disk, just somewhere else                       |
| `LogManager.Use<DefaultFactory>().Level(Fatal)`           | yes             | **silences the entire host logging pipeline — avoid**    |
| `LogManager.UseFactory(new ExtensionsLoggerFactory(f))`   | yes             | needs the deprecated Extensions.Logging package          |
| `LogManager.Use<TCustom>()` with your own definition      | yes             | ~40 lines you own, no extra package                      |

Note what this means: **the only non-obsolete option, `Configure<RollingLoggerProviderOptions>`,
cannot control the fallback logger at all.** `FallbackLoggerFactory` builds its private
container from `DefaultFactory`'s directory and level alone, so the only reachable knobs are
deprecated ones.

### Why `Level(Fatal)` must not be used

It does suppress the file, but `AddNServiceBusLoggingProviders` calls `SetMinimumLevel` on
the **host's** logging builder, not just its own providers. In the measured run the entire
console output was the probe report — no ASP.NET Core startup logs, no NServiceBus logs,
nothing. It would silence their App Insights telemetry too.

### The option worth recommending

`LoggingMode=CustomFactoryDefinition` (see `CustomLogging.cs`) implements
`LoggingFactoryDefinition` and `ILog` directly over `Microsoft.Extensions.Logging`. It
suppresses the file, routes every log statement — in-slot and out-of-slot — to the host
pipeline, and drops the dependency on `NServiceBus.Extensions.Logging` entirely. That leaves
`LogManager.Use<T>` in core as the only deprecated API in play instead of two.

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
| `DefaultFactoryLevelFatal`  | `LogManager.Use<DefaultFactory>().Level(Fatal)`                   | no file — but silences everything |
| `CustomFactoryDefinition` | Own `LoggingFactoryDefinition` over MEL, no extra package          | **no file**, logs go to MEL     |
| `HostFactoryAfterBuild` | `UseFactory` with the **host's** `ILoggerFactory`, after `Build()`   | **no file**, logs keep App Insights |

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
