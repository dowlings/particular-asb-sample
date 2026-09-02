using NServiceBus;
using Repro.Concurrent;

using DefaultFactory = NServiceBus.Logging.DefaultFactory;
using LogManager = NServiceBus.Logging.LogManager;

var logDirectory = args[0];
var shutdownAfterMs = int.Parse(args[1]);
var id = args[2];

Directory.CreateDirectory(logDirectory);
LogManager.Use<DefaultFactory>().Directory(logDirectory);

var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<ClaimCheck>();

var endpointConfiguration = new EndpointConfiguration($"Repro.Concurrent.{id}");
endpointConfiguration.UseTransport(new LearningTransport
{
    StorageDirectory = Path.Combine(Path.GetTempPath(), "repro-concurrent-transport")
});
endpointConfiguration.UseSerialization<SystemJsonSerializer>();

builder.Services.AddNServiceBusEndpoint(endpointConfiguration);

var host = builder.Build();
host.Services.GetRequiredService<ClaimCheck>();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(async () =>
{
    await Task.Delay(shutdownAfterMs);
    lifetime.StopApplication();
});

try
{
    await host.RunAsync();
    return 0;
}
catch (Exception exception)
{
    var root = exception.GetBaseException();
    Console.WriteLine($"FAILURE[{id}] {root.GetType().Name}: {root.Message}");

    // Distinguish a file that was genuinely removed from a directory entry that never
    // referred to a readable file.
    if (root is FileNotFoundException { FileName: { } missing })
    {
        Console.WriteLine($"  File.Exists now      : {File.Exists(missing)}");
        Console.WriteLine($"  in re-enumeration    : {Directory.EnumerateFiles(logDirectory, "nsb_log_*.txt").Any(f => f == missing)}");
    }

    Console.WriteLine($"  directory now        : {string.Join(", ", Directory.EnumerateFiles(logDirectory, "nsb_log_*.txt").Select(Path.GetFileName).Order())}");
    return 1;
}
