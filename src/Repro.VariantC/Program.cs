using NServiceBus;
using Repro.VariantC;

using DefaultFactory = NServiceBus.Logging.DefaultFactory;
using LogManager = NServiceBus.Logging.LogManager;

var race = args.Contains("--race");
var logDirectory = Path.Combine(Path.GetTempPath(), "repro-variantc-logs");
Directory.CreateDirectory(logDirectory);
foreach (var stale in Directory.EnumerateFiles(logDirectory, "nsb_log_*.txt"))
{
    File.Delete(stale);
}

LogManager.Use<DefaultFactory>().Directory(logDirectory);

// No logging providers of any kind. NServiceBus' own rolling file provider therefore stays
// enabled in the host container, so in-slot logging also writes to the log directory.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

builder.Services.AddSingleton<ClaimCheck>();
if (race)
{
    builder.Services.AddHostedService(sp => new FileChurn(sp.GetRequiredService<IHostApplicationLifetime>(), logDirectory));
}

var endpointConfiguration = new EndpointConfiguration("Repro.VariantC");
endpointConfiguration.UseTransport(new LearningTransport
{
    StorageDirectory = Path.Combine(Path.GetTempPath(), "repro-variantc-transport")
});
endpointConfiguration.UseSerialization<SystemJsonSerializer>();

builder.Services.AddNServiceBusEndpoint(endpointConfiguration);

var host = builder.Build();
host.Services.GetRequiredService<ClaimCheck>();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    lifetime.StopApplication();
});

int exitCode;
try
{
    await host.RunAsync();
    Console.WriteLine();
    Console.WriteLine("RESULT: clean shutdown");
    exitCode = 0;
}
catch (Exception exception)
{
    Console.WriteLine();
    Console.WriteLine($"RESULT: threw - {exception.GetBaseException().GetType().Name}: {exception.GetBaseException().Message}");
    return 1;
}

foreach (var file in Directory.EnumerateFiles(logDirectory, "nsb_log_*.txt").Order())
{
    Console.WriteLine($"--- {Path.GetFileName(file)} ---");
    try
    {
        Console.WriteLine(File.ReadAllText(file));
    }
    catch (IOException)
    {
    }
}

return exitCode;
