using NServiceBus;
using Repro.LoggerFix;

using DefaultFactory = NServiceBus.Logging.DefaultFactory;
using LogManager = NServiceBus.Logging.LogManager;

var mode = args.Contains("--race")
    ? ShutdownFailureMode.DeleteLogFileRace
    : ShutdownFailureMode.RemoveLogDirectory;

// A directory of our own, so the scenario never touches the build output. This is also the
// only knob that reaches LogManager's fallback logger: Configure<RollingLoggerProviderOptions>
// only affects the provider registered in the host container, which is disabled here.
var logDirectory = Path.Combine(Path.GetTempPath(), "repro-loggerfix-logs");
Directory.CreateDirectory(logDirectory);
foreach (var stale in Directory.EnumerateFiles(logDirectory, "nsb_log_*.txt"))
{
    File.Delete(stale);
}

#pragma warning disable CS0618 // deprecated, and the only API that reaches the fallback logger
LogManager.Use<DefaultFactory>().Directory(logDirectory);
#pragma warning restore CS0618

var builder = Host.CreateApplicationBuilder(args);

// Host.CreateApplicationBuilder registers Console/Debug/EventSource, so NServiceBus' own
// providers stay disabled in the host container. Out-of-slot writes therefore go to
// LogManager's private fallback factory, where the rolling file provider is always enabled.
builder.Services.AddSingleton(new ShutdownLogFileDeletionRaceSettings
{
    LogDirectory = logDirectory,
    Mode = mode
});
builder.Services.AddHostedService<ShutdownLogFileDeletionRace>();
builder.Services.AddSingleton<ClaimCheckLike>();

var endpointConfiguration = new EndpointConfiguration("Samples.Repro.LoggerFix");
endpointConfiguration.UseTransport(new LearningTransport
{
    StorageDirectory = Path.Combine(Path.GetTempPath(), "repro-loggerfix-transport")
});
endpointConfiguration.UseSerialization<SystemJsonSerializer>();

builder.Services.AddNServiceBusEndpoint(endpointConfiguration);

var host = builder.Build();

// Resolve it so the container owns it and disposes it during shutdown.
_ = host.Services.GetRequiredService<ClaimCheckLike>();

Console.WriteLine($"Log directory: {logDirectory}");
Console.WriteLine($"Mode         : {mode}");

// --debug leaves the exception unhandled and waits for Ctrl+C, so a debugger breaks on the
// throw instead of the sample swallowing it. Without it the sample self-terminates and
// reports a result, which is what the scripts need.
var debugMode = args.Contains("--debug");

if (debugMode)
{
    Console.WriteLine();
    Console.WriteLine("Debug mode: press Ctrl+C to shut down and trigger the failure.");
    Console.WriteLine("Set a breakpoint in NServiceBus RollingLogger.cs at SyncFileSystem().");
    await host.RunAsync();
    return 0;
}

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    lifetime.StopApplication();
});

try
{
    await host.RunAsync();
    Console.WriteLine();
    Console.WriteLine("RESULT: shut down cleanly - SyncFileSystem is guarded.");
    return 0;
}
catch (Exception exception)
{
    Console.WriteLine();
    Console.WriteLine("RESULT: shutdown threw - SyncFileSystem is NOT guarded.");
    Console.WriteLine(exception);
    return 1;
}
