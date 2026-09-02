using NServiceBus;
using NServiceBus.Logging;
using Repro.Minimal;

var logDirectory = Path.Combine(Path.GetTempPath(), "repro-minimal-logs");
Directory.CreateDirectory(logDirectory);

LogManager.Use<DefaultFactory>().Directory(logDirectory);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ClaimCheck>();
builder.Services.AddHostedService(_ => new LogDirectoryRemover(logDirectory));

var endpointConfiguration = new EndpointConfiguration("Repro.Minimal");
endpointConfiguration.UseTransport(new LearningTransport
{
    StorageDirectory = Path.Combine(Path.GetTempPath(), "repro-minimal-transport")
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

await host.RunAsync();
