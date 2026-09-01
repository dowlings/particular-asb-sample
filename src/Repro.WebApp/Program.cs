using Microsoft.Extensions.Logging.Abstractions;
using NServiceBus;
using Repro.WebApp;

using DefaultFactory = NServiceBus.Logging.DefaultFactory;
using ExtensionsLoggerFactory = NServiceBus.Extensions.Logging.ExtensionsLoggerFactory;
using LogManager = NServiceBus.Logging.LogManager;

var builder = WebApplication.CreateBuilder(args);

var appSettings = new AppSettings();
builder.Configuration.GetSection("AppSettings").Bind(appSettings);

var loggingMode = LoggingModes.Parse(builder.Configuration["LoggingMode"] ?? appSettings.LoggingMode);

// Start from a clean slate so the probe result at the end is unambiguous.
LogFileProbe.DeleteExistingLogFiles();

ConfigureNServiceBus(builder, appSettings, loggingMode);

var app = builder.Build();

app.MapGet("/", () => Results.Text(LogFileProbe.Report(loggingMode, "on request"), "text/plain"));

app.MapPost("/ping", async (IMessageSession messageSession) =>
{
    await messageSession.SendLocal(new Ping());
    return Results.Accepted();
});

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

// The shutdown path described on the ticket. By the time these callbacks run the
// endpoint's log slot has been unregistered, so NServiceBus log statements fall
// back to LogManager's private factory and hit the file system.
lifetime.ApplicationStopping.Register(() => ReproLog.OutOfSlot.Info("ApplicationStopping"));
lifetime.ApplicationStopped.Register(() => ReproLog.OutOfSlot.Info("ApplicationStopped"));

ReproLog.OutOfSlot.Info("Web app starting up");

app.Run();

Console.WriteLine(LogFileProbe.Report(loggingMode, "after shutdown"));

// ---------------------------------------------------------------------------
// Below is the customer's wire-up from the ticket, with their custom code
// (SendGrid, Redis, callback queues, OpenTelemetry, MVC) removed.
// ---------------------------------------------------------------------------

static void ConfigureNServiceBus(WebApplicationBuilder builder, AppSettings appSettings, LoggingMode loggingMode)
{
    switch (loggingMode)
    {
        case LoggingMode.Customer:
            // The ticket as filed: no logging wire-up before the host is built.
            break;

        case LoggingMode.NullLoggerProvider:
            // What we suggested on the ticket. This adds an ILoggerProvider to the
            // host container, which does disable the rolling provider registered in
            // that container - but not the one inside LogManager's fallback factory.
            builder.Logging.AddProvider(NullLoggerProvider.Instance);
            break;

        case LoggingMode.RollingLoggerOptions:
            // Documented replacement for DefaultFactory. Also only reaches the
            // provider registered in the host container.
            builder.Services.Configure<RollingLoggerProviderOptions>(
                options => options.Directory = LogFileProbe.RedirectDirectory);
            break;

        case LoggingMode.DefaultFactoryDirectory:
#pragma warning disable CS0618 // deprecated in 10.2, still the only knob the fallback factory reads
            LogManager.Use<DefaultFactory>().Directory(LogFileProbe.RedirectDirectory);
#pragma warning restore CS0618
            break;

        case LoggingMode.LogManagerUseFactory:
            // Point LogManager at Microsoft.Extensions.Logging before anything else
            // runs. This is the NServiceBus.Extensions.Logging bridge the customer skipped.
#pragma warning disable CS0618 // UseFactory: error from NServiceBus 11. ExtensionsLoggerFactory: error from Extensions.Logging 5
            LogManager.UseFactory(new ExtensionsLoggerFactory(CreateLoggerFactory(builder)));
#pragma warning restore CS0618
            break;

        case LoggingMode.DefaultFactoryLevelFatal:
            // Minimal change: leave everything else alone and just raise the
            // fallback logger's minimum level. Fatal entries would still be written.
#pragma warning disable CS0618
            LogManager.Use<DefaultFactory>().Level(NServiceBus.Logging.LogLevel.Fatal);
#pragma warning restore CS0618
            break;

        case LoggingMode.CustomFactoryDefinition:
            // Same effect as LogManagerUseFactory, but implemented in this repo
            // instead of via the NServiceBus.Extensions.Logging package.
            MelLoggingFactoryDefinition.Factory = CreateLoggerFactory(builder);
#pragma warning disable CS0618
            LogManager.Use<MelLoggingFactoryDefinition>();
#pragma warning restore CS0618
            break;
    }

    builder.Host
        .ConfigureServices((_, services) =>
        {
            ConfigureTelemetry(services, appSettings);

            var endpointConfiguration = ReproNServiceBusConfiguration.ConfigEndPoint(appSettings);

            services.AddNServiceBusEndpoint(endpointConfiguration);
        });
}

static void ConfigureTelemetry(IServiceCollection services, AppSettings appSettings)
{
    if (string.IsNullOrWhiteSpace(appSettings.APPLICATIONINSIGHTS_CONNECTION_STRING))
    {
        return;
    }

    // Registers ApplicationInsightsLoggerProvider, a non-NServiceBus ILoggerProvider.
    services.AddApplicationInsightsTelemetry(
        options => options.ConnectionString = appSettings.APPLICATIONINSIGHTS_CONNECTION_STRING);
}

static ILoggerFactory CreateLoggerFactory(WebApplicationBuilder builder) =>
    LoggerFactory.Create(logging =>
    {
        logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        logging.AddSimpleConsole(options => options.SingleLine = true);
    });
